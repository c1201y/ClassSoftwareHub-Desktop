using System;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Pages.Tools;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views.Share;

/// <summary>
/// 二维码分享的独立窗口：链接与文件都在这里生成，侧边栏或右键直接打开，不经过主界面。
/// </summary>
public sealed partial class QrShareWindow : Window
{
    private static QrShareWindow? _instance;

    private AppWindow? _appWindow;
    private QrCodeToolPage? _page;

    private QrShareWindow()
    {
        InitializeComponent();
        Configure();
    }

    /// <summary>侧边栏调用：不带文件，直接开一个空白分享窗口。</summary>
    public static void Show() => ShowCore(null);

    /// <summary>右键菜单调用：打开并预选指定文件。</summary>
    public static void Show(string path) => ShowCore(path);

    private static void ShowCore(string? path)
    {
        try
        {
            _instance ??= new QrShareWindow();
            _instance.Activate();

            if (!string.IsNullOrEmpty(path)) _instance.Load(path!);
        }
        catch (Exception ex)
        {
            AppLog.Exception("qrcode", ex, "打开二维码分享窗口");
        }
    }

    public static void CloseForExit()
    {
        var window = _instance;
        _instance = null;
        try { window?.Close(); } catch { }
    }

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "二维码分享";

            ModernWindow.ApplyTitleBar(this, TitleBarHost, Root);
            ThemeHost.Apply(Root);
            BackdropHost.Apply(this, Root, App.Settings.Current.Backdrop);

            _page = new QrCodeToolPage();
            HostRoot.Children.Add(_page);

            _appWindow.Resize(new SizeInt32(1000, 760));
            CenterOnScreen();

            // 分享窗口是一次性工具：不给最小化、最大化，尺寸也固定（2026-11-21 Nick 定）。
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.IsResizable = false;
            }

            Closed += (_, _) =>
            {
                _page = null;
                _instance = null;
            };

            AppLog.Step("qrcode", "二维码分享窗口已打开");
        }
        catch (Exception ex)
        {
            AppLog.Exception("qrcode", ex, "二维码分享窗口初始化");
        }
    }

    private void CenterOnScreen()
    {
        if (_appWindow is null) return;

        try
        {
            var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
            var work = area.WorkArea;
            var size = _appWindow.Size;

            _appWindow.Move(new PointInt32(
                work.X + Math.Max(0, (work.Width - size.Width) / 2),
                work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
        }
        catch { }
    }

    private void Load(string path)
    {
        _page?.LoadFile(path);
    }
}
