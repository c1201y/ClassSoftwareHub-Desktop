using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 首页：大标题 ClassSoftwareHub → 首页大标题（站点大标题）→ 版本图 + 版本号 → 快速开始卡片。
/// 全部原生，不加载网页。
/// </summary>
public sealed partial class WelcomePage : Page
{
    public WelcomePage()
    {
        InitializeComponent();

        // ⚠️ 为什么要在 Loaded 里订阅 App.Content.Changed：安装包不自带内容包之后，软件清单是
        //    **联网才到**的 —— 首启时首页先空着，内容同步完不会自己通知这个页面。
        //    先退订再订阅：本页是 NavigationCacheMode=Enabled 的常驻实例，Loaded 会反复触发，
        //    这样写保证只挂一次。
        Loaded += (_, _) =>
        {
            // 每次进首页换一批推荐（「随便推」⇒ 随机种子；「换一批」按钮也会再换）
            _recommendSeed = Random.Shared.Next();

            App.Content.Changed -= OnContentChanged;
            App.Content.Changed += OnContentChanged;

            // 更新状态可能在首页不在眼前时就变了（启动检查跑在后台）⇒ 每次 Loaded 重挂一次，防重复订阅
            Services.Updating.UpdateNotice.Changed -= OnUpdateNoticeChanged;
            Services.Updating.UpdateNotice.Changed += OnUpdateNoticeChanged;

            Populate();
            RefreshUpdateCard();
        };

        // 快捷入口始终一行：窗口一变宽就重算格宽（见 FitQuickRow）
        QuickGrid.SizeChanged += (_, e) => FitQuickRow(e.NewSize.Width);

        // 主题一切（浅色 ⇄ 深色）就把 SVG 快捷图标重染成对应颜色
        ActualThemeChanged += (_, _) => ApplyQuickLinkIcons();

        // 推荐区：视口 Clip 跟着尺寸走；面板铺好前视口宽为 0 ⇒ 等尺寸到了再铺一次
        RecommendViewport.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                RecommendViewport.Clip = new RectangleGeometry
                {
                    Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
                };
            }

