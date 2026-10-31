using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 托盘图标的右键菜单，用 WinUI 的 <see cref="MenuFlyout"/> 画（路线图第 25 项）。
///
/// 做法：开一个 **1×1 的隐形宿主窗口**，挪到鼠标位置，再把 MenuFlyout 锚在它的根元素上。
/// 菜单本体是系统弹出的独立浮层，不受宿主窗口尺寸限制；菜单一收就把宿主窗口藏起来。
///
/// ⚠️ 为什么不在主窗口上弹：托盘菜单最常见的使用场景是主窗口已经收进托盘，
///    隐藏窗口上的 XamlRoot 弹不出浮层。
///
/// ⚠️ 三条规矩：
///   1. 只有一个实例；重复右键时先收起旧菜单；
///   2. 宿主窗口**从不 Close**，只 Hide —— 关了就得重建整个 XamlRoot；
///   3. 菜单项的点击回调由 <see cref="TrayIcon"/> 传进来，最终仍走 <c>CommandInvoked</c>。
/// </summary>
public sealed partial class TrayMenuWindow : Window
{
    private static TrayMenuWindow? _instance;

    private readonly AppWindow _appWindow;

    private MenuFlyout? _menu;
    private Action<string>? _invoke;

    /// <summary>首次弹出时窗口内容还没挂上视觉树，菜单先存这儿，等 Loaded 再弹。</summary>
    private IReadOnlyList<TrayMenuItem>? _pendingItems;

    private TrayMenuWindow()
    {
        InitializeComponent();

        ThemeHost.Apply(Root);

        var hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        _appWindow.Title = "托盘菜单";
        _appWindow.IsShownInSwitchers = false;      // 不进 Alt+Tab、不占任务栏

        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }

        WindowChrome.RemoveBorder(hwnd, rounded: false, dark: Root.ActualTheme == ElementTheme.Dark);
    }

    /// <summary>在鼠标位置弹出菜单。返回 false 时调用方回退到 Win32 原生菜单。</summary>
    public static bool Show(IReadOnlyList<TrayMenuItem> items, Action<string> invoke)
    {
        try
        {
            _instance ??= new TrayMenuWindow();
            return _instance.Present(items, invoke);
        }
        catch (Exception ex)
        {
            AppLog.Info("tray", "WinUI 托盘菜单失败: " + ex);
            return false;
        }
    }

    private bool Present(IReadOnlyList<TrayMenuItem> items, Action<string> invoke)
    {
        if (!GetCursorPos(out var pt)) return false;

        if (_menu is not null)
        {
            try { _menu.Hide(); } catch { }
            _menu = null;
        }

        _invoke = invoke;

        // 宿主窗口挪到鼠标处：菜单锚点 (0,0) 就落在光标上
        _appWindow.MoveAndResize(new RectInt32(pt.X, pt.Y, 1, 1));

        // ⚠️ 第一次弹出时窗口刚建好，Activate 之后内容才会挂上视觉树、Root 才有 XamlRoot；
        //    这时直接 ShowAt 会抛「This element does not have a XamlRoot」。
        if (Root.XamlRoot is null)
        {
            _pendingItems = items;
            Root.Loaded -= Root_Loaded;
            Root.Loaded += Root_Loaded;
        }
        else
        {
            ShowFlyout(items);
        }

        Activate();
        return true;
    }

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= Root_Loaded;
        var items = _pendingItems;
        _pendingItems = null;
        if (items is not null) ShowFlyout(items);
    }

    private void ShowFlyout(IReadOnlyList<TrayMenuItem> items)
    {
        try
        {
            var menu = Build(items);
            menu.Closed += Menu_Closed;
            menu.Opened += Menu_Opened;
            _menu = menu;

            menu.ShowAt(Root, new FlyoutShowOptions
            {
                Position = new Windows.Foundation.Point(0, 0),
                ShowMode = FlyoutShowMode.Standard,
            });
        }
        catch (Exception ex)
        {
            AppLog.Info("tray", "WinUI 托盘菜单弹出失败: " + ex);
            try { _appWindow.Hide(); } catch { }
        }
    }

    private MenuFlyout Build(IReadOnlyList<TrayMenuItem> items)
    {
        var menu = new MenuFlyout();

        foreach (var item in items)
        {
            if (item.Separator)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                continue;
            }

            var entry = new MenuFlyoutItem
            {
                Text = item.Text,
                // 系统菜单里默认项是粗体（Win11 的「打开」那种），照做
                FontWeight = item.IsDefault
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
            };

            var command = item.Command;
            entry.Click += (_, _) =>
            {
                // 先收菜单再执行：打开主窗口 / 退出这类动作不该压在菜单浮层底下
                try { _menu?.Hide(); } catch { }
                _invoke?.Invoke(command);
            };

            menu.Items.Add(entry);
        }

        return menu;
    }

    /// <summary>
    /// 菜单一打开，键盘焦点会落进第一项，系统就在那项上画一圈白色焦点框 ——
    /// 原生右键菜单不会这样（点开时干干净净，按方向键才出现高亮）。
    /// 把焦点交给浮层本体、按指针方式获取：键盘照样能用，但不画那圈框。
    /// </summary>
    private void Menu_Opened(object? sender, object e)
    {
        try
        {
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot))
            {
                if (popup.Child is MenuFlyoutPresenter presenter)
                {
                    presenter.Focus(FocusState.Pointer);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Info("tray", "托盘菜单聚焦失败: " + ex.Message);
        }
    }

    private void Menu_Closed(object? sender, object e)
    {
        if (!ReferenceEquals(sender, _menu)) return;
        _menu = null;
        try { _appWindow.Hide(); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
