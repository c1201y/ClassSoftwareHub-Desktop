using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 屏幕边缘侧边栏：贴在屏幕的某一条边上（上下左右都能放）。
/// 全屏放 PPT / 视频时够不到任务栏，从边上点一下就能用工具。
///   · 收起 = 一小截圆角抓手（亚克力底）：点一下展开；**按住可以拖**着挪位置、或拖到别的边
///   · 展开 = 四个工具 + 「收起 / 位置复原 / 隐藏」（展开状态下不能拖，免得跟点按钮打架）
/// 位置（贴哪条边 + 沿边位置）会记在设置里；「位置复原」= 回到右边的居中位置。
/// 置顶、不进任务栏、无标题栏、不能缩放/最大化/最小化。
///
/// 用法：<c>ToolSidebarWindow.ShowSidebar()</c> / <c>HideSidebar()</c> / <c>ApplySetting()</c>。
/// </summary>
public sealed partial class ToolSidebarWindow : Window
{
    private const int CollapsedThicknessDip = 20;   // 收起时那条的厚度（竖条=宽，横条=高）
    private const int CollapsedLengthDip = 110;     // 收起时的长度（竖条=高）
    private const int PanelThicknessDip = 92;       // 展开后的厚度（竖着放时=宽）
    private const int PanelLengthDip = 450;         // 展开后的长度（竖着放时=高）
    private const int PanelThicknessFlatDip = 112;  // 上/下边时：两行（工具一行、按钮一行）的高度
    private const int PanelLengthFlatDip = 470;     // 上/下边时：一排四个按钮（每个 104 dip + 间距）要放得下

    private static ToolSidebarWindow? _instance;

    private AppWindow? _appWindow;
    private bool _expanded;
    private int _collapseEpoch;                            // 收起淡出的批次号：展开/再次收起都能把它作废
    private bool _shownOnce;
    private bool _visible;

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _idle;

    /// <summary>展开滑动用的按帧计时器（滑完置空）。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _slideTimer;

    /// <summary>窗口滑动动画正在跑（这期间别开始拖拽，不然 _dragOrigin 会算出乱位置）。</summary>
    private bool _sliding;

    /// <summary>
    /// 滑动动画的"代次"。每次状态变化（收起/展开/拖动/隐藏）都 +1，让**还在跑的那一波动画立刻作废**。
    /// 没有它就会出现"展开到一半快速点收起 → 动画的后续帧又把窗口挪回去"的竞态（按钮跑到左上角就是这么来的）。
    /// </summary>
    private int _slideEpoch;

    /// <summary>展开面板里的工具按钮（按「侧边布局」的模块清单动态生成，见 BuildToolButtons）。</summary>
    private readonly List<Button> _toolButtons = new();

    /// <summary>当前那个"要先问一句"的原生 Flyout（一次只有一个）。</summary>
    private Flyout? _confirmFlyout;

    /// <summary>「收起」键的那个图标（内容会按贴边重建，所以要留住当前这个实例才好换箭头方向）。</summary>
    private FontIcon? _foldIcon;

    /// <summary>「常驻」键的图标（同上，钉住/松开要换样子）。</summary>
    private FontIcon? _pinIcon;

    /// <summary>
    /// 暂时压住"自动收起"（按住型动作、以及"还有话要问用户"的确认面板期间）。
    ///
    /// ⚠️ 这必须在**自己会过期**：早先写成普通 bool，一旦设了 true 而对面又没回来清（比如面板没弹出来、
    /// 用户压根没理），侧边栏就**永远不再自动收起**了 —— 真出过这个 bug。
    /// 现在本质是"压到某个时间点为止"，最长 `SuppressMaxSeconds`，到点自己恢复。
    /// </summary>
    public static bool SuppressAutoCollapse
    {
        get => DateTime.Now < _suppressUntil;
        set => _suppressUntil = value ? DateTime.Now.AddSeconds(SuppressMaxSeconds) : DateTime.MinValue;
    }

    private static DateTime _suppressUntil = DateTime.MinValue;

    /// <summary>压住自动收起的最长时间（秒）。确认面板活 10 秒，这里给够余量。</summary>
    private const int SuppressMaxSeconds = 15;

    /// <summary>侧边栏当前在屏幕上的矩形（给"挨着它弹提示"用）；还没建/拿不到就返回 null。</summary>
    public static Windows.Graphics.RectInt32? CurrentRect
    {
        get
        {
            try
            {
                if (_instance?._appWindow is null) return null;
                var pos = _instance._appWindow.Position;
                var size = _instance._appWindow.Size;
                return new Windows.Graphics.RectInt32(pos.X, pos.Y, size.Width, size.Height);
            }
            catch
            {
                return null;
            }
        }
    }

    // 收起状态下的拖拽（屏幕坐标算，别用窗口内坐标，会自己滚起来）
    private bool _pressed;
    private bool _dragging;
    private POINT _dragStart;
    private PointInt32 _dragOrigin;
    private double _pressInWindowDipX;        // 触摸/笔：按下时手指在窗口里的位置（DIP）
    private double _pressInWindowDipY;
    private bool _useSnapToFinger;            // true = 触摸/笔，走"窗口跟着手指走"算法
    private int _pressOffsetPx;               // 按下时手指在窗口里的物理偏移（绝对坐标拖动用）
    private int _pressOffsetPy;
    private bool _loggedSource;               // 这次拖动记过"坐标源"了吗（每拖只记一次）
    private bool _haveLastSample;             // 相对算法：本次拖动记过上一次采样吗
    private double _lastCurDipX;              // 上一次采样：手指相对窗口（DIP）
    private double _lastCurDipY;
    private double _winTargetX;               // 相对算法：累加出来的"窗口该在哪儿"（浮点，别再拿读数纠偏）
    private double _winTargetY;
    private int _issuedX;                     // 我们自己最后发出去的窗口位置（只信自己，不读 API）
    private int _issuedY;
    private int _lastIssuedDeltaX;            // 上一次采样之后我们自己挪了多少（算 ΔF 要用）
    private int _lastIssuedDeltaY;
    private int _dragSamples;                 // 诊断用：这次拖动记了几条样本

    private ToolSidebarWindow()
    {
        InitializeComponent();

        // 展开后没人动 → 自己收回去（触屏没地方"点空白处收起"）
        _idle = DispatcherQueue.CreateTimer();
        _idle.Interval = TimeSpan.FromSeconds(10);
        _idle.IsRepeating = false;
        _idle.Tick += (_, _) => { if (_expanded && !App.Settings.Current.SidebarPinned && !SuppressAutoCollapse) Collapse(); };

        Configure();
    }

    // ── 对外入口 ─────────────────────────────────────────────

