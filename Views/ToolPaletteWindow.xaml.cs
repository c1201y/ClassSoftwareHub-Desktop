using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 常用工具窗口：**标准窗口**（系统标题栏 + 原生边框），里面塞四个简版工具
/// （随机抽号 / 课堂计时 / 秒表计时 / 全屏时钟）。不需要开主界面，从托盘就能点出来。
///
/// ⚠️ 2026-09-24 改版：以前是"无边框浮窗"（自己画拖动、自己去 DWM 白边），
///    自绘那套在触屏上拖不动、边框还有毛病 → 干脆退回**标准窗口**：
///    拖动/触屏拖动/阴影/圆角全交给 Windows，我们只管"不进任务栏、不能最大化、不能最小化"。
///
/// 用法：<c>ToolPaletteWindow.ShowTool("timer")</c> / <c>HidePalette()</c> / <c>TogglePalette()</c>。
/// </summary>
public sealed partial class ToolPaletteWindow : Window
{
    private const int PaletteWidthDip = 400;      // 客户区逻辑像素（dip）
    private const int PaletteHeightDip = 360;

    /// <summary>位置记忆的版本号。改了默认位置算法就 +1：老版本存的坐标会被忽略，重新按新默认摆一次。</summary>
    private const int PositionVersion = 2;

    private static ToolPaletteWindow? _instance;

    private AppWindow? _appWindow;
    private string _tool = "pick-number";
    private bool _shownOnce;
    private bool _visible;
    private bool _syncingPivot;                         // 代码拨选中时别让它反过来再切一遍
    private bool _pivotLoaded;                          // SelectorBar 加载完（自动选第一项那一下已过去）才放行用户点击
    private int _fadeEpoch;                              // 淡入/淡出的代次：淡出播完别回头把刚显示的窗藏掉

    public bool IsVisible => _visible;

