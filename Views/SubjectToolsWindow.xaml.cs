using System;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;




















public sealed partial class SubjectToolsWindow : Window
{
    private const int DefaultWidthDip = 1180;
    private const int DefaultHeightDip = 780;

    
    private static readonly (string Id, string Name, Type Page)[] Tools =
    {
        ("function-plot", "函数图像", typeof(Pages.FunctionPlotPage)),
        ("equation", "方程求解", typeof(Pages.Tools.EquationPage)),
        ("chemistry", "化学方程式配平", typeof(Pages.Tools.ChemistryPage)),
    };

    private static SubjectToolsWindow? _instance;

    private AppWindow? _appWindow;

    
    
    
    
    
    private bool _suppress = true;

    
    private string? _wantId;

    private SubjectToolsWindow()
    {
        InitializeComponent();
        Configure();

        
        
        Root.Loaded += (_, _) =>
        {
            _suppress = false;
            SelectTool(_wantId);
        };
    }

    

    
    
    public static void ShowTool(string? toolId)
    {
        try
        {
            _instance ??= new SubjectToolsWindow();

            var id = Normalize(toolId) ?? Normalize(App.Settings.Current.SubjectToolsLastTool) ?? Tools[0].Id;
            _instance._wantId = id;
            _instance.SelectTool(id);
            _instance.BringUp();
        }
        catch (Exception ex)
        {
            AppLog.Exception("subject-tools", ex, "打开学科工具窗口");
        }
    }

    
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
        AppLog.Info("subject-tools", "学科工具窗口实例已请求 Close");
    }

    

    
    private void SelectTool(string? toolId)
    {
        var id = Normalize(toolId) ?? Tools[0].Id;
        var index = Array.FindIndex(Tools, t => t.Id == id);
        if (index < 0) index = 0;

        var was = _suppress;
        _suppress = true;
        try
        {
            Nav.SelectedItem = Nav.MenuItems[index];
        }
        finally
        {
            _suppress = was;
        }

        NavigateTo(Tools[index].Page, Tools[index].Id);
        Remember(Tools[index].Id);
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppress) return;
        if (args.SelectedItem is not NavigationViewItem { Tag: string id }) return;

        var index = Array.FindIndex(Tools, t => t.Id == id);
        if (index < 0) return;

        NavigateTo(Tools[index].Page, Tools[index].Id);
        Remember(Tools[index].Id);
    }

    private void NavigateTo(Type nativePage, string? toolId)
    {
        
        
        if (toolId is not null && ToolCarrier.RequestFor(toolId, nativePage, null) is { } request)
        {
            
            if (ContentFrame.Content is Pages.ToolHostPage) return;
            ContentFrame.Navigate(typeof(Pages.ToolHostPage), request);
            return;
        }

        
        if (ContentFrame.Content?.GetType() == nativePage) return;
        ContentFrame.Navigate(nativePage);
    }

    private static void Remember(string id)
    {
        try
        {
            if (App.Settings.Current.SubjectToolsLastTool == id) return;
            App.Settings.Current.SubjectToolsLastTool = id;
            App.Settings.Save();
        }
        catch (Exception ex)
        {
            AppLog.Warning("subject-tools", "记录上次使用的工具失败：" + ex.Message);
        }
    }

    private static string? Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var key = id.Trim();
        return Array.Exists(Tools, t => t.Id == key) ? key : null;
    }

    

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "学科工具";

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
            AppLog.Exception("subject-tools", ex, "学科工具窗口初始化");
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

            
            var w = Math.Clamp(DefaultWidthDip, 960, Math.Max(960, maxW));
            var h = Math.Clamp(DefaultHeightDip, 560, Math.Max(560, maxH));

            _appWindow.ResizeClient(new SizeInt32(
                (int)Math.Round(w * scale), (int)Math.Round(h * scale)));
        }
        catch (Exception ex)
        {
            AppLog.Warn("subject-tools", "学科工具窗尺寸设置失败：" + ex.Message);
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
            AppLog.Warn("subject-tools", "学科工具窗定位失败：" + ex.Message);
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
            AppLog.Exception("subject-tools", ex, "激活学科工具窗口");
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
