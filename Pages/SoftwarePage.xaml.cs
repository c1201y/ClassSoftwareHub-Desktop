using System;
using System.Collections.Generic;
using System.Linq;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>软件下载页：搜索 + 分类筛选 + 软件卡片墙（全原生，不加载任何网页）。</summary>
public sealed partial class SoftwarePage : Page
{
    private string _category = "";
    /// <summary>
    /// 关键词过滤链（2026-10-18 起**没有 UI 入口** —— 页内搜索框已删，顶部总搜索接手了）。
    /// 刻意保留字段与 <see cref="Apply"/> 里的过滤：以后若要把"筛列表"接回顶部搜索，
    /// 只需要给它赋值，不用再把整条链路重写一遍。当前恒为空串，行为等于不过滤。
    /// </summary>
    private string _keyword = "";
    private string _view = "tile";          // tile | grid
    private bool _loading;

    /// <summary>
    /// 上一次真正重建列表时的「状态指纹」（分类|关键词|视图|内容包版本|内容来源）。
    ///
    /// 为什么要有它：本页 XAML 是 NavigationCacheMode="Enabled"，实例会被 Frame 留下来，
    /// 但 <see cref="Apply"/> 每次都把 ItemsSource 换成新的 List —— GridView 见到新集合
    /// 会把**所有卡片容器整个重建一遍**（含图标），那 200ms 左右的停顿又回来了，缓存等于白做。
    /// 指纹没变就什么都不做，"退出去再进来"就变成零成本。
    /// </summary>
    private string _appliedKey = "";

    public SoftwarePage()
    {
        InitializeComponent();

        // 两侧留白 / 标题行上的滚轮也转给卡片列表（见 Core/WheelRelay.cs）
        Core.WheelRelay.Attach(this, TileGrid, CompactGrid);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // ⛔ **只有带了分类参数才改分类**（2026-10-22）。
        //    以前无条件 `_category = e.Parameter as string ?? ""`，于是从「系统工具」点进某个软件、
        //    再返回时参数是 null ⇒ 分类被清成「全部」，用户得重新点一遍（Nick 反馈的核心问题）。
        //    现在：带参数 = 用户主动切分类；不带参数（返回/从导航进来）= **保持上次选择**。
        if (e.Parameter is string requestedCategory && requestedCategory.Length > 0)
            _category = requestedCategory;

        // 总闸关掉时入口卡整体隐藏（Nick 2026-10-18 定：不占位、不留灰块）。
        // ⚠️ 必须每次导航都重读一次 —— 本页 NavigationCacheMode=Enabled 实例长驻，
        //    用户去设置里开关一次再回来，页面不会重建，只在构造时读一次会一直是旧状态。
        HubAiEntry.Visibility = App.Settings.Current.HubAiEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;

        _view = App.Settings.Current.AppCardView == "grid" ? "grid" : "tile";
        _loading = true;
        ViewPicker.SelectedIndex = _view == "grid" ? 1 : 0;
        _loading = false;
        UpdateView();

        // 内容同步好之后自己要刷新 —— 安装包不自带清单，首启就停在"正在获取"这一屏上，
        // 用户多半不会为了看到列表专门切一次页（2026-10-05）。
        // ⚠️ 本页 NavigationCacheMode=Enabled（实例长驻），先 -= 再 += 保证只挂一次。
        App.Content.Changed -= OnContentChanged;
        App.Content.Changed += OnContentChanged;

        if (Fingerprint() == _appliedKey) return;   // 状态没变 → 保留现有卡片，别重建

        BuildChips();
        Apply();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        App.Content.Changed -= OnContentChanged;
        base.OnNavigatedFrom(e);
    }