    /// <summary>显示侧边栏（托盘菜单等入口调它；顺手把设置里的开关打开）。</summary>
    public static void ShowSidebar()
    {
        if (!App.Settings.Current.SidebarEnabled)
        {
            App.Settings.Current.SidebarEnabled = true;
            App.Settings.Save();
        }
        (_instance ??= new ToolSidebarWindow()).Present();
    }

    public static void HideSidebar() => _instance?.HideSelf();

    /// <summary>
    /// 「截屏」专用的收起：把边条收成那条细把手（**不是隐藏**），免得它被照进截图里。
    /// 边条展开着的时候才需要收；已经收着就啥也不做。
    /// </summary>
    public static void CollapseForCapture()
    {
        try
        {
            var inst = _instance;
            if (inst is null) return;
            if (!inst._expanded) return;
            inst.Collapse(animate: false);                // 截图前必须当帧收干净，不然把淡出中的边条也照进去
        }
        catch { }
    }

    public static bool IsSidebarVisible => _instance?._visible == true;

    /// <summary>设置里的开关/边选项变了：开就显示、关就藏起来；边变了重新贴过去。</summary>
    public static void ApplySetting()
    {
        if (!App.Settings.Current.SidebarEnabled) { HideSidebar(); return; }
        ShowSidebar();
        _instance?.SnapToSetting();
    }

    private void SnapToSetting()
    {
        ApplyEdgeLayout();
        ApplySize();
        MoveToEdge();
    }

    // ── 窗口本身 ─────────────────────────────────────────────

    /// <summary>贴哪条边：left | right | top | bottom（默认 right）。</summary>
    private static string Edge
    {
        get
        {
            var e = App.Settings.Current.SidebarEdge;
            return e is "left" or "top" or "bottom" ? e : "right";
        }
    }

    private static bool IsFlat => Edge is "top" or "bottom";

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "工具侧边栏";
            _appWindow.IsShownInSwitchers = false;          // 不进任务栏、不进 Alt+Tab

            if (_appWindow.Presenter is OverlappedPresenter p)
            {
                p.SetBorderAndTitleBar(false, false);       // 就一条贴着边的条，不要标题栏/边框
                p.IsResizable = false;
                p.IsMaximizable = false;
                p.IsMinimizable = false;
                p.IsAlwaysOnTop = true;                    // 全屏播放时也要显示在上面
            }