            if (RecommendStrip.Children.Count == 0 && RecommendSection.Visibility == Visibility.Visible)
                LayoutRecommend();
        };

        // 离开首页就停掉滚动（页面是缓存实例，不停会一直在后台空转）
        Unloaded += (_, _) =>
        {
            _recommendStoryboard?.Stop();
            _recommendStoryboard = null;
        };
    }

    /// <summary>内容清单变了（同步完成 / 手动刷新）→ 把「已收录 XX 款软件」那行刷一下。</summary>
    private void OnContentChanged()
    {
        // ContentStore 的同步跑在后台线程 ⇒ 回 UI 线程再动控件
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnContentChanged);
            return;
        }

        RefreshCollectedCount();
        BuildRecommendations();
    }

    /// <summary>
    /// 「发现新版本」状态变了（见 <see cref="Services.Updating.UpdateNotice"/>）。
    /// ⚠️ 通知发出来的线程不保证是 UI 线程（检查跑在后台任务里）⇒ 回 UI 线程再动控件。
    /// </summary>
    private void OnUpdateNoticeChanged()
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            RefreshUpdateCard();
            return;
        }

        DispatcherQueue.TryEnqueue(RefreshUpdateCard);
    }

    // ── 更新状态（2026-10-06 起，挂在快速开始区的「查看更新内容」主题色卡上） ──

    /// <summary>
    /// 「查看更新内容」卡的三种状态（优先级从上到下）：
    /// ①「待装」—— 后台下载完成、存档里有待装标记，且包还在、tag 还不是当前版本 → 右端换出「立即安装」；
    /// ②「发现新版本」—— **只是查到了、还没下载** → 右端换出「立即更新」（2026-10-16 新增）；
    /// ③「已是最新」—— 其余所有情况 → 副标题写当前版本号，点击进「更新日志」页。
    ///
    /// ⛔⛔ 首页**绝不自己发起网络检查**（那会拖慢首屏，这条规矩一直有效）。
    ///    状态②的数据来自 <see cref="Services.Updating.UpdateNotice"/> —— MainWindow 在启动时 / 用户
    ///    手动检查后把结果放进去，本页订阅它的变化重刷。所以这里零额外网络开销。
    ///
    /// 之所以原先不做状态②（旧注释写的是"后台本来就会自动检查并下载，真下了包自然落到状态①"）：
    /// 那条路成立的前提是**用户没关掉自动下载**。Nick 2026-10-16 报的实际体验是——更新提示只在开机
    /// 弹一次，用户选「稍后」之后就再也找不到入口了，很容易忘。所以补上这一态。
    ///
    /// 待装标记的自检清理逻辑与旧 InfoBar 完全一致：装上新版本再进来、或包被清理了 → 顺手清档，不摆死卡片。
    /// </summary>
    private void RefreshUpdateCard()
    {
        var s = App.Settings.Current;
        var path = s.UpdatePendingPath ?? "";
        var pending = false;

        if (path.Length > 0)
        {
            // 标记悬空（包被清了 / 档是手抄的），或待装 tag == 当前版本＝已经装上了 → 待装周期结束
            if (!System.IO.File.Exists(path)
                || s.UpdatePendingTag == ShellConfig.VersionPrefix + ShellConfig.ShellVersion)
            {
                s.UpdatePendingPath = "";
                s.UpdatePendingTag = "";
                App.Settings.Save();
            }
            else
            {
                pending = true;
            }
        }

        // 目标元素＝快速开始区那张「查看更新内容」主题色卡（2026-10-06 从顶部独立卡片并入这里）。
        // 注意：卡片本身就是强调色底 ⇒ 不再靠 BorderBrush 表达"待装"，改用右端那颗按钮。
        // ⚠️ 2026-10-22 Nick：「立即更新的标志很不明显，有更新的话用这个横幅（Success 的 InfoBar 颜色）」。
        //    有更新时整张卡片变绿底 + 对勾图标 + 深色字，一眼能和其它入口卡分开。
        ApplyUpdateHighlight(pending || Services.Updating.UpdateNotice.Pending is not null);

        if (pending)
        {
            // ① 待装：**离"装上"最近，优先级最高** —— 哪怕这会儿又查到别的版本，也先把手上这个装掉。
            var tag = s.UpdatePendingTag.Length > 0 ? s.UpdatePendingTag : "新版本";
            ChangelogCardTitle.Text = $"{tag} 已下载就绪";
            ChangelogCardSubtitle.Text = "安装包已通过校验，可立即安装";
            UpdateInstallButton.Visibility = Visibility.Visible;
            QuickUpdateButton.Visibility = Visibility.Collapsed;
            ChangelogChevron.Visibility = Visibility.Collapsed;
            return;
        }

        // ② 发现新版本（还没下载）
        if (Services.Updating.UpdateNotice.Pending is { } found)
        {
            ChangelogCardTitle.Text = $"{found.Tag} 可用";
            ChangelogCardSubtitle.Text = "发现新版本，可立即更新";
            UpdateInstallButton.Visibility = Visibility.Collapsed;
            QuickUpdateButton.Visibility = Visibility.Visible;
            ChangelogChevron.Visibility = Visibility.Collapsed;
            return;
        }

        // ③ 已是最新
        ChangelogCardTitle.Text = "查看更新内容";
        ChangelogCardSubtitle.Text =
            $"当前版本 {ShellConfig.VersionPrefix}{ShellConfig.ShellVersion} · 已是最新版本";
        UpdateInstallButton.Visibility = Visibility.Collapsed;
        QuickUpdateButton.Visibility = Visibility.Collapsed;
        ChangelogChevron.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 点「查看更新内容」卡 → 去「更新日志」页。
    /// ⛔ 两颗按钮任一可见时直接让开 —— 这张卡此刻是"行动项"，点击归按钮。
    /// </summary>
    private void ChangelogCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (UpdateInstallButton.Visibility == Visibility.Visible) return;
        if (QuickUpdateButton.Visibility == Visibility.Visible) return;
        App.MainWindow?.Shell.NavigateTo("changelog");
    }

    /// <summary>
    /// 有更新时把「查看更新内容」卡片换成绿色横幅（与 InfoBar Success 同套配色）；
    /// 无更新时恢复原来的强调色卡片——它仍是「查看更新内容」的入口。
    /// </summary>
    private void ApplyUpdateHighlight(bool highlight)
    {
        Brush Fill(string key) => (Brush)Application.Current.Resources[key];

        if (highlight)
        {
            ChangelogCard.Background = Fill("SystemFillColorSuccessBackgroundBrush");
            ChangelogIcon.Glyph = "\uE930";                      // 对勾
            ChangelogIcon.Foreground = Fill("TextFillColorPrimaryBrush");
            ChangelogCardTitle.Foreground = Fill("TextFillColorPrimaryBrush");
            ChangelogCardSubtitle.Foreground = Fill("TextFillColorPrimaryBrush");
            ChangelogChevron.Foreground = Fill("TextFillColorPrimaryBrush");
        }
        else
        {
            ChangelogCard.Background = Fill("AccentFillColorDefaultBrush");
            ChangelogIcon.Glyph = "\uE81C";                      // 时钟
            ChangelogIcon.Foreground = Fill("TextOnAccentFillColorPrimaryBrush");
            ChangelogCardTitle.Foreground = Fill("TextOnAccentFillColorPrimaryBrush");
            ChangelogCardSubtitle.Foreground = Fill("TextOnAccentFillColorPrimaryBrush");
            ChangelogChevron.Foreground = Fill("TextOnAccentFillColorPrimaryBrush");
        }
    }

    /// <summary>
    /// 「立即更新」：用户点它就是要装，所以**不再弹三选一**（那张对话框是"刚发现新版本"时的分岔口，
    /// 这一态本来就是从那儿选完「稍后」过来的，再问一遍纯属找麻烦）。
    /// 直接进「正在更新」流程：下载 → 校验 → 安装 → 应用自动退出并重开。
    /// </summary>
    private async void QuickUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (Services.Updating.UpdateNotice.Pending is not { } release) return;
        if (XamlRoot is not { } root) return;

        // 防连点：这条链路会下载几十 MB 并最终退出应用，多点只会并发起来
        if (sender is Button b) b.IsEnabled = false;
        try
        {
            var service = Services.Updating.UpdateService.CreateDefault();
            await Services.Updating.UpdateFlow.RunAsync(root, service, release);
        }
        finally
        {
            // 走完正常流程应用已经退出了，这一句基本用不上；失败/取消回来时得把按钮放开
            if (sender is Button bb) bb.IsEnabled = true;
        }
    }

    private async void InstallPending_Click(object sender, RoutedEventArgs e)
    {
        // 防连点：安装会退出应用，多点只会并发起安装器
        if (sender is Button b) b.IsEnabled = false;
        await Services.Updating.UpdateFlow.InstallPendingNowAsync();
    }

    private async void Populate()
    {
        // ⚠️ 首页大标题下面那行站点名副标题（原 HomeTitleText）已按 Nick 2026-10-06 要求整行删除，
        //    连带这里的 ui.AppTitle / title 取值一起清掉 —— 别再补回来。
        // 只显示软件自己的版本（前缀 VersionPrefix + ShellVersion，如 dv1.1.0-insider1.0 或正式版 dv1.1.0）。
        // ⚠️ 别再往这里挂"网站版本"——软件是独立发布物，不摆成网站版本的附属品（2026-09-26 删）。
        ShellVersionText.Text = ShellConfig.VersionPrefix + ShellConfig.ShellVersion;

        RefreshCollectedCount();

        LoadBanner();
        BuildQuickLinks();
        BuildRecommendations();

        // 「硬件信息 / 系统信息」：注册表 + SMBIOS 表 + 显示适配器一串读取，全压在 UI 线程上
        // 会让首页刚出来先僵一下。挪到后台线程算，算完再填 —— 这块内容不参与首屏关键渲染。
        try
        {
            var q = await System.Threading.Tasks.Task.Run(Core.SystemInfo.Gather);
            FillRows(HardwareList, q.Hardware);
            FillRows(SystemList, q.System);
        }
        catch (Exception ex)
        {
            // 读不到就不填，别把首页搞崩
            PerfLog.Mark("首页系统信息读取失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 首页那条「收录情况」InfoBar（<see cref="CollectedBar"/>）：标题＝已收录 N 款软件，
    /// 正文＝过去48小时收录 N 款＋具体名字。2026-10-07 Nick 要求**两项合并**到这一条，放在设置卡片正下方。
    ///
    /// 数字来自**本机已同步的**软件清单（<c>App.Content.Apps</c>，源头是站点仓库的 软件数据/apps/）——
    /// 零网络请求、离线也准。
    ///
    /// 清单还没同步下来时（Apps 为空）**整条收起**，不显示"已收录 0 款"这种假数字。
    /// </summary>
    private void RefreshCollectedCount()
    {
        var count = App.Content.Apps.Count;

        if (count <= 0)
        {
            CollectedBar.IsOpen = false;       // 清单都还没到，更谈不上「新增」
            return;
        }

        CollectedBar.Title = $"已收录 {count} 款软件";
        CollectedBar.Message = string.Empty;   // 正文只留给「过去48小时收录」，没有就空着
        CollectedBar.IsOpen = true;

        _ = RefreshRecentAddedAsync(count);
    }

    /// <summary>
    /// 补「过去48小时收录」那半句：**具体是哪几款**（用中文名，不是 slug）。
    ///
    /// 数据得联网：软件 json 里没有任何日期字段，只能去问 GitHub 的提交历史
    /// （算法与代价见 <see cref="NewlyAddedService"/>）。
    /// **只增不补** —— 只有算出 &gt;0 才写正文；算不出来、或窗口内确实没新增，正文就保持空。
    /// ⛔ 不摆「过去48小时收录 0 款」，免得那条 InfoBar 里多一个会骗人的数字。
    /// </summary>
    private async Task RefreshRecentAddedAsync(int collected)
    {
        var added = await NewlyAddedService.GetRecentAsync(TimeSpan.FromHours(48));
        if (added is null || added.Count <= 0) return;

        // 名字从本机清单里取（slug → 中文名）；万一清单里找不到这一条（刚加还没来得及同步？）
        // 就退回 slug 本身，总比不显示强
        var names = added.Ids.Select(id => App.Content.FindById(id)?.Name ?? id).ToList();

        // await 回来不一定还在 UI 线程（服务内部一律 ConfigureAwait(false)）
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ApplyRecentAdded(collected, names));
            return;
        }

        ApplyRecentAdded(collected, names);
    }

    /// <summary>把名单写进 InfoBar 正文（Message 原生支持自动换行 + 卡片随内容撑高）。
    /// 全部软件名都列出（不截断、不用「等 N 款」）。</summary>
    private void ApplyRecentAdded(int collected, IReadOnlyList<string> names)
    {
        // 这期间清单又变了（同步完成）→ 已经有人重刷过，别用旧数字盖回去
        if (App.Content.Apps.Count != collected) return;

        CollectedBar.Title = $"已收录 {collected} 款软件";
        CollectedBar.Message = "过去48小时收录 " + names.Count + " 款：" + string.Join("、", names);
        CollectedBar.IsOpen = true;
    }

    /// <summary>一行快捷入口：项目仓库 / 作者主页 / 赞助作者 / 加入Q群 / 更新日志。</summary>
    private void BuildQuickLinks()
    {
        QuickGrid.ItemsSource = QuickLinks.Build(App.Content.Ui);
        ApplyQuickLinkIcons();

        // 面板是随 ItemsSource 才生成的 ⇒ 等一帧再按实际宽度收格宽（SizeChanged 也会兜一次）
        DispatcherQueue.TryEnqueue(() => FitQuickRow(QuickGrid.ActualWidth));
    }

    /// <summary>给带 SVG 的快捷入口按当前主题色染图标；主题一切就重染（浅色/深色自适应）。</summary>
    private void ApplyQuickLinkIcons()
    {
        if (QuickGrid.ItemsSource is not List<Core.QuickLink> links) return;
        var color = ((SolidColorBrush)ThemeBrush.Get(this, "TextFillColorPrimaryBrush")).Color;
        foreach (var link in links)
            if (!string.IsNullOrEmpty(link.IconSvg))
                link.IconSource = QuickLinkIconLoader.GetIcon(link.IconSvg, color);
    }

    /// <summary>
    /// 快捷入口**必须排在一行**（Nick 2026-10-17：窗口变窄会折行，很难看）。
    ///
    /// <para>
    /// 做法是按可用宽度重算 <see cref="ItemsWrapGrid.ItemWidth"/>：宽屏维持 192 不动，
    /// 窄了就把间距压小、五颗平分一行；只有窄到 104×5 以下才允许折行（那时确实放不下了）。
    /// </para>
    /// </summary>
    private void FitQuickRow(double width)
    {
        if (QuickGrid.ItemsPanelRoot is not ItemsWrapGrid panel) return;

        var count = QuickGrid.Items.Count;
        if (count == 0 || width <= 0) return;

        // ⛔ 不能正好用 width / count：格宽乘回来会因浮点/取整差出零点几像素，
        //    第五颗就被挤到下一行（实测 900 宽的窗口仍然 4+1）。留 1px 余量。
        var itemWidth = Math.Clamp(Math.Floor(width / count) - 1d, 104d, 192d);
        if (Math.Abs(panel.ItemWidth - itemWidth) > 0.5) panel.ItemWidth = itemWidth;
    }

    // ── 推荐应用（2026-10-17 重做：Store「精选实用软件」横向慢滚） ──────────

    /// <summary>「换一批」的种子（每次进首页随机、点按钮再随机）。</summary>
    private int _recommendSeed;

    /// <summary>当前这一批挑中的软件（布局/重建时复用）。</summary>
    private List<SoftwareApp> _recommendPicks = new();

    /// <summary>正在跑的滚动动画，悬停时 Pause / 移开 Resume。</summary>
    private Storyboard? _recommendStoryboard;

    private const double RecommendCardW = 128;     // 整卡宽度（比原来小一圈，向 Store 靠）
    private const double RecommendGap = 12;       // 卡片间距
    private const double RecommendSpeed = 24;     // 滚动速度（px/秒），宁慢勿快
    private const double RecommendTopH = 128;     // 上半部分（模糊图标区）高度
    private const double RecommendIconSize = 64;  // 中央清晰图标边长（约卡片一半）
    private const double RecommendCardRadius = 8; // 整卡圆角
    private const double RecommendIconRadius = 12;// 图标圆角

    /// <summary>
    /// 兜底色板：图标还没取到 / 没有图标地址时先用它（按 id 稳定散列，同一软件永远同色）；
    /// 图标字节一到，整卡底色会换成从图标里提取的主色（见 LoadIconColorAsync）。
    /// </summary>
    private static readonly Windows.UI.Color[] RecommendTileColors =
    {
        Windows.UI.Color.FromArgb(255, 0x1D, 0x9E, 0x75), // 青绿
        Windows.UI.Color.FromArgb(255, 0x37, 0x8A, 0xDD), // 蓝
        Windows.UI.Color.FromArgb(255, 0xD4, 0x53, 0x7E), // 玫红
        Windows.UI.Color.FromArgb(255, 0xE2, 0x4B, 0x4A), // 红
        Windows.UI.Color.FromArgb(255, 0x7F, 0x77, 0xDD), // 紫
        Windows.UI.Color.FromArgb(255, 0xBA, 0x75, 0x17), // 琥珀
        Windows.UI.Color.FromArgb(255, 0x18, 0x5F, 0xA5), // 深蓝
        Windows.UI.Color.FromArgb(255, 0x5F, 0x5E, 0x5A), // 灰
    };

    private void BuildRecommendations()
    {
        var apps = App.Content.Apps;

        _recommendStoryboard?.Stop();
        _recommendStoryboard = null;

        _recommendPicks = apps.Count == 0
            ? new List<SoftwareApp>()
            : PickRecommendations(apps, 12, _recommendSeed);

        if (_recommendPicks.Count == 0)
        {
            RecommendSection.Visibility = Visibility.Collapsed;
            RecommendStrip.Children.Clear();
            return;
        }

        RecommendSection.Visibility = Visibility.Visible;

        // 面板要等一帧才拿到可用宽度 ⇒ 下一帧再决定每组几张、再铺卡片
        DispatcherQueue.TryEnqueue(LayoutRecommend);
    }

    /// <summary>
    /// 按视口宽度铺卡片：一组 = 挑中的软件循环铺满视口（至少 picks.Count 张），
    /// 再**复制一组**首尾相接 ⇒ 位移一整组宽度后画面完全重合，做到无缝循环。
    /// </summary>
    private void LayoutRecommend()
    {
        if (_recommendPicks.Count == 0) return;

        var viewport = RecommendViewport.ActualWidth;
        if (viewport <= 0) return;   // 还没布局出来，等 SizeChanged 再铺

        var unit = RecommendCardW + RecommendGap;
        var perGroup = Math.Max(_recommendPicks.Count,
            (int)Math.Ceiling((viewport + RecommendGap) / unit));

        var group = new List<SoftwareApp>(perGroup);
        for (var i = 0; i < perGroup; i++) group.Add(_recommendPicks[i % _recommendPicks.Count]);

        RecommendStrip.Children.Clear();

        // 两组首尾相接：位移一组宽度即无缝回到起点
        for (var copy = 0; copy < 2; copy++)
            foreach (var app in group)
                RecommendStrip.Children.Add(BuildRecommendCard(app));

        var distance = group.Count * unit;                 // 一整组的位移距离
        var seconds = Math.Max(20, distance / RecommendSpeed);
        StartRecommendScroll(distance, seconds);
    }

    private void StartRecommendScroll(double distance, double seconds)
    {
        // 视口尺寸和入队那一帧可能各触发一次布局 ⇒ 起新动画前先停旧的，避免两个动画抢同一个 X
        _recommendStoryboard?.Stop();
        _recommendStoryboard = null;

        var anim = new DoubleAnimation
        {
            From = 0,
            To = -distance,
            Duration = new Duration(TimeSpan.FromSeconds(seconds)),
        };
        Storyboard.SetTarget(anim, RecommendTransform);
        Storyboard.SetTargetProperty(anim, "X");

        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        sb.Children.Add(anim);

        _recommendStoryboard = sb;
        sb.Begin();
    }

    /// <summary>
    /// 一张推荐卡（照 Store 的画法）：**整卡圆角**，上半部分是「图标放大 + 亚克力模糊」的底、
    /// 中央叠一个圆角清晰图标；下半部分是**加粗名称 + 一句短简介**。
    /// ⛔ 不是把图标拉满整格。
    /// </summary>
    private FrameworkElement BuildRecommendCard(SoftwareApp app)
    {
        // 先给一个散列色兜底，等图标主色取回来再回填
        var baseTint = ToCardTint(RecommendTileColors[
            (int)((uint)Core.StableRank.Of(app.Id) % (uint)RecommendTileColors.Length)]);

        var card = new Border
        {
            Width = RecommendCardW,
            CornerRadius = new CornerRadius(RecommendCardRadius),
            // ⚠️ 整卡底色用**主题卡片面**，不跟图标色走 —— 下半文字区是一块统一的深色面（Store 就是这样），
            //    图标色只留在上半的模糊底里。主题画刷保证浅色主题下也不会白字配浅底。
            Background = ThemeBrushOf("CardBackgroundFillColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrushOf("CardStrokeColorDefaultBrush"),
        };

        // 上半部分：图标放大铺底 + AcrylicBrush 实时毛玻璃 + 中央清晰图标
        var acrylic = new AcrylicBrush
        {
            TintColor = baseTint,
            TintOpacity = 0.18,
            TintLuminosityOpacity = 0.85,
            FallbackColor = baseTint,
        };

        var top = new Border
        {
            Height = RecommendTopH,
            CornerRadius = new CornerRadius(RecommendCardRadius, RecommendCardRadius, 0, 0),
            Background = new SolidColorBrush(baseTint),
        };

        var topGrid = new Grid();

        // 放大铺满的图标（AcrylicBrush 就在它上面做实时模糊）
        if (app.IconImage is { } backdrop)
        {
            topGrid.Children.Add(new Image
            {
                Source = backdrop,
                Stretch = Stretch.UniformToFill,
            });
        }

        // 亚克力层：盖在放大图标上
        topGrid.Children.Add(new Border
        {
            Background = acrylic,
            CornerRadius = new CornerRadius(RecommendCardRadius, RecommendCardRadius, 0, 0),
        });

        // 兜底：名称首字（图标加载成功后自动收起）
        var monogram = new Border
        {
            Width = RecommendIconSize,
            Height = RecommendIconSize,
            CornerRadius = new CornerRadius(RecommendIconRadius),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(46, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = FirstChar(app.Name),
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        BindingOperations.SetBinding(monogram, UIElement.VisibilityProperty, new Binding
        {
            Path = new PropertyPath(nameof(SoftwareApp.IconPlaceholder)),
            Source = app,
            Mode = BindingMode.OneWay,
        });
        topGrid.Children.Add(monogram);

        // 中央清晰图标：用 Border 的 ImageBrush 画，才能把图标自身的直角裁成圆角
        if (app.IconImage is { } crisp)
        {
            topGrid.Children.Add(new Border
            {
                Width = RecommendIconSize,
                Height = RecommendIconSize,
                CornerRadius = new CornerRadius(RecommendIconRadius),
                Background = new ImageBrush { ImageSource = crisp, Stretch = Stretch.UniformToFill },
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        top.Child = topGrid;

        // 下半部分：加粗名称 + 短简介（文字跟主题走，底是统一的卡片面，不带图标色）
        var text = new StackPanel
        {
            Padding = new Thickness(10, 9, 10, 10),
            Spacing = 3,
        };
        text.Children.Add(new TextBlock
        {
            Text = app.Name,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeBrushOf("TextFillColorPrimaryBrush"),
        });
        text.Children.Add(new TextBlock
        {
            Text = IntroOf(app),
            FontSize = 11,
            Height = 30,
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeBrushOf("TextFillColorSecondaryBrush"),
        });

        var content = new StackPanel();
        content.Children.Add(top);
        content.Children.Add(text);
        card.Child = content;

        card.Tapped += (_, _) => Frame.Navigate(typeof(DetailPage), app.Id);
        ToolTipService.SetToolTip(card, app.Name);

        // 异步提取图标主色，回来给亚克力上色（取不到就保留散列色）
        _ = ApplyIconColorAsync(app, top, acrylic);

        return card;
    }

    private static readonly System.Net.Http.HttpClient IconHttp =
        new() { Timeout = TimeSpan.FromSeconds(6) };

    private static readonly Dictionary<string, Task<Windows.UI.Color?>> IconColorCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>图标主色回来：把亚克力和底色都换成它。</summary>
    private async Task ApplyIconColorAsync(SoftwareApp app, Border top, AcrylicBrush acrylic)
    {
        var color = await GetIconColorAsync(app.Icon);
        if (color is not { } c) return;

        var tint = ToCardTint(c);

        DispatcherQueue.TryEnqueue(() =>
        {
            top.Background = new SolidColorBrush(tint);
            acrylic.TintColor = tint;
            acrylic.FallbackColor = tint;
        });
    }

    private static Task<Windows.UI.Color?> GetIconColorAsync(string url)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<Windows.UI.Color?>(null);

        lock (IconColorCache)
        {
            if (!IconColorCache.TryGetValue(url, out var task))
            {
                task = LoadIconColorAsync(url);
                IconColorCache[url] = task;
            }
            return task;
        }
    }

    /// <summary>
    /// 取图标字节 → 缩到 20×20 采样算**主色**（跳过透明/近白/近黑，按饱和度加权，避免平均成灰）。
    /// 模糊交给 AcrylicBrush 自己做，这里只负责给亚克力上色。
    /// </summary>
    private static async Task<Windows.UI.Color?> LoadIconColorAsync(string url)
    {
        try
        {
            var bytes = await IconHttp.GetByteArrayAsync(url);

            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);

            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);

            const int size = 20;

            // 20×20 采样：读像素算主色。这张 SoftwareBitmap 不交给任何 ImageSource，可以安全释放。
            using var small = await decoder.GetSoftwareBitmapAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                new Windows.Graphics.Imaging.BitmapTransform
                {
                    ScaledWidth = size,
                    ScaledHeight = size,
                    InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant,
                },
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);

            var buffer = new byte[size * size * 4];
            small.CopyToBuffer(buffer.AsBuffer());

            long r = 0, g = 0, b = 0, weight = 0;
            for (var i = 0; i < buffer.Length; i += 4)
            {
                var bb = buffer[i];
                var gg = buffer[i + 1];
                var rr = buffer[i + 2];
                var aa = buffer[i + 3];
                if (aa < 40) continue;

                var ur = rr * 255 / aa;
                var ug = gg * 255 / aa;
                var ub = bb * 255 / aa;
                if (ur > 245 && ug > 245 && ub > 245) continue;   // 白底不算主题色
                if (ur < 12 && ug < 12 && ub < 12) continue;      // 近黑也不算

                var max = Math.Max(ur, Math.Max(ug, ub));
                var min = Math.Min(ur, Math.Min(ug, ub));
                var wgt = (max - min) + 24;                       // 越鲜艳权重越高

                r += (long)ur * wgt;
                g += (long)ug * wgt;
                b += (long)ub * wgt;
                weight += wgt;
            }

            return weight > 0
                ? Windows.UI.Color.FromArgb(255,
                    (byte)(r / weight), (byte)(g / weight), (byte)(b / weight))
                : Windows.UI.Color.FromArgb(255, 90, 90, 90);
        }
        catch
        {
            // SVG / 网络超时 / 解码失败 ⇒ 保留散列色兜底
            return null;
        }
    }

    /// <summary>把图标主色压暗成适合白字的整卡底色。</summary>
    private static Windows.UI.Color ToCardTint(Windows.UI.Color c) =>
        Windows.UI.Color.FromArgb(255,
            (byte)Math.Clamp(c.R * 0.34 + 14, 0, 255),
            (byte)Math.Clamp(c.G * 0.34 + 14, 0, 255),
            (byte)Math.Clamp(c.B * 0.34 + 14, 0, 255));

    private static string FirstChar(string name)
    {
        var t = name?.Trim() ?? "";
        return t.Length == 0 ? "?" : t[..1].ToUpperInvariant();
    }

    /// <summary>安全取主题画刷：键不存在时返回 null（不抛 KeyNotFoundException）。</summary>
    /// <summary>
    /// 按**元素所在那棵树**的主题取画刷。
    /// ⛔ 原来这里查的是 <c>Application.Current.Resources</c> —— 那个认的是应用级主题（跟着系统定死），
    /// 和窗口自己的 RequestedTheme 不一致时（应用浅色 + 系统深色）会拿到反色的画刷，
    /// 表现就是浅色首页上推荐卡片的字是白的（2026-10-18 Nick 截图指出）。
    /// 传 null 也能工作：<see cref="ThemeBrush.IsDark"/> 会依次回退到主窗口与实际设置。
    /// </summary>
    private static Brush ThemeBrushOf(string key) => ThemeBrush.Get(null, key);

    /// <summary>短简介：优先一句话标语，其次长简介，最后退分类名。</summary>
    private static string IntroOf(SoftwareApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.Tagline)) return app.Tagline.Trim();
        if (!string.IsNullOrWhiteSpace(app.Description)) return app.Description.Trim();
        return app.CategoryDisplay;
    }

    /// <summary>随便推：按稳定哈希排个序取前 N 个（同一种子顺序固定，不用 Random 对象）。</summary>
    private static List<SoftwareApp> PickRecommendations(IReadOnlyList<SoftwareApp> apps, int count, int seed)
    {
        return apps
            .OrderBy(a => Core.StableRank.Of(a.Id + "#" + seed))
            .Take(count)
            .ToList();
    }

    private void Recommend_Reroll(object sender, RoutedEventArgs e)
    {
        _recommendSeed = Random.Shared.Next();
        BuildRecommendations();
    }

    private void Recommend_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        _recommendStoryboard?.Pause();

    private void Recommend_PointerExited(object sender, PointerRoutedEventArgs e) =>
        _recommendStoryboard?.Resume();

    private void Quick_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not QuickLink link) return;

        // 带 Tag 的是**应用内页面**（目前只有「更新日志」）：走导航栏那套，左侧高亮也会跟过去
        if (link.Tag.Length > 0)
        {
            App.MainWindow?.Shell.NavigateTo(link.Tag);
            return;
        }

        if (link.Url.Length > 0)
            App.MainWindow?.OpenExternal(link.Url);
    }

    /// <summary>把一行行「图标 + 标签 + 值」填进列表（行间用 <c>InfoRowDivider</c> 那条分隔线）。</summary>
    private void FillRows(StackPanel panel, IReadOnlyList<SystemInfo.InfoLine> lines)
    {
        panel.Children.Clear();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0 && Resources["InfoRowDivider"] is Style divider)
                panel.Children.Add(new Border { Style = divider });
            panel.Children.Add(BuildRow(lines[i]));
        }
    }

    private static Grid BuildRow(SystemInfo.InfoLine line)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 9, 0, 9) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(148) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new FontIcon
        {
            Glyph = RowGlyphs.TryGetValue(line.Label, out var glyph) ? glyph : "\uE946",
            FontSize = 14,
            Opacity = 0.75,
            Width = 20,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var label = new TextBlock
        {
            Text = line.Label,
            FontSize = 12,
            Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var value = new TextBlock
        {
            Text = line.Value,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = true,
        };
        ToolTipService.SetToolTip(value, line.Value);

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(label, 1);
        Grid.SetColumn(value, 2);
        row.Children.Add(icon);
        row.Children.Add(label);
        row.Children.Add(value);
        return row;
    }

    /// <summary>每行的图标（码位取自 Segoe Fluent Icons 官方名字表）。</summary>
    private static readonly Dictionary<string, string> RowGlyphs = new()
    {
        ["处理器"] = "\uEEA1",   // CPU
        ["内存"] = "\uEEA0",     // RAM
        ["硬盘"] = "\uEDA2",     // HardDrive
        ["触摸"] = "\uEDA4",     // Touchscreen
        ["显卡"] = "\uE7F4",     // TVMonitor
        ["显存"] = "\uE714",     // Video
        ["显示器"] = "\uE7F3",   // SettingsDisplaySound
        ["操作系统"] = "\uE770",  // System
        ["系统版本"] = "\uE946",  // Info
        ["安装日期"] = "\uE787",  // Calendar
        ["虚拟内存"] = "\uEDA2",  // HardDrive
        ["虚拟化"] = "\uEEA3",   // VirtualMachineGroup
        ["DirectX"] = "\uE7FC",  // Game
    };

    private void LoadBanner()
    {
        try
        {
            // 换 banner 时这里和 csproj 的 EmbeddedResource 一起改。
            // ⚠️ 输出名固定 banner.png：缓存已按版本分目录（见 CacheHome），同名也不会串到别的版本，
            //    所以不再需要以前那套「文件名带版本号防串图」。
            var path = EmbeddedAssets.ExtractToCache("dv1.2.png", "banner.png");
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                // DecodePixelHeight：图是 1512×720，实际只按高度 140 显示。
                // 不设的话会整张全尺寸解码（≈4MB 位图）再缩下去；按 140 × 2 倍高 DPI 解就够了。
                Banner.Source = new BitmapImage(new Uri(path)) { DecodePixelHeight = 280 };
            }
        }
        catch { /* 图片读不到就不显示 */ }
    }

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement el) el.Opacity = 0.88;
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement el) el.Opacity = 1.0;
    }

    private void Card_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string;
        switch (tag)
        {
            case "web":
                // 「体验网页版」→ 交给系统默认浏览器打开整站
                // （应用内的 WebSheet 浮层留给软件详情页那种"顺手看一眼"的场景）
                App.MainWindow?.OpenExternal(ShellConfig.SiteUrl);
                break;
            case "apps":
                App.MainWindow?.Shell.NavigateTo("apps");
                break;
            case "tools":
                // 走专用入口：除了切到工具索引页，还要把导航里的分组展开（从外面跳进来时看不出里面有子项）
                App.MainWindow?.Shell.NavigateToTools();
                break;
            case "sidebar":
                App.MainWindow?.Shell.NavigateTo("sidebar");
                break;
            case "experimental":
                // 走专用入口：除了切到总览页，还要把导航里的分组展开（从外面跳进来时看不出里面有子项）
                App.MainWindow?.Shell.NavigateToExperimental();
                break;
            case "submit":
                App.MainWindow?.Shell.NavigateTo("submit");
                break;
            case "feedback":
                App.MainWindow?.Shell.NavigateTo("feedback");
                break;
            case "settings":
                App.MainWindow?.Shell.NavigateTo("settings");
                break;
            case "changelog":
                // 「查看更新内容」快速卡片 → 更新日志页（跟顶部更新卡片走同一条路）
                App.MainWindow?.Shell.NavigateTo("changelog");
                break;
        }
    }
}
