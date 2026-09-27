using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ClassSoftwareHub.Desktop.Data;
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
    private const int SwapMs = 220;

    /// <summary>预览格子的尺寸，和真侧边栏一致。</summary>
    private const double TileW = 96;
    private const double TileH = 56;

    /// <summary>当前拼好的模块 id，顺序 = 侧边栏上从上到下。</summary>
    private readonly List<string> _ids = new();

    /// <summary>预览条里的模块格子（顺序同上）。</summary>
    private readonly List<FrameworkElement> _tiles = new();

    /// <summary>按下那一刻各格子的原始 Y（不含偏移）—— 算落点用它，免得被让位动画带着来回抖。</summary>
    private readonly List<double> _tileBaseY = new();

    private string? _dragId;                // 正被按住的模块
    private bool _dragFromPreview;          // 是从预览里拖的，还是从库里拖的
    private bool _dragging;                 // 已越过阈值，真在拖了
    private uint _pointerId;
    private Point _pressInRoot;
    private FrameworkElement? _pressSource;
    private Border? _ghost;                 // 跟着指针跑的那张卡
    private Border? _spacer;                // 让位用的"空档"（在缝的位置插进去，自己长高）
    private int _gapIndex = -1;             // 现在让开的是第几个缝
    private bool _swapping;                 // ↑/↓ 的滑动动画正在进行（这段时间别再点）
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
            Text = used ? "已经在侧边栏里了" : m.Hint,
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
        ToolTipService.SetToolTip(card, used ? $"{m.Name}（已在侧边栏里）" : $"{m.Name} —— 点一下加进侧边栏，也可以拖到右边");

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
        _tiles.Clear();
        _spacer = null;

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
                Text = "把左边\n卡片拖进来",
                FontSize = 10.5,
                Opacity = 0.5,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Width = 96,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(4, 10, 4, 10)
            });
        }

        // 注：侧边栏自带的「收起 / 位置复原 / 隐藏」在这里**不画**（不能拼不能删，画出来只会挤位置）。
        // 让位用的空档也不再有"顶到下面固定键"的顾虑，所以不用预留额外高度。
    }

    /// <summary>
    /// 预览里的一行：左边是模块格子（照侧边栏的样子），右边三个独立按钮（上移 / 下移 / 移除）。
    /// 按钮做得大（38×38）且常显 —— 之前挤在格子里 20×17，触屏根本点不准。
    /// 整行一起做让位动画，所以带按钮一起挪。
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
        actions.Children.Add(ActionButton("\uE70E", "往上挪一位", m.Id, MoveUp_Click, index > 0));
        actions.Children.Add(ActionButton("\uE70D", "往下挪一位", m.Id, MoveDown_Click, index < _ids.Count - 1));
        actions.Children.Add(ActionButton("\uE711", "从侧边栏移除", m.Id, Remove_Click, true));

        var row = new Grid { Height = 56, ColumnSpacing = 8 };
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
        ToolTipService.SetToolTip(tile, $"{m.Name} —— 拖着重排，拖到别处松手就移除");

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
        ToolTipService.SetToolTip(tile, "侧边栏自带的按钮，不参与拼接");
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
        _gapIndex = -1;
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

        CaptureBaseY();

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
        _gapIndex = -1;
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
            if (_pressSource is not null) _pressSource.Opacity = 0.45;   // 原位置变淡，视觉上"被抠起来了"
            ShowGhost();
        }

        e.Handled = true;
        _pointerInLayer = e.GetCurrentPoint(DragLayer).Position;
        PlaceGhost();                                   // 尺寸动画可能还没跑完，位置交给每帧回调兜住

        var inStrip = e.GetCurrentPoint(PreviewStrip).Position;
        var inside = inStrip.X >= 0 && inStrip.Y >= 0
                     && inStrip.X <= PreviewStrip.ActualWidth && inStrip.Y <= PreviewStrip.ActualHeight;

        UpdateGap(inside ? IndexFromPointer(e.GetCurrentPoint(PreviewPanel).Position.Y) : -1);

        // 进了预览条就收缩成"预览格子"的样子，离开再变回卡片大小 —— 拖到哪儿就是哪儿的样子
        if (!_dragFromPreview) MorphGhost(inside);

        // 从预览里往外拖：提示"松手就移除"
        RemoveHint.Visibility = (!inside && _dragFromPreview) ? Visibility.Visible : Visibility.Collapsed;
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
        var index = inside ? IndexFromPointer(e.GetCurrentPoint(PreviewPanel).Position.Y) : -1;

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
        _gapIndex = -1;
        _maxMove = 0;
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

    /// <summary>算落点：指针落在第几个格子的上半边，就插到它前面（全在下面 = 插到最后）。</summary>
    private int IndexFromPointer(double yInPanel)
    {
        for (var i = 0; i < _tiles.Count && i < _tileBaseY.Count; i++)
        {
            if (yInPanel < _tileBaseY[i] + _tiles[i].ActualHeight / 2) return i;
        }
        return _tiles.Count;
    }

    /// <summary>拖起来那一刻记下每个格子的原始位置（让位动画会挪它们，落点判断不能受它影响）。</summary>
    private void CaptureBaseY()
    {
        _tileBaseY.Clear();
        foreach (var t in _tiles)
        {
            var p = t.TransformToVisual(PreviewPanel).TransformPoint(new Point(0, 0));
            _tileBaseY.Add(p.Y);
        }
    }

    /// <summary>
    /// 把缝让出来：在 index 之前插一个"空档"，让它自己长到一格高 —— 后面的模块是被**真实布局**顶下去的，
    /// 不是靠 transform 平移（平移那套会飘、会被裁，之前"三个组件下滑消失"就是它）。
    /// index &lt; 0 = 合上缝。
    /// </summary>
    private void UpdateGap(int index)
    {
        if (index == _gapIndex) return;
        _gapIndex = index;
        Log($"缝 -> {index}（当前 {_tiles.Count} 行）");

        if (index < 0)
        {
            CloseSpacer();
            return;
        }

        var spacer = EnsureSpacer();
        if (PreviewPanel.Children.Contains(spacer)) PreviewPanel.Children.Remove(spacer);

        // 插到第 index 个模块前面（index = 个数时插到最后）
        var anchor = index < _tiles.Count ? _tiles[index] : null;
        var pos = anchor is null ? PreviewPanel.Children.Count : PreviewPanel.Children.IndexOf(anchor);
        PreviewPanel.Children.Insert(pos, spacer);

        // 空档 56 + StackPanel 间距 3 = 59，正好一格
        AnimateSpacer(spacer, GapDip - 3);
    }

    private Border EnsureSpacer() =>
        _spacer ??= new Border
        {
            Width = TileW,
            Height = 0,
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Br("ModHoverBg"),
            BorderBrush = Br("ModAccent"),
            BorderThickness = new Thickness(1)
        };

    private void CloseSpacer()
    {
        if (_spacer is null) return;
        var spacer = _spacer;
        AnimateSpacer(spacer, 0, () =>
        {
            if (_gapIndex < 0 && spacer.Height <= 0.5) PreviewPanel.Children.Remove(spacer);
        });
    }

    /// <summary>空档高度动画。⚠️ 动 Height 属于"依赖动画"，必须开 EnableDependentAnimation，否则直接被忽略。</summary>
    private static void AnimateSpacer(FrameworkElement spacer, double to, Action? done = null)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(130)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(anim, spacer);
        Storyboard.SetTargetProperty(anim, "Height");

        var sb = new Storyboard();
        sb.Children.Add(anim);
        if (done is not null) sb.Completed += (_, _) => done();
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
    /// ↑ / ↓ 点一下：两行**滑过去**再落位（瞬间跳太不显眼）。
    ///
    /// ⚠️ 为什么不能只是"两行同时反向平移"（之前那版就是这么写的，看着很怪）：
    ///    两行尺寸一样、底色一样，平移路程都是 59px 而且是同时反向跑的，
    ///    于是中途**必然完全重合**，屏幕上就是"两张卡片糊成一团、然后又分开"。
    ///    现在让被点的那一行"浮起来"：滑动时略微缩小 + 压低透明度，像从另一行上面飘过去，
    ///    重合的那一瞬间也能一眼分清是两张卡。缓动也换成 EaseInOut（EaseOut 起步太冲）。
    /// </summary>
    private void AnimateSwap(int i, int j)
    {
        if (_swapping) return;
        if (i == j || i < 0 || j < 0 || i >= _tiles.Count || j >= _tiles.Count) return;

        var mover = _tiles[i];          // 用户点的那一行：让它浮起来走
        var other = _tiles[j];
        var dy = (j - i) * GapDip;      // 相邻两行 = 59

        var tm = Shift(mover);
        var to = Shift(other);
        ResetTransform(tm);
        ResetTransform(to);
        _swapping = true;

        var sb = new Storyboard();
        sb.Children.Add(SlideY(tm, dy));
        sb.Children.Add(SlideY(to, -dy));
        sb.Children.Add(Fade(mover, 0.72));
        sb.Children.Add(Fade(other, 0.88));
        sb.Children.Add(ScaleKey(tm, "ScaleX", 0.94));
        sb.Children.Add(ScaleKey(tm, "ScaleY", 0.94));
        sb.Completed += (_, _) =>
        {
            (_ids[i], _ids[j]) = (_ids[j], _ids[i]);
            Refresh();          // 先重建：新行没有 transform，静态位置就是动画终点，看不出接缝
            sb.Stop();          // 再显式收工 —— HoldEnd 会把旧行钉在中间位置，虽然它已经不在树上了
            _swapping = false;
            Save();
        };
        sb.Begin();
    }

    /// <summary>拿这一行的变换对象（没有就配一个）。⚠️ 缩放要围绕行中心，不然会从左上角缩。</summary>
    private static CompositeTransform Shift(FrameworkElement el)
    {
        if (el.RenderTransform is CompositeTransform t) return t;
        t = new CompositeTransform();
        el.RenderTransform = t;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        return t;
    }

    private static void ResetTransform(CompositeTransform t)
    {
        t.TranslateX = 0;
        t.TranslateY = 0;
        t.ScaleX = 1;
        t.ScaleY = 1;
    }

    private static DoubleAnimation SlideY(CompositeTransform t, double to)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(SwapMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(anim, t);
        Storyboard.SetTargetProperty(anim, "TranslateY");
        return anim;
    }

    /// <summary>滑到一半变淡一点、过去之后再回来 —— 这就是"浮起来"的那点层次感。</summary>
    private static DoubleAnimationUsingKeyFrames Fade(FrameworkElement el, double mid)
    {
        var anim = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(SwapMs))
        };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 1 });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = TimeSpan.FromMilliseconds(SwapMs * 0.45),
            Value = mid,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = TimeSpan.FromMilliseconds(SwapMs),
            Value = 1,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        });
        Storyboard.SetTarget(anim, el);
        Storyboard.SetTargetProperty(anim, "Opacity");
        return anim;
    }

    private static DoubleAnimationUsingKeyFrames ScaleKey(CompositeTransform t, string prop, double mid)
    {
        var anim = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(SwapMs))
        };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 1 });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = TimeSpan.FromMilliseconds(SwapMs * 0.45),
            Value = mid,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = TimeSpan.FromMilliseconds(SwapMs),
            Value = 1,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        });
        Storyboard.SetTarget(anim, t);
        Storyboard.SetTargetProperty(anim, prop);
        return anim;
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
}
