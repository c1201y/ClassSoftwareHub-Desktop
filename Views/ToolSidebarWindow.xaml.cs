using System;
using System.Collections.Generic;
using System.Linq;
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
///   · 展开 = 工具 + 「收起 / 常驻 / 位置复原 / 隐藏 / 打开应用」（展开状态下不能拖，免得跟点按钮打架）
/// 位置（贴哪条边 + 沿边位置）会记在设置里；「位置复原」= 回到右边的居中位置。
/// 置顶、不进任务栏、无标题栏、不能缩放/最大化/最小化。
///
/// ⚠️ 2026-09-27（Nick）：**左右两边可以同时显示**（设置里选「左右两边」= <c>SidebarEdge</c> 为 "both"）。
/// 所以它不再是个单例 —— 见 <see cref="_pool"/>：一条边一个实例，设置里的 "both" 会被拆成 left + right 两条。
/// 两条共享 <c>SidebarPosRatio</c>，上下位置天然一致；拖任意一条时对面实时跟随（<see cref="FollowPartner"/>）。
/// 上边 / 下边**只有单条**（Nick：横着放一条就够了）。
///
/// ⚠️ 2026-09-28（Nick）：再加**两种模式**（<c>SidebarMode</c>）——
///   · <c>dock</c> 贴靠模式：**只贴左右两条边**（可选「左右两边」同时显示两条），沿边位置共用
///     <c>SidebarPosRatio</c>；上/下边**不归它管**。
///   · <c>free</c> 自由模式：**四条边都能吸**（左/右/上/下），贴哪条边存 <c>SidebarFreeEdge</c>，
///     贴上/下边时是横条。它**不是**"浮在屏幕中间不吸边"—— 侧边栏永远吸在某条边上。
/// 两种模式走的是**同一套**实例池与吸附逻辑（<see cref="_pool"/> / <see cref="SyncInstances"/>），
/// 差别只在"允许哪几条边"（见 <see cref="DesiredEdges"/>）与"松手时把边存进哪个设置"（<see cref="DockToNearestEdge"/>）。
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
    private const int PanelLengthFlatDip = 470;     // 上/下边时：展开面板长度的**下限**（真实宽度按内容算，见 PlannedSize）

    /// <summary>
    /// 收起动画的第二段：面板滑出屏幕之后，**抓手从屏幕外滑回贴边位**要花的毫秒（2026-10-01）。
    /// 第一段是 <see cref="SlideOutToEdge"/> 的 240ms（整个面板推出屏幕）。两段加起来 ≈ 430ms。
    /// 抓手的行程很短（只有自身厚度 20dip + 2px ≈ 27px），所以这段比第一段快一些才跟得上。
    /// </summary>
    private const double CollapseSlideMs = 190;

    /// <summary>
    /// 底部按钮那一排在竖条里的高度增量 —— <b>五颗全显</b>时的经验值（2026-09-27 定的）。
    /// 注意它**不是**五颗的真实高度（那是 5×44 + 4×2 = 228）：面板高度基数
    /// <see cref="PanelLengthDip"/>（450）里本来就已经含了标题和一段留白，这 94 只是把五颗"补齐"。
    /// ⚠️ 2026-09-29 底排改成**逐颗开关**后，这里必须按**可见颗数**摊算（见 <see cref="FooterDipFor"/>），
    ///    别改写成 <c>n × 44</c> 这种"真实高度" —— 会跟 450 的基数重复计算，面板凭空长一截。
    /// </summary>
    private const int FooterBaseDip = 94;

    /// <summary>底排可见 <paramref name="visible"/> 颗时，竖条高度里的增量（0 颗 = 完全不留）。</summary>
    private static int FooterDipFor(int visible) =>
        visible <= 0 ? 0 : (int)Math.Round(FooterBaseDip * visible / 5.0);

    /// <summary>
    /// 上/下边（横条）下**只有工具那一行**时的高度（= 底排一颗都不显示）。
    /// 两行版是 <see cref="PanelThicknessFlatDip"/>（112）；底排一颗都没有时只剩一行，
    /// 面板上下的 padding（5+6）+ 外框（2）+ 工具按钮的行高（图标 20 + 间距 3 + 文字 11 ≈ 54）
    /// ≈ 67，给几像素余量取 72。不跟着缩的话底下会空出一大块；反过来给小了会把按钮裁掉。
    /// </summary>
    private const int PanelThicknessFlatBareDip = 72;

    // 上/下边（横条）时底排按钮的排版参数。
    // ⚠️ 横条宽度必须容得下**底排这一整排**（见 PlannedSize），不然最后一颗会被面板裁掉 ——
    //    2026-09-27 用户截图就是这个：「打开应用」只露出半个「打」字。
    private const int FooterFlatButtonWidthDip = 104;   // 底排每个按钮的最小宽度
    private const int FooterFlatSpacingDip = 8;         // 底排按钮之间的间距
    private const int FlatRowPadDip = 24;               // 横条里每一行的左右内边距 + 余量

    /// <summary>
    /// 按边缓存的窗口实例（键 = left | right | top | bottom）。
    ///
    /// ⚠️ 2026-09-27（Nick 需求）：竖直状态要能**左右同时**有侧边栏，所以从"单例"改成了"一条边一个实例"。
    /// 建过的实例留着复用 —— 切边只是显隐，不反复建窗。当前该显示哪几条见 <see cref="SyncInstances"/>。
    /// 设置里的 "both" 在这儿会被拆成 left + right 两个实例，每个实例的边存在 <see cref="_edge"/>（不读设置）。
    /// 换边 / 换模式都只是改这个字典里"该显示哪几条" —— 同一条路（<see cref="SyncInstances"/>）。
    /// </summary>
    private static readonly Dictionary<string, ToolSidebarWindow> _pool = new();

    /// <summary>这条实例贴的边：left | right | top | bottom。**实例级** —— 设置里那个 "both" 是拆出来的两条，不是它的值。</summary>
    private readonly string _edge;

    private AppWindow? _appWindow;
    private bool _expanded;

    private int _collapseEpoch;                            // 收起滑出/抓手淡入的批次号：展开、再次收起都能把它作废
    private bool _shownOnce;
    private bool _visible;

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _idle;

    /// <summary>展开滑动用的渲染循环帧回调（滑完置空）。⚠️ 见 <see cref="TweenWindow"/>。</summary>
    private EventHandler<object>? _slideFrame;

    /// <summary>渲染循环万一停摆（窗口被藏、渲染暂停）时的收尾兜底定时器。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _slideWatchdog;

    /// <summary>滑动的**落点存档**（目标位置）。滑动进行中 <see cref="CurrentRect"/> 返回它，
    /// 别让锚定方（音量浮窗）读到半路上的实时位置 —— 锚到半路的位置，等边条滑到位两个就叠上了（2026-10-01 修）。</summary>
    private Windows.Graphics.RectInt32? _restRect;

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

    /// <summary>
    /// 侧边栏当前在屏幕上的矩形（给"挨着它弹提示"用）；还没建/拿不到就返回 null。
    /// 左右两条同时显示时**优先右边那条** —— 音量浮窗一直挂右边（见 <c>VolumeWindow.Edge</c>），锚点得跟它一致。
    /// </summary>
    public static Windows.Graphics.RectInt32? CurrentRect
    {
        get
        {
            var w = AnchorInstance();
            if (w?._appWindow is null) return null;
            try
            {
                // 滑动进行中：返回落点存档（动画的目标位置）。实时位置还在半路上，
                // 锚定方拿到它会把自己的落座点算歪，等边条滑到位就叠上了。
                if (w._slideFrame is not null && w._restRect is { } rest) return rest;

                var pos = w._appWindow.Position;
                var size = w._appWindow.Size;
                return new Windows.Graphics.RectInt32(pos.X, pos.Y, size.Width, size.Height);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>挑一条"可见的"实例当锚点：优先右边 → 再任意一条可见的 → 都没有就 null。</summary>
    private static ToolSidebarWindow? AnchorInstance()
    {
        if (_pool.TryGetValue("right", out var r) && r._visible) return r;
        foreach (var w in _pool.Values) if (w._visible) return w;
        return null;
    }

    /// <summary>
    /// 侧边栏**当前实际**贴的那条边（音量浮窗按它决定往哪边排）。
    ///
    /// ⚠️ 绝不能用 <c>App.Settings.Current.SidebarEdge</c> 代替：那条是**停靠模式**的设置，
    ///    **自由模式**下它可能还是老值（`SidebarFreeEdge` 才是真身），用户还能把边条拖到任意一边。
    ///    读错方向的后果（2026-10-01 踩到）：浮窗被摆到边条的**另一侧**（等于屏幕外）→
    ///    又被 ClampToWork 夹回屏幕边缘 → 正好压在边条（乃至合成器）身上，看着就是"三个窗叠一起"。
    /// </summary>
    public static string CurrentEdge
    {
        get
        {
            try
            {
                var inst = AnchorInstance();
                if (inst is not null) return inst._edge;

                // 没有可见实例：按设置推一个（音量浮窗一直挂右边，双双模式也取右）
                var want = DesiredEdges();
                return want.Contains("right") ? "right" : want[0];
            }
            catch
            {
                return "right";
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
    private int _lastTargetX;                 // 上一次真正发出去的目标位置（相同就别重复 Move）
    private int _lastTargetY;
    private int _dragSamples;                 // 诊断用：这次拖动记了几条样本

    private ToolSidebarWindow(string edge)
    {
        _edge = edge;
        InitializeComponent();

        // 展开后没人动 → 自己收回去（触屏没地方"点空白处收起"）
        _idle = DispatcherQueue.CreateTimer();
        _idle.Interval = TimeSpan.FromSeconds(10);
        _idle.IsRepeating = false;
        _idle.Tick += (_, _) => { if (_expanded && !App.Settings.Current.SidebarPinned && !SuppressAutoCollapse) Collapse(); };

        Configure();

        Core.AppLog.Info("exit", $"侧边栏实例已建 edge={_edge}");
        Closed += (_, _) => Core.AppLog.Info("exit", $"侧边栏 Closed edge={_edge}");
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
        foreach (var w in SyncInstances()) w.Present();
    }

    public static void HideSidebar()
    {
        foreach (var w in _pool.Values) w.HideSelf();
    }

    /// <summary>
    /// 退出应用时把实例池里的窗口**真正关掉**（平时只是显隐，从不销毁）。
    /// ⛔ 为什么必须有：Application.Exit() 在 WinUI 3 里会漏窗口 —— 2026-10-04 实测，
    ///    托盘「退出」后侧边栏常常是唯一活下来的那个窗口，把消息循环撑住、进程退不掉。
    /// </summary>
    public static void CloseForExit()
    {
        foreach (var w in _pool.Values.ToList())
        {
            try { w.Close(); } catch { }
        }
        _pool.Clear();
        Core.AppLog.Info("exit", "侧边栏实例已请求 Close");
    }

    /// <summary>
    /// 按设置把实例集合对齐到"该有哪几条边"：该显示的建出来/显示，不该显示的藏起来。
    /// 返回**本次该显示的那些实例**（顺序：左 → 右；单条就是一个）。
    /// </summary>
    private static List<ToolSidebarWindow> SyncInstances()
    {
        var want = DesiredEdges();

        foreach (var kv in _pool)
            if (!want.Contains(kv.Key)) kv.Value.HideSelf();

        var list = new List<ToolSidebarWindow>();
        foreach (var e in want)
        {
            if (!_pool.TryGetValue(e, out var w))
            {
                w = new ToolSidebarWindow(e);
                _pool[e] = w;
            }
            list.Add(w);
        }
        return list;
    }

    /// <summary>
    /// 设置说该有哪几条边。
    ///   · 贴靠模式（<c>dock</c>）：**只有左右两条边** —— <c>both</c> 拆成 left + right。
    ///   · 自由模式（<c>free</c>）：用户选的那**一条边**，左/右/上/下都行（<see cref="FreeEdgeSetting"/>），
    ///     贴上/下边时是横条（<see cref="IsFlat"/>）。自由模式**不提供「左右两边」**。
    /// </summary>
    private static List<string> DesiredEdges()
    {
        if (IsFreeMode) return new List<string> { FreeEdgeSetting };

        return App.Settings.Current.SidebarEdge switch
        {
            "left" => new List<string> { "left" },
            "both" => new List<string> { "left", "right" },
            // ⚠️ 贴靠模式只认左右。老设置里如果留着 top/bottom（那时贴靠也能贴上下边），这里一律归到右边 ——
            //    想贴上下边得切到自由模式（Nick 2026-09-28 定：贴靠 = 左右模式）。
            _ => new List<string> { "right" },
        };
    }

    /// <summary>自由模式贴的那条边（设置值认不出就退回 right）。</summary>
    private static string FreeEdgeSetting => App.Settings.Current.SidebarFreeEdge switch
    {
        "left" or "top" or "bottom" => App.Settings.Current.SidebarFreeEdge,
        _ => "right",
    };

    /// <summary>
    /// 音量浮窗（主音量 + 合成器）**全部收干净了**叫一声：边条这时候也该跟着收回去。
    ///
    /// 为什么必须有它：点「音量」时我们压住了自动收起（不然浮窗一抢焦点边条就缩），
    /// 那份压制一旦放开，边条自己的"失焦收起"**早就在被压制时错过了**，
    /// 结果就是"浮窗收了两键也缩了、边条却赖着不动"。所以在这儿显式叫它收。
    /// 钉了常驻（SidebarPinned）的不收 —— 用户明确要它留着。
    /// </summary>
    public static void CollapseAfterVolumeFlyoutsClosed()
    {
        foreach (var w in _pool.Values)
        {
            try
            {
                if (!w._visible || !w._expanded) continue;
                if (App.Settings.Current.SidebarPinned) continue;
                w.Collapse();
            }
            catch { }
        }
    }

    /// <summary>
    /// 「截屏」专用的收起：把边条收成那条细把手（**不是隐藏**），免得它被照进截图里。
    /// 边条展开着的时候才需要收；已经收着就啥也不做。
    /// </summary>
    public static void CollapseForCapture()
    {
        foreach (var w in _pool.Values)
        {
            try
            {
                if (!w._expanded) continue;
                w.Collapse(animate: false);               // 截图前必须当帧收干净，不然把淡出中的边条也照进去
            }
            catch { }
        }
    }

    public static bool IsSidebarVisible
    {
        get
        {
            foreach (var w in _pool.Values) if (w._visible) return true;
            return false;
        }
    }

    /// <summary>设置里的开关/边选项变了：开就显示、关就藏起来；边变了重新贴过去。</summary>
    public static void ApplySetting()
    {
        if (!App.Settings.Current.SidebarEnabled) { HideSidebar(); return; }
        foreach (var w in SyncInstances())
        {
            w.Present();
            w.SnapToSetting();
        }
    }

    private void SnapToSetting()
    {
        ApplyEdgeLayout();
        ApplySize();
        MoveToEdge();
    }

    // ── 窗口本身 ─────────────────────────────────────────────

    /// <summary>这条实例贴的边：left | right | top | bottom。⚠️ 实例级 —— 别改回"读设置"，那样两条实例会贴到同一条边上去。</summary>
    private string Edge => _edge;

    /// <summary>横条（贴上/下边）还是竖条（贴左/右边）。</summary>
    private bool IsFlat => _edge is "top" or "bottom";

    /// <summary>设置里选的是自由模式（能吸四条边）。⚠️ 认不出的值一律当贴靠模式。</summary>
    private static bool IsFreeMode => App.Settings.Current.SidebarMode == "free";

    /// <summary>左右两条同时显示（贴靠模式里选的 "both"）。拖动时靠它决定"锁竖直、不换边"以及要不要带对面一起动。</summary>
    private static bool IsDual => !IsFreeMode && App.Settings.Current.SidebarEdge == "both";

    /// <summary>对面那条实例（双边模式下用来做位置联动）；自由模式、单边模式都是 null。</summary>
    private ToolSidebarWindow? Partner()
    {
        if (!IsDual) return null;
        var other = _edge == "left" ? "right" : "left";
        return _pool.TryGetValue(other, out var w) ? w : null;
    }

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "工具侧边栏";
            _appWindow.Closing += (_, _) => Core.AppLog.Info("exit", $"侧边栏 Closing edge={_edge}");

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
                UpdatePinVisual();                         // 常驻块的图标颜色也跟主题（钉住=主题色，松开=默认前景）
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

            // 首显那几帧亚克力底还没跟上 —— 面板先用不透明主题底色顶住，别让"没底的黑"露出来。
            HoldOpaqueForTransition();

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

        // 音量浮窗（主音量 + 合成器）跟着一起收
        VolumeFlyoutGroup.CloseAll();

        try { ShowWindow(WindowNative.GetWindowHandle(this), SW_HIDE); } catch { }
    }

    /// <summary>
    /// 面板底色：亚克力上面再压一层很淡的色（深色压深、浅色压白），字才看得清。
    ///
    /// <paramref name="opaque"/>=true 时**不压薄纱，直接用不透明的主题底色** ——
    /// 专门给"窗口刚放大 / 刚显形"的那几帧用：那会儿亚克力底还没跟上，
    /// 半透明薄纱压上去等于压在黑上，一整条纯黑就是这么来的（2026-10-04 Nick 截图那个黑框）。
    /// 色值跟 <see cref="Core.WindowChrome.SetBackgroundFallback"/> 的兜底底色同族，别各挑各的。
    /// </summary>
    private void ApplyPanelBrush(bool opaque = false)
    {
        var dark = Root.ActualTheme == ElementTheme.Dark;
        Panel.Background = new SolidColorBrush(opaque
            ? (dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
                    : Windows.UI.Color.FromArgb(255, 243, 243, 243))
            : (dark ? Windows.UI.Color.FromArgb(95, 20, 20, 20)
                    : Windows.UI.Color.FromArgb(110, 255, 255, 255)));
    }

    /// <summary>
    /// 过渡期保护：先把面板压成**不透明**，过 <paramref name="frames"/> 帧再放回"亚克力 + 薄纱"。
    ///
    /// 为什么只保前几帧：亚克力底跟上就不需要它了。给多了整段动画都是纯色面板，
    /// 材质会"啪"一下冒出来 —— 反而更显眼。
    /// ⚠️ 帧数别低于 4：慢机器（本次反馈就是教学机）亚克力底要好几帧才跟上。
    /// </summary>
    private void HoldOpaqueForTransition(int frames = 8)
    {
        ApplyPanelBrush(opaque: true);
        try
        {
            var left = frames;
            EventHandler<object>? handler = null;
            handler = (_, _) =>
            {
                if (left-- > 0) return;
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= handler!;
                ApplyPanelBrush();
            };
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += handler;
        }
        catch
        {
            ApplyPanelBrush();       // 挂不上帧回调就直接恢复，别留个纯色面板
        }
    }

    // ── 展开 / 收起 / 隐藏 ───────────────────────────────────

    private void Expand()
    {
        StopSlide();                                      // 掐掉上一次没跑完的（防连点/竞态）
        _collapseEpoch++;                                 // 取消可能还在跑的"收起滑出/抓手淡入"
        _expanded = true;
        ResetPanelOpacity();

        // 这一下会把窗口放大到展开尺寸（见 SlideInFromEdge），新露出来的那几帧
        // "内容还没铺出来 / 亚克力底还没跟上"就是那块黑框的来源 —— 先用不透明主题底色顶住。
        HoldOpaqueForTransition();

        CollapsedView.Visibility = Visibility.Collapsed;
        ExpandedView.Visibility = Visibility.Visible;
        ApplyScrollLimit();                               // 先把滚动范围算好；尺寸交给滑入动画一帧设到位

        // ⚠️ 这里**不要**再单独 ApplySize() + MoveToEdge()：
        //    那会让窗口先出现在"终点位置"并画出一帧展开态，紧接着又被滑入动画挪到起点 ——
        //    肉眼就是"闪一下，然后再滑"。滑入动画自己会用**一次** MoveAndResize
        //    把"起点位置 + 展开尺寸"同时设下去（2026-09-26 优化）。
        SlideInFromEdge();
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

        _expanded = false;                                // 先立旗：滑出期间自动收起那条路别再来一遍
        var epoch = ++_collapseEpoch;
        // 滑完才真收，而且要让抓手**淡入**：滑出终点在屏幕外，小条直接"啪"一下冒出来太生硬
        if (SlideOutToEdge(() => { if (epoch == _collapseEpoch) FinishCollapse(fadeIn: true); })) return;
        FinishCollapse();                                 // 没有窗口可滑（极少数）：当帧收干净，不做动画
    }


    /// <summary>
    /// 真收：换回抓手 + 落位。中途用户又展开了就别收（_expanded 已被置回 true）。
    /// </summary>
    /// <param name="fadeIn">
    /// true = 抓手淡入（**走滑动动画那条路用它**：面板刚滑出屏幕，小条淡入比"啪一下出现"自然）。
    /// false = 当帧就位（截图前、窗口刚出现时那种要立刻收干净的场合，不能有任何可见动画）。
    /// </param>
    private void FinishCollapse(bool fadeIn = false)
    {
        if (_expanded) return;

        // 先按住透明度，等落位之后再放出来 —— 顺序反了会先闪一帧不透明的小条
        if (fadeIn) SetPanelOpacity(0f);

        ExpandedView.Visibility = Visibility.Collapsed;
        CollapsedView.Visibility = Visibility.Visible;

        // 一次到位：收起尺寸 + 位置。
        // 滑出动画已经把**整个窗口**推出屏幕外了，所以这一步的"变身"（尺寸 92×450 → 20×110、
        // 内容换视图）用户在屏幕上看不到 —— 不会再出现"没滑出去就突然缩一下"（2026-09-26 修）。
        //
        // ⚠️ fadeIn 的落点是**屏幕外的抓手起点**，不是贴边位（2026-10-01 改）：
        //    面板滑出去之后，抓手再从屏幕外滑回贴边（见下面的 TweenWindow）——
        //    "大块滑走 + 小条滑回"一口气看完，比"大块滑走 + 小条原地淡入"连贯。
        //    起点在屏幕外，所以尺寸变身依旧藏得住。
        PointInt32? slideFrom = null, slideTo = null;
        try
        {
            if (_appWindow is not null)
            {
                var size = CollapsedSize();
                var pos = EdgePosition(size.Width, size.Height);
                var startPos = fadeIn
                    ? OutwardOffset(pos, (IsFlat ? size.Height : size.Width) + 2)   // 整个抓手推到屏幕外 + 2px 余量
                    : pos;

                _appWindow.MoveAndResize(new RectInt32(startPos.X, startPos.Y, size.Width, size.Height));

                if (fadeIn)
                {
                    slideFrom = startPos;
                    slideTo = pos;
                    _restRect = new Windows.Graphics.RectInt32(pos.X, pos.Y, size.Width, size.Height);  // 滑动期间 CurrentRect 返回它
                }
            }
        }
        catch (Exception ex)
        {
            Log("收尾落位失败: " + ex.Message);
        }

        if (fadeIn)
        {
            FadePanelToOpaque(CollapseSlideMs);
            if (slideFrom is { } f && slideTo is { } t) TweenWindow(f, t, CollapseSlideMs);
        }
        else ResetPanelOpacity();

        ReassertCollapsed();
    }

    private void ResetPanelOpacity() => SetPanelOpacity(1f);

    /// <summary>把整块面板的透明度按住（不走动画）。收起后要给抓手"淡入"，就先用它把面板压到 0。</summary>
    private void SetPanelOpacity(float value)
    {
        try
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(Panel);
            visual.StopAnimation("Opacity");
            visual.Opacity = value;
        }
        catch { }
    }

    /// <summary>面板从当前透明度淡到不透明（收起后让小条"浮现"而不是"闪现"）。</summary>
    private void FadePanelToOpaque(double ms)
    {
        try
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(Panel);
            var compositor = visual.Compositor;
            visual.StopAnimation("Opacity");

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.2f, 0f), new Vector2(0f, 1f)));      // 缓出：一开始就明显起来，尾巴柔和
            fade.Duration = TimeSpan.FromMilliseconds(ms);
            visual.StartAnimation("Opacity", fade);
        }
        catch (Exception ex)
        {
            Log("抓手淡入失败: " + ex.Message);
            ResetPanelOpacity();                            // 出问题就退回"直接可见"，别留个透明的边条
        }
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

                // ⚠️ 抓手正在从屏幕外滑入时**别纠位**：这一帧它本来就还在路上，
                //    抢先挪到贴边位会把滑入动画打断成"闪一下就到了"（2026-10-01）。
                //    落位交给动画自己的最后一帧（TweenWindow 里那次 SetWindowPos）。
                if (_slideFrame is not null) return;

                // ⚠️ 已经落对了就**什么都别做**。多挪一次窗口就多一帧重绘 ——
                //    动画刚结束那一下最容易看出抖，这里不能无脑再摆一次（2026-09-26 优化）。
                var size = CollapsedSize();
                var pos = EdgePosition(size.Width, size.Height);
                var now = _appWindow.Size;
                var at = _appWindow.Position;
                if (now.Width == size.Width && now.Height == size.Height
                    && Math.Abs(at.X - pos.X) <= 2 && Math.Abs(at.Y - pos.Y) <= 2) return;

                Log("兜底落位：尺寸/位置没对上，纠一次");
                _appWindow.MoveAndResize(new RectInt32(pos.X, pos.Y, size.Width, size.Height));
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

                if (pinned)
                {
                    _pinIcon.Foreground = new SolidColorBrush(AccentColor());
                }
                else
                {
                    // ⚠️ 松开状态**清掉本地值、跟着主题走**（2026-10-01 修「亮色模式下钉子白得看不见」）：
                    //    以前这里写死成 SolidColorBrush（暗色给白、亮色给黑）—— 可换主题时只有面板底色会重刷
                    //    （ActualThemeChanged 里只调了 ApplyPanelBrush），这个写死的颜色不会跟着变：
                    //    暗色下启动过再切亮色，就成了「白钉子压白底」，整颗图标消失。
                    //    清掉本地值后它回到 IconElement 默认前景（主题画刷），深浅色都自动对。
                    _pinIcon.ClearValue(FontIcon.ForegroundProperty);
                }
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

    /// <summary>
    /// 位置复原：沿边居中；两种模式都顺便把边退回默认的那条。
    /// ⚠️ 双边模式（both）下**不改边** —— 它没有"哪一条边"的概念，保持两条并一起摆正。
    /// </summary>
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (!IsDual)
        {
            if (IsFreeMode) App.Settings.Current.SidebarFreeEdge = "right";
            else App.Settings.Current.SidebarEdge = "right";
        }

        App.Settings.Current.SidebarPosRatio = -1;
        App.Settings.Save();

        // 边可能变了 → 得走 ApplySetting()（按新设置重建实例集合），不能只 SnapToSetting()
        ApplySetting();
        Partner()?.SnapToSetting();
        Touch();
    }

    /// <summary>隐藏：直接关掉（设置里也不显示了），想找回来去「内置工具」页或托盘图标菜单。</summary>
    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.SidebarEnabled = false;
        App.Settings.Save();
        HideSidebar();                                    // 双边时两条一起藏
    }

    /// <summary>
    /// 打开应用：把**主窗口**叫出来（可能收在托盘里、也可能只是最小化了）。
    /// 侧边栏是独立小窗，经常是屏幕上唯一露着的东西 —— 给老师留一条回主界面的近路。
    /// </summary>
    private void OpenApp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            App.MainWindow?.ShowFromTray();
        }
        catch (Exception ex)
        {
            Log("打开主界面失败: " + ex.Message);
        }
    }

    /// <summary>设置里的模块清单变了（侧边布局页改完调它）：重建按钮 + 重新量尺寸贴边（左右两条都要）。</summary>
    public static void ApplyModules()
    {
        if (!App.Settings.Current.SidebarEnabled) return;
        foreach (var w in _pool.Values) w.RebuildModules();
    }

    /// <summary>
    /// 底排那几颗按钮（收起 / 常驻 / 位置复原 / 隐藏 / 打开应用）的显示开关变了
    /// （「侧边布局」页底部那五个开关，逐颗）。
    /// 跟换模块清单走同一条路：重排 + 重新量尺寸 —— 面板高矮、横条宽窄都跟着**可见颗数**变。
    /// </summary>
    public static void ApplyFooterSetting() => ApplyModules();

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
                // ⚠️ 「要先问一句」的动作（关全部）不能按老办法在 Run 之后就还焦点：
                //    侧边栏一失活，刚弹出来的确认面板就会被系统按"点了别处"关掉。
                //    Run 里已经按 AsksFirst 区分过了，这里只管把面板弹出来。
                var asks = TeachingActions.AsksFirst(m.Id);
                var hint = TeachingActions.Run(m.Id);

                if (hint is { Length: > 0 })
                {
                    // ① 边条**不许收**（失焦/空闲两条自动收起路都要压住，否则他还得重新点开）；
                    // ② 用**原生 Flyout** 弹确认（框架自带的描边/圆角/投影 + 点别处自动收）。
                    SuppressAutoCollapse = true;
                    var lines = hint.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    var title = lines.Length > 0 ? lines[0].Trim() : hint;
                    var body = lines.Length > 1 ? lines[1].Trim() : null;

                    var confirmed = false;

                    // ⚠️ 不只是"待确认"能走到这儿：出错时也会有提示（例如"截屏启动失败，看日志"）。
                    //    那种没什么可确认的，给个「知道了」就行 —— 别摆红色危险键、也别去执行什么。
                    ShowConfirmFlyout(b, title, body, asks ? "确定关闭" : "知道了", () =>
                    {
                        if (!asks) return;                 // 纯提示：按掉就完事
                        confirmed = true;
                        SuppressAutoCollapse = false;
                        TeachingActions.ConfirmPending(m.Id);
                        Touch();
                    },
                    onClosed: () =>
                    {
                        // 面板没了：只有"没真执行"（取消 / 超时 / 点别处）才把焦点还给用户原来的窗口，
                        // 别打断讲课。真执行了就不还 —— 那些窗口刚被关掉，还给谁都不对。
                        if (!confirmed) TeachingActions.RefocusPrevious();
                    },
                    dangerStyle: asks);

                    // ⚠️ 这里**绝不能**再调 RefocusPrevious()：那会立刻把侧边栏踢下台，
                    //    刚弹出的面板当场被关掉 —— 这正是"前台有窗口时按了没反应"的直接原因。
                }
                else
                {
                    HideConfirmFlyout();
                    Collapse();
                }
                return;
            }

            // 贴边展开的常驻面板（音量 / 屏幕亮度）：挨着边条长一栏，不跳窗口、不开新窗
            if (m.Kind == SidebarModuleKinds.Panel)
            {
                if (m.Id == "brightness") ToggleBrightness();
                else ToggleVolume();
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


    // ── 贴边面板（音量 / 屏幕亮度）──────────────────────────────────────────

    /// <summary>
    /// 「音量」模块的入口：**边条不收**（浮窗是挨着边条长出来的一栏，边条留着才像一个整体），
    /// 直接把主音量浮窗开在边条内侧；浮窗里再点「展开」看合成器。
    /// ⚠️ 必须先把自动收起压住：浮窗一显形就抢焦点，边条会以为自己"失焦"当场缩回去。
    /// </summary>
    private void ToggleVolume()
    {
        SuppressAutoCollapse = true;
        VolumeWindow.Toggle(VolumeWindow.Target.Volume);
    }

    /// <summary>
    /// 「屏幕亮度」模块：跟音量**同一个浮窗**（换成亮度那一栏：下面那颗键是自动亮度，没有二级浮窗）。
    /// 其余（挨着边条、边条不收）完全一样。
    /// </summary>
    private void ToggleBrightness()
    {
        SuppressAutoCollapse = true;
        VolumeWindow.Toggle(VolumeWindow.Target.Brightness);
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

    /// <summary>
    /// 在边条旁边弹一个原生确认 Flyout（红底确定键）。
    /// `onClosed`：面板消失后调（不管用户是按了红键、按取消、点别处还是超时自动收）。
    /// </summary>
    private void ShowConfirmFlyout(Button? anchor, string title, string? body, string okText, Action onConfirm,
                                   Action? onClosed = null, bool dangerStyle = true)
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

            // 纯提示（非危险动作）不给「取消」——那会和「知道了」语义重复
            if (dangerStyle)
            {
                var cancel = new Button { Content = "取消", FontSize = 13, MinWidth = 76, Padding = new Thickness(0, 6, 0, 6) };
                cancel.Click += (_, _) => HideConfirmFlyout();
                buttons.Children.Add(cancel);
            }

            var danger = new Button { Content = okText, FontSize = 13, MinWidth = 88, Padding = new Thickness(0, 6, 0, 6) };
            if (dangerStyle)
            {
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
            }
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
                try { onClosed?.Invoke(); } catch (Exception ex) { Log("确认面板收尾失败: " + ex.Message); }
            };
            danger.Click += (_, _) =>
            {
                // ⚠️ 顺序要紧：先把"确认"做完（onConfirm 会把 confirmed 立起来），再收面板。
                //    反过来的话，Closed 里的收尾会误当成"用户取消了"而去抢焦点。
                try { onConfirm(); } catch (Exception ex) { Log("确认动作失败: " + ex.Message); }
                flyout.Hide();
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
            SuppressAutoCollapse = false;            // 面板没弹出来 → 别把"禁止收起"这个旗一直立着
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
        _lastTargetX = _dragOrigin.X;
        _lastTargetY = _dragOrigin.Y;
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
                // 拿不到绝对坐标（这台机器上 GetPointerInfo 不认 WinUI 的 PointerId）→
                // 用「**真实窗口位置 + 手指相对读数**」做绝对推算：
                //     目标 = 当前窗口真实位置(GetWindowRect) + (当前读数 − 按下读数)
                // 手指按在原位 → 窗口不动；手指挪 N px → 窗口挪 N px。读数里混着的"窗口自己挪的那部分"
                // 正好被"当前真实位置"加回来，两项都是**当下**的绝对量。
                // ⚠️ 别再用"增量累加 + 上次移动量补偿"：AppWindow.Move 是异步落地的，读数滞后一帧时
                //    补偿会被重复计入（一次挪算两次），下一帧又往回找 —— 来回过冲，就是"拖起来癫痫"的根源。
                //    绝对推算不进累加器，一帧滞后只造成一次性的小偏差、下一帧自愈，不会震荡。
                LogSource("窗口相对·绝对推算");
                var scale = DpiScaleOf();
                var cur = e.GetCurrentPoint(Root).Position;
                var real = WindowRectNow();

                targetX = real.X + (int)Math.Round(cur.X * scale) - _pressOffsetPx;
                targetY = real.Y + (int)Math.Round(cur.Y * scale) - _pressOffsetPy;
                LogDrag(cur.X, cur.Y, targetX - real.X, targetY - real.Y, targetX, targetY);
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
            return;                                      // 手还没动够，先当点击
        }

        _dragging = true;

        // ⚠️ 双边模式：拖动**只沿竖直走** —— 横向锁在自己那条边，免得一拖就把"两边都有"拖成单边。
        //    上下方向由下面每帧 Move 之后的 FollowPartner 带给对面那条。
        if (IsDual) targetX = _dragOrigin.X;

        if (targetX == _lastTargetX && targetY == _lastTargetY) { e.Handled = true; return; }
        _lastTargetX = targetX;
        _lastTargetY = targetY;

        _appWindow.Move(new PointInt32(targetX, targetY));

        // 双边：对面那条实时跟到同一高度（拖动中不落盘，松手才存）
        if (IsDual) FollowPartner(targetY, _appWindow.Size.Height);

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
            Log($"样本{_dragSamples}: 读数=({curDipX:0.0},{curDipY:0.0}) 手偏=({dxF:0.0},{dyF:0.0}) 目标=({tx},{ty}) 实读=({real.X},{real.Y}) 自称=({said.X},{said.Y})");
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

    /// <summary>
    /// 松手时定位置：看窗口中心离哪条边最近就吸过去，沿边的位置按松手处记下来。
    ///   · 自由模式：左/右/上/下**四条边**都参与吸附，吸到哪条边存进 <c>SidebarFreeEdge</c>。
    ///   · 贴靠模式：**只吸左右两条** —— 松手时中心若更靠上下，也按左右就近归位（贴靠 = 左右模式）。
    ///     ⚠️ 双边模式（设置 = both）下**不许换边** —— 换边就等于把"两边都有"拆成单边了。
    /// </summary>
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

            string edge;
            if (IsDual)
            {
                edge = _edge;                            // 留在自己这条边，只挪上下
            }
            else
            {
                var dt = Math.Abs(cy - work.Y);
                var db = Math.Abs(work.Y + work.Height - cy);
                var min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
                edge = min == dl ? "left" : min == dr ? "right" : min == dt ? "top" : "bottom";

                if (IsFreeMode)
                {
                    App.Settings.Current.SidebarFreeEdge = edge;    // 四条边都收
                }
                else
                {
                    // 贴靠模式没有上下边：更靠上/下时按左右就近归位
                    if (edge is "top" or "bottom") edge = dl <= dr ? "left" : "right";
                    App.Settings.Current.SidebarEdge = edge;
                }
            }

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

            // ⚠️⚠️ 2026-09-28 修：单边模式换不了边（Nick 报的"拖了但固定不到上/下/左边"）。
            //   上次把单例改成"一条边一个实例"之后，_edge 变成**实例级只读**字段，
            //   这里只改设置再 SnapToSetting() 是没用的 —— 这条实例还按自己那条老边走，松手一贴就弹回原边。
            //   换边必须走 ApplySetting()：它按新设置重建实例集合（新的那条建出来、这条藏起来）再贴过去。
            if (edge != _edge)
            {
                Log("换边: " + _edge + " → " + edge);
                ApplySetting();
                return;
            }

            SnapToSetting();

            // 双边：比例共享，让对面那条也落回同一高度收尾
            Partner()?.SnapToSetting();
        }
        catch (Exception ex)
        {
            Log("换边失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 双边模式的位置联动：把自己当前的沿边位置算成 0~1 比例甩给对面那条，让它立刻贴到同一高度。
    ///
    /// 拖动中每帧都会调，所以**故意不落盘** —— 每帧写一次 settings.json 会顿。
    /// 落盘交给松手时的 <see cref="DockToNearestEdge"/> 统一做。
    /// 用 <c>verify: false</c> 挪对面：跟着走的东西要跟手，别一帧里读回位置纠偏好几次。
    /// </summary>
    private void FollowPartner(int myY, int myHeight)
    {
        var other = Partner();
        if (other is null) return;
        try
        {
            var work = DisplayArea.Primary.WorkArea;
            var free = Math.Max(0, work.Height - myHeight);
            App.Settings.Current.SidebarPosRatio =
                free <= 0 ? 0.5 : Math.Clamp((myY - work.Y) / (double)free, 0, 1);
            other.MoveToEdge(verify: false);
        }
        catch (Exception ex)
        {
            Log("联动对面失败: " + ex.Message);
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

        var size = PlannedSize();                        // 展开尺寸（此处 _expanded 已经置为 true）
        var finalPos = EdgePosition(size.Width, size.Height);
        _restRect = new Windows.Graphics.RectInt32(finalPos.X, finalPos.Y, size.Width, size.Height);   // 滑动期间 CurrentRect 返回它

        // 起步只推"半块"：保证窗口还有一半留在屏幕里。整块挪到屏幕外的窗口
        // DWM 常常不给它刷帧，那滑进来的第一帧会发虚（见方法注释）。
        var start = OutwardOffset(finalPos, (int)((IsFlat ? size.Height : size.Width) / 2.0));

        // 一次把"起点位置 + 展开尺寸"设下去。分成 Resize + Move 两次的话，
        // 中间那一帧会被系统画出来 —— 那就是"闪一下再滑"的来源（2026-09-26 优化）。
        try { _appWindow.MoveAndResize(new RectInt32(start.X, start.Y, size.Width, size.Height)); }
        catch (Exception ex) { Log("滑入落位失败: " + ex.Message); }

        TweenWindow(start, finalPos, 220);
    }

    /// <summary>
    /// 按帧把窗口从 from 挪到 to（缓出）。⚠️ 状态一变（收起/拖动/再展开）这一波就作废，绝不许它回头改窗口。
    ///
    /// ⚠️ 驱动方式（2026-10-01 修"展开/收起卡顿掉帧"）：**渲染循环（CompositionTarget.Rendering）**
    ///    而不是 16ms 的 DispatcherQueueTimer —— 那个计时器精度低、忙时会合并 tick，实测一合并就是
    ///    30fps 甚至更低，肉眼全是掉帧感。渲染回调每个 vsync 准时一帧，和 DWM 上屏节奏对齐。
    ///    挪窗口也换成直接 SetWindowPos（NOACTIVATE|NOZORDER|NOSIZE），比 AppWindow.Move 轻得多。
    /// </summary>
    private void TweenWindow(PointInt32 from, PointInt32 to, double ms, Action? done = null)
    {
        if (_appWindow is null) return;
        StopSlide();

        var epoch = ++_slideEpoch;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hwnd = WindowNative.GetWindowHandle(this);

        EventHandler<object> onFrame = (_, _) =>
        {
            if (epoch != _slideEpoch) return;                // 状态已经变了：这一波到此为止

            var t = Math.Clamp(sw.Elapsed.TotalMilliseconds / ms, 0, 1);
            var e = 1 - Math.Pow(1 - t, 3);              // ease-out cubic
            var x = (int)Math.Round(from.X + (to.X - from.X) * e);
            var y = (int)Math.Round(from.Y + (to.Y - from.Y) * e);
            _ = SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSizeFlag | SwpNoZOrderFlag | SwpNoActivateFlag);

            if (t < 1) return;
            StopSlide();
            _ = SetWindowPos(hwnd, IntPtr.Zero, to.X, to.Y, 0, 0, SwpNoSizeFlag | SwpNoZOrderFlag | SwpNoActivateFlag);
            try { _restRect = new Windows.Graphics.RectInt32(to.X, to.Y, _appWindow.Size.Width, _appWindow.Size.Height); } catch { }
            done?.Invoke();                                  // 滑完了再收尾（收起就是靠它）
        };
        _slideFrame = onFrame;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += onFrame;

        // 兜底：万一渲染回调停摆（窗口被藏、island 暂停渲染），动画不能卡死不收尾
        var watchdog = DispatcherQueue.CreateTimer();
        watchdog.Interval = TimeSpan.FromMilliseconds(ms + 400);
        watchdog.IsRepeating = false;
        watchdog.Tick += (_, _) =>
        {
            if (epoch != _slideEpoch) return;
            StopSlide();
            _ = SetWindowPos(hwnd, IntPtr.Zero, to.X, to.Y, 0, 0, SwpNoSizeFlag | SwpNoZOrderFlag | SwpNoActivateFlag);
            try { _restRect = new Windows.Graphics.RectInt32(to.X, to.Y, _appWindow.Size.Width, _appWindow.Size.Height); } catch { }
            done?.Invoke();
        };
        _slideWatchdog = watchdog;
        watchdog.Start();
    }

    /// <summary>收起：整个窗口往贴着的那条边**滑出去**（跟展开滑进来同一条路子，方向相反），滑完再真收。</summary>
    private bool SlideOutToEdge(Action done)
    {
        if (_appWindow is null) return false;
        StopSlide();

        var from = _appWindow.Position;
        var size = _appWindow.Size;

        // ⚠️ 必须滑到**整个窗口都在屏幕外**（推的距离 = 当前厚度 + 2px 余量）。
        //    以前只推"展开尺寸 − 收起尺寸"（92−20=72），滑完还剩 20dip 的展开态残片贴在屏幕边上，
        //    紧接着又被 Resize 成抓手 —— 肉眼看就是"没滑出去就突然变身"，很出戏（2026-09-26 修）。
        //    现在滑到底时屏幕边缘是干净的，"变身"那一下（92×450 → 20×110）用户在屏幕外看不到，
        //    抓手再淡入接上，就顺了。
        var thickness = IsFlat ? size.Height : size.Width;
        var to = OutwardOffset(from, thickness + 2);
        TweenWindow(from, to, 240, done);
        return true;
    }

    private void StopSlide()
    {
        _slideEpoch++;                                    // 让在跑的那一波作废（关键：竞态的根治）
        if (_slideFrame is not null)
        {
            try { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= _slideFrame; } catch { }
            _slideFrame = null;
        }
        _slideWatchdog?.Stop();
        _slideWatchdog = null;
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
            FooterStack.Spacing = flat ? FooterFlatSpacingDip : 2;
            FooterStack.HorizontalAlignment = flat ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;

            // 底部那排按钮是**逐颗**开关（「侧边布局」页底部那五个开关，key 见 SidebarFooterKeys）。
            // 关掉的那颗用 Collapsed —— StackPanel 不吃折叠子项，整排自己跟着收；一颗都不剩时连分隔线一起收。
            // ⚠️ 但**别靠 Visibility 反推高度**：尺寸那几处都有独立的"可见颗数"分支
            //    （<see cref="PlannedSize"/> / <see cref="ExpandedLimits"/> / <see cref="ApplyScrollLimit"/>），
            //    靠 Visibility 算会在"重排前先量尺寸"的顺序上算错。
            var hiddenFooter = App.Settings.Current.SidebarFooterHidden ?? Array.Empty<string>();
            foreach (var (key, btn) in FooterItems())
                btn.Visibility = hiddenFooter.Contains(key) ? Visibility.Collapsed : Visibility.Visible;

            var footerCount = VisibleFooterCount();
            FooterStack.Visibility = footerCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            Sep.Visibility = footerCount > 0 ? Visibility.Visible : Visibility.Collapsed;

            // 贴上下边时给按钮留出宽度，别挤成一坨
            foreach (var b in _toolButtons)
            {
                b.MinWidth = flat ? 64 : 0;
                b.MinHeight = flat ? 48 : 52;
                b.Padding = flat ? new Thickness(4, 6, 4, 6) : new Thickness(0, 8, 0, 8);
            }
            foreach (var (_, b) in FooterItems())
            {
                b.MinWidth = flat ? FooterFlatButtonWidthDip : 0;
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
                // 箭头 = "点了会往哪边收"：贴着哪条边就朝哪边收
                var dir = edge;
                _foldIcon.Glyph = dir switch
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
    /// 底下**五个**按钮（收起 / 常驻 / 位置复原 / 隐藏 / 打开应用）的内容：
    /// 贴左右边是**竖条** → 图标在上、文字在下（跟工具块一个样式）；
    /// 贴上/下边是**横条**（面板只有 112 高）→ 左图标、右文字，竖排会被裁掉。
    /// ⚠️ 「打开应用」那颗是**图片图标**（软件自己的图标），走 SetFooterImageButton，别跟字形混。
    /// </summary>
    private void ApplyFooterContent(bool flat)
    {
        SetFooterButton(FoldButton, "\uE76C", "收起", flat, out _foldIcon);
        SetFooterButton(PinButton, "\uE718", "常驻", flat, out _pinIcon);
        SetFooterButton(ResetButton, "\uE777", "位置复原", flat, out _);
        SetFooterButton(HideButton, "\uED1A", "隐藏", flat, out _);
        SetFooterImageButton(OpenAppButton, "打开应用", flat);
        UpdatePinVisual();
    }

    /// <summary>
    /// 底排那颗「打开应用」：版式跟 SetFooterButton 一模一样，只是把字形换成**软件自己的图标**
    /// （Assets\AppIcon-512.png，内嵌资源；Nick 2026-09-26 要求：这颗不要用别的图标）。
    /// </summary>
    private static void SetFooterImageButton(Button b, string label, bool flat)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = flat ? 11 : 10.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        var img = new Image
        {
            Source = AppIconImage(),
            Width = flat ? 14 : 17,
            Height = flat ? 14 : 17,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        if (flat)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            row.Children.Add(img);
            row.Children.Add(text);
            b.Content = row;
        }
        else
        {
            text.HorizontalAlignment = HorizontalAlignment.Center;
            var col = new StackPanel { Spacing = 2 };
            col.Children.Add(img);
            col.Children.Add(text);
            b.Content = col;
        }
    }

    private static ImageSource? _appIconImage;

    /// <summary>软件自己的图标（内嵌 AppIcon-512.png，只解一次、缓存住）。解不开就返回 null（那颗按钮只剩文字，不会崩）。</summary>
    private static ImageSource? AppIconImage()
    {
        if (_appIconImage is not null) return _appIconImage;
        try
        {
            var path = EmbeddedAssets.ExtractToCache("AppIcon-512.png", "AppIcon-512.png");
            if (string.IsNullOrEmpty(path)) return null;
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            bmp.DecodePixelWidth = 64;          // 底排就 17px 大，解 64 足够，省内存
            bmp.UriSource = new Uri(path);
            _appIconImage = bmp;
        }
        catch (Exception ex)
        {
            Log("解应用图标失败: " + ex.Message);
        }
        return _appIconImage;
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

    /// <summary>
    /// 底排五颗按钮 + 各自的设置 key（顺序 = 在侧边栏上的先后）。
    /// ⚠️ key 是**存档格式**的一部分（<see cref="SidebarFooterKeys"/>），别随手改字面量。
    /// </summary>
    private (string Key, Button Btn)[] FooterItems() => new (string, Button)[]
    {
        (SidebarFooterKeys.Fold, FoldButton),
        (SidebarFooterKeys.Pin, PinButton),
        (SidebarFooterKeys.Reset, ResetButton),
        (SidebarFooterKeys.Hide, HideButton),
        (SidebarFooterKeys.OpenApp, OpenAppButton),
    };

    /// <summary>
    /// 底排当前**可见**几颗（0~5）—— 按设置里"关掉了哪几颗"过滤。
    /// 尺寸处处用它，⛔ 别写死 5：逐颗开关之后，少一颗面板就得少一截。
    /// </summary>
    private static int VisibleFooterCount()
    {
        var hidden = App.Settings.Current.SidebarFooterHidden ?? Array.Empty<string>();
        return SidebarFooterKeys.All.Count(k => !hidden.Contains(k));
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

    /// <summary>当前状态下窗口该多大（dip → 物理像素）。尺寸会跟着模块数量走。</summary>
    private SizeInt32 PlannedSize()
    {
        var scale = Scale();
        // 模块数量：按钮 52 + 间距 3；竖着时面板高度 = 200 + 55×个数（少于 4 个也不缩太多，免得看着空）
        var count = Math.Max(1, _toolButtons.Count);
        // 底排现在能看见几颗（逐颗开关，见 VisibleFooterCount）：尺寸处处按它算，
        // 少一颗竖条矮一截、横条窄一截，一颗不剩时横条还会从两行变一行（薄一截）。
        var footerCount = VisibleFooterCount();
        int dipW, dipH;
        if (_expanded)
        {
            var lim = ExpandedLimits();
            if (IsFlat)
            {
                // 上/下边：竖着两行 —— 第一行工具、第二行底排按钮（一颗不剩就只剩第一行）。
                // 宽度取**两行里更宽的那行**：底排是固定几颗（不被裁的硬要求），
                // 工具行多到放不下时由 ToolsScroll 横向滚动兜底。
                var toolsRow = count * 64 + (count - 1) * 8 + FlatRowPadDip;
                var footerRow = footerCount > 0
                    ? footerCount * FooterFlatButtonWidthDip
                      + (footerCount - 1) * FooterFlatSpacingDip + FlatRowPadDip
                    : 0;

                dipW = (int)Math.Min(Math.Max(Math.Max(PanelLengthFlatDip, toolsRow), footerRow), lim.W);
                dipH = footerCount > 0 ? PanelThicknessFlatDip : PanelThicknessFlatBareDip;
            }
            else
            {
                dipW = PanelThicknessDip;
                // 底排最多五颗（收起/常驻/位置复原/隐藏/打开应用），增量按可见颗数摊（见 FooterDipFor）
                dipH = (int)Math.Min(Math.Max(PanelLengthDip, 200 + 55 * count) + FooterDipFor(footerCount), lim.H);
            }
        }
        else
        {
            return CollapsedSize();
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
        var footerCount = VisibleFooterCount();

        if (IsFlat)
            return (Math.Min(workW, Math.Max(PanelLengthFlatDip, workW * 0.92)),
                    footerCount > 0 ? PanelThicknessFlatDip : PanelThicknessFlatBareDip);

        return (PanelThicknessDip,
                Math.Min(workH, Math.Max(PanelLengthDip + FooterDipFor(footerCount), workH * 0.85)));
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
                // 底排：可见 n 颗 → n×44 + (n−1)×2；一颗都不显示时这段是 0（工具区能多吃掉这份高度）
                var fn = VisibleFooterCount();
                var footer = fn > 0 ? fn * 44 + (fn - 1) * 2 : 0;
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

    /// <summary>收起态的窗口尺寸（跟 PlannedSize 的收起分支同一套算法，抽出来给动画算落位用）。</summary>
    private SizeInt32 CollapsedSize()
    {
        var scale = Scale();
        var dipW = IsFlat ? CollapsedLengthDip : CollapsedThicknessDip;
        var dipH = IsFlat ? CollapsedThicknessDip : CollapsedLengthDip;
        return new SizeInt32((int)Math.Round(dipW * scale), (int)Math.Round(dipH * scale));
    }

    /// <summary>按当前贴的边算"贴边位置"（**纯计算，不动窗口**）。参数是窗口按哪套尺寸算。</summary>
    private PointInt32 EdgePosition(int width, int height)
    {
        var work = DisplayArea.Primary.WorkArea;

        var edge = Edge;
        var flat = edge is "top" or "bottom";

        var alongLen = flat ? work.Width : work.Height;
        var myLen = flat ? width : height;
        var free = Math.Max(0, alongLen - myLen);

        var ratio = App.Settings.Current.SidebarPosRatio;
        var offset = ratio < 0 ? free / 2.0 : Math.Clamp(ratio * free, 0, free);

        return edge switch
        {
            "left" => new PointInt32(work.X, (int)Math.Round(work.Y + offset)),
            "top" => new PointInt32((int)Math.Round(work.X + offset), work.Y),
            "bottom" => new PointInt32((int)Math.Round(work.X + offset), work.Y + work.Height - height),
            _ => new PointInt32(work.X + work.Width - width, (int)Math.Round(work.Y + offset))
        };
    }

    /// <summary>
    /// 把位置往"屏幕外"方向推开 dist 像素（贴右往右推、贴左往左推，上/下同理）。
    /// 纯计算，不动窗口；距离由调用方按用途给（滑入起步用半块、滑出收尾用整个厚度）。
    /// </summary>
    private PointInt32 OutwardOffset(PointInt32 p, int dist)
    {
        if (dist < 1) dist = 1;

        return Edge switch
        {
            "left" => new PointInt32(p.X - dist, p.Y),
            "top" => new PointInt32(p.X, p.Y - dist),
            "bottom" => new PointInt32(p.X, p.Y + dist),
            _ => new PointInt32(p.X + dist, p.Y)
        };
    }

    /// <summary>贴到设置里那条边；沿边的位置按 SidebarPosRatio（&lt;0 = 居中）。</summary>
    /// <param name="verify">
    /// false = 只挪一次，**不读回位置做纠偏**。纠偏最多会连挪 4 次窗口，放在动画前会把头几帧挤掉，
    /// 所以动画路径上用它（落位精度由动画自己的最后一帧保证）。
    /// </param>
    private void MoveToEdge(bool verify = true)
    {
        if (_appWindow is null) return;
        try
        {
            // ⚠️ 用自己的目标尺寸算，别读 _appWindow.Size —— Resize 刚调完它还没更新，会按老尺寸贴边
            var size = PlannedSize();
            var pos = EdgePosition(size.Width, size.Height);
            var x = pos.X;
            var y = pos.Y;

            if (!verify)
            {
                _appWindow.Move(pos);
                return;
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

    // ── 滑动动画用的窗口移动（比 AppWindow.Move 轻，见 TweenWindow）──
    private const uint SwpNoSizeFlag = 0x0001;
    private const uint SwpNoZOrderFlag = 0x0004;
    private const uint SwpNoActivateFlag = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static void Log(string message)
    {
        try
        {
            Core.AppLog.Info("sidebar", message);
        }
        catch { }
    }
}