    private ToolPaletteWindow()
    {
        InitializeComponent();
        Configure();

        // SelectorBar 在 Loaded 时会自动选中第一项、把 _tool 冲回 pick-number，
        // 所以 ShowTool 指定的工具要等加载完再按 _tool 拨回（照 MiniPickNumber 的处理）。
        ToolPivot.Loaded += (_, _) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _pivotLoaded = true;
            SyncPivotToTool();
        });
    }

    /// <summary>标题栏那颗按钮：当前这个工具在主界面里的完整页面（全屏时钟没有页面，直接开全屏）。</summary>
    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        switch (_tool)
        {
            case "timer":
            case "stopwatch":
                HidePalette();
                App.MainWindow?.OpenToolSettings(typeof(Pages.Tools.TimerToolPage));
                break;

            case "clock":
                HidePalette();   // 全屏前面不能挡着浮窗（照 MiniClock 全屏的做法）
                try { ClockFullscreenWindow.Show(null, Root.ActualTheme == ElementTheme.Dark); } catch { }
                break;

            default:             // 随机抽号
                HidePalette();
                App.MainWindow?.OpenToolSettings(typeof(Pages.Tools.PickNumberToolPage));
                break;
        }
    }

    // ── 对外入口 ─────────────────────────────────────────────

    /// <summary>显示工具窗口；<paramref name="toolId"/> 非空就切到那个工具。</summary>
    public static void ShowTool(string? toolId = null)
    {
        var win = _instance ??= new ToolPaletteWindow();
        win.ShowPalette(toolId);
    }

    public static void HidePalette() => _instance?.Collapse();

    public static void TogglePalette()
    {
        if (_instance is { _visible: true }) _instance.Collapse();
        else ShowTool();
    }

    public static bool IsPaletteVisible => _instance is { _visible: true };

    /// <summary>窗口置顶开关变了之后同步一下。</summary>
    public static void ApplyOnTopSetting()
    {
        if (_instance?._appWindow?.Presenter is OverlappedPresenter p)
            p.IsAlwaysOnTop = App.Settings.Current.PaletteOnTop;
    }

    // ── 窗口本身 ─────────────────────────────────────────────

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "常用工具";
            _appWindow.IsShownInSwitchers = false;          // 不进任务栏、不进 Alt+Tab（入口是**托盘上它自己的图标**）

            if (_appWindow.Presenter is OverlappedPresenter p)
            {
                p.SetBorderAndTitleBar(true, true);         // 框架留着（拖动/阴影/圆角靠系统），标题栏内容我们自己画
                p.IsResizable = false;
                p.IsMaximizable = false;                    // 不能最大化 → 也没法全屏
                p.IsMinimizable = false;                    // 不能最小化
                p.IsAlwaysOnTop = App.Settings.Current.PaletteOnTop;
            }

            UseToolWindowExStyle(hwnd);                     // 不进任务栏（靠 WS_EX_TOOLWINDOW）
            ConfigureTitleBar();

            // 系统背景：浮窗**优先亚克力**（小浮窗用云母会把字糊在壁纸上，读不清），用户选"纯色"就纯色
            var want = App.Settings.Current.Backdrop;
            BackdropHost.Apply(this, Root,
                string.Equals(want, "mica", StringComparison.OrdinalIgnoreCase) ? "acrylic" : want);

            Root.ActualThemeChanged += (_, _) => { UpdateCaptionButtonColors(); };   // 换主题时系统按钮颜色跟着变
            ThemeHost.Apply(Root);                          // 跟「设置」里的深浅色走
            ResizeClientDip(PaletteWidthDip, PaletteHeightDip);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[toolwin] 初始化失败: " + ex.Message);
        }

        ApplyPosition();

        if (_appWindow is not null)
            _appWindow.Closing += (_, args) => { args.Cancel = true; HidePalette(); };   // 关掉 = 收起来，别真销毁
    }

    /// <summary>自绘标题栏：外观我们自己的（不是系统那根），拖动还是系统管（含触屏）。</summary>
    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        if (_appWindow is null) return;
        try
        {
            var bar = _appWindow.TitleBar;
            bar.ExtendsContentIntoTitleBar = true;
            bar.PreferredHeightOption = TitleBarHeightOption.Standard;   // 32px，跟 AppTitleBar 一样高
            bar.ButtonBackgroundColor = Colors.Transparent;              // 按钮底透明，跟面板底色融为一体
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[toolwin] 标题栏定制不可用: " + ex.Message);
        }

        UpdateCaptionButtonColors();

        // ⚠️ 上面那句 ExtendsContentIntoTitleBar 会把窗口圆角偏好按回 Default —— 实测在 Win11 上
        //    渲染出来是**直角**，跟"圆角交给系统"的设计意图正好相反。必须显式要一次 ROUND。
        Core.WindowChrome.SetRounded(WindowNative.GetWindowHandle(this), rounded: true);
    }

    /// <summary>右上角系统按钮的配色跟着深浅色走（照搬主窗口那套）。</summary>
    private void UpdateCaptionButtonColors()
    {
        if (_appWindow is null) return;
        var dark = Root.ActualTheme == ElementTheme.Dark;
        try
        {
            var bar = _appWindow.TitleBar;
            bar.ButtonForegroundColor = dark ? Colors.White : Windows.UI.Color.FromArgb(255, 30, 30, 30);
            bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
            bar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(26, 255, 255, 255) : Windows.UI.Color.FromArgb(20, 0, 0, 0);
            bar.ButtonPressedForegroundColor = dark ? Windows.UI.Color.FromArgb(255, 200, 200, 200) : Windows.UI.Color.FromArgb(255, 90, 90, 90);
            bar.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(160, 255, 255, 255) : Windows.UI.Color.FromArgb(140, 0, 0, 0);
            bar.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(40, 255, 255, 255) : Windows.UI.Color.FromArgb(30, 0, 0, 0);
        }
        catch { }
    }

    /// <summary>加上 WS_EX_TOOLWINDOW：窗口不出现在任务栏上（入口是托盘里的图标）。</summary>
    private static void UseToolWindowExStyle(IntPtr hwnd)
    {
        try
        {
            var ex = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(ex | WsExToolWindow));
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[toolwin] 工具窗口样式设置失败: " + ex.Message);
        }
    }

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private static void Log(string message)
    {
        try
        {
            Core.AppLog.Info("palette", message);
        }
        catch { }
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

    /// <summary>
    /// 按逻辑像素（dip）定**客户区**大小：AppWindow 这套 API 吃的是物理像素，
    /// 所以先乘一遍 DPI 缩放；客户区之外的标题栏/边框由系统自己加，不含在这两个数里。
    /// </summary>
    private void ResizeClientDip(int dipWidth, int dipHeight)
    {
        if (_appWindow is null) return;
        var scale = Scale();
        var wantW = (int)Math.Round(dipWidth * scale);
        var wantH = (int)Math.Round(dipHeight * scale);

        try
        {
            _appWindow.ResizeClient(new SizeInt32(wantW, wantH));
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[toolwin] ResizeClient 不可用，退回 Resize: " + ex.Message);
        }

        // 兜底：粗估标题栏 + 边框（约 40 dip 高、16 dip 宽），宁可大一点
        try { _appWindow.Resize(new SizeInt32(wantW + (int)Math.Round(16 * scale), wantH + (int)Math.Round(40 * scale))); }
        catch { }
    }

    /// <summary>
    /// 位置：用户动过就用他放的（并保证还在屏幕里）；没动过就摆**屏幕正中间**。
    /// ⚠️ 老版本存在右下角的旧坐标会在 PositionVersion 升级后被忽略一次，重新居中。
    /// </summary>
    private void ApplyPosition()
    {
        if (_appWindow is null) return;
        try
        {
            var work = DisplayArea.Primary.WorkArea;
            var size = _appWindow.Size;
            var s = App.Settings.Current;

            var hasSaved = s.PalettePosVersion >= PositionVersion && s.PaletteX > -10000 && s.PaletteY > -10000;

            int x, y;
            if (hasSaved)
            {
                x = Math.Clamp(s.PaletteX, work.X, Math.Max(work.X, work.X + work.Width - size.Width));
                y = Math.Clamp(s.PaletteY, work.Y, Math.Max(work.Y, work.Y + work.Height - size.Height));
            }
            else
            {
                x = work.X + (work.Width - size.Width) / 2;
                y = work.Y + (work.Height - size.Height) / 2;
            }

            // Move 同样是物理像素；迭代几次是为了兜住"换算/贴边被系统挪"的情况，正常一遍就到位
            var target = new PointInt32(x, y);
            for (var i = 0; i < 4; i++)
            {
                _appWindow.Move(target);
                var now = _appWindow.Position;
                var dx = x - now.X;
                var dy = y - now.Y;
                if (Math.Abs(dx) <= 2 && Math.Abs(dy) <= 2) break;
                target = new PointInt32(target.X + dx, target.Y + dy);
            }

            var actual = _appWindow.Position;
            Log($"几何：请求 {x},{y} 尺寸 {size.Width}x{size.Height} → 实际 {actual.X},{actual.Y}（scale={Scale():0.##}，用存档={hasSaved}）");
        }
        catch (Exception ex)
        {
            Log("定位失败: " + ex.Message);
        }
    }

    /// <summary>收起之前把当前位置记住（用户是用系统标题栏拖的，所以直接读实测位置）。</summary>
    private void SavePosition()
    {
        if (_appWindow is not { } app) return;
        try
        {
            var pos = app.Position;
            App.Settings.Current.PaletteX = pos.X;
            App.Settings.Current.PaletteY = pos.Y;
            App.Settings.Current.PalettePosVersion = PositionVersion;
            App.Settings.Save();
        }
        catch { }
    }

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private void ShowPalette(string? toolId)
    {
        if (!string.IsNullOrWhiteSpace(toolId)) _tool = toolId!;
        SelectPane(_tool, reload: true);

        // 淡入：先压透明再露脸，然后淡到不透明（系统 Flyout 那种"浮现"而不是"啪"一下）。
        // ⚠️ 压透明必须在 ShowWindow/Activate **之前**：否则先闪一帧满不透明，再被我压 0 重淡，等于闪了一下。
        _fadeEpoch++;                            // 作废还在跑的淡出（别让它回头把窗藏掉）
        FlyoutFade.Prepare(Root);

        try
        {
            if (_appWindow?.Presenter is OverlappedPresenter p)
                p.IsAlwaysOnTop = App.Settings.Current.PaletteOnTop;

            var hwnd = WindowNative.GetWindowHandle(this);
            if (!_shownOnce)
            {
                _shownOnce = true;
                Activate();
            }
            else
            {
                ShowWindow(hwnd, SW_SHOW);
                SetForegroundWindow(hwnd);
                Activate();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[toolwin] 显示失败: " + ex.Message);
        }

        _visible = true;
        try { Root.Focus(FocusState.Programmatic); } catch { }     // Esc 收起

        FlyoutFade.In(Root, 150);

        // 里面的表该走的走起来
        try { ClockView.Resume(); TimerView.Resume(); StopwatchView.Resume(); } catch { }
    }

    /// <summary>
    /// 收起来 = 藏到托盘（托盘里那个"常用工具"图标一直在，点一下就回来）。
    /// </summary>
    private void Collapse()
    {
        if (!_visible) return;
        _visible = false;
        SavePosition();

        // 收起 = 没人看：停掉里面的表，顺手把内存还给系统
        try { ClockView.Pause(); TimerView.Pause(); StopwatchView.Pause(); } catch { }

        // 先淡出，播完再藏 —— 别"啪"一下消失（系统 Flyout 收起也是淡出）
        var epoch = ++_fadeEpoch;
        FlyoutFade.Out(Root, 150, () =>
        {
            if (epoch != _fadeEpoch) return;             // 淡出期间又点开了：别把刚显示的窗藏掉
            try { ShowWindow(WindowNative.GetWindowHandle(this), SW_HIDE); }
            catch (Exception ex) { Debug.WriteLine("[toolwin] 收起失败: " + ex.Message); }

            MemoryTrimmer.Trim();
        });
    }

    // ── 四个工具的切换 ───────────────────────────────────────

    /// <summary>
    /// 切到某个工具：把 Pivot 选中项对过去（Pivot 自己会滚到可见）再放内容。
    /// ⚠️ 切换时要把 _syncing 置起来：Pivot 的选中变化会反过来触发 SelectionChanged，
    ///    不挡住就会跟着再跑一遍 SelectPane（存档会被多写一次）。
    /// </summary>
    private void SelectPane(string tool, bool reload)
    {
        PanePick.Visibility = tool == "pick-number" ? Visibility.Visible : Visibility.Collapsed;
        PaneTimer.Visibility = tool == "timer" ? Visibility.Visible : Visibility.Collapsed;
        PaneStopwatch.Visibility = tool == "stopwatch" ? Visibility.Visible : Visibility.Collapsed;
        PaneClock.Visibility = tool == "clock" ? Visibility.Visible : Visibility.Collapsed;

        // 只记内存不落盘：切一次页写一遍设置文件是切换卡顿的一半原因；存档统一在收起时落
        App.Settings.Current.PaletteTool = tool;

        if (reload && tool == "pick-number") PickView.Reload();

        SyncPivotToTool();
    }

    /// <summary>按 _tool 把 SelectorBar 的选中项拨过去（代码切页时用；用户点击时不用调）。</summary>
    private void SyncPivotToTool()
    {
        var item = _tool switch
        {
            "pick-number" => PivotPick,
            "timer" => PivotTimer,
            "stopwatch" => PivotStopwatch,
            _ => PivotClock,
        };

        // ⚠️ 必须转成 SelectorBarItem 再比：SelectedItem 是 object，直接 `!=` 会走引用比较（CS0252）
        var current = ToolPivot.SelectedItem as SelectorBarItem;
        if (current != item)
        {
            _syncingPivot = true;
            try { ToolPivot.SelectedItem = item; }
            finally { _syncingPivot = false; }
        }
    }

    private void ToolPivot_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncingPivot || !_pivotLoaded) return;   // 加载前（含自动选第一项那一下）别把 _tool 冲掉
        if (ToolPivot.SelectedItem is not SelectorBarItem item) return;
        if (item.Tag is not string tag || tag == _tool) return;

        _tool = tag;
        SelectPane(tag, reload: true);
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape) HidePalette();
    }
}
