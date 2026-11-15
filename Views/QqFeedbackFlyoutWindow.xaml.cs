using System;
using System.Runtime.InteropServices;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;

















public sealed partial class QqFeedbackFlyoutWindow : Window
{
    
    private const int PanelWidthDip = 320;

    private const int MinPanelHeightDip = 118;

    
    private const int MarginDip = 22;

    private static QqFeedbackFlyoutWindow? _instance;

    private readonly FlyoutChrome _chrome;

    
    private string _copyText = "";

    private bool _visible;

    private QqFeedbackFlyoutWindow()
    {
        InitializeComponent();
        _chrome = new FlyoutChrome(this, Root, Panel, "反馈信息");
        Configure();
    }

    
    public static bool IsVisible => _instance?._visible == true;

    
    
    
    
    public static void Show(string copyText)
    {
        try
        {
            _instance ??= new QqFeedbackFlyoutWindow();
            _instance.Present(copyText);
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("反馈提醒浮窗显示失败: " + ex.Message);
        }
    }

    public static void CloseIfOpen() => _instance?.HideSelf();

    private void Configure()
    {
        try
        {
            ThemeHost.Apply(Root);                 

            
            Root.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Escape) HideSelf();
            };

            
            Root.ActualThemeChanged += (_, _) => _chrome.ApplyMaterial();
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("反馈提醒浮窗初始化失败: " + ex.Message);
        }
    }

    

    private void Present(string copyText)
    {
        _copyText = copyText;

        var scale = _chrome.Scale;
        var w = (int)Math.Round(PanelWidthDip * scale);
        var h = (int)Math.Round(SizeDip() * scale);

        _chrome.Present(BestPosition(w, h), w, h);
        _visible = true;

        _chrome.Finish(Root.ActualTheme == ElementTheme.Dark);   
        FlyoutFade.In(ContentHost, 140);
    }

    private void HideSelf()
    {
        if (!_visible) return;
        _visible = false;
        _chrome.HideDirect();      
    }

    
    
    
    
    private double SizeDip()
    {
        try
        {
            if (Panel.Child is not FrameworkElement host) return MinPanelHeightDip;

            host.Measure(new Windows.Foundation.Size(PanelWidthDip - 2, double.PositiveInfinity));
            return Math.Max(host.DesiredSize.Height + 2, MinPanelHeightDip);   
        }
        catch
        {
            return MinPanelHeightDip;
        }
    }

    
    
    
    
    private PointInt32 BestPosition(int w, int h)
    {
        var work = DisplayArea.Primary.WorkArea;
        var margin = (int)Math.Round(MarginDip * _chrome.Scale);

        var x = work.X + work.Width - w - margin;
        var y = work.Y + work.Height - h - margin;

        if (App.MainWindow is { } main && TryMainRect(main, out var r))
        {
            
            x = r.X + r.Width - w - margin;
            y = r.Y + r.Height - h - margin;
        }

        return new PointInt32(
            Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - w)),
            Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - h)));
    }

    
    
    
    
    private static bool TryMainRect(Window main, out RectInt32 rect)
    {
        rect = default;
        try
        {
            var hwnd = WindowNative.GetWindowHandle(main);
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
            if (!GetWindowRect(hwnd, out var r)) return false;
            if (r.Left < -10000 || r.Top < -10000) return false;

            rect = new RectInt32(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            return rect.Width > 0 && rect.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        QqFeedback.Copy(_copyText);
        HideSelf();

        
        
        QqFeedbackGuideWindow.Show(_copyText);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => HideSelf();

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape) HideSelf();
    }

    

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
}
