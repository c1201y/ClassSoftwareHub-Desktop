using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Views;
using Microsoft.UI;
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
/// </summary>
public sealed partial class SidebarLayoutPage : Page
{
    /// <summary>指针挪过这么多像素才算"在拖"，否则当点击。</summary>
    private const double DragSlop = 4;

    /// <summary>让位开的那条缝 = 格子高 56 + 间距 3。</summary>
    private const double GapDip = 59;

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
    private Point _grabOffset;              // 按下点在卡片内的位置（替身要按这个对齐指针）
    private FrameworkElement? _pressSource;
    private Border? _ghost;                 // 跟着指针跑的那张卡
    private Border? _spacer;                // 让位用的"空档"（在缝的位置插进去，自己长高）
    private int _gapIndex = -1;             // 现在让开的是第几个缝
    private bool _swapping;                // ↑/↓ 的滑动动画正在进行（这段时间别再点）
    private double _pageScrollOffset;       // 拖起来之前的滚动位置（拖的时候要把整页滚动锁掉）

    public SidebarLayoutPage()
    {
        InitializeComponent();
        LoadFromSettings();

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
            Width = 96,
            Height = 56,
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

        Log($"按下 {id} 来源={(fromPreview ? "预览" : "模块库")} 行数={_tiles.Count}");

        _dragId = id;
        _dragFromPreview = fromPreview;
        _dragging = false;
        _gapIndex = -1;
        _pressSource = src;
        _pointerId = e.Pointer.PointerId;
        _pressInRoot = e.GetCurrentPoint(WorkspaceRoot).Position;

        var inSrc = e.GetCurrentPoint(src).Position;
        _grabOffset = new Point(inSrc.X, inSrc.Y);

        CaptureBaseY();

        // 拖的时候把整页滚动锁掉：不然触摸拖拽会被外层 ScrollViewer 当"滚动"处理，
        // 整页跟着跑 —— 看起来就像模块全没了
        _pageScrollOffset = PageScroll.VerticalOffset;
        PageScroll.VerticalScrollMode = ScrollMode.Disabled;
        PageScroll.HorizontalScrollMode = ScrollMode.Disabled;

        src.CapturePointer(e.Pointer);
        e.Handled = true;                              // 别让外层的滚动条抢走
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragId is null || e.Pointer.PointerId != _pointerId) return;

        var inRoot = e.GetCurrentPoint(WorkspaceRoot).Position;
        if (!_dragging)
        {
            if (Math.Abs(inRoot.X - _pressInRoot.X) + Math.Abs(inRoot.Y - _pressInRoot.Y) < DragSlop) return;

            _dragging = true;
            if (_pressSource is not null) _pressSource.Opacity = 0.45;   // 原位置变淡，视觉上"被抠起来了"
            ShowGhost();
        }

        e.Handled = true;
        MoveGhost(e.GetCurrentPoint(DragLayer).Position);

        var inStrip = e.GetCurrentPoint(PreviewStrip).Position;
        var inside = inStrip.X >= 0 && inStrip.Y >= 0
                     && inStrip.X <= PreviewStrip.ActualWidth && inStrip.Y <= PreviewStrip.ActualHeight;

        UpdateGap(inside ? IndexFromPointer(e.GetCurrentPoint(PreviewPanel).Position.Y) : -1);