    /// <summary>内容变了（同步完成 / 同步状态变化）：重建列表，或只刷一下空清单那几句提示。</summary>
    private void OnContentChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnContentChanged);
            return;
        }

        if (Fingerprint() != _appliedKey)
        {
            BuildChips();
            Apply();
            return;
        }

        // 指纹没变 = 内容本身没换，只是"正在同步"的开关翻了页 → 不必重建卡片，
        // 否则每次同步开始/结束都要付一次列表重建的代价。
        if (App.Content.Apps.Count == 0) UpdateEmptyState(0);
    }

    /// <summary>
    /// 当前"页面状态"的指纹；任一要素变了才值得重建列表与分类条。
    ///
    /// ⚠️ 内容那一位必须用 <see cref="Core.ContentStore.Revision"/>（每次 Load 都涨），
    /// **不能**用 ContentVersion / Source（一个恒为空、一个恒为同一路径，索引不到"清单被补全了"，
    /// 首启同步完页面就不会自己刷新 —— 2026-10-05 实测踩到）。
    /// </summary>
    private string Fingerprint()
        => string.Join("\u0001", _category, _keyword, _view,
                       App.Content.Revision.ToString(), App.Content.Source);

    /// <summary>磁贴（3 列，带简介）/ 网格（5 列，紧凑）切换，选择会记进设置。</summary>
    private void ViewPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ViewPicker.SelectedItem is not ComboBoxItem { Tag: string tag }) return;

        _view = tag == "grid" ? "grid" : "tile";
        App.Settings.Current.AppCardView = _view;
        App.Settings.Save();
        UpdateView();
    }

    private void UpdateView()
    {
        var tile = _view != "grid";
        TileGrid.Visibility = tile ? Visibility.Visible : Visibility.Collapsed;
        CompactGrid.Visibility = tile ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 分类条（2026-10-18 换成系统 <see cref="SelectorBar"/>）。
    /// ⚠️ 原来是一排自绘 ToggleButton + 横向 ScrollViewer：选中态要自己维护、
    /// 键盘导航没有、滚动条还占一行。SelectorBar 自带下划线指示器与键盘操作。
    /// </summary>
    private void BuildChips()
    {
        CategoryBar.Items.Clear();

        CategoryBar.Items.Add(new SelectorBarItem
        {
            Text = $"全部 {App.Content.Apps.Count}",
            Tag = "",
        });

        foreach (var category in App.Content.Categories)
        {
            CategoryBar.Items.Add(new SelectorBarItem
            {
                Text = $"{category.Name} {App.Content.CountInCategory(category.Key)}",
                Tag = category.Key,
            });
        }

        // 回填时不触发 Apply（否则每次切页都会重建一遍列表）
        _loading = true;
        try
        {
            CategoryBar.SelectedItem = CategoryBar.Items
                .FirstOrDefault(i => (string?)i.Tag == _category);

            if (CategoryBar.SelectedItem is null && CategoryBar.Items.Count > 0)
                CategoryBar.SelectedItem = CategoryBar.Items[0];
        }
        finally
        {
            _loading = false;
        }
    }

    private void CategoryBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_loading) return;
        if (sender.SelectedItem is not SelectorBarItem item) return;

        _category = item.Tag as string ?? "";
        Apply();
    }

    private void Apply()
    {
        var list = App.Content.Apps
            .Where(a => _category.Length == 0 || a.Category == _category)
            .Where(a => a.Matches(_keyword))
            .ToList();

        TileGrid.ItemsSource = list;
        CompactGrid.ItemsSource = list;

        WarmPalette(list);

        TitleText.Text = _category.Length == 0
            ? (_keyword.Length == 0 ? "软件下载" : $"搜索：{_keyword}")
            : App.Content.CategoryName(_category);

        UpdateEmptyState(list.Count);

        _appliedKey = Fingerprint();   // 记下这次是按什么状态建的，下次同状态直接跳过
    }

    /// <summary>
    /// 预热详情页色带要用的图标主色。
    ///
    /// ⛔⛔ 详情页不能"等页面画完再去取色"—— 那必然是先显示一种颜色、过一会儿跳成另一种。
    /// 列表这里提前把前几十个算好（`IconPalette.Preload` 内部有缓存与去重，重复调用不重复下载）。
    /// 只做首屏那几十个：再往后滚到的软件，用户点进去时图标早就加载过了，那时再算也来得及。
    /// </summary>
    private static void WarmPalette(List<SoftwareApp> list)
    {
        const int limit = 32;

        for (var i = 0; i < list.Count && i < limit; i++)
            Services.IconPalette.Preload(list[i].Icon);
    }

    /// <summary>
    /// 空清单时别只写一句"没有匹配的软件"——要分清三种情况：
    /// ① 搜索/分类筛掉了 ② 正常：安装包不自带清单，正在联网取 ③ 异常：真的没取到
    /// </summary>
    private void UpdateEmptyState(int shown)
    {
        if (shown > 0)
        {
            EmptyPanel.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyPanel.Visibility = Visibility.Visible;

        EmptyDetail.Text = $"当前内容来源：{App.Content.SourceLabel}" +
                           (App.Content.Issues.Count > 0 ? $"\n读取问题：{App.Content.Issues[0].Message}" : "");

        var hasFilter = _keyword.Length > 0 || _category.Length > 0;
        if (hasFilter)
        {
            EmptyTitle.Text = "未找到匹配的软件";
            EmptyText.Text = "请更换关键词，或单击「全部」查看所有软件。";
            EmptyText.Visibility = Visibility.Visible;
            EmptyDetail.Text = $"当前内容来源：{App.Content.SourceLabel}（共 {App.Content.Apps.Count} 个软件）";
            EmptyRetry.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyRetry.Visibility = Visibility.Visible;
        EmptyRetry.IsEnabled = true;
        EmptyRetry.Content = "重新获取清单";

        // 正在联网取 —— 这是最正常的首启状态（安装包不再内置清单），别吓唬用户
        if (App.Content.IsSyncing)
        {
            EmptyTitle.Text = "正在获取软件清单";
            EmptyText.Text = "正在从网络获取最新的软件清单，请稍候。";
            EmptyRetry.IsEnabled = false;
            EmptyRetry.Content = "正在获取";
            return;
        }

        EmptyTitle.Text = "软件清单为空";
        EmptyText.Text = "软件清单不随安装包提供，需要联网获取。" +
                         "若始终为空，多半是当前网络连不上（教学机、校园网常见），换个网络再点下面的按钮。";
    }

    /// <summary>空状态里的「重新获取清单」：拉一次网络内容再重读（失败就照实说）。</summary>
    private async void EmptyRetry_Click(object sender, RoutedEventArgs e)
    {
        EmptyRetry.IsEnabled = false;
        EmptyRetry.Content = "正在获取";
        EmptyTitle.Text = "正在获取软件清单";
        EmptyText.Text = "正在从网络获取最新清单。";
        EmptyDetail.Text = "";

        try
        {
            var result = await Services.ContentUpdater.SyncAsync(
                new Progress<string>(text => EmptyDetail.Text = text));

            if (result.Updated) App.Content.Load();

            BuildChips();
            Apply();

            if (App.Content.Apps.Count == 0)
            {
                EmptyTitle.Text = "仍未获取到清单";
                EmptyText.Text = "网络不可用，或当前网络访问获取源不通。可以换个网络再试；" +
                                 "如问题持续，请将本页截图提供给维护人员。";
                EmptyDetail.Text = result.Message;
            }
        }
        catch (Exception ex)
        {
            EmptyTitle.Text = "获取失败";
            EmptyText.Text = "网络不可用或暂时无法访问，请稍后重试。";
            EmptyDetail.Text = ex.Message;
        }
        finally
        {
            EmptyRetry.IsEnabled = true;
            EmptyRetry.Content = "重新获取清单";
        }
    }

    private void AppGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SoftwareApp app)
            Frame.Navigate(typeof(DetailPage), app.Id);
    }

    // 卡片自己的悬停反馈（容器 chrome 已关掉，见 XAML 里的 CshCardItemStyle）
    private void Card_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Opacity = 0.88;
    }

    private void Card_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Opacity = 1.0;
    }

    // 占位字形（购物袋）的显隐由 SoftwareApp.IconPlaceholder 驱动（见 Data/SoftwareApp.cs）：
    // 没图 / 加载中 / 加载失败都显示，加载成功才收掉。用属性 + INotifyPropertyChanged 而不是
    // Image 元素事件，是因为 GridView 回收容器时属性会重新求值，不会被上一次的状态卡住。

    // ══════════════════════════════════════════════════════
    //  Hub AI 入口与浮层（2026-10-18）
    //  ⚠️ 现在**没有接模型**：发送只回一句占位。AiClient / 诊断采集都还没写，
    //     这一版只为把形态定下来（尺寸、配色、展开位置），别当成能用。
    // ══════════════════════════════════════════════════════

    private void HubAiEntry_Click(object sender, RoutedEventArgs e) => ShowHubAiPanel(true);

    private void HubAiClose_Click(object sender, RoutedEventArgs e) => ShowHubAiPanel(false);

    /// <summary>
    /// 浮层升到完整页（2026-10-18）。走 <c>Shell.NavigateTo</c> 而不是 <c>Frame.Navigate</c> ——
    /// 后者只会换掉内容区，导航栏的选中态还停在「软件下载」，看着像点错了。
    /// </summary>
    private void HubAiExpand_Click(object sender, RoutedEventArgs e)
    {
        ShowHubAiPanel(false);
        App.MainWindow?.Shell.NavigateTo("hubai");
    }

    /// <summary>浮层显隐。展开时把焦点给输入框 —— 用户点进来的目的就是打字。</summary>
    /// <summary>
    /// 浮窗显隐（2026-10-18 加动画）。
    /// ⚠️ 原来是瞬间 Visibility 切换，弹出来很生硬（Nick：「有点生硬，能不能滑入滑出」）。
    /// 现在：展开 = 从下往上滑 24px + 淡入；收起 = 反向，动画结束后才真正 Collapsed
    /// （过早设 Collapsed 会让动画直接消失）。
    /// </summary>
    private void ShowHubAiPanel(bool show)
    {
        if (show)
        {
            HubAiPanel.Visibility = Visibility.Visible;

            // ⚠️ 必须先摆到起点：DoubleAnimation 只给 To 时会从**当前值**动画过去，
            //    不设起点就是 0→0，只剩淡入、看不出滑动。
            HubAiPanelTransform.Y = 24;
            HubAiPanel.Opacity = 0;

            AnimatePanel(toY: 0, toOpacity: 1, durationMs: 240);
            HubAiInput.Focus(FocusState.Programmatic);
            return;
        }

        if (HubAiPanel.Visibility != Visibility.Visible) return;

        AnimatePanel(toY: 24, toOpacity: 0, durationMs: 170,
            onDone: () => HubAiPanel.Visibility = Visibility.Collapsed);
    }

    /// <summary>滑入滑出。⚠️ 位移与透明度都是**非合成动画**，必须开 EnableDependentAnimation 才会动。</summary>
    private void AnimatePanel(double toY, double toOpacity, int durationMs, Action? onDone = null)
    {
        var slide = new DoubleAnimation
        {
            To = toY,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EasingFunction = new CubicEase
            {
                EasingMode = toOpacity > 0.5 ? EasingMode.EaseOut : EasingMode.EaseIn,
            },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(slide, HubAiPanelTransform);
        Storyboard.SetTargetProperty(slide, "Y");

        var fade = new DoubleAnimation
        {
            To = toOpacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, HubAiPanel);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);

        if (onDone is not null) storyboard.Completed += (_, _) => onDone();

        storyboard.Begin();
    }

    private void HubAiSend_Click(object sender, RoutedEventArgs e) => SendHubAiMessage();

    private void HubAiInput_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        SendHubAiMessage();
    }

    /// <summary>
    /// 浮层里发送 = 把问题交给完整页，不在浮层里做对话（2026-10-18）。
    /// 完整页有历史、工具调用、推荐卡片与用量统计，这里再实现一套必然两边行为不一致。
    /// </summary>
    private void SendHubAiMessage()
    {
        var text = HubAiInput.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        HubAiInput.Text = string.Empty;
        ShowHubAiPanel(false);

        HubAiPage.PendingQuestion = text;
        App.MainWindow?.Shell.NavigateTo("hubai");
    }

    /// <summary>
    /// 造一条消息气泡。背景走系统的 <c>SubtleFillColorSecondaryBrush</c> 而不是 App.xaml 里那套
    /// HubAi 资源 —— 代码里取主题字典的键很别扭（ThemeDictionaries 是延迟解析的），
    /// 而这条气泡本来就该跟系统"次级填充"一致，不必另立品牌色。
    /// </summary>
    private static Border MakeBubble(string text, bool alignRight)
    {
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
            MaxWidth = alignRight ? 310 : 320,
            HorizontalAlignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap },
        };

        try
        {
            if (Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out var brush)
                && brush is Brush b)
            {
                bubble.Background = b;
            }
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("hubai", ex, "取气泡背景画刷");
        }

        return bubble;
    }
}
