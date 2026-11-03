using System;
using System.Collections.Generic;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 放映时长按屏幕弹出的菜单（2026-11-03）。
///
/// <para>
/// 视觉一律交给系统：菜单本体是 <see cref="MenuFlyout"/>，和托盘菜单（<see cref="TrayMenuWindow"/>）
/// 是同一套做法 —— 1×1 隐形宿主窗口 + 系统画的浮层，圆角、阴影、悬停、键盘导航全由 WinUI 出，
/// 本文件只负责「摆在哪、放哪些项、点了谁去执行」。
/// </para>
///
/// <para>
/// ⚠️ 三条规矩（和托盘菜单一致，都是踩过的）：
///   1. 只有一个实例，重复长按先收掉旧的；
///   2. 宿主窗口**从不 Close**，只 Hide —— 关掉就得重建整个 XamlRoot；
///   3. 收盘时如果用户是点空白处关的（而不是点了菜单项），要把前台还给 PowerPoint，
///      否则放映窗口会一直停在「被别的窗口盖住」的状态。
/// </para>
/// </summary>
public sealed partial class PptMenuWindow : Window
{
    private static PptMenuWindow? _instance;

    private readonly AppWindow _appWindow;

    private MenuFlyout? _menu;

    /// <summary>首次弹出时窗口内容还没挂上视觉树，菜单先存这儿，等 Loaded 再弹。</summary>
    private bool _pending;

    /// <summary>防呆定时器：菜单万一没正常收起，钩子会一直把触摸吞掉，那就彻底点不动了。</summary>
    private DispatcherQueueTimer? _watchdog;

    private PptMenuWindow()
    {
        InitializeComponent();

        ThemeHost.Apply(Root);

        var hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        _appWindow.Title = "放映菜单";
        _appWindow.IsShownInSwitchers = false;      // 不进 Alt+Tab、不占任务栏

        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;                 // ⚠️ 必须：放映窗口是全屏的，不置顶就看不见
        }