        // 从预览里往外拖：提示"松手就移除"
        RemoveHint.Visibility = (!inside && _dragFromPreview) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragId is null || e.Pointer.PointerId != _pointerId) return;

        var id = _dragId;
        var fromPreview = _dragFromPreview;
        var dragged = _dragging;

        var inStrip = e.GetCurrentPoint(PreviewStrip).Position;
        var inside = inStrip.X >= 0 && inStrip.Y >= 0
                     && inStrip.X <= PreviewStrip.ActualWidth && inStrip.Y <= PreviewStrip.ActualHeight;
        var index = inside ? IndexFromPointer(e.GetCurrentPoint(PreviewPanel).Position.Y) : -1;

        (_pressSource as UIElement)?.ReleasePointerCapture(e.Pointer);
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

    /// <summary>拖到一半被系统打断（比如窗口失焦）：收拾干净，不动数据。</summary>
    private void CancelDrag()
    {
        if (_dragId is null) return;
        EndDrag();
        Refresh();
    }

    private void EndDrag()
    {
        HideGhost();
        RemoveHint.Visibility = Visibility.Collapsed;
        if (_pressSource is not null) _pressSource.Opacity = 1;

        // 滚动解锁 + 把位置放回去（某些情况下锁滚动会把偏移归零）
        PageScroll.VerticalScrollMode = ScrollMode.Auto;
        PageScroll.HorizontalScrollMode = ScrollMode.Auto;
        PageScroll.ChangeView(null, _pageScrollOffset, null, true);

        _dragId = null;
        _dragging = false;
        _pressSource = null;
        _gapIndex = -1;
    }

    private void ShowGhost()
    {
        if (_dragId is null || SidebarModules.Find(_dragId) is not { } m) return;

        var content = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        content.Children.Add(new FontIcon { Glyph = m.Glyph, FontSize = 19, HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock
        {
            Text = m.ShortName,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Br("ModText")
        });

        var ghost = new Border
        {
            Width = 92,
            Height = 56,
            CornerRadius = new CornerRadius(8),
            Background = Br("ModCardBg"),
            BorderBrush = Br("ModAccent"),
            BorderThickness = new Thickness(1.5),
            Opacity = 0.95,
            Child = content,
            Shadow = new ThemeShadow(),
            Translation = new Vector3(0, 0, 32)
        };

        _ghost = ghost;
        DragLayer.Children.Add(ghost);
    }

    private void MoveGhost(Point inLayer)
    {
        if (_ghost is null) return;
        Canvas.SetLeft(_ghost, inLayer.X - _grabOffset.X);
        Canvas.SetTop(_ghost, inLayer.Y - _grabOffset.Y);
    }

    private void HideGhost()
    {
        if (_ghost is null) return;
        DragLayer.Children.Remove(_ghost);
        _ghost = null;
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
            Width = 96,
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
    /// ↑ / ↓ 点一下：两行**滑过去**再落位，不是瞬间跳（瞬跳太不显眼了）。
    /// 做法：给这两行套上 TranslateTransform，一行 +59、另一行 −59 同时跑完；
    /// 动画结束后把 transform 归零再重建列表 —— 重建后的静态位置跟动画终点重合，看不出接缝。
    /// 动画期间不接受新的点击（180ms，很短）。
    /// </summary>
    private void AnimateSwap(int i, int j)
    {
        if (_swapping) return;
        if (i == j || i < 0 || j < 0 || i >= _tiles.Count || j >= _tiles.Count) return;

        var a = _tiles[i];
        var b = _tiles[j];
        var dy = (j - i) * GapDip;      // 相邻两行 = 59

        var ta = Shift(a);
        var tb = Shift(b);
        ta.Y = 0;
        tb.Y = 0;
        _swapping = true;

        var sb = new Storyboard();
        sb.Children.Add(Slide(ta, dy));
        sb.Children.Add(Slide(tb, -dy));
        sb.Completed += (_, _) =>
        {
            _swapping = false;
            ta.Y = 0;                   // 先归零，免得重建那一刻闪一下
            tb.Y = 0;
            (_ids[i], _ids[j]) = (_ids[j], _ids[i]);
            Save();
            Refresh();
        };
        sb.Begin();
    }

    /// <summary>拿这一行的位移动画对象（没有就配一个）。</summary>
    private static TranslateTransform Shift(FrameworkElement el)
    {
        if (el.RenderTransform is TranslateTransform t) return t;
        t = new TranslateTransform();
        el.RenderTransform = t;
        return t;
    }

    private static DoubleAnimation Slide(TranslateTransform t, double to)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, t);
        Storyboard.SetTargetProperty(anim, "Y");
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
