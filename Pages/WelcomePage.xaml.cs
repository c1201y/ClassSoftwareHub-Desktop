using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

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
            App.Content.Changed -= OnContentChanged;
            App.Content.Changed += OnContentChanged;
            Populate();
            RefreshUpdateCard();
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
    }

    // ── 更新状态（2026-10-06 起，挂在快速开始区的「查看更新内容」主题色卡上） ──

    /// <summary>
    /// 「查看更新内容」卡的两种状态：
    /// ①「待装」——后台下载完成、存档里有待装标记，且包还在、tag 还不是当前版本 → 右端换出「立即安装」按钮；
    /// ②「已是最新」——其余所有情况 → 副标题写当前版本号 + 已是最新版本，点击进「更新日志」页。
    ///
    /// 之所以不做「发现新版本」这一态：那需要在首页发起网络检查，会拖慢首屏。
    /// 后台本来就会自动检查并下载，真下了包自然落到状态①，够用了。
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
        // 注意：卡片本身就是强调色底 ⇒ 不再靠 BorderBrush 表达"待装"，改用右端「立即安装」按钮。
        if (pending)
        {
            var tag = s.UpdatePendingTag.Length > 0 ? s.UpdatePendingTag : "新版本";
            ChangelogCardTitle.Text = $"{tag} 已下载就绪";
            ChangelogCardSubtitle.Text = "安装包已通过校验，可立即安装";
            UpdateInstallButton.Visibility = Visibility.Visible;
            ChangelogChevron.Visibility = Visibility.Collapsed;
        }
        else
        {
            ChangelogCardTitle.Text = "查看更新内容";
            ChangelogCardSubtitle.Text =
                $"当前版本 {ShellConfig.VersionPrefix}{ShellConfig.ShellVersion} · 已是最新版本";
            UpdateInstallButton.Visibility = Visibility.Collapsed;
            ChangelogChevron.Visibility = Visibility.Visible;
        }
    }

    /// <summary>点「查看更新内容」卡 → 去「更新日志」页（待装态由「立即安装」按钮接管，这里让开）。</summary>
    private void ChangelogCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (UpdateInstallButton.Visibility == Visibility.Visible) return;
        App.MainWindow?.Shell.NavigateTo("changelog");
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
    /// 正文＝今日新增 N 款＋具体名字。2026-10-07 Nick 要求**两项合并**到这一条，放在设置卡片正下方。
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
        CollectedBar.Message = string.Empty;   // 正文只留给「今日新增」，没有就空着
        CollectedBar.IsOpen = true;

        _ = RefreshTodayAddedAsync(count);
    }

    /// <summary>
    /// 补「今日新增」那半句：**具体是哪几款**（用中文名，不是 slug）。
    ///
    /// 数据得联网：软件 json 里没有任何日期字段，只能去问 GitHub 的提交历史
    /// （算法与代价见 <see cref="NewlyAddedService"/>）。
    /// **只增不补** —— 只有算出 &gt;0 才写正文；算不出来、或今天确实没新增，正文就保持空。
    /// ⛔ 不摆「今日新增 0 款」，免得那条 InfoBar 里多一个会骗人的数字。
    /// </summary>
    private async Task RefreshTodayAddedAsync(int collected)
    {
        var added = await NewlyAddedService.GetTodayAsync();
        if (added is null || added.Count <= 0) return;

        // 名字从本机清单里取（slug → 中文名）；万一清单里找不到这一条（刚加还没来得及同步？）
        // 就退回 slug 本身，总比不显示强
        var names = added.Ids.Select(id => App.Content.FindById(id)?.Name ?? id).ToList();

        // await 回来不一定还在 UI 线程（服务内部一律 ConfigureAwait(false)）
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ApplyTodayAdded(collected, names));
            return;
        }

        ApplyTodayAdded(collected, names);
    }

    /// <summary>把名单写进 InfoBar 正文。名单太长只列前 6 个，后面补「等 N 款」。</summary>
    private void ApplyTodayAdded(int collected, IReadOnlyList<string> names)
    {
        // 这期间清单又变了（同步完成）→ 已经有人重刷过，别用旧数字盖回去
        if (App.Content.Apps.Count != collected) return;

        CollectedBar.Title = $"已收录 {collected} 款软件";
        CollectedBar.Message = "今日新增 " + names.Count + " 款：" + (names.Count <= 6
            ? string.Join("、", names)
            : string.Join("、", names.Take(6)) + $" 等 {names.Count} 款");
        CollectedBar.IsOpen = true;
    }

    /// <summary>一行快捷入口：项目仓库 / 作者主页 / 赞助作者 / 加入Q群 / 更新日志。</summary>
    private void BuildQuickLinks() => QuickGrid.ItemsSource = QuickLinks.Build(App.Content.Ui);

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
            // ⚠️ 缓存文件名也带上版本号：ExtractToCache 靠"长度不同"判过期，
            //    万一同尺寸换图会被判成没过期，带上版本号就不会串图。
            var path = EmbeddedAssets.ExtractToCache("dv1.1.png", "banner-dv1.1.png");
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