            var ex = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(ex | WsExToolWindow));

            // 亚克力底（模糊背后的画面）；机器不支持的话会自动退回纯色，不会崩
            try { SystemBackdrop = new DesktopAcrylicBackdrop(); }
            catch (Exception ex2) { Log("亚克力不可用: " + ex2.Message); }

            ApplyPanelBrush();
            ThemeHost.Apply(Root);                         // 跟「设置」里的深浅色走（不只是跟系统走）
            BuildToolButtons();                            // 按「侧边布局」的模块清单生成工具按钮
            Root.PointerMoved += (_, _) => Touch();
            Root.PointerPressed += (_, _) => Touch();

            Activated += (_, args) =>
            {
                // 常驻时：失焦也不收（鼠标点去别处、切到别的窗口都保持展开）
                if (args.WindowActivationState == WindowActivationState.Deactivated
                    && _expanded && !_pressed && !App.Settings.Current.SidebarPinned && !SuppressAutoCollapse)
                    Collapse();
            };
            Root.ActualThemeChanged += (_, _) =>
            {
                ApplyPanelBrush();
                // 窗口那圈边的颜色也是跟着深浅色走的，换主题得重画一次
                try
                {
                    WindowChrome.RemoveBorder(WindowNative.GetWindowHandle(this), rounded: true,
                                              dark: Root.ActualTheme == ElementTheme.Dark);
                }
                catch { }
            };

            ApplyEdgeLayout();
        }
        catch (Exception ex)
        {
            Log("初始化失败: " + ex.Message);
        }
    }

    private void Present()
    {
        try
        {
            ApplyEdgeLayout();
            ApplySize();

            var hwnd = WindowNative.GetWindowHandle(this);

            if (!_shownOnce)
            {
                _shownOnce = true;
                Activate();
            }
            else
            {
                // 之前被「隐藏」或收起过 → 得显式再显示出来，否则窗口一直藏着
                ShowWindow(hwnd, SW_SHOW);
                SetForegroundWindow(hwnd);
            }

            _visible = true;
            Collapse(animate: false);                      // 每次出现都从收起状态开始（不挡画面）

            if (_appWindow?.Presenter is OverlappedPresenter p) p.IsAlwaysOnTop = true;

            MoveToEdge();
            // ⚠️ 外观要等窗口真显示出来之后再收尾（构造期改窗口样式会让窗口显示不出来）
            WindowChrome.RemoveBorder(hwnd, rounded: true, dark: Root.ActualTheme == ElementTheme.Dark);
        }
        catch (Exception ex)
        {
            Log("显示失败: " + ex.Message);
        }
    }

    private void HideSelf()
    {
        if (!_visible) return;
        StopSlide();
        _visible = false;
        try { ShowWindow(WindowNative.GetWindowHandle(this), SW_HIDE); } catch { }
    }

    /// <summary>亚克力上面再压一层很淡的色：深色主题压深、浅色主题压白，保证字看得清。</summary>
    private void ApplyPanelBrush()
    {
        var dark = Root.ActualTheme == ElementTheme.Dark;
        Panel.Background = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(95, 20, 20, 20)
            : Windows.UI.Color.FromArgb(110, 255, 255, 255));
    }

    // ── 展开 / 收起 / 隐藏 ───────────────────────────────────

    private void Expand()
    {
        StopSlide();                                      // 掐掉上一次没跑完的（防连点/竞态）
        _collapseEpoch++;                                 // 取消可能还在跑的"收起淡出"
        _expanded = true;
        ResetPanelOpacity();
        CollapsedView.Visibility = Visibility.Collapsed;
        ExpandedView.Visibility = Visibility.Visible;
        ApplySize();
        MoveToEdge();
        SlideInFromEdge();                                // 整个窗口从贴的那条边滑进来（见下面说明）
        Touch();
    }

    private void Collapse(bool animate = true)
    {
        StopSlide();                                      // ⚠️ 必须停：不然展开动画的后续帧还会把窗口挪回去
        _idle.Stop();

        // 展开 → 收起给个过渡：整个窗口往贴的那条边**滑出去**（跟滑进来同一条路子，方向相反），滑完再真收。
        // animate=false 用在"要立刻消失"的场合（截图前、刚出现时），那种必须当帧就收干净。
        if (!_expanded || ExpandedView.Visibility != Visibility.Visible || !animate)
        {
            FinishCollapse();
            return;
        }

        _expanded = false;                                // 先立旗：淡出期间自动收起那条路别再来一遍
        var epoch = ++_collapseEpoch;
        if (SlideOutToEdge(() => { if (epoch == _collapseEpoch) FinishCollapse(); })) return;
        FinishCollapse();
    }


    /// <summary>真收：换回抓手 + 挪窗口。中途用户又展开了就别收（_expanded 已被置回 true）。</summary>
    private void FinishCollapse()
    {
        if (_expanded) return;
        ExpandedView.Visibility = Visibility.Collapsed;
        CollapsedView.Visibility = Visibility.Visible;
        ResetPanelOpacity();
        ApplySize();
        MoveToEdge();
        ReassertCollapsed();
    }

    private void ResetPanelOpacity()
    {
        try
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(Panel);
            visual.StopAnimation("Opacity");
            visual.Opacity = 1f;
        }
        catch { }
    }

    /// <summary>
    /// 下一帧再确认一次"收起态"的尺寸和位置。
    /// 有些时候尺寸/位置要等系统下一帧才对得上（尤其是刚快速开关过），再兜一次就不会跑偏。
    /// 只在"仍然是收起态"时才兜 —— 用户这会儿要展开的话，不能跟展开打架。
    /// </summary>
    private void ReassertCollapsed()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_expanded || _appWindow is null) return;
                ApplySize();
                MoveToEdge();
            }
            catch (Exception ex)
            {
                Log("兜底落位失败: " + ex.Message);
            }
        });
    }

    private void Touch()
    {
        if (!_expanded || _pressed) return;
        if (App.Settings.Current.SidebarPinned) return;    // 常驻：不启动自动收起计时
        _idle.Stop();
        _idle.Start();
    }

    private void Collapse_Click(object sender, RoutedEventArgs e) => Collapse();

    /// <summary>常驻开关：钉住 = 展开后不自动收起（手动「收起」还是能收）。</summary>
    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        var pinned = !App.Settings.Current.SidebarPinned;
        App.Settings.Current.SidebarPinned = pinned;
        App.Settings.Save();

        _idle.Stop();                       // 先把已经排队的自动收起取消掉
        UpdatePinVisual();
        Touch();                            // 松开时重新起计时；钉住时 Touch 自己会跳过
        Log(pinned ? "常驻：开" : "常驻：关");
    }

    /// <summary>「常驻」键的样子：钉住 = 实心钉 + 主题色；松开 = 空心钉。</summary>
    private void UpdatePinVisual()
    {
        try
        {
            var pinned = App.Settings.Current.SidebarPinned;
            if (_pinIcon is not null)
            {
                _pinIcon.Glyph = pinned ? "\uE840" : "\uE718";      // Pinned / Pin
                _pinIcon.Foreground = pinned
                    ? new SolidColorBrush(AccentColor())
                    : (Root.ActualTheme == ElementTheme.Dark
                        ? new SolidColorBrush(Colors.White)
                        : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 30, 30, 30)));
            }

            if (PinButton is not null)
                ToolTipService.SetToolTip(PinButton, pinned
                    ? "常驻中：展开后不会自动收起（再点一下松开）"
                    : "常驻：展开后不自动收起（手动点「收起」还是能收）");
        }
        catch (Exception ex)
        {
            Log("常驻外观更新失败: " + ex.Message);
        }
    }

    private static Windows.UI.Color AccentColor() =>
        new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);

    /// <summary>位置复原：回右边、沿边居中。</summary>
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.SidebarEdge = "right";
        App.Settings.Current.SidebarPosRatio = -1;
        App.Settings.Save();
        SnapToSetting();
        Touch();
    }

    /// <summary>隐藏：直接关掉（设置里也不显示了），想找回来去「内置工具」页或托盘图标菜单。</summary>
    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.SidebarEnabled = false;
        App.Settings.Save();
        HideSelf();
    }

    /// <summary>设置里的模块清单变了（侧边布局页改完调它）：重建按钮 + 重新量尺寸贴边。</summary>
    public static void ApplyModules()
    {
        if (_instance is null) return;
        if (!App.Settings.Current.SidebarEnabled) return;
        _instance.RebuildModules();
    }

    private void RebuildModules()
    {
        try
        {
            BuildToolButtons();
            ApplyEdgeLayout();
            ApplySize();
            MoveToEdge();
        }
        catch (Exception ex)
        {
            Log("重建模块失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 按 AppSettings.SidebarModuleIds（= 「侧边布局」页里勾选+排序的结果）生成工具按钮。
    /// 清单里认不出来的 id（老设置里存了后来删掉的模块）直接跳过，不会崩。
    /// </summary>
    private void BuildToolButtons()
    {
        ToolStack.Children.Clear();
        _toolButtons.Clear();

        foreach (var id in App.Settings.Current.SidebarModuleIds ?? Array.Empty<string>())
        {
            var m = SidebarModules.Find(id);
            if (m is null) continue;

            var content = new StackPanel { Spacing = 3 };
            content.Children.Add(new FontIcon
            {
                Glyph = m.Glyph,
                FontSize = 20,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            content.Children.Add(new TextBlock
            {
                Text = m.ShortName,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            var btn = new Button
            {
                Tag = m.Id,
                MinWidth = 0,
                MinHeight = 52,
                Padding = new Thickness(0, 8, 0, 8),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = content
            };
            ToolTipService.SetToolTip(btn, m.Note ?? m.Name);

            if (m.Kind == SidebarModuleKinds.Action && TeachingActions.IsHold(m.Id))
            {
                // 按住型（放大镜）：按下开始 → 松手结束。按住期间别让侧边栏自动收起，
                // 否则按钮被一起收走、指针捕获也会断，放大镜就"按两下才亮"了。
                var holdId = m.Id;
                btn.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
                {
                    SuppressAutoCollapse = true;
                    TeachingActions.Begin(holdId);
                }), true);
                btn.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => EndHold(holdId)), true);
                btn.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndHold(holdId)), true);
            }
            else
            {
                btn.Click += Tool_Click;
            }

            ToolStack.Children.Add(btn);
            _toolButtons.Add(btn);
        }

        // 一个模块都没选：面板里给一句说明，别让用户以为坏了
        if (_toolButtons.Count == 0)
        {
            ToolStack.Children.Add(new TextBlock
            {
                Text = "还没选模块\n去「侧边布局」挑几个",
                FontSize = 11,
                Opacity = 0.7,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(2, 6, 2, 6)
            });
        }
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string id) return;
        try
        {
            var m = SidebarModules.Find(id);
            if (m is null) return;

            // 讲台动作：按一下就干活（放大镜是"按住"型，走的是按下/松手那条路，不走这里）
            if (m.Kind == SidebarModuleKinds.Action)
            {
                var hint = TeachingActions.Run(m.Id);
                if (hint is { Length: > 0 })
                {
                    // 这个动作要先问一句（比如「关全部」）：
                    // ① 边条**不许收**（失焦/空闲两条自动收起路都要压住，否则他还得重新点开）；
                    // ② 用**原生 Flyout** 弹确认（框架自带的描边/圆角/投影 + 点别处自动收）。
                    SuppressAutoCollapse = true;
                    var lines = hint.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    var title = lines.Length > 0 ? lines[0].Trim() : hint;
                    var body = lines.Length > 1 ? lines[1].Trim() : null;

                    ShowConfirmFlyout(b, title, body, "确定关闭", () =>
                    {
                        SuppressAutoCollapse = false;
                        TeachingActions.ConfirmPending(m.Id);
                        Touch();
                    });

                    TeachingActions.RefocusPrevious();     // Flyout 显示时可能把焦点拽走，还回去
                }
                else
                {
                    HideConfirmFlyout();
                    Collapse();
                }
                return;
            }

            Collapse();                                        // 先把边条收起来，别挡着工具窗口
            if (m.Kind == SidebarModuleKinds.Page && m.Page is not null)
                App.MainWindow?.OpenToolSettings(m.Page);      // 拉出主窗口并跳到那一页
            else
                ToolPaletteWindow.ShowTool(m.Id);              // 小浮窗
        }
        catch (Exception ex)
        {
            Log("打开工具失败: " + ex.Message);
        }
    }


    private void EndHold(string id)
    {
        try { TeachingActions.End(id); }
        finally { SuppressAutoCollapse = false; }
    }

    // ── "要先问一句"的原生 Flyout ────────────────────────────
    //
    // 为什么是原生 Flyout（而不是自己开个小窗口）：自己开窗要自己管窗口边框/背景/置顶/圆角，
    // 结果被 Nick 一眼看穿"很丑、有奇妙的白色边框、还不置顶"。用框架的 Flyout：
    // 描边/圆角/投影/主题全归系统，点别处自动收（light dismiss），也不用管 z 序。
    //
    // ⚠️ 唯一关键点：`ShouldConstrainToRootBounds = false`。
    //    默认是 true → Flyout 会被限制在**自己窗口**的边界内，而边条只有 92dip 宽，
    //    弹出来会被剪成一条没法用。框架自己在 ComboBox/Flyout 里也是设 false 的。

    /// <summary>在边条旁边弹一个原生确认 Flyout（红底确定键）。</summary>
    private void ShowConfirmFlyout(Button? anchor, string title, string? body, string okText, Action onConfirm)
    {
        try
        {
            HideConfirmFlyout();

            var panel = new Grid { Width = 300, RowSpacing = 12 };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var text = new StackPanel { Spacing = 4 };
            text.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            if (!string.IsNullOrWhiteSpace(body))
            {
                text.Children.Add(new TextBlock
                {
                    Text = body,
                    FontSize = 12,
                    Opacity = 0.78,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            Grid.SetRow(text, 0);
            panel.Children.Add(text);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            var cancel = new Button { Content = "取消", FontSize = 13, MinWidth = 76, Padding = new Thickness(0, 6, 0, 6) };
            cancel.Click += (_, _) => HideConfirmFlyout();
            buttons.Children.Add(cancel);

            var danger = new Button { Content = okText, FontSize = 13, MinWidth = 88, Padding = new Thickness(0, 6, 0, 6) };
            // 红底危险键：这条资源链上按钮的刷子全改红，否则一 hover/按下就变回主题灰
            var red = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xC4, 0x2B, 0x1C));
            var redHover = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xD1, 0x43, 0x35));
            var redPressed = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xA8, 0x22, 0x16));
            var white = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
            var clear = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            danger.Resources["ButtonBackground"] = red;
            danger.Resources["ButtonBackgroundPointerOver"] = redHover;
            danger.Resources["ButtonBackgroundPressed"] = redPressed;
            danger.Resources["ButtonBackgroundDisabled"] = redPressed;
            danger.Resources["ButtonForeground"] = white;
            danger.Resources["ButtonForegroundPointerOver"] = white;
            danger.Resources["ButtonForegroundPressed"] = white;
            danger.Resources["ButtonBorderBrush"] = clear;
            danger.Resources["ButtonBorderBrushPointerOver"] = clear;
            danger.Resources["ButtonBorderBrushPressed"] = clear;
            buttons.Children.Add(danger);

            Grid.SetRow(buttons, 1);
            panel.Children.Add(buttons);

            var flyout = new Flyout
            {
                ShouldConstrainToRootBounds = false,     // ⚠️ 必须 false，否则被剪在边条窗口里
                Placement = ConfirmPlacement(),
                Content = panel,
            };
            flyout.Closed += (_, _) =>
            {
                if (ReferenceEquals(_confirmFlyout, flyout)) _confirmFlyout = null;
                SuppressAutoCollapse = false;            // 面板没了 → 恢复自动收起
            };
            danger.Click += (_, _) =>
            {
                flyout.Hide();
                try { onConfirm(); } catch (Exception ex) { Log("确认动作失败: " + ex.Message); }
            };

            _confirmFlyout = flyout;

            var target = (FrameworkElement?)anchor ?? Root;
            flyout.ShowAt(target, new FlyoutShowOptions { Placement = flyout.Placement });
            Log($"确认面板：原生 Flyout 已弹出（标题={title} 锚点={(target as FrameworkElement)?.Name} 方位={flyout.Placement}）");

            // 兜底：20 秒还没人理就自己收（原生 Flyout 不退的话会一直挂着挡点击）
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _ = System.Threading.Tasks.Task.Delay(20000).ContinueWith(_ =>
            {
                try
                {
                    queue?.TryEnqueue(() =>
                    {
                        if (ReferenceEquals(_confirmFlyout, flyout)) HideConfirmFlyout();
                    });
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            Log("确认面板弹出失败: " + ex.Message);
        }
    }

    private void HideConfirmFlyout()
    {
        try
        {
            var f = _confirmFlyout;
            _confirmFlyout = null;
            f?.Hide();
        }
        catch { }
    }

    /// <summary>边条贴哪条边 → Flyout 往哪边弹（贴左往右弹，依此类推）。</summary>
    private static FlyoutPlacementMode ConfirmPlacement()
    {
        try
        {
            if (CurrentRect is { } r)
            {
                var work = DisplayArea.Primary.WorkArea;
                if (r.X <= work.X + 8) return FlyoutPlacementMode.Right;
                if (r.X + r.Width >= work.X + work.Width - 8) return FlyoutPlacementMode.Left;
                if (r.Y <= work.Y + 8) return FlyoutPlacementMode.Bottom;
                if (r.Y + r.Height >= work.Y + work.Height - 8) return FlyoutPlacementMode.Top;
            }
        }
        catch { }
        return FlyoutPlacementMode.Right;
    }

    // ── 收起状态：点一下展开 / 按住拖动 ──────────────────────

    private void Strip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_appWindow is null) return;
        StopSlide();                                     // 滑到一半被按住拖？先停下，别跟拖动抢窗口
        _dragOrigin = _appWindow.Position;
        _dragging = false;

        // ⚠️ 触摸/笔**不能**问系统要"屏幕坐标"：
        //    · GetPointerInfo 常常根本不是这个指针（拿鼠标的 info 回来的），
        //    · GetCursorPos 给的必然是鼠标位置（手指拖动时鼠标压根不动）。
        //    于是"按下点"会被记成鼠标待的地方（离边条两百多像素）→ 一拖就跳、看着像"拖不动"。
        //    触摸只能用**手指在窗口里的相对位置**，拖动时让窗口"跟着手指走"（算法见 Moved）。
        _useSnapToFinger = !IsMousePointer(e);
        if (_useSnapToFinger)
        {
            var press = e.GetCurrentPoint(Root).Position;
            _pressInWindowDipX = press.X;
            _pressInWindowDipY = press.Y;

            // 手指在窗口里的**物理像素**偏移（整数，后面按绝对坐标拖动要用）
            var pressScale = DpiScaleOf();
            _pressOffsetPx = (int)Math.Round(press.X * pressScale);
            _pressOffsetPy = (int)Math.Round(press.Y * pressScale);
        }
        else
        {
            PointerScreenPoint(e, out _dragStart);       // 鼠标：光标位置就是屏幕坐标，最准
        }

        _pressed = true;
        _loggedSource = false;
        _haveLastSample = false;
        _winTargetX = _dragOrigin.X;
        _winTargetY = _dragOrigin.Y;
        _issuedX = _dragOrigin.X;
        _issuedY = _dragOrigin.Y;
        _lastIssuedDeltaX = 0;
        _lastIssuedDeltaY = 0;
        _dragSamples = 0;
        _idle.Stop();
        Log($"按下: device={e.Pointer.PointerDeviceType} pointerId={e.Pointer.PointerId} 算法={(_useSnapToFinger ? "跟随手指" : "绝对位移")} 屏幕点=({_dragStart.X},{_dragStart.Y}) 窗口=({_dragOrigin.X},{_dragOrigin.Y})");
        if (sender is UIElement u) u.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Strip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed || _appWindow is null) return;

        int targetX;
        int targetY;

        if (_useSnapToFinger)
        {
            // 触摸/笔：**优先**问系统要"这个指针自己的屏幕坐标"——
            // 那是绝对坐标，跟窗口现在在哪儿无关，所以窗口追得准、也不会自己跟自己震荡。
            if (TryGetPointerScreenPoint(e, out var abs))
            {
                LogSource("系统指针坐标(绝对)");
                targetX = abs.X - _pressOffsetPx;
                targetY = abs.Y - _pressOffsetPy;
            }
            else
            {
                // 拿不到绝对坐标（这台机器上 GetPointerInfo 不认 WinUI 的 PointerId）→ 用**增量累加**。
                // 关键：手指相对窗口的读数里混着"窗口自己挪的那部分"，把它减掉，只累加手指的真实屏幕位移，
                //      然后**只按累加值走**，不再拿这个读数逐帧纠偏 —— 逐帧纠偏就是抖的根源（一帧滞后→来回过冲）。
                LogSource("窗口相对·增量累加");
                var scale = DpiScaleOf();
                var cur = e.GetCurrentPoint(Root).Position;

                if (!_haveLastSample)
                {
                    _lastCurDipX = cur.X;
                    _lastCurDipY = cur.Y;
                    _lastIssuedDeltaX = 0;
                    _lastIssuedDeltaY = 0;
                    _haveLastSample = true;
                    e.Handled = true;
                    return;                                  // 第一次只记基准
                }

                // 「窗口自己挪了多少」= **上一次我们自己发出去的移动量**。
                // ⚠️ 别去读 AppWindow.Position / 也别信"读回来就是新的"：读回来的可能是旧的，
                //    拿旧值当基准就会把「窗口没挪」当成「手指没动」，来回过冲 → 速度越快抖得越狠。
                var dxFinger = (cur.X - _lastCurDipX) * scale + _lastIssuedDeltaX;
                var dyFinger = (cur.Y - _lastCurDipY) * scale + _lastIssuedDeltaY;

                _lastCurDipX = cur.X;
                _lastCurDipY = cur.Y;

                // 死区：亚像素噪声不累加（否则会慢慢飘）
                if (Math.Abs(dxFinger) < 1.0 && Math.Abs(dyFinger) < 1.0) { e.Handled = true; return; }

                _winTargetX += dxFinger;
                _winTargetY += dyFinger;

                targetX = (int)Math.Round(_winTargetX);
                targetY = (int)Math.Round(_winTargetY);
                LogDrag(cur.X, cur.Y, dxFinger, dyFinger, targetX, targetY);
            }
        }
        else
        {
            if (!PointerScreenPoint(e, out var pt)) return;
            targetX = _dragOrigin.X + (pt.X - _dragStart.X);
            targetY = _dragOrigin.Y + (pt.Y - _dragStart.Y);
        }

        if (!_dragging && Math.Abs(targetX - _dragOrigin.X) + Math.Abs(targetY - _dragOrigin.Y) < 8)
        {
            _lastIssuedDeltaX = 0;                       // 没挪窗
            _lastIssuedDeltaY = 0;
            return;                                      // 手还没动够，先当点击
        }

        _dragging = true;

        _lastIssuedDeltaX = targetX - _issuedX;          // 这一下我们自己要挪多少（下一步算 ΔF 要用）
        _lastIssuedDeltaY = targetY - _issuedY;
        if (_lastIssuedDeltaX == 0 && _lastIssuedDeltaY == 0) { e.Handled = true; return; }
        _issuedX = targetX;
        _issuedY = targetY;

        _appWindow.Move(new PointInt32(targetX, targetY));
        e.Handled = true;
    }

    /// <summary>鼠标光标 → 屏幕像素坐标。拿不到就 false（触摸/笔不走这条路）。</summary>
    private static bool PointerScreenPoint(PointerRoutedEventArgs e, out POINT pt)
    {
        pt = default;
        try
        {
            if (GetCursorPos(out var cursor))
            {
                pt = cursor;
                return true;
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// 触摸/笔：要"这个指针自己的屏幕像素坐标"（绝对坐标）。
    /// 光看 pointerId 不够——这台机器上曾经拿鼠标的 info 回来过，所以**再核一次 pointerType**
    /// （1=PT_POINTER 2=PT_TOUCH 3=PT_PEN 4=PT_MOUSE），只认触摸/笔。
    /// </summary>
    private static bool TryGetPointerScreenPoint(PointerRoutedEventArgs e, out POINT pt)
    {
        pt = default;
        try
        {
            var id = e.Pointer.PointerId;
            if (id == 0) return false;
            if (!GetPointerInfo(id, out var info)) return false;
            if (info.pointerId != id) return false;                     // 不是同一个指针，宁可用别的路
            if (info.pointerType != 2 && info.pointerType != 3) return false;   // 只要触摸(2)/笔(3)
            if (info.ptPixelLocation.X == 0 && info.ptPixelLocation.Y == 0) return false;

            pt = new POINT(info.ptPixelLocation.X, info.ptPixelLocation.Y);
            return true;
        }
        catch { return false; }
    }

    /// <summary>这次拖动用的哪条取点路子（每拖只记一次，排查"抖/不跟手"时看它）。</summary>
    private void LogSource(string src)
    {
        if (_loggedSource) return;
        _loggedSource = true;
        Log($"拖动坐标源: {src}");
    }

    /// <summary>
    /// 诊断用：把这次拖动的每一条采样记下来（最多 40 条）。
    /// 重点是「目标」「Win32 实读」「AppWindow 自称」三个位置——用来确认"读回来的位置是不是真的"。
    /// </summary>
    private void LogDrag(double curDipX, double curDipY, double dxF, double dyF, int tx, int ty)
    {
        if (++_dragSamples > 40) return;
        try
        {
            var real = WindowRectNow();
            var said = _appWindow?.Position ?? new PointInt32(-1, -1);
            Log($"样本{_dragSamples}: 读数=({curDipX:0.0},{curDipY:0.0}) ΔF=({dxF:0.0},{dyF:0.0}) 目标=({tx},{ty}) 实读=({real.X},{real.Y}) 自称=({said.X},{said.Y}) 上一步挪=({_lastIssuedDeltaX},{_lastIssuedDeltaY})");
        }
        catch { }
    }

    /// <summary>现读窗口位置（直接问 Win32，不走缓存/不带 DPI 换算）。</summary>
    private PointInt32 WindowRectNow()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var r))
                return new PointInt32(r.Left, r.Top);
        }
        catch { }

        return _appWindow?.Position ?? new PointInt32(0, 0);
    }

    /// <summary>窗口当前的 DPI 缩放（DIP → 物理像素）。</summary>
    private double DpiScaleOf()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd != IntPtr.Zero)
            {
                var dpi = GetDpiForWindow(hwnd);
                if (dpi > 0) return dpi / 96.0;
            }
        }
        catch { }

        return 1.0;
    }

    /// <summary>
    /// 是不是鼠标指针。用 ToString 比对，绕开 PointerDeviceType 枚举命名空间的版本差异
    /// （WinUI 3 里在 Microsoft.UI.Input，UWP 那套在 Windows.Devices.Input，两个都见过）。
    /// </summary>
    private static bool IsMousePointer(PointerRoutedEventArgs e)
    {
        try
        {
            return string.Equals(e.Pointer.PointerDeviceType.ToString(), "Mouse", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private void Strip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        if (sender is UIElement u) u.ReleasePointerCapture(e.Pointer);

        if (_dragging)
        {
            _dragging = false;
            DockToNearestEdge();
        }
        else
        {
            Expand();                                      // 没动 → 当成点了一下
        }

        e.Handled = true;
    }

    /// <summary>松手时：看窗口中心离哪条边最近就贴哪条边，沿边的位置按松手处记下来。</summary>
    private void DockToNearestEdge()
    {
        if (_appWindow is null) return;
        try
        {
            var work = DisplayArea.Primary.WorkArea;
            var size = _appWindow.Size;
            var pos = _appWindow.Position;
            double cx = pos.X + size.Width / 2.0;
            double cy = pos.Y + size.Height / 2.0;

            var dl = Math.Abs(cx - work.X);
            var dr = Math.Abs(work.X + work.Width - cx);
            var dt = Math.Abs(cy - work.Y);
            var db = Math.Abs(work.Y + work.Height - cy);
            var min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
            var edge = min == dl ? "left" : min == dr ? "right" : min == dt ? "top" : "bottom";

            if (edge != Edge) Log("换边: " + edge);
            App.Settings.Current.SidebarEdge = edge;

            // 沿边位置存成 0~1 的比例，这样收起/展开尺寸不一样时也能对得上
            var flat = edge is "top" or "bottom";
            var alongStart = flat ? work.X : work.Y;
            var alongLen = flat ? work.Width : work.Height;
            var at = flat ? pos.X : pos.Y;
            var myLen = flat ? size.Width : size.Height;
            var free = Math.Max(0, alongLen - myLen);
            App.Settings.Current.SidebarPosRatio =
                free <= 0 ? 0.5 : Math.Clamp((at - alongStart) / (double)free, 0, 1);

            App.Settings.Save();
            SnapToSetting();
        }
        catch (Exception ex)
        {
            Log("换边失败: " + ex.Message);
        }
    }

    // ── 动画 ─────────────────────────────────────────────────

    /// <summary>
    /// 展开：**整个窗口**（连亚克力底、圆角、描边一起）从贴的那条边滑进去。
    /// 以前是"窗口先瞬间铺开成一大块 → 里面的内容才自己滑"，所以先闪一个灰框，很出戏。
    /// 窗口位置交给不了合成器，只能按帧 Move（16ms 一帧 ≈ 60fps）；
    /// 而且从"露出半块"起步，不从屏幕外起步 —— 完全挪到屏幕外的窗口 DWM 往往不给它刷帧，
    /// 滑进来的第一帧会发虚。半块起步肉眼就是"从边上滑进来"。
    /// </summary>
    private void SlideInFromEdge()
    {
        if (_appWindow is null) return;
        StopSlide();                                     // 上一次没滑完就再展开：作废重来

        var finalPos = _appWindow.Position;
        var half = (IsFlat ? _appWindow.Size.Height : _appWindow.Size.Width) / 2.0;

        var start = Edge switch
        {
            "left" => new PointInt32(finalPos.X - (int)half, finalPos.Y),
            "top" => new PointInt32(finalPos.X, finalPos.Y - (int)half),
            "bottom" => new PointInt32(finalPos.X, finalPos.Y + (int)half),
            _ => new PointInt32(finalPos.X + (int)half, finalPos.Y)
        };

        _appWindow.Move(start);
        TweenWindow(start, finalPos, 220);
    }

    /// <summary>按帧把窗口从 from 挪到 to（缓出）。⚠️ 状态一变（收起/拖动/再展开）这一波就作废，绝不许它回头改窗口。</summary>
    private void TweenWindow(PointInt32 from, PointInt32 to, double ms, Action? done = null)
    {
        if (_appWindow is null) return;
        StopSlide();

        var epoch = ++_slideEpoch;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            if (epoch != _slideEpoch)                        // 状态已经变了：这一波到此为止
            {
                timer.Stop();
                return;
            }

            var t = Math.Clamp(sw.Elapsed.TotalMilliseconds / ms, 0, 1);
            var e = 1 - Math.Pow(1 - t, 3);              // ease-out cubic
            _appWindow.Move(new PointInt32(
                (int)Math.Round(from.X + (to.X - from.X) * e),
                (int)Math.Round(from.Y + (to.Y - from.Y) * e)));

            if (t < 1) return;
            StopSlide();
            _appWindow.Move(to);                          // 最后一帧对到准确位置
            if (done is not null) done();                 // 滑完了再收尾（收起就是靠它）
        };

        _slideTimer = timer;
        _sliding = true;
        timer.Start();
    }

    /// <summary>收起：整个窗口往贴着的那条边**滑出去**（跟展开滑进来同一条路子，方向相反），滑完再真收。</summary>
    private bool SlideOutToEdge(Action done)
    {
        if (_appWindow is null) return false;
        StopSlide();
        var from = _appWindow.Position;
        var half = (IsFlat ? _appWindow.Size.Height : _appWindow.Size.Width) / 2.0;
        var to = Edge switch
        {
            "left" => new PointInt32(from.X - (int)half, from.Y),
            "top" => new PointInt32(from.X, from.Y - (int)half),
            "bottom" => new PointInt32(from.X, from.Y + (int)half),
            _ => new PointInt32(from.X + (int)half, from.Y)
        };
        TweenWindow(from, to, 200, done);
        return true;
    }

    private void StopSlide()
    {
        _slideEpoch++;                                    // 让在跑的那一波作废（关键：竞态的根治）
        _slideTimer?.Stop();
        _slideTimer = null;
        _sliding = false;
    }

    /// <summary>旧的内容滑入（只动 Panel 里的东西，窗口先铺开）——现在不用了，留着备用。</summary>
    private void PlaySlideIn()
    {
        try
        {
            var v = ElementCompositionPreview.GetElementVisual(Panel);
            var compositor = v.Compositor;

            var from = IsFlat
                ? new Vector3(0, Edge == "top" ? -56f : 56f, 0)
                : new Vector3(Edge == "left" ? -56f : 56f, 0, 0);

            v.Offset = from;
            v.Opacity = 0.4f;

            var slide = compositor.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(1f, Vector3.Zero,
                compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
            slide.Duration = TimeSpan.FromMilliseconds(190);
            v.StartAnimation("Offset", slide);

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1f, 1f);
            fade.Duration = TimeSpan.FromMilliseconds(190);
            v.StartAnimation("Opacity", fade);
        }
        catch (Exception ex)
        {
            Log("滑入动画失败: " + ex.Message);
        }
    }

    // ── 排版 / 位置 / 尺寸 ───────────────────────────────────

    /// <summary>按贴的边决定排版：贴左右 = 一列；贴上/下 = 两行（工具一行、按钮一行）。</summary>
    private void ApplyEdgeLayout()
    {
        try
        {
            var edge = Edge;
            var flat = edge is "top" or "bottom";

            // 收起状态的抓手：竖条时竖着、横条时横着
            GripStack.Orientation = flat ? Orientation.Horizontal : Orientation.Vertical;
            Grip.Width = flat ? 48 : 5;
            Grip.Height = flat ? 5 : 48;

            // 展开面板：永远竖着排（贴上下边时就是两行）
            ExpandedStack.Orientation = Orientation.Vertical;
            TitleRow.Visibility = flat ? Visibility.Collapsed : Visibility.Visible;

            ToolStack.Orientation = flat ? Orientation.Horizontal : Orientation.Vertical;
            ToolStack.Spacing = flat ? 8 : 3;
            ToolStack.HorizontalAlignment = flat ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;

            Sep.Margin = flat ? new Thickness(10, 4, 10, 4) : new Thickness(2, 3, 2, 3);

            FooterStack.Orientation = flat ? Orientation.Horizontal : Orientation.Vertical;
            FooterStack.Spacing = flat ? 8 : 2;
            FooterStack.HorizontalAlignment = flat ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;

            // 贴上下边时给按钮留出宽度，别挤成一坨
            foreach (var b in _toolButtons)
            {
                b.MinWidth = flat ? 64 : 0;
                b.MinHeight = flat ? 48 : 52;
                b.Padding = flat ? new Thickness(4, 6, 4, 6) : new Thickness(0, 8, 0, 8);
            }
            foreach (var b in FooterButtons())
            {
                b.MinWidth = flat ? 104 : 0;
                // 横条时矮一点，不然两行加起来超出面板高度，下面一排会被裁掉
                b.MinHeight = flat ? 32 : 44;
                b.Padding = flat ? new Thickness(6, 2, 6, 2) : new Thickness(8, 4, 8, 4);
                b.HorizontalContentAlignment = flat ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            }

            ApplyFooterContent(flat);

            // 模块多了就让工具区自己滚动（贴上下边时改成左右滚）
            ToolsScroll.VerticalScrollMode = flat ? ScrollMode.Disabled : ScrollMode.Auto;
            ToolsScroll.VerticalScrollBarVisibility = flat ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            ToolsScroll.HorizontalScrollMode = flat ? ScrollMode.Auto : ScrollMode.Disabled;
            ToolsScroll.HorizontalScrollBarVisibility = flat ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            ApplyScrollLimit();
            if (_foldIcon is not null)
            {
                _foldIcon.Glyph = edge switch
                {
                    "left" => "\uE76C",      // 往左收
                    "top" => "\uE70D",       // 往下收
                    "bottom" => "\uE70E",    // 往上收
                    _ => "\uE76B",           // 往右收
                };
            }
        }
        catch (Exception ex)
        {
            Log("排版失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 底下三个按钮（收起 / 位置复原 / 隐藏）的内容：
    /// 贴左右边是**竖条** → 图标在上、文字在下（跟工具块一个样式）；
    /// 贴上/下边是**横条**（面板只有 112 高）→ 左图标、右文字，竖排会被裁掉。
    /// </summary>
    private void ApplyFooterContent(bool flat)
    {
        SetFooterButton(FoldButton, "\uE76C", "收起", flat, out _foldIcon);
        SetFooterButton(PinButton, "\uE718", "常驻", flat, out _pinIcon);
        SetFooterButton(ResetButton, "\uE777", "位置复原", flat, out _);
        SetFooterButton(HideButton, "\uED1A", "隐藏", flat, out _);
        UpdatePinVisual();
    }

    private static void SetFooterButton(Button b, string glyph, string label, bool flat, out FontIcon icon)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = flat ? 11 : 10.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = flat ? 13 : 15,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        if (flat)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            row.Children.Add(icon);
            row.Children.Add(text);
            b.Content = row;
        }
        else
        {
            text.HorizontalAlignment = HorizontalAlignment.Center;
            var col = new StackPanel { Spacing = 2 };
            col.Children.Add(icon);
            col.Children.Add(text);
            b.Content = col;
        }
    }

    private Button[] FooterButtons() =>
        new Button[] { FoldButton, PinButton, ResetButton, HideButton };
    private double Scale()
    {
        try
        {
            var dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch { return 1.0; }
    }

    /// <summary>当前状态下窗口该多大（dip → 物理像素）。尺寸会跟着模块数量走。</summary>
    private SizeInt32 PlannedSize()
    {
        var scale = Scale();
        // 模块数量：按钮 52 + 间距 3；竖着时面板高度 = 200 + 55×个数（少于 4 个也不缩太多，免得看着空）
        var count = Math.Max(1, _toolButtons.Count);
        int dipW, dipH;
        if (_expanded)
        {
            var lim = ExpandedLimits();
            if (IsFlat)
            {
                // 上/下边：竖着两行 —— 第一行工具、第二行按钮
                dipW = (int)Math.Min(Math.Max(PanelLengthFlatDip, count * 64 + (count - 1) * 8 + 24), lim.W);
                dipH = PanelThicknessFlatDip;
            }
            else
            {
                dipW = PanelThicknessDip;
                // 底下那排按钮现在有四个（收起/常驻/位置复原/隐藏），比原来多一格，高度基数 +48
                dipH = (int)Math.Min(Math.Max(PanelLengthDip, 200 + 55 * count) + 48, lim.H);
            }
        }
        else
        {
            dipW = IsFlat ? CollapsedLengthDip : CollapsedThicknessDip;
            dipH = IsFlat ? CollapsedThicknessDip : CollapsedLengthDip;
        }

        return new SizeInt32((int)Math.Round(dipW * scale), (int)Math.Round(dipH * scale));
    }

    /// <summary>
    /// 展开面板最多占多大（dip）。模块再多也不让它长满整屏 —— 超出的部分交给工具区滚动（ToolsScroll）。
    /// </summary>
    private (double W, double H) ExpandedLimits()
    {
        var scale = Scale();
        var work = DisplayArea.Primary.WorkArea;
        var workW = work.Width / scale - 32;          // 离屏幕两边留点空
        var workH = work.Height / scale - 32;

        if (IsFlat)
            return (Math.Min(workW, Math.Max(PanelLengthFlatDip, workW * 0.92)), PanelThicknessFlatDip);

        return (PanelThicknessDip, Math.Min(workH, Math.Max(PanelLengthDip + 48, workH * 0.85)));
    }

    /// <summary>工具区最多能占多高/多宽（面板高度 - 标题/分隔线/底排按钮）。</summary>
    private void ApplyScrollLimit()
    {
        try
        {
            if (!_expanded) return;
            var scale = Scale();
            var work = DisplayArea.Primary.WorkArea;

            if (IsFlat)
            {
                var chrome = 12 + 24;                                    // 面板 padding + 间距余量
                ToolsScroll.MaxWidth = Math.Max(120, work.Width / scale - 32 - chrome);
                ToolsScroll.MaxHeight = double.PositiveInfinity;
            }
            else
            {
                var title = TitleRow.Visibility == Visibility.Visible ? 26 : 0;
                var footer = 4 * 44 + 3 * 2;                             // 底排四个按钮 + 间距
                var chrome = title + 7 + footer + 17 + 9;                // + 分隔线 + 面板 padding + 几个间距
                ToolsScroll.MaxHeight = Math.Max(120, ExpandedLimits().H - chrome);
                ToolsScroll.MaxWidth = double.PositiveInfinity;
            }
        }
        catch (Exception ex)
        {
            Log("算滚动范围失败: " + ex.Message);
        }
    }


    private void ApplySize()
    {
        if (_appWindow is null) return;
        try
        {
            ApplyScrollLimit();
            _appWindow.Resize(PlannedSize());
        }
        catch (Exception ex)
        {
            Log("改尺寸失败: " + ex.Message);
        }
    }

    /// <summary>贴到设置里那条边；沿边的位置按 SidebarPosRatio（&lt;0 = 居中）。</summary>
    private void MoveToEdge()
    {
        if (_appWindow is null) return;
        try
        {
            var work = DisplayArea.Primary.WorkArea;
            // ⚠️ 用自己的目标尺寸算，别读 _appWindow.Size —— Resize 刚调完它还没更新，会按老尺寸贴边
            var size = PlannedSize();
            var edge = Edge;
            var flat = edge is "top" or "bottom";

            var alongLen = flat ? work.Width : work.Height;
            var myLen = flat ? size.Width : size.Height;
            var free = Math.Max(0, alongLen - myLen);

            var ratio = App.Settings.Current.SidebarPosRatio;
            var offset = ratio < 0 ? free / 2.0 : Math.Clamp(ratio * free, 0, free);

            int x, y;
            switch (edge)
            {
                case "left":
                    x = work.X;
                    y = (int)Math.Round(work.Y + offset);
                    break;
                case "top":
                    x = (int)Math.Round(work.X + offset);
                    y = work.Y;
                    break;
                case "bottom":
                    x = (int)Math.Round(work.X + offset);
                    y = work.Y + work.Height - size.Height;
                    break;
                default:
                    x = work.X + work.Width - size.Width;
                    y = (int)Math.Round(work.Y + offset);
                    break;
            }

            // 移动有时不精确，量一下再纠偏几次
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
        }
        catch (Exception ex)
        {
            Log("贴边失败: " + ex.Message);
        }
    }

    // ── P/Invoke ─────────────────────────────────────────────

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    /// <summary>Win32 POINTER_INFO（字段顺序/对齐照 WinUser.h 来，不能少不能换）——只用得到 ptPixelLocation。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [DllImport("user32.dll")]
    private static extern bool GetPointerInfo(uint id, out POINTER_INFO pointerInfo);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private static string LogPath => System.IO.Path.Combine(SettingsStore.Dir, "sidebar.log");

    private static void Log(string message)
    {
        try
        {
            System.IO.Directory.CreateDirectory(SettingsStore.Dir);
            System.IO.File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { }
    }
}