        WindowChrome.RemoveBorder(hwnd, rounded: false, dark: Root.ActualTheme == ElementTheme.Dark);
    }

    /// <summary>
    /// 在屏幕坐标 (x, y) 处弹出菜单。坐标是**物理像素**，直接从钩子拿。
    /// 任何一步失败都只记日志，不抛给钩子线程。
    /// </summary>
    public static void Show(int x, int y)
    {
        var queue = App.MainWindow?.DispatcherQueue;
        if (queue is null)
        {
            AppLog.Warning("pptmenu", "主窗口不在了，长按菜单没弹出来");
            return;
        }

        queue.TryEnqueue(() =>
        {
            try
            {
                _instance ??= new PptMenuWindow();
                _instance.Present(x, y);
            }
            catch (Exception ex)
            {
                AppLog.Exception("pptmenu", ex, "弹出长按菜单");
                PptMenu.IsOpen = false;
            }
        });
    }

    private void Present(int x, int y)
    {
        // 上一次的还没收（长按连按两下），先收掉再说
        if (_menu is not null)
        {
            try { _menu.Hide(); } catch { }
            _menu = null;
        }

        PptMenu.IsOpen = true;
        PptMenu.OpenedTick = Environment.TickCount64;

        // 宿主窗口只有 1×1，摆在手指位置，菜单锚点 (0,0) 就落在手指上。
        //
        // ⚠️ 别改成「铺满全屏来让 flyout 自己接住窗外点击」（2026-11-03 试过并回退）：
        //    Grid 的 Background 设为 Transparent 只让**内容**透明，窗口自身还有一层不透明的
        //    底色（深色主题下就是近黑），铺满之后整张幻灯片被盖住 —— 屏幕上看到的是一片黑。
        //    「点空白处收起」现在由鼠标钩子负责（PptTouch.HandleMenuClick）。
        _appWindow.MoveAndResize(new RectInt32(x, y, 1, 1));

        // ⚠️ 第一次弹出时窗口刚建好，Activate 之后内容才会挂上视觉树、Root 才有 XamlRoot；
        //    这时直接 ShowAt 会抛「This element does not have a XamlRoot」。
        if (Root.XamlRoot is null)
        {
            _pending = true;
            Root.Loaded -= Root_Loaded;
            Root.Loaded += Root_Loaded;
        }
        else
        {
            ShowFlyout();
        }

        Activate();
        ArmWatchdog();
    }

    /// <summary>
    /// 从钩子线程叫过来：把菜单收起来。用在「点在菜单外面」那一下。
    /// 真正的收尾（隐藏窗口、把前台还给 PowerPoint）在 <see cref="Menu_Closed"/> 里。
    /// </summary>
    public static void HideMenu()
    {
        var queue = App.MainWindow?.DispatcherQueue;
        queue?.TryEnqueue(() =>
        {
            try { _instance?._menu?.Hide(); } catch { }
        });
    }

    /// <summary>
    /// 兜底：菜单开着一分钟还没收，就自己收掉。
    ///
    /// <para>
    /// 宿主窗口现在是铺满全屏且置顶的 —— 万一浮层因为任何原因没能正常关闭，
    /// 那张全屏窗口就会一直盖在 PowerPoint 上面，放映整个点不动。
    /// 宁肯菜单意外消失一次，也不能让屏幕被一张看不见的窗挡住。
    /// </para>
    /// </summary>
    private void ArmWatchdog()
    {
        if (_watchdog is not null) return;

        _watchdog = DispatcherQueue.CreateTimer();
        _watchdog.Interval = TimeSpan.FromSeconds(5);
        _watchdog.Tick += (_, _) =>
        {
            // 菜单已经正常收起来了，定时器可以撤了
            if (_menu is null) { DisarmWatchdog(); return; }

            if (unchecked(Environment.TickCount64 - PptMenu.OpenedTick) < 60_000) return;

            AppLog.Warning("pptmenu", "菜单开了 60 秒没收，强制收起");
            try { _menu?.Hide(); } catch { }
            PptMenu.IsOpen = false;
            _menu = null;
            try { _appWindow.Hide(); } catch { }
            DisarmWatchdog();
        };
        _watchdog.Start();
    }

    private void DisarmWatchdog()
    {
        if (_watchdog is null) return;
        _watchdog.Stop();
        _watchdog = null;
    }

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= Root_Loaded;
        if (!_pending) return;
        _pending = false;
        ShowFlyout();
    }

    private void ShowFlyout()
    {
        try
        {
            var menu = new MenuFlyout();
            Fill(menu.Items, PptMenu.Build(PptTouchConfig.Load().MenuExtras));

            menu.Opened += Menu_Opened;
            menu.Closed += Menu_Closed;
            _menu = menu;

            menu.ShowAt(Root, new FlyoutShowOptions
            {
                Position = new Windows.Foundation.Point(0, 0),
                ShowMode = FlyoutShowMode.Standard,
            });
        }
        catch (Exception ex)
        {
            AppLog.Exception("pptmenu", ex, "长按菜单弹出失败");
            PptMenu.IsOpen = false;
            try { _appWindow.Hide(); } catch { }
        }
    }

    /// <summary>
    /// 把菜单模型铺成 <see cref="MenuFlyoutItem"/> 树。
    /// ⚠️ 分隔线与子菜单都要各自成型，别指望一个控件全接住。
    /// </summary>
    private void Fill(IList<MenuFlyoutItemBase> target, IEnumerable<PptMenuItem> items)
    {
        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                target.Add(new MenuFlyoutSeparator());
                continue;
            }

            if (item.Children is { Count: > 0 } children)
            {
                var sub = new MenuFlyoutSubItem { Text = item.Text };
                ApplyIcon(sub, item);
                Fill(sub.Items, children);
                target.Add(sub);
                continue;
            }

            var id = item.Id;
            var entry = new MenuFlyoutItem { Text = item.Text };
            ApplyIcon(entry, item);
            entry.Click += (_, _) =>
            {
                // 先收菜单再执行：执行时要把前台还给 PowerPoint，菜单不能压在上面
                try { _menu?.Hide(); } catch { }
                PptMenu.Invoke(id);
            };

            target.Add(entry);
        }
    }

    /// <summary>给菜单项挂图标（字形码已经在字体里逐个核对过，见 <see cref="PptMenuItem.Glyph"/>）。</summary>
    private static void ApplyIcon(MenuFlyoutItemBase target, PptMenuItem item)
    {
        if (string.IsNullOrEmpty(item.Glyph)) return;

        var icon = new FontIcon { Glyph = item.Glyph };

        switch (target)
        {
            case MenuFlyoutItem i: i.Icon = icon; break;
            case MenuFlyoutSubItem s: s.Icon = icon; break;
        }
    }

    /// <summary>
    /// 菜单一打开，键盘焦点会落进第一项，系统就在那项上画一圈白色焦点框 ——
    /// 原生菜单不会这样。把焦点交给浮层本体、按指针方式获取：键盘照样能用，但不画那圈框。
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
        catch { }
    }

    private void Menu_Closed(object? sender, object e)
    {
        if (!ReferenceEquals(sender, _menu)) return;
        _menu = null;

        DisarmWatchdog();

        var clicked = !PptMenu.IsOpen;
        PptMenu.IsOpen = false;

        // 无论是点菜单项、点窗外还是按 Esc 收的，接下来那一下抬起都别再补动作
        if (!clicked) PptMenu.MarkDismissed();

        try { _appWindow.Hide(); } catch { }

        // 点空白处关掉的（没点菜单项）：把前台还给放映窗口，不然 PowerPoint 一直是被盖住的状态，
        // 下一次触摸就会被我们的钩子判成「前台不是放映窗口」而整个丢掉。
        if (!clicked) PptTouch.RestoreSlideshowForeground();
    }
}
