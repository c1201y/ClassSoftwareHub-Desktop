using System;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinRT.Interop;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace ClassSoftwareHub.Desktop.Views.Markdown;

/// <summary>
/// Markdown 沉浸模式：全屏只显示排版结果，适合投影与教室大屏。
/// Esc 退出，PageUp / PageDown 或空格翻页，滚轮照常滚动。
/// </summary>
public sealed partial class MarkdownStageWindow : Window
{
    private static MarkdownStageWindow? _instance;

    private AppWindow? _appWindow;
    private DispatcherQueueTimer? _hintTimer;

    private MarkdownStageWindow()
    {
        InitializeComponent();
        Configure();
    }

    public static void Show(string markdown)
    {
        if (_instance is not null)
        {
            _instance.SetContent(markdown);
            _instance.Activate();
            return;
        }

        try
        {
            _instance = new MarkdownStageWindow();
            _instance.SetContent(markdown);
            _instance.Activate();
            AppLog.Info("markdown", "沉浸模式已打开");
        }
        catch (Exception ex)
        {
            AppLog.Exception("markdown", ex, "沉浸模式窗口创建失败");
            _instance = null;
        }
    }

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "Markdown 展示";

            ThemeHost.Apply(Root);

            // 全屏交给系统 presenter，标题栏与边框一并收起。
            _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

            AddAccelerator(VirtualKey.Escape, Close);
            AddAccelerator(VirtualKey.PageDown, () => ScrollPage(1));
            AddAccelerator(VirtualKey.PageUp, () => ScrollPage(-1));
            AddAccelerator(VirtualKey.Space, () => ScrollPage(1));
            AddAccelerator(VirtualKey.Down, () => ScrollPage(0.35));
            AddAccelerator(VirtualKey.Up, () => ScrollPage(-0.35));

            Closed += (_, _) =>
            {
                _hintTimer?.Stop();
                _instance = null;
            };
        }
        catch (Exception ex)
        {
            AppLog.Exception("markdown", ex, "沉浸模式初始化");
        }
    }

    private void AddAccelerator(VirtualKey key, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key };

        accelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            action();
        };

        Root.KeyboardAccelerators.Add(accelerator);
    }

    private void ScrollPage(double pages)
    {
        var step = Math.Max(160, Scroller.ViewportHeight * 0.85) * pages;
        var target = Math.Max(0, Scroller.VerticalOffset + step);
        Scroller.ChangeView(null, target, null);
    }

    private void SetContent(string markdown)
    {
        if (Stage is null) return;

        MarkdownView.Render(
            Stage,
            markdown,
            ThemeBrush.Get(Root, "TextFillColorPrimaryBrush"),
            1.35);

        Scroller.ChangeView(null, 0, null, disableAnimation: true);
        ShowHint();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowHint()
    {
        HintText.Text = "右上角「退出」返回 · PageUp / PageDown 翻页";
        Hint.Visibility = Visibility.Visible;

        if (_hintTimer is null)
        {
            _hintTimer = DispatcherQueue.CreateTimer();
            _hintTimer.Interval = TimeSpan.FromSeconds(3);
            _hintTimer.IsRepeating = false;
            _hintTimer.Tick += (_, _) => Hint.Visibility = Visibility.Collapsed;
        }

        _hintTimer.Stop();
        _hintTimer.Start();
    }
}
