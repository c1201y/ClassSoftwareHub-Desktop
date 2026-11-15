using System;
using System.Diagnostics;
using System.Linq;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;
















public sealed partial class LegalWindow : Window
{
    private const int DefaultWidthDip = 920;
    private const int DefaultHeightDip = 800;

    private static LegalWindow? _instance;

    private AppWindow? _appWindow;

    
    
    private string _wantKey = "agreement";

    
    private bool _suppress;

    private LegalWindow()
    {
        InitializeComponent();
        BuildTabs();
        Configure();

        
        
        Root.Loaded += (_, _) => ShowDoc(_wantKey);
    }

    

    
    
    public static void Show(string? key)
    {
        try
        {
            _instance ??= new LegalWindow();
            _instance._wantKey = NormalizeKey(key);
            _instance.BuildTabs();          
            _instance.ShowDoc(_instance._wantKey);
            _instance.BringUp();
        }
        catch (Exception ex)
        {
            AppLog.Warning("legal", "打开条款窗口失败：" + ex.Message);
        }
    }

    
    
    
    
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
        AppLog.Info("exit", "条款窗口实例已请求 Close");
    }

    

    
    
    
    
    
    private void BuildTabs()
    {
        try
        {
            _suppress = true;
            DocSelector.Items.Clear();

            foreach (var doc in LegalTextSource.All)
            {
                DocSelector.Items.Add(new SelectorBarItem { Text = doc.Title, Tag = doc.Key });
            }

            _suppress = false;
        }
        catch (Exception ex)
        {
            _suppress = false;
            AppLog.Warning("legal", "条款档位生成失败：" + ex.Message);
        }
    }

    
    private void ShowDoc(string key)
    {
        try
        {
            var doc = LegalTextSource.Find(key) ?? LegalTextSource.All.FirstOrDefault();
            if (doc is null) return;                       

            _wantKey = doc.Key;
            DocTitle.Text = doc.Title;
            DocVersion.Text = doc.VersionLine;

            _suppress = true;
            SelectTab(doc.Key);
            _suppress = false;

            Render(doc);
            UpdateSourceBar();
        }
        catch (Exception ex)
        {
            AppLog.Warning("legal", "条款渲染失败：" + ex.Message);
        }
    }

    
    
    
    
    
    
    
    
    private void UpdateSourceBar()
    {
        try
        {
            if (!LegalTextSource.IsBuiltIn)
            {
                SourceBar.IsOpen = false;
                return;
            }

            SourceBar.Severity = InfoBarSeverity.Informational;
            SourceBar.Message = "当前显示随程序内置的条款，可能不是最新；联网后会自行获取最新版本。";
            SourceBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            AppLog.Warning("legal", "条款来源提示刷新失败：" + ex.Message);
        }
    }

    
    private void SelectTab(string key)
    {
        foreach (var obj in DocSelector.Items)
        {
            if (obj is SelectorBarItem item &&
                string.Equals(item.Tag as string, key, StringComparison.OrdinalIgnoreCase))
            {
                DocSelector.SelectedItem = item;
                return;
            }
        }
    }

    private void DocSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_suppress) return;
        if (sender.SelectedItem?.Tag is not string key) return;
        ShowDoc(key);
    }

    
    private void Render(LegalDoc doc)
    {
        BodyHost.Children.Clear();

        if (!string.IsNullOrWhiteSpace(doc.Intro))
            BodyHost.Children.Add(NewLine(doc.Intro, "LegalIntroStyle"));

        foreach (var section in doc.Sections)
        {
            if (!string.IsNullOrWhiteSpace(section.Heading))
                BodyHost.Children.Add(NewLine(section.Heading, "LegalHeadingStyle"));

            foreach (var item in section.Items)
                BodyHost.Children.Add(NewLine(item, "LegalBodyStyle"));
        }

        
        try
        {
            if (BodyHost.Parent is ScrollViewer sv) sv.ChangeView(null, 0, null, true);
        }
        catch {  }
    }

    private TextBlock NewLine(string text, string styleKey) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        Style = Root.Resources[styleKey] as Style,
    };

    private static string NormalizeKey(string? key)
        => string.IsNullOrWhiteSpace(key) ? "agreement" : key.Trim().ToLowerInvariant();

    

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "条款与声明";

            if (_appWindow.Presenter is OverlappedPresenter p)
            {
                
                p.IsResizable = true;                 
                p.IsMaximizable = true;
                p.IsMinimizable = true;
            }

            
            ModernWindow.ApplyTitleBar(this, TitleBarHost, Root);

            
            
            BackdropHost.Apply(this, Root, App.Settings.Current.Backdrop);
            ThemeHost.Apply(Root);

            ApplySize();
            CenterOnScreen();

            
            Closed += (_, _) => { _instance = null; };
        }
        catch (Exception ex)
        {
            AppLog.Warning("legal", "条款窗口初始化失败：" + ex.Message);
        }
    }

    
    private void ApplySize()
    {
        if (_appWindow is null) return;
        try
        {
            var scale = Scale();
            var work = DisplayArea.Primary.WorkArea;

            var maxW = work.Width / scale - 40;
            var maxH = work.Height / scale - 60;

            var w = Math.Clamp(DefaultWidthDip, 620, Math.Max(620, maxW));
            var h = Math.Clamp(DefaultHeightDip, 460, Math.Max(460, maxH));

            _appWindow.ResizeClient(new SizeInt32(
                (int)Math.Round(w * scale), (int)Math.Round(h * scale)));
        }
        catch (Exception ex)
        {
            AppLog.Warn("legal", "条款窗尺寸设置失败：" + ex.Message);
        }
    }

    private void CenterOnScreen()
    {
        if (_appWindow is null) return;
        try
        {
            var work = DisplayArea.Primary.WorkArea;
            var size = _appWindow.Size;
            _appWindow.Move(new PointInt32(
                work.X + Math.Max(0, (work.Width - size.Width) / 2),
                work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
        }
        catch (Exception ex)
        {
            AppLog.Warn("legal", "条款窗定位失败：" + ex.Message);
        }
    }

    private double Scale()
    {
        try
        {
            var dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch { return 1.0; }
    }

    private void BringUp()
    {
        try
        {
            Activate();
            var hwnd = WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, SW_SHOW);
            SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            AppLog.Exception("legal", ex, "唤醒条款窗");
        }
    }

    private const int SW_SHOW = 5;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
