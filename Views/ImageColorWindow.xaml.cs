using System;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Pages.Tools;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;




















public sealed partial class ImageColorWindow : Window
{
    private const int WidthDip = 520;

    
    
    
    
    private const int EmptyHeightDip = 215;

    
    private const int PaletteHeightDip = 660;

    private static ImageColorWindow? _instance;

    private AppWindow? _appWindow;
    private ImageColorToolPage? _page;

    private ImageColorWindow()
    {
        InitializeComponent();
        Configure();
    }

    

    
    
    
    
    
    
    
    public static void Show()
    {
        try
        {
            _instance ??= new ImageColorWindow();
            _instance.BringUp();
        }
        catch (Exception ex)
        {
            AppLog.Exception("image-color", ex, "打开图片取色窗口");
        }
    }

    
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
    }

    

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "图片取色";

            if (_appWindow.Presenter is OverlappedPresenter p)
            {
                p.IsResizable = true;
                p.IsMaximizable = false;
                p.IsMinimizable = false;
            }

            
            WindowChrome.HideMinimizeMaximizeButtons(hwnd);

            ModernWindow.ApplyTitleBar(this, TitleBarHost, Root);

            
            BackdropHost.Apply(this, Root, App.Settings.Current.Backdrop);
            ThemeHost.Apply(Root);

            
            _page = new ImageColorToolPage(embedded: true)
            {
                HostWindowHandle = hwnd,
                ContentStateChanged = OnContentStateChanged,
            };
            ContentHost.Content = _page;

            ApplyHeight(EmptyHeightDip, center: true);

            Closed += (_, _) =>
            {
                _instance = null;
                _page = null;
            };
        }
        catch (Exception ex)
        {
            AppLog.Exception("image-color", ex, "图片取色窗口初始化");
        }
    }

    
    private void OnContentStateChanged(bool hasPalette)
    {
        
        ApplyHeight(hasPalette ? PaletteHeightDip : EmptyHeightDip, center: false);
    }

    
    
    
    
    
    
    
    
    private void ApplyHeight(int heightDip, bool center)
    {
        if (_appWindow is null) return;
        try
        {
            var scale = Scale();
            var work = DisplayArea.Primary.WorkArea;

            var maxW = work.Width / scale - 40;
            var maxH = work.Height / scale - 60;

            var w = Math.Clamp(WidthDip, 420, Math.Max(420, maxW));
            
            var h = Math.Clamp(heightDip, 200, Math.Max(200, maxH));

            _appWindow.ResizeClient(new SizeInt32(
                (int)Math.Round(w * scale), (int)Math.Round(h * scale)));

            var size = _appWindow.Size;

            if (center)
            {
                _appWindow.Move(new PointInt32(
                    work.X + Math.Max(0, (work.Width - size.Width) / 2),
                    work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
                return;
            }

            var pos = _appWindow.Position;
            var bottom = pos.Y + size.Height;
            var limit = work.Y + work.Height - 8;
            var top = bottom > limit ? Math.Max(work.Y + 8, limit - size.Height) : pos.Y;

            if (top != pos.Y) _appWindow.Move(new PointInt32(pos.X, top));

            AppLog.Step("image-color", $"窗口高度按内容调整：{h:0} dip");
        }
        catch (Exception ex)
        {
            AppLog.Warn("image-color", "图片取色窗尺寸设置失败：" + ex.Message);
        }
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
            AppLog.Exception("image-color", ex, "激活图片取色窗口");
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

    private const int SW_SHOW = 5;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
