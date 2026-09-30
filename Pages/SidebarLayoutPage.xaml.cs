using System;
using System.Collections.Generic;
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
    private const int MorphMs = 150;

    /// <summary>↑/↓ 交换动画的时长。</summary>
    private const int SwapMs = 210;

    /// <summary>让位（行跟着空档挪）的时长。跟空档的上下出现同一档，看着才是"一起动"。</summary>
    private const int SlotMs = 145;

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
    private Border? _hole;                  // 浮层里画的那个"空档"提示框
    private Border? _pad;                   // 末尾的垫片：空档打开时它长一格高，预览条跟着变长
    private FrameworkElement? _hiddenRow;   // 从预览里拿起来后暂时收起来的那一行
    private int _holeSlot = -1;             // 空档开在第几个可见位置（-1 = 合上）
    private int _dragSrcIndex = -1;         // 拿起来的那一行在 _tiles 里的下标（-1 = 从模块库拖的）
    private double _rowsTop;                // 第一行在 PreviewPanel 里的 Y（按下那一刻量的）
    private bool _swapping;                 // ↑/↓ 的滑动动画正在进行（这段时间别再点）
    private bool _footerSync;               // 正在把设置刷进底排的那五个开关（此时 Toggled 是"回声"，别当用户拨的）
    private double _pageScrollOffset;       // 拖起来之前的滚动位置（拖的时候要把整页滚动锁掉）
    private double _maxMove;                // 按下之后指针走过的最大直线距离（用来看"这是想滚还是想拖"）

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
        Loaded += (_, _) => { Services.ThemeBrush.Probe(this, "SidebarLayoutPage.Loaded"); Refresh(); };
        ActualThemeChanged += (_, _) => Refresh();
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

        // 从预览里往外拖：提示"松手就移除"
        RemoveHint.Visibility = (!inside && _dragFromPreview) ? Visibility.Visible : Visibility.Collapsed;
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

        var inStrip = e.GetCurrentPoint(PreviewStrip).Position;
        var inside = inStrip.X >= 0 && inStrip.Y >= 0
                     && inStrip.X <= PreviewStrip.ActualWidth && inStrip.Y <= PreviewStrip.ActualHeight;

        // ⚠️ 这一步必须在 EndDrag 之前算：_dragSrcIndex / _rowsTop 都是拖动期间的状态，收尾时会清掉。
        var index = inside
            ? InsertIndexFromSlot(SlotFromPointer(e.GetCurrentPoint(PreviewPanel).Position.Y))
            : -1;

        // ⚠️ ReleasePointerCapture 会**同步**触发 PointerCaptureLost → 我们的处理器会去 CancelDrag()，
        //    那里面又 Refresh() 重建列表 —— 等于在收尾到一半时把列表换掉，后面的 MoveTo 踩在新建的对象上。
        //    用 _ending 把这段圈起来：自己收尾的时候，CaptureLost 不参与。
        _ending = true;
        (_pressSource as UIElement)?.ReleasePointerCapture(e.Pointer);
        _ending = false;
        EndDrag();

        if (!dragged)
        {
            if (!fromPreview) AddToEnd(id);             // 没拖动 = 点了一下 → 加到末尾
            return;
        }

        if (inside && index >= 0)
        {
            MoveTo(id, index);
        }
        else if (fromPreview)
        {
            _ids.Remove(id);                            // 拖出预览 = 移除
            Save();
            Refresh();
        }
        else
        {
            Refresh();                                  // 从库里拖到空处：当没干（顺手把视觉复原）
        }
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

    private void EndDrag(bool keepScrollLocked = false)
    {
        _holdTimer?.Stop();
        HintPress(false);
        HideGhost();
        HideHole();
        RestoreHiddenRow();
        RemoveHint.Visibility = Visibility.Collapsed;
        if (_pressSource is not null) _pressSource.Opacity = 1;

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
        _holeSlot = -1;
        _dragSrcIndex = -1;
        _maxMove = 0;
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

        var ghost = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(8),
            Background = Br("ModCardBg"),
            BorderBrush = Br("ModAccent"),
            BorderThickness = new Thickness(1.5),
            Opacity = 0.95,
            Child = layers,
            Shadow = new ThemeShadow(),
            Translation = new Vector3(0, 0, 32)
        };

        _ghost = ghost;
        DragLayer.Children.Add(ghost);
        HookGhostFrame();
        PlaceGhost();
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

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new FontIcon
        {
            Glyph = m.Glyph,
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
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

        var sb = new Storyboard();
        sb.Children.Add(SizeTo(_ghost, "Width", toW));
        sb.Children.Add(SizeTo(_ghost, "Height", toH));
        if (_ghostCard is not null) sb.Children.Add(FadeTo(_ghostCard, toTile ? 0 : 1, 0));
        if (_ghostTile is not null) sb.Children.Add(FadeTo(_ghostTile, toTile ? 1 : 0, MorphMs / 2));
        sb.Begin();
    }

    /// <summary>尺寸动画。⚠️ 动 Width / Height 属于"依赖动画"，不开 EnableDependentAnimation 会被直接忽略。</summary>
    private static DoubleAnimation SizeTo(FrameworkElement el, string prop, double to)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(MorphMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(anim, el);
        Storyboard.SetTargetProperty(anim, prop);
        return anim;
    }

    private static DoubleAnimation FadeTo(FrameworkElement el, double to, int delayMs)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(MorphMs / 2)),
            BeginTime = TimeSpan.FromMilliseconds(delayMs)
        };
        Storyboard.SetTarget(anim, el);
        Storyboard.SetTargetProperty(anim, "Opacity");
        return anim;
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
        Canvas.SetLeft(_ghost, _pointerInLayer.X - _grabRatio.X * w);
        Canvas.SetTop(_ghost, _pointerInLayer.Y - _grabRatio.Y * h);
    }

    private void HideGhost()
    {
        UnhookGhostFrame();
        if (_ghost is null) return;
        DragLayer.Children.Remove(_ghost);
        _ghost = null;
        _ghostCard = null;
        _ghostTile = null;
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
        public long StartMs;
        public bool EaseInOut;
        public Action? Done;
    }

    private readonly List<SlideTween> _tweens = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _tweenTick;

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

        StartTween(row, v => SetRowShift(row, v), from, to, SlotMs, easeInOut: false, done: null);
    }

    /// <summary>起一个补间。同一元素上已有的补间被顶掉（后发制人）。</summary>
    private void StartTween(FrameworkElement owner, Action<double> apply, double from, double to, int ms,
                            bool easeInOut, Action? done)
    {
        CancelTween(owner);
        _tweens.Add(new SlideTween
        {
            Owner = owner,
            Apply = apply,
            From = from,
            To = to,
            Ms = ms,
            StartMs = Environment.TickCount64,
            EaseInOut = easeInOut,
            Done = done
        });
        EnsureTweenTick();
    }

    private void CancelTween(FrameworkElement owner)
    {
        for (var i = _tweens.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_tweens[i].Owner, owner)) _tweens.RemoveAt(i);
        }
    }

    private void EnsureTweenTick()
    {
        if (_tweenTick is null)
        {
            _tweenTick = DispatcherQueue.CreateTimer();
            _tweenTick.Interval = TimeSpan.FromMilliseconds(15);
            _tweenTick.IsRepeating = true;
            _tweenTick.Tick += (_, _) => StepTweens();
        }
        if (!_tweenTick.IsRunning) _tweenTick.Start();
    }

    private void StepTweens()
    {
        for (var i = _tweens.Count - 1; i >= 0; i--)
        {
            var tw = _tweens[i];
            var p = tw.Ms <= 0 ? 1 : Math.Min(1, (Environment.TickCount64 - tw.StartMs) / (double)tw.Ms);
            var e = tw.EaseInOut ? EaseInOutCubic(p) : EaseOutCubic(p);
            tw.Apply(tw.From + (tw.To - tw.From) * e);

            if (p >= 1)
            {
                _tweens.RemoveAt(i);
                tw.Done?.Invoke();      // ⚠️ 放在移除之后：回调里可能 Refresh()（整列表换掉）
            }
        }
        if (_tweens.Count == 0) _tweenTick?.Stop();
    }

    private static double EaseOutCubic(double p) => 1 - Math.Pow(1 - p, 3);

    private static double EaseInOutCubic(double p) =>
        p < 0.5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2;

    /// <summary>高度动画（垫片用）。⚠️ Height 是布局属性，属于"依赖动画"，不开 EnableDependentAnimation 会被直接忽略。</summary>
    private static void AnimateHeight(FrameworkElement el, double to, int ms)
    {
        if (Math.Abs(el.Height - to) < 0.5) return;

        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(anim, el);
        Storyboard.SetTargetProperty(anim, "Height");

        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
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

        StartTween(mover, v => SetRowShift(mover, v), 0, dy, SwapMs, easeInOut: true, done: Finish);
        StartTween(other, v => SetRowShift(other, v), 0, -dy, SwapMs, easeInOut: true, done: Finish);
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
}
