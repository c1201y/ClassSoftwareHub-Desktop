using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Views;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 「侧边布局」：搭积木那样拼侧边栏。
/// 左 = 模块库（卡片），右 = 按真实侧边栏比例画的预览条。
/// 加点：把卡片拖进右边 / 点一下卡片（放到末尾）；排序：在右边拖；移除：拖出右边松手，或格子上的 ×。
/// 拖动是**自己画的**（Pointer 捕获 + 浮在上面的替身卡片），所以卡片会跟着指针走、
/// 预览里的模块会实时"让开位置"，松手才落位。结果存在 AppSettings.SidebarModuleIds，改完立刻生效。
///
/// ⚠️ 触摸和鼠标在这里是**两套启动规则**（这是必须的，别为了"统一"改回去）：
///    · 鼠标 = 按下即拖。鼠标没有"上下滑页面"这个手势，按下就是想拖东西。
///    · 触摸 = 先按住 400ms 再拖。手指落在卡片上时，人可能只是想滚页面；
///      如果我们一按下就抢走指针，页面就再也滚不动了（只能在空白处滑）—— 群里反馈过这个。
///      位移超过 12px 更是直接判定为"想滚"，把手势原封不动还给 ScrollViewer。
/// </summary>
public sealed partial class SidebarLayoutPage : Page
{
    /// <summary>指针挪过这么多像素才算"在拖"，否则当点击。</summary>
    private const double DragSlop = 4;

    /// <summary>让位开的那条缝 = 格子高 56 + 间距 3。</summary>
    private const double GapDip = 59;

    /// <summary>触摸按下后，按住这么久才可能进入拖动模式。太短会误吞滚动，太长会觉得"点了没反应"。</summary>
    private const int TouchHoldMs = 400;

    /// <summary>闸门期间手指挪过这么多像素，就认定是"想滚页面"，直接放弃拖动。</summary>
    private const double TouchSlop = 12;

    /// <summary>
    /// 接管前额外要求：最近这么多毫秒内手指是**静止**的。
    /// ⚠️ 这条不是"手感微调"，是正确性所需：让 ScrollViewer 松手（CancelDirectManipulations）
    ///    是在 DirectManipulation 线程上做的，落地要几十毫秒。如果手指在那一瞬间正好在滑，
    ///    DM 会先一步把这个触点判成"开始平移"，我们的捕获当场被抢走 —— 拖拽还是断。
    ///    所以等手指真的定住了再接管：定住期间那几十毫秒足够它松手落地。
    /// </summary>
    private const int TouchStillMs = 150;

    /// <summary>"手指还在动"的判定容差：位移比之前多出这么多像素才算动了（忽略触摸自身的抖动）。</summary>
    private const double StillSlop = 3;

    /// <summary>
    /// 「等定住」的兜底上限：按够这么久就不再等了。
    /// 指尖慢慢漂移时"定住"可能永远不成立，但慢速漂移也抢不过我们 —— 几十毫秒里挪不到 1px。
    /// </summary>
    private const int TouchHoldMaxMs = 900;

    /// <summary>闸门检查的节拍。</summary>
    private const int GateTickMs = 60;

    /// <summary>ghost 收缩 / 展开的时长（卡片尺寸 ↔ 预览格子尺寸）。</summary>
    private const int MorphMs = 190;

    /// <summary>↑/↓ 交换动画的时长。</summary>
    private const int SwapMs = 250;

    /// <summary>让位（行跟着空档挪）的时长。跟空档的上下出现同一档，看着才是"一起动"。</summary>
    private const int SlotMs = 180;

    /// <summary>松手后那张卡的收场时长：取消时飞回原位、移除时飞向模块库。</summary>
    private const int FlightMs = 240;

    /// <summary>
    /// 落位之后的"收势"时长：先（可选地）轻微收缩一下，同时蓝描边淡走 / 影子收回 / 透明度补满，
    /// 走完才撤掉替身卡。收缩与否由「动画方案」那张卡决定（见 <see cref="_dropDip"/>）。
    /// ⚠️ 这一段不是"淡出"—— 飞行全程完全不透明、不做淡出（Nick 明确要求过），退场的只有"提起来"的装饰。
    /// </summary>
    private const int SettleMs = 190;

    /// <summary>
    /// 「轻落一下」那一下的收缩幅度（<see cref="SettleGhost"/>）。1 = 不缩。
    /// ⚠️ 别压太狠：收缩期间四周会露出底下那张**已经变亮的真卡**，压到 0.95 以下就明显穿帮了。
    ///    0.972 在库卡片（约 400 宽）上露出约 5px，正好是"按回桌面"而不是"缩小一圈"。
    /// </summary>
    private const double DipScale = 0.972;

    /// <summary>收缩压到最低的那个时间点（占收势总时长的比例）。之后复位并让装饰退场。</summary>
    private const double DipAt = 0.38;

    /// <summary>拿起卡片时放大到这个比例（"浮起来"的手感），收场时回到 1。</summary>
    private const double LiftScale = 1.035;

    /// <summary>预览格子的尺寸，和真侧边栏一致。</summary>
    private const double TileW = 96;
    private const double TileH = 56;

    /// <summary>当前拼好的模块 id，顺序 = 侧边栏上从上到下。</summary>
    private readonly List<string> _ids = new();

    /// <summary>预览条里的模块格子（顺序同上）。⚠️ 拖动过程中**这个列表不变** ——
    /// 让位不靠真实布局，靠每行的 TranslateY，所以顺序一乱就对不上了。</summary>
    private readonly List<FrameworkElement> _tiles = new();

    private string? _dragId;                // 正被按住的模块
    private bool _dragFromPreview;          // 是从预览里拖的，还是从库里拖的
    private bool _dragging;                 // 已越过阈值，真在拖了
    private uint _pointerId;
    private Point _pressInRoot;
    private FrameworkElement? _pressSource;
    private Border? _ghost;                 // 跟着指针跑的那张卡
    private Microsoft.UI.Composition.Visual? _ghostVisual;  // 它的"浮起"缩放（Composition 层，见 PopGhost）
    private Border? _ghostRing;             // 替身卡上那圈"提起来"的蓝描边（落位时单独淡走，见 SettleGhost）
    private double _dropDip = DipScale;     // 落位收缩幅度：1 = 平滑收势，DipScale = 轻落一下（「动画方案」卡切）
    private Border? _hole;                  // 浮层里画的那个"空档"提示框
    private Border? _pad;                   // 末尾的垫片：空档打开时它长一格高，预览条跟着变长
    private FrameworkElement? _hiddenRow;   // 从预览里拿起来后暂时收起来的那一行
    private int _holeSlot = -1;             // 空档开在第几个可见位置（-1 = 合上）
    private int _dragSrcIndex = -1;         // 拿起来的那一行在 _tiles 里的下标（-1 = 从模块库拖的）
    private double _rowsTop;                // 第一行在 PreviewPanel 里的 Y（按下那一刻量的）
    private bool _swapping;                 // ↑/↓ 的滑动动画正在进行（这段时间别再点）
    private bool _footerSync;               // 正在把设置刷进底排的那五个开关（此时 Toggled 是"回声"，别当用户拨的）
    private bool _toolSync;                 // 同上，但针对「常用工具」与「截图」那几张卡（开关 / 下拉的"回声"）
    private bool _edgeRebuild;              // 「贴在哪条边」下拉正在按模式重建选项（期间忽略 SelectionChanged）
    private double _pageScrollOffset;       // 拖起来之前的滚动位置（拖的时候要把整页滚动锁掉）
    private double _maxMove;                // 按下之后指针走过的最大直线距离（用来看"这是想滚还是想拖"）

    /// <summary>
    /// 进页面后把滚动位置拉回顶部的定时器。
    /// ⚠️ 必须存成字段 —— DispatcherQueueTimer 被 GC 收走就不会触发（本项目踩过）。
    /// </summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _settleTimer;

    // ── 触摸闸门 ──────────────────────────────────────────────────────────

    private bool _isTouch;                  // 这一轮按下是不是手指
    private bool _armed;                    // 指针已被我们接管（真能拖了）
    private bool _ending;                   // 正在自己收尾（此时 CaptureLost 是预期内的，别当"被打断"）
    private bool _latchScroll;              // 被意外打断后，滚动锁一直扣到手指抬起为止（别让页面顺着手跳）
    private Pointer? _heldPointer;          // 留住 Pointer，闸门到点时才拿它去 CapturePointer
    private DispatcherQueueTimer? _holdTimer;
    private DateTime _downAt;               // 按下的时刻
    private DateTime _lastMoveAt;           // 手指最后一次"真的动了"的时刻（闸门靠它判断"定住没有"）

    // ── ghost 的形状（拖动时会从"库卡片"大小平滑变成"预览格子"大小）────────

    private double _srcW, _srcH;            // 按下时源元素的实际尺寸
    private Point _grabRatio;               // 抓点在源元素内的相对位置（0~1），换尺寸时按它保持跟手
    private Point _pointerInLayer;          // 最近一次指针位置（DragLayer 坐标）
    private FrameworkElement? _ghostCard;   // ghost 里"库卡片"那一层
    private FrameworkElement? _ghostTile;   // ghost 里"预览格子"那一层（两层交叉淡入淡出）
    private bool _ghostTileMode;            // ghost 现在是不是格子形态
    private bool _flying;                   // 松手后那张卡正在飞（这段时间别让新的拖动插进来）
    private bool _ghostFrameHooked;         // 是否已订阅每帧回调

