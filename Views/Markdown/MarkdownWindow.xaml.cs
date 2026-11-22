using System;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Pages.Tools;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views.Markdown;

/// <summary>
/// Markdown 写作的独立窗口：侧边栏点一下或右键 .md 文件直接进来，不必先打开主界面。
/// 页面本身仍是 <see cref="MarkdownToolPage"/>，主窗口里的那个入口照旧可用。
/// </summary>
public sealed partial class MarkdownWindow : Window
{
    private static MarkdownWindow? _instance;

    private AppWindow? _appWindow;
    private MarkdownToolPage? _page;

    private MarkdownWindow()
    {
        InitializeComponent();
        Configure();
    }

    /// <summary>侧边栏调用：不带文件，直接开一个空白写作窗口。</summary>
    public static void Show() => ShowCore(null);

    /// <summary>右键菜单调用：打开并导入指定文件。</summary>
    public static void Show(string path) => ShowCore(path);

    private static void ShowCore(string? path)
    {
        try
        {
            _instance ??= new MarkdownWindow();
            _instance.Activate();

            if (!string.IsNullOrEmpty(path)) _instance.Load(path!);
        }
        catch (Exception ex)
        {
            AppLog.Exception("markdown", ex, "打开 Markdown 窗口");
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
            _appWindow.Title = "Markdown 工具";

            ModernWindow.ApplyTitleBar(this, TitleBarHost, Root);
            ThemeHost.Apply(Root);
            BackdropHost.Apply(this, Root, App.Settings.Current.Backdrop);

            _page = new MarkdownToolPage(embedded: true);
            HostRoot.Children.Add(_page);

            _appWindow.Resize(new SizeInt32(1180, 800));
            CenterOnScreen();

            Closed += (_, _) =>
            {
                _page?.CloseWebView();
                _page = null;
                _instance = null;
            };

            AppLog.Step("markdown", "Markdown 窗口已打开");
        }
        catch (Exception ex)
        {
            AppLog.Exception("markdown", ex, "Markdown 窗口初始化");
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
        if (_page is null) return;
        _ = _page.ImportFromPath(path);
    }
}
