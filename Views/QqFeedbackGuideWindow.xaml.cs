using System;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;
















public sealed partial class QqFeedbackGuideWindow : Window
{
    private const int GuideWidthDip = 700;
    private const int GuideHeightDip = 720;

    private static QqFeedbackGuideWindow? _instance;

    private AppWindow? _appWindow;

    
    private string _copyText = "";

    private bool _imagesLoaded;
    private bool _visible;

    private QqFeedbackGuideWindow()
    {
        InitializeComponent();
        Configure();
    }

    public static bool IsVisible => _instance?._visible == true;

    
    public static void Show(string copyText)
    {
        try
        {
            _instance ??= new QqFeedbackGuideWindow();
            _instance._copyText = copyText;
            _instance.EnsureImages();
            _instance.BringUp();
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("Q群反馈流程窗显示失败: " + ex.Message);
        }
    }

    public static void CloseIfOpen() => _instance?.HideSelf();

    
    
    
    
    
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
        Core.AppLog.Info("exit", "QQ反馈窗实例已请求 Close");
    }

    

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "在 Q 群中反馈";

            if (_appWindow.Presenter is OverlappedPresenter p)
            {
                p.SetBorderAndTitleBar(true, true);      
                p.IsResizable = false;                   
                p.IsMaximizable = false;
                p.IsMinimizable = true;                  
            }

            ConfigureTitleBar();
            
            BackdropHost.Apply(this, Root, App.Settings.Current.Backdrop);
            ThemeHost.Apply(Root);

            ApplySize();
            CenterOnScreen();

            Root.ActualThemeChanged += (_, _) => UpdateCaptionButtonColors();

            
            Root.AddHandler(UIElement.KeyDownEvent,
                new KeyEventHandler((_, e) =>
                {
                    if (e.Key == Windows.System.VirtualKey.Escape) HideSelf();
                }), true);

            
            
            
            _appWindow.Closing += (_, args) =>
            {
                Core.AppLog.Info("exit", $"QQ反馈窗 Closing: IsExiting={App.IsExiting}");
                if (App.IsExiting) return;
                args.Cancel = true;
                HideSelf();
            };
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("Q群反馈流程窗初始化失败: " + ex.Message);
        }
    }

    
    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        if (_appWindow is null) return;
        try
        {
            var bar = _appWindow.TitleBar;
            bar.ExtendsContentIntoTitleBar = true;
            bar.PreferredHeightOption = TitleBarHeightOption.Standard;    
            bar.ButtonBackgroundColor = Colors.Transparent;               
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("qq-guide", "Q群反馈流程窗标题栏定制不可用：" + ex.Message);
        }

        UpdateCaptionButtonColors();

        
        WindowChrome.SetRounded(WindowNative.GetWindowHandle(this), rounded: true);
    }

    
    private void UpdateCaptionButtonColors()
    {
        if (_appWindow is null) return;
        var dark = Root.ActualTheme == ElementTheme.Dark;
        try
        {
            var bar = _appWindow.TitleBar;
            bar.ButtonForegroundColor = dark ? Colors.White : Windows.UI.Color.FromArgb(255, 30, 30, 30);
            bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
            bar.ButtonHoverBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(26, 255, 255, 255)
                : Windows.UI.Color.FromArgb(20, 0, 0, 0);
            bar.ButtonPressedForegroundColor = dark
                ? Windows.UI.Color.FromArgb(255, 200, 200, 200)
                : Windows.UI.Color.FromArgb(255, 90, 90, 90);
            bar.ButtonInactiveForegroundColor = dark
                ? Windows.UI.Color.FromArgb(160, 255, 255, 255)
                : Windows.UI.Color.FromArgb(140, 0, 0, 0);
            bar.ButtonPressedBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(40, 255, 255, 255)
                : Windows.UI.Color.FromArgb(30, 0, 0, 0);
        }
        catch { }
    }

    
    
    
    
    private void ApplySize()
    {
        if (_appWindow is null) return;
        try
        {
            var scale = Scale();
            var work = DisplayArea.Primary.WorkArea;

            
            var maxW = work.Width / scale - 60;
            var maxH = work.Height / scale - 100;

            var w = (int)Math.Round(Math.Clamp(GuideWidthDip, 420, Math.Max(420, maxW)));
            var h = (int)Math.Round(Math.Clamp(GuideHeightDip, 380, Math.Max(380, maxH)));

            _appWindow.ResizeClient(new SizeInt32(
                (int)Math.Round(w * scale), (int)Math.Round(h * scale)));
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("qq-guide", "Q群反馈流程窗尺寸设置失败：" + ex.Message);
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
            Core.AppLog.Warn("qq-guide", "Q群反馈流程窗定位失败：" + ex.Message);
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
        _visible = true;
        try
        {
            Activate();
            var hwnd = WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, SW_SHOW);
            SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            
            Core.AppLog.Warn("qq-guide", "Q群反馈流程窗激活失败：" + ex.Message);
        }
    }

    private void HideSelf()
    {
        _visible = false;
        try { ShowWindow(WindowNative.GetWindowHandle(this), SW_HIDE); } catch { }
    }

    

    
    
    
    
    private void EnsureImages()
    {
        if (_imagesLoaded) return;
        _imagesLoaded = true;

        Bind(StepImage1, "qq-step-album-panel.png");
        Bind(StepImage2, "qq-step-create-album.png");
        Bind(StepImage3, "qq-step-upload-button.png");
        Bind(StepImage4, "qq-step-upload-pick.png");
    }

    private static void Bind(Microsoft.UI.Xaml.Controls.Image target, string fileName)
    {
        try
        {
            var path = EmbeddedAssets.ExtractToCache(fileName, fileName);
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
            target.Source = new BitmapImage(new Uri(path));
        }
        catch (Exception ex)
        {
            
            Core.AppLog.Warn("qq-guide", "操作截图加载失败 " + fileName + "：" + ex.Message);
        }
    }

    

    
    private void CopyAgain_Click(object sender, RoutedEventArgs e)
    {
        if (_copyText.Length == 0) return;
        if (QqFeedback.Copy(_copyText))
            (sender as Microsoft.UI.Xaml.Controls.Button)!.Content = "已复制到剪贴板";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => HideSelf();

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape) HideSelf();
    }

    

    private const int SW_SHOW = 5;
    private const int SW_HIDE = 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