    public SidebarLayoutPage()
    {
        InitializeComponent();
        LoadFromSettings();

        // ⚠️ 挂在根上、并且 handledEventsToo: true —— 触摸闸门要判断"手指到底动没动"，
        //    而那时我们**还没捕获指针**，卡片自己的 PointerMoved 在手指滑出去之后就收不到了。
        //    根节点覆盖整页，能稳稳接住这个事件。
        WorkspaceRoot.AddHandler(PointerMovedEvent, new PointerEventHandler(OnRootPointerMoved), true);

        // 被意外打断时滚动锁会一直扣着（见 EndDrag），在根上接"手指抬起 / 下一次按下"来放锁。
        // 同样要 handledEventsToo：抬手那一刻事件落点不一定是卡片。
        WorkspaceRoot.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnRootPointerReleased), true);
        WorkspaceRoot.AddHandler(PointerPressedEvent, new PointerEventHandler(OnRootPointerPressed), true);

        // ⚠️ 别在构造函数里就画：那会儿页面还没挂到窗口的主题树上，ActualTheme 还是 Default，
        //    颜色会按"系统主题"画（= 你深色系统 → 画成深色，看着像没做浅色适配）。
        //    等挂上去（Loaded）再画，主题已经确定；以后主题一变（ActualThemeChanged）也重画。
        Loaded += (_, _) =>
        {
            Services.ThemeBrush.Probe(this, "SidebarLayoutPage.Loaded");
            InitToolSettings();
            Refresh();
            SettleScrollToTop();
        };
        ActualThemeChanged += (_, _) => Refresh();

        // 定时器 / 逐帧订阅都别留着：这一页 NavigationCacheMode=Disabled，走了就没人管它了
        Unloaded += (_, _) =>
        {
            _settleTimer?.Stop();
            _settleTimer = null;
            _tweens.Clear();            // 挂在旧行身上的补间一并作废
            UnhookTweenFrame();
            StopTweenWatch();
        };
    }

    // ── 数据 ──────────────────────────────────────────────────────────────

    private void LoadFromSettings()
    {
        _ids.Clear();
        foreach (var id in App.Settings.Current.SidebarModuleIds ?? Array.Empty<string>())
        {
            // 认不出来的 id（老设置里存了已删除的模块）丢掉；重复的也丢掉
            if (SidebarModules.Find(id) is not null && !_ids.Contains(id)) _ids.Add(id);
        }
    }

    private void Save()
    {
        App.Settings.Current.SidebarModuleIds = _ids.ToArray();
        App.Settings.Save();
        ToolSidebarWindow.ApplyModules();      // 真侧边栏立刻跟着变
    }

    private void Refresh()
    {
        BuildLibrary();
        BuildPreview();

        if (SidebarButton is not null)
            SidebarButton.Content = ToolSidebarWindow.IsSidebarVisible ? "隐藏侧边栏" : "显示侧边栏";

        SyncFooterToggles();
        SyncToolSettings();
    }

    /// <summary>
    /// 「常用工具」那四张卡进页面时的**一次性**初始化（2026-10-01 从设置页搬来的那四项）：
    /// 先修掉设置里的非法值，再按模式把「贴在哪条边」的选项建出来。
    /// ⚠️ 只在 Loaded 跑：下拉重建会引发 SelectionChanged，页面还没挂上主题树时跑这串纯属浪费。
    /// </summary>
    private void InitToolSettings()
    {
        var s = App.Settings.Current;

        // 贴靠模式只认左右两条边（Nick 2026-09-28 定：贴靠 = 左右模式）。
        // 老设置里若留着 top/bottom，这里归到右边 —— 想贴上下边需切到自由模式。
        if (s.SidebarEdge is not ("left" or "both" or "right"))
        {
            s.SidebarEdge = "right";
            App.Settings.Save();
        }

        // 自由模式：左 / 右 / 上 / 下四条边
        if (s.SidebarFreeEdge is not ("left" or "right" or "top" or "bottom"))
        {
            s.SidebarFreeEdge = "right";
            App.Settings.Save();
        }

        RebuildEdgeCombo();
        UpdateSidebarHints();
    }

    /// <summary>
    /// 把「常用工具」与「截图」那几张卡的值从设置刷过来。
    /// ⚠️ 跟 <see cref="SyncFooterToggles"/> 同一个道理：赋值会触发 Toggled / SelectionChanged，
    ///    全程压着 <c>_toolSync</c> 挡住那声"回声"，否则每次进这一页都会白写一遍设置。
    /// ⚠️ 只刷值，**不重建**「贴在哪条边」的选项 —— 那玩意儿重建一次下拉会闪一下，
    ///    只在进页面（<see cref="InitToolSettings"/>）和切换放置模式时做。
    /// </summary>
    private void SyncToolSettings()
    {
        var s = App.Settings.Current;

        _toolSync = true;
        try
        {
            if (PaletteTopSwitch is not null) PaletteTopSwitch.IsOn = s.PaletteOnTop;
            if (SidebarSwitch is not null) SidebarSwitch.IsOn = s.SidebarEnabled;
            if (SidebarModeCombo is not null)
            {
                var index = s.SidebarMode == "free" ? 1 : 0;
                if (SidebarModeCombo.SelectedIndex != index) SidebarModeCombo.SelectedIndex = index;
            }

            // 「动画方案」：拖放卡片的落位收尾用哪种（两种都保留，Nick 2026-10-01 定）
            _dropDip = DropDipScale(s.SidebarDropAnim);
            if (DropAnimCombo is not null)
            {
                var index = _dropDip < 0.999 ? 1 : 0;
                if (DropAnimCombo.SelectedIndex != index) DropAnimCombo.SelectedIndex = index;
            }

            // 「截图」那两张卡（2026-10-01 从设置页搬来，同一套"回声"处理）
            if (ShotAutoSaveSwitch is not null) ShotAutoSaveSwitch.IsOn = s.ShotAutoSave;
            if (ShotDirText is not null) RefreshShotDir();
        }
        finally { _toolSync = false; }
    }

    /// <summary>
    /// 把设置里的"底下那排按钮哪几颗被关了"刷进五个开关。
    /// ⚠️ 赋值会触发 Toggled，全程压着 <c>_footerSync</c> 挡住那声"回声"，否则每次进这一页都会
    ///    白写一遍设置、还顺手重建一遍真侧边栏（页面还没挂到窗口上时更没必要）。
    /// </summary>
    private void SyncFooterToggles()
    {
        var hidden = App.Settings.Current.SidebarFooterHidden ?? Array.Empty<string>();

        _footerSync = true;
        try
        {
            // 设置里记的是"关掉的"，所以这里取反 = 开关的"显示"
            if (FoldToggle is not null) FoldToggle.IsOn = !hidden.Contains(SidebarFooterKeys.Fold);
            if (PinToggle is not null) PinToggle.IsOn = !hidden.Contains(SidebarFooterKeys.Pin);
            if (ResetToggle is not null) ResetToggle.IsOn = !hidden.Contains(SidebarFooterKeys.Reset);
            if (HideToggle is not null) HideToggle.IsOn = !hidden.Contains(SidebarFooterKeys.Hide);
            if (OpenAppToggle is not null) OpenAppToggle.IsOn = !hidden.Contains(SidebarFooterKeys.OpenApp);
        }
        finally { _footerSync = false; }
    }

    /// <summary>
    /// 进页面固定从顶部开始看。
    /// ⚠️ 跟设置页同一个毛病（见 <c>SettingsPage.OnNavigatedTo</c> 里那段）：初始化会给下拉框设
    ///    SelectedIndex、还会把模块库和预览整个重建，两件事都会让 ScrollViewer 的锚点漂走 ——
    ///    实测一进页就停在模块库中段，连标题都被滚没了。等布局稳一拍再拉回顶部；
    ///    别在 Loaded 里同步做，那会儿锚点还没定下来，拉了也白拉。
    /// </summary>
    private void SettleScrollToTop()
    {
        _settleTimer ??= DispatcherQueue.CreateTimer();
        _settleTimer.Interval = TimeSpan.FromMilliseconds(220);
        _settleTimer.IsRepeating = false;
        _settleTimer.Tick -= OnSettleTick;
        _settleTimer.Tick += OnSettleTick;
        _settleTimer.Start();
    }

    private void OnSettleTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        PageScroll.ChangeView(null, 0, null, true);
    }

    private void AddToEnd(string id)
    {
        if (_ids.Contains(id)) return;
        _ids.Add(id);
        Save();
        Refresh();
    }

    // ── 主题取色 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 拿主题色刷子。⚠️ 键名是**运行时**校验的：这版 WinUI 里没有 AccentFillColor* / CardBackgroundFillColorDefault
    /// 这些「颜色」键（只有 ...Brush 刷子键）—— 之前 XAML 解析就是崩在这上面。这里多找几层 + 兜底，绝不抛异常。
    /// </summary>
    /// <summary>
    /// 拿主题色刷子（键名 -> WinUI 刷子键 -> **按这棵树**的主题取色）。
    /// ⚠️ 以前第一句是 `Application.Current.Resources.TryGetValue` —— 那个查的是**应用级**主题（跟系统走），
    /// 浅色界面 + 深色系统时拿到的还是深色那套，于是这一页"没做浅色适配"。现在一律走 ThemeBrush。
    /// </summary>
    private Brush Br(string shortName)
    {
        var key = shortName switch
        {
            "ModCardBg" => "CardBackgroundFillColorDefaultBrush",
            "ModCardStroke" => "CardStrokeColorDefaultBrush",
            "ModHoverBg" => "SubtleFillColorSecondaryBrush",
            "ModAccent" => "AccentFillColorDefaultBrush",
            "ModTextDim" => "TextFillColorSecondaryBrush",
            _ => "TextFillColorPrimaryBrush",
        };

        return Services.ThemeBrush.Get(this, key);
    }

    /// <summary>应用资源 + 合并字典（XamlControlsResources 在这一层，主题色都在它的 ThemeDictionaries 里）。</summary>
    private static IEnumerable<ResourceDictionary> ResourceDictionaries()
    {
        var root = Application.Current.Resources;
        yield return root;

        foreach (var md in root.MergedDictionaries)
        {
            yield return md;
            foreach (var nested in md.MergedDictionaries) yield return nested;
        }
    }

    private bool Dark => ActualTheme == ElementTheme.Dark
        || (ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);

    private static Color AccentColor() =>
        new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);

    // ── 左：模块库 ────────────────────────────────────────────────────────

    private void BuildLibrary()
    {
        LibraryPanel.Children.Clear();

        Grid? row = null;
        var col = 0;
        foreach (var m in SidebarModules.All)
        {
            if (col == 0)
            {
                row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                LibraryPanel.Children.Add(row);
            }

            var card = BuildLibraryCard(m, _ids.Contains(m.Id));
            Grid.SetColumn(card, col);
            row!.Children.Add(card);
            col = col == 0 ? 1 : 0;
        }
    }

    /// <summary>库里的卡片。可用的能拖能点；已经在侧边栏里的变灰、只做展示。</summary>
    private Border BuildLibraryCard(SidebarModule m, bool used)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = m.Name,
            FontSize = 14.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Br("ModText")
        });
        text.Children.Add(new TextBlock
        {
            Text = used ? "已在侧边栏中" : m.Hint,
            FontSize = 11.5,
            Opacity = 0.65,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Br("ModTextDim")
        });

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new FontIcon
        {
            Glyph = m.Glyph,
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var badge = new FontIcon
        {
            Glyph = used ? "\uE73E" : "\uE710",        // 已加 = 对勾 / 未加 = 加号
            FontSize = 13,
            Opacity = used ? 0.55 : 0.8,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(badge, 2);
        grid.Children.Add(badge);

        var card = new Border
        {
            Height = 72,
            Padding = new Thickness(14, 0, 14, 0),
            Background = Br("ModCardBg"),
            BorderBrush = Br("ModCardStroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Tag = m.Id,
            Opacity = used ? 0.5 : 1,
            Child = grid
        };
        ToolTipService.SetToolTip(card, used ? $"{m.Name}（已在侧边栏中）" : $"{m.Name}：单击加入侧边栏，亦可拖入右侧");

        if (used) return card;                          // 已经在了：只展示，不加不拖

        card.PointerEntered += (_, _) => card.BorderBrush = Br("ModAccent");
        card.PointerExited += (_, _) => card.BorderBrush = Br("ModCardStroke");
        card.PointerPressed += (s, e) => BeginPress(s, e, m.Id, false);
        card.PointerMoved += OnPointerMoved;
        card.PointerReleased += OnPointerReleased;
        card.PointerCaptureLost += (_, _) => CancelDrag();
        // 触点被系统收走（DirectManipulation 接管、窗口失焦、设备状态变化…）：同样要收尾，
        // 否则 ghost 会一直挂在屏幕上。
        card.PointerCanceled += (_, _) => CancelDrag();
        return card;
    }

    // ── 右：侧边栏预览 ────────────────────────────────────────────────────

    private void BuildPreview()
    {
        PreviewPanel.Children.Clear();
        HoleLayer.Children.Clear();
        _tiles.Clear();
        _hole = null;
        _tweens.Clear();        // 旧行要整个换掉，挂在它们身上的补间一并作废
        UnhookTweenFrame();     // 列表空了就不用再逐帧跑了
        StopTweenWatch();

        // 顶部那条"常用工具"标题（照侧边栏的样子来）
        PreviewPanel.Children.Add(new TextBlock
        {
            Text = "常用工具",
            FontSize = 10,
            Opacity = 0.55,
            Width = 96,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 2)
        });

        foreach (var id in _ids)
        {
            if (SidebarModules.Find(id) is { } m) PreviewPanel.Children.Add(BuildRow(m));
        }

        if (_ids.Count == 0)
        {
            PreviewPanel.Children.Add(new TextBlock
            {
                Text = "将左侧\n卡片拖入此处",
                FontSize = 10.5,
                Opacity = 0.5,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Width = 96,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(4, 10, 4, 10)
            });
        }

        // 末尾垫片：默认 0 高、全透明，只在空档打开时长到一格高。
        // 它负责让预览条跟着变长 —— 不然行滑下去的部分会伸出边框外面（预览条是 Auto 高，得有人把它撑开）。
        _pad = new Border
        {
            Width = TileW,
            Height = 0,
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        PreviewPanel.Children.Add(_pad);

        // 让位状态归零（重建之后每行都是新对象，本来就没有偏移）
        _holeSlot = -1;
        _hiddenRow = null;
        _dragSrcIndex = -1;

        // 注：侧边栏自带的「收起 / 位置复原 / 隐藏」在这里**不画**（不能拼不能删，画出来只会挤位置）。
    }

    /// <summary>
    /// 预览里的一行：左边是模块格子（照侧边栏的样子），右边三个独立按钮（上移 / 下移 / 移除）。
    /// 按钮做得大（38×38）且常显 —— 之前挤在格子里 20×17，触屏根本点不准。
    ///
    /// ⚠️ 让位（空档挪位置时其它行退开）**不靠真实布局**，靠每行自己的 <see cref="SetRowShift"/> 偏移 ——
    ///    靠布局的话，空档每跨过一行，那一行会被瞬间顶走一格（2026-09-29 复现：用户说的"截屏自己突变到上面去了"）。
    ///    而偏移之所以用 Margin、不用 <c>TranslateTransform</c>，见 <see cref="RowShift"/> 上面那段（整格位移会整片不重画）。
    /// </summary>
    private FrameworkElement BuildRow(SidebarModule m)
    {
        var index = _ids.IndexOf(m.Id);
        var tile = BuildTile(m);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        actions.Children.Add(ActionButton("\uE70E", "上移一位", m.Id, MoveUp_Click, index > 0));
        actions.Children.Add(ActionButton("\uE70D", "下移一位", m.Id, MoveDown_Click, index < _ids.Count - 1));
        actions.Children.Add(ActionButton("\uE711", "从侧边栏移除", m.Id, Remove_Click, true));

        var row = new Grid { Height = TileH, ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(tile);
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);

        _tiles.Add(row);
        return row;
    }

    /// <summary>右边那几个按钮：38×38，常显，悬停给个底色（触屏没有悬停也能用）。</summary>
    private Button ActionButton(string glyph, string tip, string id, RoutedEventHandler onClick, bool enabled)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 15 },
            Width = 38,
            Height = 38,
            MinWidth = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Tag = id,
            IsEnabled = enabled,
            Opacity = enabled ? 0.85 : 0.28
        };
        ToolTipService.SetToolTip(b, tip);
        b.PointerEntered += (_, _) => { if (enabled) b.Background = Br("ModHoverBg"); };
        b.PointerExited += (_, _) => b.Background = new SolidColorBrush(Colors.Transparent);
        b.Click += onClick;
        return b;
    }

    /// <summary>预览里的一个模块格子：图标 + 短名，跟真侧边栏一个模样。</summary>
    private Border BuildTile(SidebarModule m)
    {
        var content = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new FontIcon
        {
            Glyph = m.Glyph,
            FontSize = 19,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = m.ShortName,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Br("ModText")
        });

        var tile = new Border
        {
            Width = TileW,
            Height = TileH,
            CornerRadius = new CornerRadius(6),
            Background = Br("ModCardBg"),
            BorderBrush = Br("ModCardStroke"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 0, 4, 0),
            Tag = m.Id,
            Child = content
        };
        ToolTipService.SetToolTip(tile, $"{m.Name}：拖动可调整顺序，拖至别处松手即移除");

        tile.PointerEntered += (_, _) =>
        {
            tile.Background = Br("ModHoverBg");
            tile.BorderBrush = Br("ModAccent");
        };
        tile.PointerExited += (_, _) =>
        {
            tile.Background = Br("ModCardBg");
            tile.BorderBrush = Br("ModCardStroke");
        };
        tile.PointerPressed += (s, e) => BeginPress(s, e, m.Id, true);
        tile.PointerMoved += OnPointerMoved;
        tile.PointerReleased += OnPointerReleased;
        tile.PointerCaptureLost += (_, _) => CancelDrag();
        tile.PointerCanceled += (_, _) => CancelDrag();

        return tile;
    }


    /// <summary>固定按钮（收起 / 位置复原 / 隐藏）现在不画在预览里了，这个方法暂时留着备用。</summary>
    private Border BuildFixedTile(string glyph, string label)
    {
        var content = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 16,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Br("ModTextDim")
        });

        var tile = new Border
        {
            Width = 96,
            Height = 46,
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = content
        };
        ToolTipService.SetToolTip(tile, "侧边栏自带按钮，不参与拼接");
        return tile;
    }

    // ── 自己画的拖拽：按下 → 跟手 → 让位 → 松手落位 ────────────────────────

    private void BeginPress(object sender, PointerRoutedEventArgs e, string id, bool fromPreview)
    {
        if (_flying) return;                            // 上一张卡还在飞 / 还在收势，等它落定（240+150ms）
        if (InsideButton(e.OriginalSource)) return;     // 小按钮的点击自己处理，别抢
        if (sender is not FrameworkElement src) return;
        if (_swapping) return;                          // ↑/↓ 正在滑动，等它落位

        Log($"按下 {id} 来源={(fromPreview ? "预览" : "模块库")} 行数={_tiles.Count} 设备={e.Pointer.PointerDeviceType}");

        _dragId = id;
        _dragFromPreview = fromPreview;
        _dragging = false;
        _armed = false;
        _holeSlot = -1;
        _hiddenRow = null;
        _dragSrcIndex = fromPreview ? _ids.IndexOf(id) : -1;
        _pressSource = src;
        _pointerId = e.Pointer.PointerId;
        _heldPointer = e.Pointer;
        _pressInRoot = e.GetCurrentPoint(WorkspaceRoot).Position;
        _maxMove = 0;

        // 按下这一刻的滚动位置。拖动结束时一律回到这里 —— 拖一下不该把页面留在别处。
        // ⚠️ 必须在**按下**时记，不能等接管时再记：触摸可能先微微飘了几像素（没到阈值），
        //    那时 ScrollViewer 已经把页面挪走一点了，等接管再记就等于"认下"了这个偏移。
        _pageScrollOffset = PageScroll.VerticalOffset;

        var inSrc = e.GetCurrentPoint(src).Position;

        // 替身一开始就跟源元素一样大 —— 这样"抠起来"的那一下和卡片严丝合缝，不会错位
        _srcW = src.ActualWidth;
        _srcH = src.ActualHeight;
        _grabRatio = new Point(
            _srcW > 1 ? Math.Clamp(inSrc.X / _srcW, 0, 1) : 0.5,
            _srcH > 1 ? Math.Clamp(inSrc.Y / _srcH, 0, 1) : 0.5);

        MeasureRowsTop();

        _isTouch = e.Pointer.PointerDeviceType == PointerDeviceType.Touch;

        if (_isTouch)
        {
            // ⚠️ 触摸**先不抢指针**。手指落在卡片上时，人很可能只是想上下滚页面；
            //    这时候抢走指针 + 把滚动锁掉，整页就再也滚不动了（只能在空白处滑）—— 群里反馈过。
            //    先按住、并确认手指定住了，才判定为"想拖卡片"；没定住就撒手，手势原样留给 ScrollViewer。
            HintPress(true);
            _downAt = DateTime.UtcNow;
            _lastMoveAt = _downAt;
            ArmHoldTimer();
            return;
        }

        // 鼠标 / 笔：没有"上下滑页面"这回事，按下就算想拖，保持原来的即时手感
        _armed = true;
        LockScroll();
        src.CapturePointer(e.Pointer);
        e.Handled = true;                              // 别让外层的滚动条抢走
    }

    /// <summary>
    /// 触摸闸门（每 60ms 检查一次）：按够久 + 手指定住，两个条件都满足才接管指针开始拖。
    /// 条件不满足就继续等；手指滑走了（超过 TouchSlop）直接放弃，把手势让给页面滚动。
    /// ⚠️ 这里拿的是**缓存的 Pointer**（EventArgs 早失效了），并且整个包在 try 里 ——
    ///    手指在这个瞬间抬起时 CapturePointer 会抛，忽略即可（随后 PointerReleased 会收尾）。
    /// </summary>
    private void OnHoldTick(DispatcherQueueTimer sender, object args)
    {
        if (_dragId is null || _pressSource is null || _armed || _heldPointer is null)
        {
            sender.Stop();
            return;
        }

        if (_maxMove > TouchSlop)
        {
            Log($"触摸闸门：手指已移动 {_maxMove:F0}px -> 判定为滚动，放弃拖 {_dragId}");
            sender.Stop();
            CancelPress();
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - _downAt).TotalMilliseconds < TouchHoldMs) return;              // 按得还不够久

        var still = (now - _lastMoveAt).TotalMilliseconds >= TouchStillMs;
        if (!still && (now - _downAt).TotalMilliseconds < TouchHoldMaxMs) return; // 手指还在动，再等等

        sender.Stop();
        _armed = true;
        LockScroll();

        // ⚠️⚠️ 光把 ScrollMode 置 Disabled **不够**：手指按下的那一刻，ScrollViewer 内部的
        //     ScrollPresenter 已经把这个触点登记进 DirectManipulation 了；之后再改滚动模式，
        //     它也不会放手。手指一动，DM 判定"开始平移"，反手把我们的指针捕获抢走
        //     —— 症状就是"按住能拖，但一动就断，然后页面顺着这根手指滚起来"（2026-09-27 实测复现，
        //     日志：接管后 16ms 收到 CaptureLost，紧接着页面开始跟手滚）。
        //     CancelDirectManipulations() 才是让 ScrollViewer 当场松开这个触点的官方手段。
        //     它落地要几十毫秒（在 DM 线程上），所以上面必须先确认手指定住了 —— 否则等于白叫。
        try { _pressSource.CancelDirectManipulations(); } catch { }
        try { _pressSource.CapturePointer(_heldPointer); } catch { }
        Log($"触摸闸门到点 -> 接管 {_dragId}");
    }

    /// <summary>
    /// 根节点上的指针移动：闸门期间只统计"手指走了多远、是不是已经定住"。
    /// ⚠️ 挂在根上（handledEventsToo）是因为闸门期间我们**还没捕获指针**，
    ///    卡片自己的 PointerMoved 在手指滑出去之后就收不到了。
    /// </summary>
    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragId is null || e.Pointer.PointerId != _pointerId) return;
        if (_armed) return;                             // 已接管：后面交给卡片自己的 PointerMoved

        var p = e.GetCurrentPoint(WorkspaceRoot).Position;
        var d = Math.Abs(p.X - _pressInRoot.X) + Math.Abs(p.Y - _pressInRoot.Y);
        if (d > _maxMove + StillSlop)                   // 真的动了（忽略触摸抖动）→ 重置"定住"计时
        {
            _maxMove = d;
            _lastMoveAt = DateTime.UtcNow;
        }

        if (_isTouch && _maxMove > TouchSlop) CancelPress();   // 手指滑走了 = 想滚页面，不抢
    }

    /// <summary>放弃这次按下（触摸闸门里判定成滚动、或指针提前抬起）：全部状态复位，不动数据、不碰滚动设置。</summary>
    private void CancelPress()
    {
        _holdTimer?.Stop();
        HintPress(false);
        _dragId = null;
        _dragging = false;
        _armed = false;
        _pressSource = null;
        _heldPointer = null;
        _holeSlot = -1;
        _dragSrcIndex = -1;
        _maxMove = 0;
    }

    /// <summary>按下时的轻反馈：卡片暗一点点，让触摸用户知道"已经按住了"，再等半秒就能拖。</summary>
    private void HintPress(bool on)
    {
        if (_pressSource is null) return;
        _pressSource.Opacity = on ? 0.75 : 1;
    }

    private void ArmHoldTimer()
    {
        _holdTimer ??= DispatcherQueue.CreateTimer();
        _holdTimer.Interval = TimeSpan.FromMilliseconds(GateTickMs);
        _holdTimer.IsRepeating = true;                 // 每 60ms 看一眼"够久了没、手指定住没有"
        _holdTimer.Tick -= OnHoldTick;                 // 每次重挂，别叠订阅
        _holdTimer.Tick += OnHoldTick;
        _holdTimer.Start();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragId is null || e.Pointer.PointerId != _pointerId) return;
        if (!_armed) return;                            // 触摸还没过闸门，等它（这期间页面该滚还能滚）

        var inRoot = e.GetCurrentPoint(WorkspaceRoot).Position;
        if (!_dragging)
        {
            if (Math.Abs(inRoot.X - _pressInRoot.X) + Math.Abs(inRoot.Y - _pressInRoot.Y) < DragSlop) return;

            _dragging = true;
            if (_dragFromPreview) HideSourceRow();       // 预览里拿起来的：这一行先收掉，别和跟手替身"变成两个"
            else if (_pressSource is not null) _pressSource.Opacity = 0.45;   // 库里拖出来的：原位只留个淡影
            ShowGhost();
        }

        e.Handled = true;
        _pointerInLayer = e.GetCurrentPoint(DragLayer).Position;
        PlaceGhost();                                   // 尺寸动画可能还没跑完，位置交给每帧回调兜住

        var inStrip = e.GetCurrentPoint(PreviewStrip).Position;
        var inside = inStrip.X >= 0 && inStrip.Y >= 0
                     && inStrip.X <= PreviewStrip.ActualWidth && inStrip.Y <= PreviewStrip.ActualHeight;

        UpdateHole(inside ? SlotFromPointer(e.GetCurrentPoint(PreviewPanel).Position.Y) : -1);

        // 进了预览条就收缩成"预览格子"的样子，离开再变回卡片大小 —— 拖到哪儿就是哪儿的样子
        if (!_dragFromPreview) MorphGhost(inside);

        // 从预览里往外拖：提示这一手会把卡片送去哪儿 ——
        // 落在模块库上 = 移除；落在别处 = 取消（卡片自己飞回来）。文案得跟着落点变，
        // 不然人拖到空白处还以为是"要删了"，松手却发现卡片又回来了。
        if (!inside && _dragFromPreview)
        {
            var overLib = IsOverLibrary(e.GetCurrentPoint(WorkspaceRoot).Position);
            RemoveHintText.Text = overLib ? "松手即从侧边栏移除" : "松手取消，卡片回到原位";
            RemoveHintIcon.Glyph = overLib ? "\uE74D" : "\uE711";     // 垃圾桶 / 取消
            RemoveHint.Visibility = Visibility.Visible;
        }
        else
        {
            RemoveHint.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 从预览里把某一行拿起来：这一行自己先收起来（高度归零 + 全透明），屏幕上只留跟手的那张替身。
    /// ⚠️ 用 Height / Opacity 而**不是** <c>Visibility.Collapsed</c> —— 折叠会连带丢掉指针捕获
    ///    （捕获点就在这行里面的格子上），拖动当场就断。
    /// ⚠️ 紧接着把各行的让位偏移**不带动画**地设一遍：这行高度归零后，它后面的行在布局上会立刻往上爬一格，
    ///    而空档正好开在它原来的位置、那些行要留在原地 —— 两者抵消。所以拿起来这一下，屏幕上除了
    ///    "多了一张跟着手走的卡"，别的什么都不该动。
    /// </summary>
    private void HideSourceRow()
    {
        if (_dragSrcIndex < 0 || _dragSrcIndex >= _tiles.Count) return;

        _hiddenRow = _tiles[_dragSrcIndex];
        _hiddenRow.Opacity = 0;
        _hiddenRow.Height = 0;

        // 空档就落在它自己原来的位置（行下标 == 可见位置：可见位置只在"排在它后面"的行上才会减一）
        _holeSlot = _dragSrcIndex;
        ApplyRowOffsets(animate: false);
        AnimatePad();
        ShowHole();
    }

    /// <summary>把"拿起来"的那一行放回去（收尾兜底；正常路径随后会整页重建）。</summary>
    private void RestoreHiddenRow()
    {
        if (_hiddenRow is not null)
        {
            _hiddenRow.Height = TileH;
            _hiddenRow.Opacity = 1;
            _hiddenRow = null;
        }
        foreach (var t in _tiles)
        {
            CancelTween(t);
            SetRowShift(t, 0);
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragId is null || e.Pointer.PointerId != _pointerId) return;

        if (!_armed)
        {
            // 触摸：还没过闸门就抬手了 —— 那是"点了一下"，库里卡片就加进侧边栏
            var tapId = _dragId;
            var tapFromPreview = _dragFromPreview;
            CancelPress();
            if (!tapFromPreview) AddToEnd(tapId);
            return;
        }

        var id = _dragId;
        var fromPreview = _dragFromPreview;
        var dragged = _dragging;

        // ⚠️ 这几样必须在 EndDrag 清状态**之前**抓下来：收场动画要拿它们当落点 / 收尾对象。
        var src = _pressSource;                     // 库里那张卡的淡影，动画结束后再恢复不透明
        var homeRow = _hiddenRow;                   // 预览里被"拿起来"收掉的那一行
        var homeIndex = _dragSrcIndex;

        var inStrip = e.GetCurrentPoint(PreviewStrip).Position;
        var inside = inStrip.X >= 0 && inStrip.Y >= 0
                     && inStrip.X <= PreviewStrip.ActualWidth && inStrip.Y <= PreviewStrip.ActualHeight;

        // ⚠️ 这一步必须在 EndDrag 之前算：_dragSrcIndex / _rowsTop 都是拖动期间的状态，收尾时会清掉。
        var index = inside
            ? InsertIndexFromSlot(SlotFromPointer(e.GetCurrentPoint(PreviewPanel).Position.Y))
            : -1;

        // 落在模块库那一片上（只有"拖出预览条"时才谈得上）
        var overLibrary = !inside && IsOverLibrary(e.GetCurrentPoint(WorkspaceRoot).Position);

        // ⚠️ ReleasePointerCapture 会**同步**触发 PointerCaptureLost → 我们的处理器会去 CancelDrag()，
        //    那里面又 Refresh() 重建列表 —— 等于在收尾到一半时把列表换掉，后面的 MoveTo 踩在新建的对象上。
        //    用 _ending 把这段圈起来：自己收尾的时候，CaptureLost 不参与。
        _ending = true;
        (_pressSource as UIElement)?.ReleasePointerCapture(e.Pointer);
        _ending = false;

        if (!dragged)
        {
            EndDrag();
            if (!fromPreview) AddToEnd(id);             // 没拖动 = 点了一下 → 加到末尾
            return;
        }

        // ① 放进了预览条 = 落位（新增 / 排序）。
        //    让卡片先"贴"到那个格子上再交接 —— 不然松手瞬间替身凭空消失、行凭空出现，
        //    看着像断了一拍（Nick 2026-10-01：「贴到预览区也要有个贴过去的动画」）。
        if (inside && index >= 0)
        {
            var slotPt = _hole is not null ? LayerPoint(_hole) : LayerPoint(PreviewStrip);
            EndDrag(keepGhost: true);
            FlyGhost(slotPt, TileW, TileH, () =>
            {
                // 真身先就位（替身正压在那格上），撤替身交给收势统一做（见 FlyGhost 的 Land）
                HideHole();
                MoveTo(id, index);
            });
            return;
        }

        // ② 拖进「模块库」区域后松手（只可能是从预览拖出来的）= 从侧边栏移除。
        //    收场 = 先展开成大卡、再飞回库里它那张卡，落地那一下由灰变亮（不淡出）。
        if (fromPreview && overLibrary)
        {
            var cardEl = LibraryCardEl(id);
            var to = cardEl is not null ? LayerPoint(cardEl) : LayerPoint(LibraryPanel);
            var (cw, ch) = CardSize();

            EndDrag(keepGhost: true);
            FlyGhost(to, cw, ch, () =>
            {
                // ⚠️ 顺序要紧：这些都必须在撤替身**之前**做完，让库重建好的亮卡就在替身底下等着。
                //    原来是把 HideGhost() 放最前，于是替身消失的那一帧底下还在由灰变亮，看着就是"突然没了"。
                HideHole();
                RestoreHiddenRow();
                _ids.Remove(id);
                Save();
                Refresh();
            });
            return;
        }

        // ③ 其余落点 = 取消，整场作废。数据一个字都不改，卡片飞回它出发的地方：
        //    从库里拖的 → 回库里那张卡；从预览拖的 → 回预览里它原来那一行。
        var homeEl = fromPreview ? (homeRow as FrameworkElement) : LibraryCardEl(id);
        var back = homeEl is not null ? LayerPoint(homeEl) : LayerPoint(WorkspaceRoot);
        var (bw, bh) = fromPreview ? (TileW, TileH) : CardSize();

        // 从预览拖出来又反悔：**先把空档在它原来那一格重新打开**，其它卡片带动画让开，
        // 卡片飞进一个已经腾好的位置。
        // ⚠️ 不这么做的话，拖出预览条时空档早就合上了（行都归位了），这儿落下去正压在下一位
        //    卡片身上，直到落地才被挤开 —— Nick 2026-10-01：「他会先和这个卡片重合然后后面才会
        //    挤开，好丑啊，他回来的时候我希望别的卡片可以自动让位」。
        // ⚠️ 必须在 EndDrag 之前调：VisibleIndex / RowOffset 都要靠 _dragSrcIndex。
        if (fromPreview && homeIndex >= 0)
        {
            UpdateHole(VisibleIndex(homeIndex));
            if (_hole is not null) back = LayerPoint(_hole);
        }

        EndDrag(keepGhost: true);
        FlyGhost(back, bw, bh, () =>
        {
            // 落地同一帧里把真身还回去：替身卡正好压在那一行上，交接看不出接缝
            HideHole();
            if (fromPreview && homeIndex >= 0) RestoreHiddenRow();
            if (src is not null) src.Opacity = 1;
            Refresh();
        });
    }

    /// <summary>拖到一半被系统打断（触点被抢、窗口失焦…）：收拾干净，不动数据。</summary>
    private void CancelDrag()
    {
        if (_dragId is null || _ending) return;
        Log($"拖动被打断（{_dragId}）—— 滚动锁扣到手指抬起");
        var midDrag = _dragging;
        EndDrag(keepScrollLocked: midDrag);
        if (midDrag) _latchScroll = true;
        Refresh();
    }

    /// <summary>
    /// 收尾清场。
    /// ⚠️ <paramref name="keepGhost"/> = true 时**先留着那张替身卡**：松手后它还要飞一段
    ///    （回原位 / 飞向模块库），飞完由飞行动画自己收掉。这里若照常 HideGhost，等于把动画半路掐掉。
    /// </summary>
    private void EndDrag(bool keepScrollLocked = false, bool keepGhost = false)
    {
        _holdTimer?.Stop();
        HintPress(false);
        if (!keepGhost)
        {
            HideGhost();
            RestoreHiddenRow();
            HideHole();
            if (_pressSource is not null) _pressSource.Opacity = 1;
        }
        RemoveHint.Visibility = Visibility.Collapsed;

        if (_armed)
        {
            if (keepScrollLocked)
            {
                // ⚠️ 这时候**不能**把滚动模式放回 Auto：DirectManipulation 一直在默默攒位移，
                //    模式一放回 Auto，ScrollViewer 会把攒下的这一大段一次性补上 ——
                //    页面"唰"地跳出去开始滚。用户描述的就是这个（"自动断掉进入滑动页面滑动状态"）。
                //    所以只把页面钉回按下时的位置，锁留着，等手指抬起再放。
                PageScroll.ChangeView(null, _pageScrollOffset, null, true);
            }
            else
            {
                UnlockScroll();
            }
        }

        _dragId = null;
        _dragging = false;
        _armed = false;
        _pressSource = null;
        _heldPointer = null;
        _dragSrcIndex = -1;
        _maxMove = 0;
        // ⚠️ 这里**不要**清 _holeSlot：keepGhost 时替身卡还要飞一段，空档得继续开着当落点；
        //    正常收场那条由 HideHole() 负责清（它自己会把 _holeSlot 置 -1）。
    }

    /// <summary>把空档合上（收尾兜底；正常路径随后整页重建）。</summary>
    private void HideHole()
    {
        _holeSlot = -1;
        if (_hole is not null) FadeElement(_hole, 0, 90);
        AnimatePad();
    }

    /// <summary>拖的时候把整页滚动锁掉：不然触摸拖拽会被外层 ScrollViewer 当"滚动"处理，整页跟着跑。</summary>
    private void LockScroll()
    {
        PageScroll.VerticalScrollMode = ScrollMode.Disabled;
        PageScroll.HorizontalScrollMode = ScrollMode.Disabled;
    }

    private void UnlockScroll()
    {
        _latchScroll = false;
        PageScroll.VerticalScrollMode = ScrollMode.Auto;
        PageScroll.HorizontalScrollMode = ScrollMode.Auto;
        PageScroll.ChangeView(null, _pageScrollOffset, null, true);
    }

    private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e) => ReleaseScrollLatch();

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e) => ReleaseScrollLatch();

    /// <summary>把"扣住的滚动锁"放开（手指抬起、或又开始了一次新操作）。</summary>
    private void ReleaseScrollLatch()
    {
        if (!_latchScroll) return;
        Log("手指抬起 -> 放开滚动锁");
        UnlockScroll();
    }

    // ── 跟手的那张"替身卡片" ──────────────────────────────────────────────
    //
    // 它有两层内容（库卡片样式 / 预览格子样式）叠在一起，靠透明度交叉切换：
    // 从库里拖出来时是卡片大小、卡片样子；拖进预览条就一边缩小一边换成格子样子。
    // 位置**不是**在移动事件里算的 —— 尺寸是动画在改，移动事件频率又和尺寸动画无关，
    // 两边各算各的就会差一帧、看起来就是"错位"。统一放到 OnGhostFrame 每帧算，永远对齐。

    private void ShowGhost()
    {
        if (_dragId is null || SidebarModules.Find(_dragId) is not { } m) return;

        // 从预览里拖出来：本来就是格子体积；从库里拖：先用卡片的真实体积
        _ghostTileMode = _dragFromPreview;
        var w = _ghostTileMode ? TileW : (_srcW > 1 ? _srcW : TileW);
        var h = _ghostTileMode ? TileH : (_srcH > 1 ? _srcH : TileH);

        _ghostCard = GhostCardContent(m);
        _ghostTile = GhostTileContent(m);
        _ghostCard.Opacity = _ghostTileMode ? 0 : 1;
        _ghostTile.Opacity = _ghostTileMode ? 1 : 0;

        var layers = new Grid();
        layers.Children.Add(_ghostCard);
        layers.Children.Add(_ghostTile);

        // ⚠️ 描边拆成"一圈灰边 + 一圈蓝圈"叠着画，不再用 Border 自己的 BorderThickness：
        //    落位时要把**蓝圈单独淡走**（灰边留在原地，和底下那张卡的长相完全一致），
        //    而 BorderBrush 是 Brush，没有可以逐帧插值的数值；画成两层就只剩一条 Opacity 的事。
        //    蓝圈盖在灰边上面，所以拖动过程中看到的仍是原来那圈蓝色描边，外观不变。
        var edge = new Border
        {
            BorderBrush = Br("ModCardStroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            IsHitTestVisible = false
        };
        _ghostRing = new Border
        {
            BorderBrush = Br("ModAccent"),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(8),
            IsHitTestVisible = false
        };
        layers.Children.Add(edge);
        layers.Children.Add(_ghostRing);

        var ghost = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(8),
            Background = Br("ModCardBg"),
            BorderThickness = new Thickness(0),     // 描边交给上面那两层，见注释
            Opacity = 0.95,
            Child = layers,
            Shadow = new ThemeShadow(),
            Translation = new Vector3(0, 0, 52)     // Z 抬高一档：影子更明显，像被提起来了
        };

        _ghost = ghost;
        DragLayer.Children.Add(ghost);

        // "浮起"的缩放走 Composition 的 visual.Scale。
        // ⛔ 别改用 RenderTransform + Storyboard 的 "RenderTransform.ScaleX" 属性路径 ——
        //    WinUI 3 解析不了，Begin() 当场抛 COMException「Cannot resolve TargetProperty」，
        //    异常从 PointerMoved 里冒出去把应用带走（2026-10-01 实测崩过）。
        try
        {
            _ghostVisual = ElementCompositionPreview.GetElementVisual(ghost);
            _ghostVisual.CenterPoint = new Vector3((float)(w / 2), (float)(h / 2), 0);
        }
        catch { _ghostVisual = null; }

        HookGhostFrame();
        PlaceGhost();
        PopGhost(up: true);
    }

    /// <summary>
    /// 那张卡"提起 / 放下"的缩放（影子同时也更重，见 Translation.Z）。
    /// ⚠️ 走 Composition 的 <c>Scale</c>，尺寸（Width/Height）归 <see cref="MorphGhost"/> 与飞行动画管 ——
    ///    两边各管一套属性，才不会被对方的动画顶掉。
    /// ⚠️ 装饰性动画一律 try/catch 吞掉：它坏了顶多不缩放，绝不能把整个拖动/应用搞崩。
    /// </summary>
    private void PopGhost(bool up)
    {
        if (_ghostVisual is null) return;
        try
        {
            var comp = _ghostVisual.Compositor;
            var ease = comp.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

            var anim = comp.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(1f, up ? (float)LiftScale : 1f, ease);
            anim.Duration = TimeSpan.FromMilliseconds(up ? MorphMs : FlightMs);

            _ghostVisual.StartAnimation("Scale.X", anim);
            _ghostVisual.StartAnimation("Scale.Y", anim);
        }
        catch { }
    }

    /// <summary>ghost 里的"库卡片"层（横排：图标 + 名称 / 说明）。</summary>
    private FrameworkElement GhostCardContent(SidebarModule m)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = m.Name,
            FontSize = 14.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Br("ModText")
        });
        text.Children.Add(new TextBlock
        {
            Text = m.Hint,
            FontSize = 11.5,
            Opacity = 0.65,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Br("ModTextDim")
        });

        // ⚠️ Margin 左右各 15 = 库里那张卡的 BorderThickness(1) + Padding(14)。替身卡**必须**对上这个内缩，
        //    否则图标直接贴到边框上、整块内容偏左（2026-10-01 Nick 截图：「图标都已经贴到左边的边框上了」）。
        // ⚠️ 15 不是 14：替身卡的外框是拿两层 Border 画的（BorderThickness=0，见 ShowGhost），
        //    它自己不再撑出那 1px，所以这 1px 得由内容层的 Margin 补上，落位交接时文字才不会横向跳一下。
        var grid = new Grid { ColumnSpacing = 12, Margin = new Thickness(15, 0, 15, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new FontIcon
        {
            Glyph = m.Glyph,
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // ⚠️ 右侧那个"＋"也得画出来：库卡片是**三列**（图标 / 文字 / ＋），替身卡原来只有两列，
        //    于是落位交接时"＋"会凭空冒出来，又是一处跳（2026-10-01 收势改造时一并补上）。
        //    替身永远复制的是"可以拖"的那张卡（已加入的灰卡根本不给拖），所以固定就是加号 E710。
        var badge = new FontIcon
        {
            Glyph = "\uE710",
            FontSize = 13,
            Opacity = 0.8,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(badge, 2);
        grid.Children.Add(badge);
        return grid;
    }

    /// <summary>ghost 里的"预览格子"层（竖排：图标 + 短名，跟真侧边栏一个模样）。</summary>
    private FrameworkElement GhostTileContent(SidebarModule m)
    {
        var content = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        content.Children.Add(new FontIcon
        {
            Glyph = m.Glyph,
            FontSize = 19,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = m.ShortName,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Br("ModText")
        });
        return content;
    }

    /// <summary>把 ghost 在「库卡片尺寸」和「预览格子尺寸」之间平滑过渡（进/出预览条时切）。</summary>
    private void MorphGhost(bool toTile)
    {
        if (_ghost is null || _ghostTileMode == toTile) return;
        _ghostTileMode = toTile;

        var toW = toTile ? TileW : (_srcW > 1 ? _srcW : TileW);
        var toH = toTile ? TileH : (_srcH > 1 ? _srcH : TileH);

        // ⚠️ 内容层**立刻**换，不做交叉淡入 —— 宽高动画期间容器正被压得很窄，
        //    卡片那层（图标 + 两行字）塞进窄容器会被挤成"文字贴在左边"的怪相。
        if (_ghostCard is not null) _ghostCard.Opacity = toTile ? 0 : 1;
        if (_ghostTile is not null) _ghostTile.Opacity = toTile ? 1 : 0;

        MorphSizeTo(toW, toH, MorphMs);
    }

    /// <summary>
    /// 替身卡的尺寸过渡：**手写插值**（走 <see cref="StartTween"/>），不用 Storyboard。
    ///
    /// ⚠️⚠️ 2026-10-01 踩实了的坑：Storyboard 一旦 <c>Stop()</c>，Width / Height 会**弹回创建时的本地值**
    ///    （我们建替身时写的是卡片尺寸）。松手收场第一件事就是接管尺寸，于是 Stop 那一下替身"啪"地
    ///    胀成一个卡片大的空框，再从头缩一遍 —— Nick 的原话是「松开的一瞬间变成这玩意儿，虽然就那么
    ///    一两帧但是特别难受」。手写插值写的是本地值，任何时候接手都是连续的，没有这个中间态。
    /// ⚠️ 同一个元素上后起的补间会自动顶掉前一条（见 StartTween），所以飞行接管时不用手动停。
    /// </summary>
    private void MorphSizeTo(double toW, double toH, int ms)
    {
        if (_ghost is not { } g) return;

        var fromW = g.ActualWidth > 0.5 ? g.ActualWidth : g.Width;
        var fromH = g.ActualHeight > 0.5 ? g.ActualHeight : g.Height;
        if (double.IsNaN(fromW) || fromW <= 0.5) fromW = toW;
        if (double.IsNaN(fromH) || fromH <= 0.5) fromH = toH;

        StartTween(g, t =>
        {
            g.Width = fromW + (toW - fromW) * t;
            g.Height = fromH + (toH - fromH) * t;
        }, 0, 1, ms, done: null);
    }

    private void HookGhostFrame()
    {
        if (_ghostFrameHooked) return;
        CompositionTarget.Rendering += OnGhostFrame;
        _ghostFrameHooked = true;
    }

    private void UnhookGhostFrame()
    {
        if (!_ghostFrameHooked) return;
        CompositionTarget.Rendering -= OnGhostFrame;
        _ghostFrameHooked = false;
    }

    /// <summary>
    /// 每帧把 ghost 摆到"指针减去抓点比例"的位置。
    /// ⚠️ 用 ActualWidth / ActualHeight（布局后的真值），不是 Width —— 后者是动画的目标值，
    ///    在 150ms 的收缩过程里读它，位置会和看得见的尺寸对不上，又变成错位。
    /// </summary>
    private void OnGhostFrame(object? sender, object e) => PlaceGhost();

    private void PlaceGhost()
    {
        if (_ghost is null) return;
        var w = _ghost.ActualWidth > 0.5 ? _ghost.ActualWidth : _ghost.Width;
        var h = _ghost.ActualHeight > 0.5 ? _ghost.ActualHeight : _ghost.Height;
        // 缩放中心跟着实际尺寸走 —— 否则从卡片缩到格子那 150ms 里，放大会歪在一边
        if (_ghostVisual is not null)
            _ghostVisual.CenterPoint = new Vector3((float)(w / 2), (float)(h / 2), 0);
        Canvas.SetLeft(_ghost, _pointerInLayer.X - _grabRatio.X * w);
        Canvas.SetTop(_ghost, _pointerInLayer.Y - _grabRatio.Y * h);
    }

    private void HideGhost()
    {
        UnhookGhostFrame();
        // ⚠️ 这里**不要**去 StopAnimation —— 传 null 当动画会让原生层直接访问违例
        //    （0xC0000005，try/catch 也拦不住，进程当场没了）。元素一移除，它的 Composition
        //    动画自己就作废了，本来也不用手动收。
        _ghostVisual = null;
        if (_ghost is null) return;
        CancelTween(_ghost);                    // 尺寸 / 飞行的插值补间挂在它身上，一起收掉
        DragLayer.Children.Remove(_ghost);
        _ghost = null;
        _ghostCard = null;
        _ghostTile = null;
        _ghostRing = null;
    }

    // ── 收场：那张卡飞回原位（取消）/ 飞向模块库（移除）─────────────────────
    //
    // 2026-10-01 Nick 要的三件事：
    //   ① 从预览拖出去时，"宽度先不变"（拖的过程里一直是格子形态），松手**之后**才见分晓；
    //   ② 取消放置（松手在空白处）→ 卡片自己飞回出发的位置，数据不动；
    //   ③ 拖进模块库移除 → 卡片展开成大卡、飞向库里它那张卡，库卡由灰变亮。

    /// <summary>
    /// 松手后那张卡的收场：一边飞向目标点、一边把尺寸换成目标形态，落地后交回 <paramref name="onDone"/>。
    /// ⚠️ 全程手写属性值，**不走 Storyboard** —— 这条路上已经踩过三次：
    ///    ① "RenderTransform.ScaleX" Begin() 当场抛（应用挂掉）；② "(Canvas.Left)" 同样不保证解析得了；
    ///    ③ Storyboard 一 Stop 就把 Width/Height 弹回本地值，接管那一刻会看见替身突然胀大。
    ///    所以形态动画（<see cref="MorphSizeTo"/>）和飞行都走同一套手写插值，同一个元素上的后一条
    ///    补间自动顶掉前一条，不用手动停。
    /// ⚠️ 飞行本身不做淡出：Nick 2026-10-01 明确「飞进去的时候不要有淡出的动画」—— 全程不透明地飞过去。
    ///    到位之后另有一段"收势"（<see cref="SettleGhost"/>），退场的是提起来的装饰，不是卡片本身。
    /// </summary>
    private void FlyGhost(Point to, double toW, double toH, Action onDone)
    {
        if (_ghost is null) { onDone(); return; }

        UnhookGhostFrame();                     // 别再让它每帧跟着指针摆
        var g = _ghost;
        _flying = true;                         // 飞行期间挡住新的按下（见 BeginPress）

        // 目标窄 = 预览格子形态，目标宽 = 库卡片形态
        var toTile = toW < 150;

        var fromX = Canvas.GetLeft(g); if (double.IsNaN(fromX)) fromX = 0;
        var fromY = Canvas.GetTop(g); if (double.IsNaN(fromY)) fromY = 0;
        var fromW = g.ActualWidth > 0.5 ? g.ActualWidth : toW;
        var fromH = g.ActualHeight > 0.5 ? g.ActualHeight : toH;

        // 形态要变的只有一种：预览格子 → 库卡片（拖进模块库移除时）。
        // ⚠️ 内容**等容器长大之后再换**，别一开头就切：那会儿容器才 96 宽，横排的卡片内容
        //    （图标 + 名字 + 描述）塞进去被压成一坨 + 文字挤在左边，这就是 Nick 说的
        //    「最后那一帧成啥了这是」（2026-10-01）。
        //    尺寸进度走到 SizeFirst 时就长到目标宽度附近了，那时候换才干净。
        //
        // ⚠️⚠️ 2026-10-01 换成方案A曲线后，这个数从 0.62 **必须**提到 0.97，别调回去：
        //    这个值是拿"缓动后的 t"去比，不是时间。新曲线前 10% 的时间就跑掉 58% 的 t，
        //    0.62 会在**第 28ms**（240ms 飞行的 11.5%）就满足 —— 小格子会"啪"地一下胀成大卡。
        //    0.97 对应飞到约 55% 的时候，容器已经有目标宽度的 97%，换内容才看不出接缝。
        var switched = _ghostTileMode == toTile;
        const double SizeFirst = 0.97;

        // 落地 / 兜底都走这一条
        void Finish()
        {
            HideGhost();
            _flying = false;                // ⚠️ 闸门要等替身真的收掉再解（收势期间仍然不许起新的拖动）
        }

        void Land()
        {
            // ① 目标形态先就位：此刻替身还完整盖在它上面，看不出底下换了什么
            try { onDone(); }
            // ② 才让"提起来"的装饰退场，走完再撤替身 —— 撤的时候两者已经长得一模一样（见 SettleGhost）
            finally { SettleGhost(Finish); }
        }

        StartTween(g, t =>
        {
            var sizeT = Math.Min(1, t / SizeFirst);         // 尺寸在前段走完，位置走满全程
            g.Width = fromW + (toW - fromW) * sizeT;
            g.Height = fromH + (toH - fromH) * sizeT;
            Canvas.SetLeft(g, fromX + (to.X - fromX) * t);
            Canvas.SetTop(g, fromY + (to.Y - fromY) * t);

            if (!switched && sizeT >= 1)
            {
                switched = true;
                _ghostTileMode = toTile;
                if (_ghostCard is not null) _ghostCard.Opacity = toTile ? 0 : 1;
                if (_ghostTile is not null) _ghostTile.Opacity = toTile ? 1 : 0;
            }
        }, 0, 1, FlightMs, done: Land);

        PopGhost(up: false);                    // 浮起的那点放大一起回落
    }

    /// <summary>
    /// 落位后的"收势"：把提起来的那几样装饰退场，走完才撤替身卡。
    ///
    /// 两次演进（都在 2026-10-01，Nick 逐次提的）：
    ///   ① 原来落位是**一帧**里 <c>HideGhost()</c> + <c>Refresh()</c> 直接交接 → 三样装饰同时蒸发，就是那下突变。
    ///      改成"先就位、再收势、最后撤替身"三步才干净。
    ///   ② 之后又给收势加了一下"轻落"（替身先轻微下沉再复位），Nick 觉得两种手感都挺好，
    ///      于是做成侧边布局页的「动画方案」卡交由用户自选 —— 走哪条由 <see cref="_dropDip"/> 决定。
    ///
    /// ⚠️⚠️ 2026-10-01 Nick 抓的 bug：「他回到模块区的时候……有那种蓝色的边框还有七七八八之类的效果，
    ///    它和底下的原本是重合的，但是突然消失，然后留下模块区下面的卡片，这个突变有点丑」。
    ///    原因是替身卡身上叠了三样"提起来"的标记（蓝描边 1.5px、ThemeShadow 投影、Opacity 0.95），
    ///    而落位时是直接 <c>HideGhost()</c> —— 三样东西在**一帧之内**同时蒸发，底下的卡同时由灰变亮。
    ///    现在拆成三步：① 目标形态先摆好（替身盖着，看不出）② 这三样慢慢退场 ③ 最后才撤替身。
    ///    走完①②，替身和底下的卡已经位置重合、长相一致，所以第③步看不出任何变化。
    /// ⚠️ 这不是"淡出"：飞行的 240ms 里替身全程不透明（Nick 明确要求过"飞进去不要淡出"），
    ///    退场的只有落位之后那一层装饰。
    /// ⚠️ 影子靠 <c>Translation.Z</c> 收：ThemeShadow 的偏移和模糊是按 Z 算的，Z 回到 0 影子就落到正下方、
    ///    被元素自己盖住。装饰性动画一律 try/catch —— 它坏了顶多影子没收回去，绝不能把应用带走。
    /// </summary>
    private void SettleGhost(Action done)
    {
        if (_ghost is not { } g) { done(); return; }

        var ring = _ghostRing;
        var fromOpacity = g.Opacity;
        var fromZ = g.Translation.Z;

        // ① "轻落一下"（「动画方案」选了它才有）：缩到 _dropDip 再缓出复位。
        //    ⚠️ 这一段**必须**走 Composition，不能给 visual.Scale 直接赋基础值：
        //       Scale.X/Y 上还挂着 PopGhost 的 Composition 动画，基础值会被动画盖住、根本看不见。
        //    关键帧：0 → 1（起点，正好接 PopGhost 收回到的 1），DipAt → 最低点，1 → 复位。
        //    ⚠️ 选了「平滑收势」时 _dropDip 就是 1.0，整段跳过 —— 别起一个原地不动的动画白占资源。
        try
        {
            if (_dropDip < 0.999 && _ghostVisual is { } vis)
            {
                var comp = vis.Compositor;
                var breathe = comp.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

                var dip = comp.CreateScalarKeyFrameAnimation();
                dip.InsertKeyFrame(0f, 1f);
                dip.InsertKeyFrame((float)DipAt, (float)_dropDip);              // 默认线性：干脆地压下去
                dip.InsertKeyFrame(1f, 1f, breathe);                            // 缓出回来，尾巴长
                dip.Duration = TimeSpan.FromMilliseconds(SettleMs);

                vis.StartAnimation("Scale.X", dip);
                vis.StartAnimation("Scale.Y", dip);
            }
        }
        catch { /* 纯装饰：坏了只是不缩那一下，绝不能影响交接 */ }

        // ② 装饰退场（方案A 原有）：蓝圈淡走 / 影子收回 / 透明度补满。跟①同时起跑、同时结束。
        StartTween(g, t =>
        {
            try
            {
                g.Opacity = fromOpacity + (1.0 - fromOpacity) * t;
                if (ring is not null) ring.Opacity = 1 - t;
                if (fromZ > 0.5) g.Translation = new Vector3(0, 0, (float)(fromZ * (1 - t)));
            }
            catch { /* 纯装饰：坏了只是收势不好看，绝不能影响交接 */ }
        }, 0, 1, SettleMs, done: done);
    }

    /// <summary>库里某张卡的左上角，在拖拽浮层里的坐标（取消/移除时的落点）。</summary>
    private Point LayerPoint(FrameworkElement el)
    {
        try { return el.TransformToVisual(DragLayer).TransformPoint(new Point(0, 0)); }
        catch { return new Point(0, 0); }
    }

    /// <summary>按模块 id 找库里那张卡（库里始终列着全部模块，已加入的只是变灰）。</summary>
    private FrameworkElement? LibraryCardEl(string id)
    {
        foreach (var row in LibraryPanel.Children)
        {
            if (row is not Grid g) continue;
            foreach (var c in g.Children)
            {
                if (c is FrameworkElement fe && (fe.Tag as string) == id) return fe;
            }
        }
        return null;
    }

    /// <summary>
    /// 库里一张卡的实际尺寸 —— "展开成大卡"的目标就是它。
    /// ⚠️ 直接量现成的那张，别自己填数字：卡片宽度是两列星型分出来的，跟窗口宽度有关。
    /// </summary>
    private (double W, double H) CardSize()
    {
        foreach (var row in LibraryPanel.Children)
        {
            if (row is not Grid g) continue;
            foreach (var c in g.Children)
            {
                if (c is FrameworkElement fe && fe.ActualWidth > 1 && fe.ActualHeight > 1)
                    return (fe.ActualWidth, fe.ActualHeight);
            }
        }
        return (_srcW > 1 ? _srcW : 300, 72);
    }

    /// <summary>松手点是不是落在左侧模块库那一片上（落那儿 = 从侧边栏移除，而不是取消）。</summary>
    private bool IsOverLibrary(Point inRoot)
    {
        try
        {
            if (LibraryPanel.ActualWidth < 1 || LibraryPanel.ActualHeight < 1) return false;
            var p = LibraryPanel.TransformToVisual(WorkspaceRoot).TransformPoint(new Point(0, 0));
            return new Rect(p.X, p.Y, LibraryPanel.ActualWidth, LibraryPanel.ActualHeight).Contains(inRoot);
        }
        catch { return false; }
    }

    // ── 让位：行不动位，靠每行自己的偏移；空档画在浮层上 ──────────────────────
    //
    // 两套坐标，别混：
    //   · 「行下标 k」= _tiles / _ids 里的下标 —— 整个拖动过程中**始终不变**（只有落位时才改 _ids）
    //   · 「可见位置 v」= 把被拿起来的那一行摘掉之后重新数的位置
    // 某一行的让位偏移 = 空档在它上面（v >= 空档位置）就往下退一格，否则不动。
    //
    // ⚠️ 为什么不把空档做成 StackPanel 里的一个元素（老做法）：那样空档每跨过一行，那一行是被**布局**
    //    瞬间顶走的，没有中间过程 —— 用户看到的就是"截屏自己突变到上面去了"。

    /// <summary>可见行的个数（被拿起来的那一行不算）。</summary>
    private int VisibleCount => _tiles.Count - (_dragSrcIndex >= 0 ? 1 : 0);

    /// <summary>行下标 → 可见位置。</summary>
    private int VisibleIndex(int k) => _dragSrcIndex >= 0 && k > _dragSrcIndex ? k - 1 : k;

    /// <summary>这一行该偏移多少：正数 = 往下退。</summary>
    /// <remarks>
    /// 被拿起来那一行的高度已经归零，所以**布局自己就把它后面的行往上收了一格**，
    /// 这里只需要再加"空档造成的下退"。别再补一次"往上" —— 那会和布局的收拢重掉（踩过）。
    /// </remarks>
    private double RowOffset(int k) =>
        _holeSlot >= 0 && VisibleIndex(k) >= _holeSlot ? GapDip : 0;

    /// <summary>把所有行挪到当前让位状态该在的位置。animate=false 用于"刚拿起来"那一下（必须瞬时就位）。</summary>
    private void ApplyRowOffsets(bool animate)
    {
        for (var k = 0; k < _tiles.Count; k++)
        {
            if (k == _dragSrcIndex) continue;           // 被拿起来那行正跟手，不参与让位
            SlideRow(_tiles[k], RowOffset(k), animate);
        }
    }

    /// <summary>
    /// 指针落在第几个可见位置（0 = 第一行上面，VisibleCount = 最后一行下面）。
    /// 行高固定 56、行距 3，所以直接按格子推 —— 不去量每行的实际位置：量出来的位置带着它自己的让位偏移，
    /// 那正是老写法"落点忽上忽下"的来源。
    /// </summary>
    private int SlotFromPointer(double yInPanel)
    {
        for (var v = 0; v < VisibleCount; v++)
        {
            if (yInPanel < _rowsTop + v * GapDip + TileH / 2) return v;
        }
        return VisibleCount;
    }

    /// <summary>可见位置 → 插回 _ids 的下标（被拿起来的那一行自己占着一个下标，要跳过去）。</summary>
    private int InsertIndexFromSlot(int slot) =>
        slot + (_dragSrcIndex >= 0 && slot >= _dragSrcIndex ? 1 : 0);

    /// <summary>量第一行在 PreviewPanel 里的 Y —— 空档画在哪儿、指针落在第几格，都靠它。</summary>
    private void MeasureRowsTop()
    {
        _rowsTop = 0;
        if (PreviewPanel.Children.Count <= 1) return;
        if (PreviewPanel.Children[1] is not FrameworkElement first) return;
        _rowsTop = first.TransformToVisual(PreviewPanel).TransformPoint(new Point(0, 0)).Y;
    }

    /// <summary>
    /// 空档挪到第 slot 个可见位置（-1 = 合上，例如指针拖出了预览条）。
    /// ⚠️ 传进来的必须是**可见位置**，别直接塞行下标。
    /// </summary>
    private void UpdateHole(int slot)
    {
        if (slot == _holeSlot) return;
        _holeSlot = slot;
        Log($"空档 -> {slot}（可见 {VisibleCount} 行 / 整体 {_tiles.Count} 行）");

        ApplyRowOffsets(animate: true);
        AnimatePad();
        ShowHole();
    }

    /// <summary>空档提示框：跟行一样大小，画在浮层上（在行的下面一层）。</summary>
    private void ShowHole()
    {
        if (_holeSlot < 0)
        {
            if (_hole is not null) FadeElement(_hole, 0, 110);
            return;
        }

        if (_hole is null)
        {
            _hole = new Border
            {
                Width = TileW,
                Height = TileH,
                CornerRadius = new CornerRadius(6),
                Background = Br("ModHoverBg"),
                BorderBrush = Br("ModAccent"),
                BorderThickness = new Thickness(1),
                Opacity = 0
            };
            HoleLayer.Children.Add(_hole);
        }

        // 位置是**瞬时**换的：空档换格时，被跨过的那一行正好滑过来把它盖住、再露出来，
        // 看着就是"缝被填上、又在下一格重新裂开"。给空档自己也做滑动，反而会和行的动画对不齐。
        Canvas.SetLeft(_hole, 0);
        Canvas.SetTop(_hole, _rowsTop + _holeSlot * GapDip);
        FadeElement(_hole, 1, 110);
    }

    /// <summary>末尾垫片：空档打开时补一格高度（预览条跟着变长），合上时收回去。</summary>
    private void AnimatePad()
    {
        if (_pad is not { } pad) return;
        AnimateHeight(pad, _holeSlot >= 0 ? GapDip : 0, SlotMs);
    }

    // ── 位移补间：自己按帧插值（别改回 TranslateTransform，也别改回 Storyboard） ──
    //
    // 为什么位移没法用动画、只能这样一帧一帧算：**见上面 <see cref="RowShift"/> 的注释** ——
    // 真正的自变量是"位移有没有到达自身高度"，跟用不用 Storyboard 无关；到达了就整片不重画。
    // 所以位移只能改 Margin，而 Margin 是布局属性，没有对应的 Animation 类型，只能自己插值。
    //
    // ⚠️ 别重走这两次误判（2026-09-29 各浪费一轮）：
    //    ① 以为"漏了 EnableDependentAnimation"；② 以为"照抄 MainWindow.PlaySheetAnimation 的写法就行"。
    //    两次的依赖属性值都在逐帧正常变化，屏幕却一动不动。
    // （Height / Opacity 的 Storyboard 动画是真能重画的，不用动。）

    private sealed class SlideTween
    {
        public FrameworkElement Owner = null!;
        public Action<double> Apply = _ => { };
        public double From;
        public double To;
        public int Ms;
        public long StartTs;                    // Stopwatch 时间戳（高精度），不是 TickCount
        public Action? Done;
    }

    private readonly List<SlideTween> _tweens = new();
    private bool _tweenFrameHooked;
    private long _lastTweenStepTs;      // 上一次推补间的高精度时间戳（给看门狗判断"逐帧回调是不是停了"）
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _tweenWatch;

    /// <summary>
    /// 读 / 写一行的让位偏移。
    ///
    /// ⚠️⚠️ **偏移走 Margin，不走 RenderTransform**（2026-09-29 实测，这条是整件事的根）：
    ///    只要用 <c>TranslateTransform.Y</c> 把一行挪到"整个离开它自己那一格"（我们的让位恰好就是挪一格 = 59 = 行高 + 间距），
    ///    在 WinUI 3 上这行就**整片不再重画** —— 依赖属性的值是对的（TransformToVisual 读出来分毫不差），
    ///    但屏幕上一片空白。用户看到的就是「一旦我下滑，剩下的组件全部划消失了」。
    ///    对照实验（同一帧、同一数值 30）：走 RenderTransform 但没离开格子的两行**画得出来**；
    ///    整格离开的两行**完全看不到**。改成 Margin 就正常 —— 它是布局属性，走的是和 Height 同一条必然重画的路径。
    ///    负的 Bottom 刚好把 Top 顶掉，所以这一行总占高不变、后面的行不会被顶走。
    /// </summary>
    private static double RowShift(FrameworkElement row) => row.Margin.Top;

    private static void SetRowShift(FrameworkElement row, double v)
    {
        row.Margin = new Thickness(0, v, 0, -v);
    }

    /// <summary>把行挪到目标偏移。animate=false 直接落值（用于"刚拿起来"那一下）。</summary>
    private void SlideRow(FrameworkElement row, double to, bool animate)
    {
        if (!animate)
        {
            CancelTween(row);
            SetRowShift(row, to);
            return;
        }

        var from = RowShift(row);
        if (Math.Abs(from - to) < 0.5) return;

        StartTween(row, v => SetRowShift(row, v), from, to, SlotMs, done: null);
    }

    /// <summary>
    /// 起一个补间。同一元素上已有的补间被顶掉（后发制人）。
    ///
    /// ⚠️⚠️ 2026-10-01 改：**驱动源从 15ms 定时器换成逐帧渲染回调**。
    ///    原来是 <c>DispatcherQueueTimer(15ms)</c> + <c>Environment.TickCount64</c>，两个毛病叠在一起：
    ///      ① 定时器 15ms 和 60Hz 的 16.7ms 不同拍 → 有时一帧跳两步、有时两帧才走一步，看着一顿一顿；
    ///      ② <c>TickCount64</c> 在 Windows 上的粒度就是系统时钟约 15.6ms，进度值是**台阶式**跳的，
    ///         最后一步还常常一口气跨到终点 —— Nick 的原话「这个直停有点生硬」，说的就是这个。
    ///    现在挂在 <see cref="CompositionTarget.Rendering"/> 上（和拖动跟随同一个节拍，逐帧、对齐 vsync），
    ///    时间读数用 <see cref="Stopwatch"/> 高精度时间戳。拖动阶段顺、松手阶段也顺，两者手感才接得上。
    /// </summary>
    private void StartTween(FrameworkElement owner, Action<double> apply, double from, double to, int ms,
                            Action? done)
    {
        CancelTween(owner);
        _tweens.Add(new SlideTween
        {
            Owner = owner,
            Apply = apply,
            From = from,
            To = to,
            Ms = ms,
            StartTs = Stopwatch.GetTimestamp(),
            Done = done
        });
        HookTweenFrame();
        EnsureTweenWatch();
    }

    private void CancelTween(FrameworkElement owner)
    {
        for (var i = _tweens.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_tweens[i].Owner, owner)) _tweens.RemoveAt(i);
        }
    }

    private void HookTweenFrame()
    {
        if (_tweenFrameHooked) return;
        CompositionTarget.Rendering += OnTweenFrame;
        _tweenFrameHooked = true;
    }

    private void UnhookTweenFrame()
    {
        if (!_tweenFrameHooked) return;
        CompositionTarget.Rendering -= OnTweenFrame;
        _tweenFrameHooked = false;
    }

    private void OnTweenFrame(object? sender, object e) => StepTweens();

    /// <summary>
    /// 兜底看门狗。
    /// ⚠️ 从定时器换成逐帧回调之后带进来的新风险：<see cref="CompositionTarget.Rendering"/> 在窗口
    ///    最小化 / 不被合成时会**整个停掉**，那样动画会卡在半路，<c>Done</c> 永远不回调 ——
    ///    飞行的收尾不回来，<c>_flying</c> 就一直是 true，这一页再也拖不动了（只有重进页面才恢复）。
    ///    老代码走的是 DispatcherQueueTimer，它不看合成，所以没这个问题；换引擎就得自己补回来。
    ///    逻辑：只有在"逐帧回调已经 200ms 没动过"时才推一把。进度是按绝对时间算的，补推不会让动画跳帧。
    /// </summary>
    private void EnsureTweenWatch()
    {
        _tweenWatch ??= DispatcherQueue.CreateTimer();
        _tweenWatch.Interval = TimeSpan.FromMilliseconds(250);
        _tweenWatch.IsRepeating = true;
        _tweenWatch.Tick -= OnTweenWatch;
        _tweenWatch.Tick += OnTweenWatch;
        if (!_tweenWatch.IsRunning) _tweenWatch.Start();
    }

    private void StopTweenWatch()
    {
        _tweenWatch?.Stop();
    }

    private void OnTweenWatch(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_tweens.Count == 0) { StopTweenWatch(); return; }

        var idleMs = (Stopwatch.GetTimestamp() - _lastTweenStepTs) / (double)Stopwatch.Frequency * 1000.0;
        if (idleMs > 200) StepTweens();
    }

    private void StepTweens()
    {
        _lastTweenStepTs = Stopwatch.GetTimestamp();

        // ⚠️ done 回调统一攒到循环**外面**再放，别在循环里放：回调可能 Refresh() 把 _tweens 整个清掉，
        //    这时候还在按下标往回走的话会越界（老代码里就埋着这条：列表里剩下不止一条时正好踩中）。
        List<SlideTween>? finished = null;

        for (var i = _tweens.Count - 1; i >= 0; i--)
        {
            var tw = _tweens[i];
            var p = tw.Ms <= 0
                ? 1
                : Math.Min(1, (Stopwatch.GetTimestamp() - tw.StartTs)
                              / (double)Stopwatch.Frequency * 1000.0 / tw.Ms);

            tw.Apply(tw.From + (tw.To - tw.From) * FluidEase(p));

            if (p >= 1)
            {
                _tweens.RemoveAt(i);
                (finished ??= new List<SlideTween>()).Add(tw);
            }
        }

        if (finished is not null)
        {
            foreach (var tw in finished) tw.Done?.Invoke();
        }

        if (_tweens.Count == 0)
        {
            UnhookTweenFrame();
            StopTweenWatch();
        }
    }

    // ── 曲线：cubic-bezier(0.1, 0.9, 0.2, 1) ──────────────────────────────────
    //
    // 这是 Windows 系统动画自己用的那条（开始菜单展开、任务栏预览之类）。形状：起步就冲出去大半，
    // 后半段拖一条长长的尾巴慢慢蹭到位 —— 末速度趋近 0 但**不是**硬刹，所以收尾不"直停"。
    //
    // ⚠️ 三条动效（飞行 FlightMs / 让位 SlotMs / 形变 MorphMs）统一走这一条。分开用不同曲线时，
    //    松手瞬间会看到"位置已经停了、缩放还在收"，节奏对不上，反而更生硬。
    // ⚠️ 这是自算的贝塞尔，不是 <c>CubicEase</c> —— 后者只有三种固定形状，尾巴不够长。
    private const double FluidX1 = 0.1, FluidY1 = 0.9, FluidX2 = 0.2, FluidY2 = 1.0;

    /// <summary>三次贝塞尔单轴求值（P0 = 0、P3 = 1，所以只剩两个控制点）。</summary>
    private static double BezierAxis(double a, double b, double t)
    {
        var m = 1 - t;
        return 3 * a * t * m * m + 3 * b * t * t * m + t * t * t;
    }

    /// <summary>
    /// 进度 p → 缓动后的进度。贝塞尔是参数式的（x 和 y 各自随 t 走），要拿到"时间 = p 时的值"，
    /// 得先反解出 t。二分 18 次 ≈ 1/26 万精度，足够，而且这段每帧最多算几十次，开销可忽略。
    /// </summary>
    private static double FluidEase(double p)
    {
        if (p <= 0) return 0;
        if (p >= 1) return 1;

        var lo = 0.0;
        var hi = 1.0;
        for (var i = 0; i < 18; i++)
        {
            var mid = (lo + hi) * 0.5;
            if (BezierAxis(FluidX1, FluidX2, mid) < p) lo = mid; else hi = mid;
        }
        return BezierAxis(FluidY1, FluidY2, (lo + hi) * 0.5);
    }

    /// <summary>
    /// 高度动画（垫片用）。
    /// ⚠️ 改走 <see cref="StartTween"/> 而不是 Storyboard：Storyboard 那边只能用 <c>CubicEase</c>，
    ///    形状和方案A（cubic-bezier 0.1/0.9/0.2/1）对不上，垫片会比旁边的行早停一截。
    ///    反正 Margin / Width / Height 本来就是同一套手写插值，走一条路更好维护。
    /// ⚠️ Height 是布局属性（"依赖动画"），Storyboard 时代要开 EnableDependentAnimation；手写插值直接写值，没这回事。
    /// </summary>
    private void AnimateHeight(FrameworkElement el, double to, int ms)
    {
        var from = el.Height;
        if (double.IsNaN(from)) from = 0;
        if (Math.Abs(from - to) < 0.5) return;

        StartTween(el, v => el.Height = v, from, to, ms, done: null);
    }

    /// <summary>
    /// 透明度淡入淡出。
    /// ⚠️ 用 <c>FillBehavior.Stop</c> + 先把基准值写成目标值：HoldEnd 会把动画值**钉住**，
    ///    之后再给 Opacity 赋基准值是不生效的（下次想淡出就淡不动了）。
    /// </summary>
    private static void FadeElement(FrameworkElement el, double to, int ms)
    {
        if (Math.Abs(el.Opacity - to) < 0.01) return;

        var anim = new DoubleAnimation
        {
            From = el.Opacity,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        el.Opacity = to;                                // 基准值 = 目标值：动画跑完自动交还给它
        Storyboard.SetTarget(anim, el);
        Storyboard.SetTargetProperty(anim, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    /// <summary>指针是不是按在某个 Button 上（那些小按钮要自己处理点击，别被拖拽抢走）。</summary>
    private static bool InsideButton(object? src)
    {
        var d = src as DependencyObject;
        while (d is not null)
        {
            if (d is ButtonBase) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>拖拽过程的流水账（出问题看 %LOCALAPPDATA%\ClassSoftwareHub\layout.log）。</summary>
    private void Log(string text)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassSoftwareHub");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "layout.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {text}{Environment.NewLine}");
        }
        catch
        {
            // 日志写不进去就算了
        }
    }

    private void MoveTo(string id, int index)
    {
        var from = _ids.IndexOf(id);
        if (from >= 0)
        {
            if (from < index) index--;                  // 先摘后插，位置要减一
            _ids.RemoveAt(from);
        }

        index = Math.Clamp(index, 0, _ids.Count);
        _ids.Insert(index, id);
        Save();
        Refresh();
    }

    // ── 格子上的小操作 ───────────────────────────────────────────────────

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string id) return;
        if (_ids.Remove(id)) { Save(); Refresh(); }
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(sender, -1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(sender, +1);

    private void Move(object sender, int delta)
    {
        if (sender is not Button b || b.Tag is not string id) return;
        var i = _ids.IndexOf(id);
        if (i < 0) return;
        var j = i + delta;
        if (j < 0 || j >= _ids.Count) return;

        AnimateSwap(i, j);
    }

    /// <summary>
    /// ↑ / ↓ 点一下：两行**同时反向滑一格**，滑完再落盘重建。
    ///
    /// ⚠️ 只做滑动 —— **不压透明度、不缩小**（老写法在中间把被点的那行压到 0.72、缩到 0.94）。
    ///    两行是朝相反方向滑开的（一个 +59、一个 −59），中途根本不会重合，
    ///    那两个原本用来"区分两张卡"的动作没有意义，反而让被点的那行在中点那一刻看着快没了
    ///    —— 用户 2026-09-29 反馈的"缩小了一下，移到一半就消失了"就是它。
    /// ⚠️ 被点的那行抬到上层（Canvas.ZIndex）：万一真和谁重叠，也该是"手上这张"在上面。
    /// </summary>
    private void AnimateSwap(int i, int j)
    {
        if (_swapping) return;
        if (i == j || i < 0 || j < 0 || i >= _tiles.Count || j >= _tiles.Count) return;
        if (_dragId is not null || _holeSlot >= 0) return;      // 正在拖的时候别叠交换动画

        var mover = _tiles[i];          // 用户点的那一行
        var other = _tiles[j];
        var dy = (j - i) * GapDip;      // 相邻两行 = 59

        SlideRow(mover, 0, animate: false);     // 起点归零，同时把可能还挂着的补间收掉
        SlideRow(other, 0, animate: false);
        Canvas.SetZIndex(mover, 1);
        _swapping = true;

        var left = 2;
        void Finish()
        {
            if (--left > 0) return;
            (_ids[i], _ids[j]) = (_ids[j], _ids[i]);
            Refresh();          // 先重建：新行没有偏移，静态位置就是动画终点，看不出接缝
            Canvas.SetZIndex(mover, 0);
            _swapping = false;
            Save();
        }

        StartTween(mover, v => SetRowShift(mover, v), 0, dy, SwapMs, done: Finish);
        StartTween(other, v => SetRowShift(other, v), 0, -dy, SwapMs, done: Finish);
    }

    private void ResetDefault_Click(object sender, RoutedEventArgs e)
    {
        _ids.Clear();
        _ids.AddRange(SidebarModules.DefaultIds);
        Save();
        Refresh();
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        if (ToolSidebarWindow.IsSidebarVisible) ToolSidebarWindow.HideSidebar();
        else ToolSidebarWindow.ShowSidebar();
        Refresh();
    }

    /// <summary>
    /// 侧边栏底部那排按钮里**某一颗**的显示开关（收起 / 常驻 / 位置复原 / 隐藏 / 打开应用，五颗各自一个）。
    /// 落设置后立刻让真侧边栏重排 —— 竖条的高矮、横条（贴上/下边）的宽窄都跟着**可见颗数**变
    /// （见 <c>ToolSidebarWindow.PlannedSize</c>）。
    /// </summary>
    private void FooterItem_Toggled(object sender, RoutedEventArgs e)
    {
        if (_footerSync) return;                                     // 是 Refresh 同步过来的回声，不是用户拨的
        if (sender is not ToggleSwitch t || t.Tag is not string key) return;

        var hidden = (App.Settings.Current.SidebarFooterHidden ?? Array.Empty<string>()).ToList();
        if (t.IsOn) hidden.Remove(key);
        else if (!hidden.Contains(key)) hidden.Add(key);

        App.Settings.Current.SidebarFooterHidden = hidden.ToArray();
        App.Settings.Save();
        ToolSidebarWindow.ApplyFooterSetting();
    }

    // ── 常用工具 / 侧边栏开关 ─────────────────────────────────────────────
    // 2026-10-01 从「设置 → 常用工具」整块搬来（浮窗置顶 / 屏幕边缘侧边栏 / 放置模式 / 贴哪条边）。
    // 逻辑原样保留，只把"加载中"的守卫从设置页的 _loading 换成这一页的 _toolSync。
    // （截图那两项没搬 —— 跟布局无关，仍在设置页。）

    private void PaletteTopSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_toolSync) return;
        App.MainWindow?.SetPaletteOnTop(PaletteTopSwitch.IsOn);
    }

    private void SidebarSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_toolSync) return;
        App.Settings.Current.SidebarEnabled = SidebarSwitch.IsOn;
        App.Settings.Save();
        ToolSidebarWindow.ApplySetting();

        // 右下角那颗按钮跟这个开关说的是同一件事（都看 SidebarEnabled），跟着换字
        if (SidebarButton is not null)
            SidebarButton.Content = ToolSidebarWindow.IsSidebarVisible ? "隐藏侧边栏" : "显示侧边栏";
    }

    // ── 侧边栏放置模式 / 贴在哪条边 ───────────────────────────────────────
    // 2026-09-28 Nick：这两项本质是"多个互斥选项里选一个"，跟「颜色模式」「更新通道」同类，
    // 一律做成下拉；不做成一排单选按钮（会把 Header 和控件挤到卡片两端，中间空出一大片）。

    /// <summary>
    /// 按当前放置模式重建「贴在哪条边」的选项：
    /// 贴靠 = 左 / 左右两边 / 右；自由 = 左 / 右 / 上 / 下。
    /// ⚠️ 重建期间必须挡住 SelectionChanged —— Items.Clear() 会把 SelectedIndex 打成 -1，
    ///    不然会把设置误写成空值。
    /// </summary>
    private void RebuildEdgeCombo()
    {
        var s = App.Settings.Current;
        var free = s.SidebarMode == "free";

        _edgeRebuild = true;
        try
        {
            SidebarEdgeCombo.Items.Clear();
            if (free)
            {
                AddEdgeItem("左边", "left");
                AddEdgeItem("右边", "right");
                AddEdgeItem("上边", "top");
                AddEdgeItem("下边", "bottom");
                SelectEdge(s.SidebarFreeEdge);
            }
            else
            {
                AddEdgeItem("左边", "left");
                AddEdgeItem("左右两边", "both");
                AddEdgeItem("右边", "right");
                SelectEdge(s.SidebarEdge);
            }
        }
        finally
        {
            _edgeRebuild = false;
        }
    }

    private void AddEdgeItem(string text, string tag) =>
        SidebarEdgeCombo.Items.Add(new ComboBoxItem { Content = text, Tag = tag });

    private void SelectEdge(string tag)
    {
        foreach (var item in SidebarEdgeCombo.Items)
        {
            if (item is ComboBoxItem it && it.Tag as string == tag)
            {
                SidebarEdgeCombo.SelectedItem = it;
                return;
            }
        }
        if (SidebarEdgeCombo.Items.Count > 0) SidebarEdgeCombo.SelectedIndex = 0;
    }

    /// <summary>说明文案随模式切换，免得对着下拉不知道是两条边还是四条边。</summary>
    private void UpdateSidebarHints()
    {
        var free = App.Settings.Current.SidebarMode == "free";

        ModeHint.Text = free
            ? "侧边栏可吸附屏幕任意一条边；贴上边或下边时呈横条。"
            : "侧边栏只吸附屏幕左右两条边，可选左右同时显示。";

        EdgeHint.Text = free
            ? "四条边均可吸附；贴上边或下边时侧边栏为横条。拖动收起状态的抓手也可改边。"
            : "选「左右两边」时两侧同时显示，上下位置保持一致，拖动其中一条另一条同步移动。拖动收起状态的抓手也可改边。";
    }

    private void SidebarMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_toolSync) return;
        if (SidebarModeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string mode) return;

        App.Settings.Current.SidebarMode = mode;
        App.Settings.Save();

        // 模式变了 → 可选的边也变了，下拉要整个换一套
        RebuildEdgeCombo();
        UpdateSidebarHints();

        if (App.Settings.Current.SidebarEnabled) ToolSidebarWindow.ApplySetting();
    }

    private void SidebarEdge_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_toolSync || _edgeRebuild) return;
        if (SidebarEdgeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string edge) return;

        var s = App.Settings.Current;
        if (s.SidebarMode == "free") s.SidebarFreeEdge = edge;
        else s.SidebarEdge = edge;
        App.Settings.Save();

        if (s.SidebarEnabled) ToolSidebarWindow.ApplySetting();
    }

    // ── 动画方案（2026-10-01：两种落位收尾都挺好，留给用户自己挑） ──────────

    /// <summary>「动画方案」的取值 → 落位收缩幅度。认不出的值一律当「轻落一下」。</summary>
    private static double DropDipScale(string? style) => style == "plain" ? 1.0 : DipScale;

    /// <summary>
    /// 「动画方案 → 落位收尾」切换。
    /// ⚠️ 立即生效、**不需要重建任何东西**：<see cref="_dropDip"/> 只在下一次 <see cref="SettleGhost"/> 里被读一次。
    /// </summary>
    private void DropAnim_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_toolSync) return;
        if (DropAnimCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string style) return;

        App.Settings.Current.SidebarDropAnim = style;
        App.Settings.Save();

        _dropDip = DropDipScale(style);
    }

    // ── 截图（2026-10-01 整组从「设置」页搬来，逻辑原样） ────────────────
    // 守卫从设置页那套 _loading 换成 _toolSync：这页的"回声"闸门就这一个。

    private void ShotAutoSave_Toggled(object sender, RoutedEventArgs e)
    {
        if (_toolSync) return;
        App.Settings.Current.ShotAutoSave = ShotAutoSaveSwitch.IsOn;
        App.Settings.Save();
        RefreshShotDir();
    }

    /// <summary>把当前保存位置显示出来（没设 = 桌面）。</summary>
    private void RefreshShotDir()
    {
        var dir = Services.ShotSaver.DirSetting();
        var custom = !string.IsNullOrWhiteSpace(App.Settings.Current.ShotSaveDir);
        ShotDirText.Text = custom ? dir : $"{dir}（默认：桌面，没改过）";
        ShotDirText.Opacity = ShotAutoSaveSwitch.IsOn ? 0.7 : 0.4;
    }

    private async void ShotDir_Change_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop,
            };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            App.Settings.Current.ShotSaveDir = folder.Path;
            App.Settings.Save();
            RefreshShotDir();
        }
        catch (Exception ex)
        {
            Services.ScreenCapture.Log("选截图目录失败: " + ex.Message);
        }
    }

    private void ShotDir_Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Services.ShotSaver.Dir();
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Services.ScreenCapture.Log("打开截图目录失败: " + ex.Message);
        }
    }

    private void ShotDir_Reset_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.ShotSaveDir = "";                // 空 = 桌面
        App.Settings.Save();
        RefreshShotDir();
    }
}
