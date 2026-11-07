using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 原生外壳：顶部标题栏 + 左侧导航 + 页面容器。
/// 导航：上半区「首页 / 软件下载 / 内置工具 / 侧边布局 / 实验性功能」，
/// 下半区「任务进行 / 提交软件 / 反馈中心 / 更新日志 / 设置」。
/// ⚠️「实验性功能」是分组父项，点它=进总览页，子项「本机核实」可直达（见 ShellPage.xaml 的注释）。
/// ⚠️「反馈中心」是**两级**：选类型页（<see cref="FeedbackPage"/>）→ 表单页（<see cref="FeedbackFormPage"/>），
///    卡片单击走 <see cref="NavigateToFeedbackForm"/>，返回走导航栏那一个返回按钮。
/// ⚠️ 所有页面（含「提交软件」）都是 WinUI3 自绘；只有详情页那种「在应用内看一眼网页」的浮层（MainWindow.WebSheet）会用到 WebView2。
/// </summary>
public sealed partial class ShellPage : UserControl
{
    /// <summary>给 MainWindow 用：SetTitleBar 需要这个元素（原生页面的拖动区）。</summary>
    public Grid TitleBarElement => TitleBarArea;

    public ShellPage()
    {
        InitializeComponent();

        // 角标只在本版本装好后的头两周出现（见 BadgesAreFresh）。
        // ⚠️ 必须在 ApplyNavLayout 之前算 —— 那里会调 ApplyNavBadges。
        _badgesFresh = BadgesAreFresh();

        InitSearch();

        // 内置工具分组的子项、以及「查阅单词 / 文档库 / 教室喊话」这三个可变页面，
        // 全按 Data/NavLayout 摆位（2026-11-04：用户可以在设置里把常用的拎到一级、把不常用的收进分组）。
        ApplyNavLayout();

        // 「实验性功能」同理，清单在 ExperimentalFeatureCatalog（2026-11-01 收编）
        BuildExperimentalNavItems();

        // 新功能角标（New / Beta）：必须在上面两组子项都建好之后挂。
        // ⚠️ 放到 Loaded 而不是构造函数里 —— 角标底色来自 ThemeBrush，要等这个控件进了树、
        //    拿到真实的 ActualTheme 才准（构造期拿到的是系统主题，跟应用主题可能对不上）。
        Loaded += (_, _) => ApplyNavBadges();
        ActualThemeChanged += (_, _) => ApplyNavBadges();

        // 搜索框要从标题栏拖动区里挖出来（见 UpdateTitleBarPassthrough 的注释）。
        // ⚠️ 只靠 TitleBarArea.SizeChanged 不够：SizeChanged 有可能在 App.MainWindow 赋值之前就触发，
        //    那一次会静默 return，之后再也不算 —— 所以进树之后再补一次。
        Loaded += (_, _) => UpdateTitleBarPassthrough();

        // 下载任务数变了 → 同步「任务进行」上的徽标（在下载就不占着界面，但得让人随时看见有几个在下）
        Services.DownloadManager.Current.Changed += UpdateDownloadBadge;
        UpdateDownloadBadge();

        // 面板显示模式变了（宽 ↔ 窄）→ 重新算内容区要不要给导航按钮让位
        Nav.DisplayModeChanged += (_, _) => ApplyPaneInset();
        ApplyPaneInset();

        // 切页计时（Debug / CSH_PERF=1 才有输出，见 Core/PerfLog.cs）。
        // 切页顺带收一次内存（2026-10-04，Nick 要求回到"跳一页收一次"的激进思路）。
        // ⚠️ 收本体在**后台线程**、且错开 0.6 秒（等动画与首帧过去）—— 跟 2026-10-01 被否掉的
        //    "UI 线程上延时 3 秒同步 GC"不是一回事，详见 Services/MemoryTrimmer.cs 的类注释。
        ContentFrame.Navigating += (_, e) => PerfLog.NavBegin(CurrentTag);
        ContentFrame.Navigated += (_, _) =>
        {
            PerfLog.NavEnd(ContentFrame.Content);
            UpdateBackButton();
            Services.MemoryTrimmer.ScheduleAfterNavigate();
        };
    }

    /// <summary>
    /// 按 <see cref="NavLayout"/> 把可移动项摆到该在的位置（2026-11-04 导航自定义）。
    ///
    /// <para>
    /// 可重复调用：设置页改完一项就调一次 <see cref="ReloadNavLayout"/>，导航立刻跟着变。
    /// 做法是**先全部撤下来再重摆** —— 逐个移动要处理索引漂移，而且「从一个分组换到另一个分组」
    /// 根本没法用 Remove/Insert 在原对象上表达。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 撤的是**导航项对象本身**：XAML 里带 x:Name 的那三个撤下来后对象还在，
    /// 原样插回去即可（图标、角标、无障碍名字都不丢），不必重建。
    /// </para>
    ///
    /// <para>
    /// 顺序规则（保持默认状态与加这个功能之前逐项一致）：教室喊话在「函数图像」上面；
    /// 其余提到一级的项统一排在「函数图像」之后、「课堂工具」之前，课堂类在前、系统类在后。
    /// </para>
    /// </summary>
    private void ApplyNavLayout()
    {
        var managed = new HashSet<string>(
            NavLayout.All.Select(p => p.Tag), StringComparer.OrdinalIgnoreCase);

        // 1) 把上次摆在一级的可移动项全部撤下来（含 XAML 里那三个静态项）
        for (var i = Nav.MenuItems.Count - 1; i >= 0; i--)
        {
            if (Nav.MenuItems[i] is NavigationViewItem { Tag: string tag } && managed.Contains(tag))
                Nav.MenuItems.RemoveAt(i);
        }

        // 2) 两个分组的子项整体重建
        ClassroomToolsNav.MenuItems.Clear();
        SystemToolsNav.MenuItems.Clear();

        // 3) 一级区：可移动项按「课堂在前、系统在后」排队，统一插在「课堂工具」上面
        var top = NavLayout.All
            .Where(p => p.AboveFunctionPlot == false && NavLayout.IsTopLevel(p.Tag))
            .OrderBy(p => p.Group == ToolGroup.System ? 1 : 0)
            .ToList();

        var at = Nav.MenuItems.IndexOf(ClassroomToolsNav);
        foreach (var placeable in top)
            Nav.MenuItems.Insert(at++, NavigationItemFor(placeable));

        // 4) 教室喊话那个特例：在一级时固定落在「函数图像」上面
        foreach (var placeable in NavLayout.Pages.Where(p => p.AboveFunctionPlot))
        {
            if (NavLayout.IsTopLevel(placeable.Tag))
                Nav.MenuItems.Insert(Nav.MenuItems.IndexOf(FunctionPlotNav), NavigationItemFor(placeable));
        }

        // 5) 二级：按分组挂回各自父项。顺序与总览页卡片同源（NavLayout.SecondLevel），两处不会对不上。
        foreach (var group in new[] { ToolGroup.Classroom, ToolGroup.System })
        {
            var parent = group == ToolGroup.System ? SystemToolsNav : ClassroomToolsNav;
            foreach (var placeable in NavLayout.SecondLevel(group))
            {
                parent.MenuItems.Add(new NavigationViewItem
                {
                    Content = placeable.Name,
                    Tag = placeable.Tag,
                    Icon = new FontIcon { Glyph = placeable.Glyph },
                });
            }
        }

        // 6) 当前页可能刚被搬了位置（一级 ⇄ 二级）—— 重新点亮，否则高亮会停在已撤下的那一项上
        SelectTag(CurrentTag);

        // 7) 角标：重建后新造出来的项还没挂（旧对象上的已随对象带走，_navLabels 记着原文，重复调用无害）
        ApplyNavBadges();

        AppLog.Step("shell", $"导航布局：一级 {Nav.MenuItems.OfType<NavigationViewItem>().Count()} 项，"
            + $"课堂工具 {ClassroomToolsNav.MenuItems.Count} 项，系统工具 {SystemToolsNav.MenuItems.Count} 项");
    }

    /// <summary>
    /// 可移动项对应的导航项：XAML 里那三个静态项复用原对象，工具项现造一个。
    /// </summary>
    private NavigationViewItem NavigationItemFor(NavPlaceable placeable)
    {
        NavigationViewItem? existing = placeable.Tag switch
        {
            "announce" => AnnounceNav,
            "dictionary" => DictionaryNav,
            "docs" => DocsNav,
            _ => null,
        };

        return existing ?? new NavigationViewItem
        {
            Content = placeable.Name,
            Tag = placeable.Tag,
            Icon = new FontIcon { Glyph = placeable.Glyph },
        };
    }

    /// <summary>
    /// 设置页改完导航项之后调这个，立即生效（不重启、不重开窗口）。
    /// </summary>
    public void ReloadNavLayout()
    {
        ApplyNavLayout();

        // 正停在工具总览页时，卡片列表也得跟着变：那个页 NavigationCacheMode=Enabled，
        // 不重新导航一次就不会重算（变化本身在 OnNavigatedTo 里）。
        if (CurrentTag is "tools-classroom" or "tools-system") NavigateTo(CurrentTag);
    }

    /// <summary>
    /// 「实验性功能」分组的子项：按 <see cref="ExperimentalFeatureCatalog.All"/> 动态生成
    /// （tag = <see cref="ExperimentalFeature.Tag"/>）。
    ///
    /// <para>
    /// ⛔ 清单只有 ExperimentalFeatureCatalog 一份 —— 导航子项与总览页卡片都从它来。
    /// 2026-11-01 之前这里是 <c>ShellPage.xaml</c> 里手写的七条，另一份在 ExperimentalPage 里，
    /// 结果「白板专杀 / AppData 管理 / 系统修复」转正搬走后总览页还留着三张旧卡，
    /// 而「虚拟内存」只加进了导航、总览页根本看不见 —— 两份清单必然漂移。
    /// </para>
    /// </summary>
    private void BuildExperimentalNavItems()
    {
        foreach (var def in ExperimentalFeatureCatalog.All)
        {
            ExperimentalNav.MenuItems.Add(new NavigationViewItem
            {
                Content = def.Name,
                Tag = def.Tag,
                Icon = new FontIcon { Glyph = def.Glyph },
            });
        }
    }

    /// <summary>
    /// 导航项右侧的文字角标：tag →（文字，是否用警告色）。
    ///
    /// <para>
    /// 目的：每次更新用户不知道加了什么，靠这个让新入口自己冒出来。三种状态的含义
    /// （2026-11-03 Nick 定的口径）：
    /// <list type="bullet">
    ///   <item><b>New</b>（绿色）＝ 这个版本刚加，用户第一次见；</item>
    ///   <item><b>Beta</b>（黄色）＝ 功能**已经确定要做**，只是还在打磨；</item>
    ///   <item><b>实验性功能</b>（页面标题旁的胶囊，<c>Controls/ExperimentalBadge</c>）＝
    ///         连要不要做都没定，随时可能拿掉 —— 和 Beta 不是一回事，别混用。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// ⚠️ 不再需要"每次发版手动改这张表"：角标**只在本版本装好后的 15 天内出现**
    /// （<see cref="BadgeLifetimeDays"/>），过期自动消失，不用谁去删行（2026-11-04 Nick 定）。
    /// 换了版本会重新起算，所以新版里的新功能仍然会被提示一轮。
    /// 表里留着的行 = 这个功能还在"新 / Beta"的语义里，只是平时不显示；功能转正时再删。
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, (string Text, bool Warning)> NavBadges =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // ── 2026-11-07：正式版首发，把 dv1.1.3 之后新增的入口全部标一遍 ──
            // 口径：New = 这一版第一次见；Beta = 确定要做、还在打磨。
            // ⚠️ 这张表是拿 dv1.1.3（2026-10-15 正式版）的导航逐项比对出来的
            //    （`git show dv1.1.3:Pages/ShellPage.xaml` + 逐个页面 `git cat-file -e dv1.1.3:<路径>`）。
            //    **1.1.3 就已经有的不标**：图片取色 / 随机抽号 / 课堂计时器 / 全屏时钟 / 编码哈希 /
            //    自动关机 / 白板专杀 / AppData 管理 / 本机核实 / 程序专杀 / 应用卸载 /
            //    侧边布局 / 任务进行 / 反馈中心 / 更新日志 / 设置。
            // ⚠️ 「课堂工具 / 系统工具」两个分组父项也不标 —— 那是分组结构的变化，不是功能。

            // Beta：能力边界还没跑满，先让人有预期
            ["hubai"] = ("Beta", true),          // 对话、内联动作、模型配置都还在迭代
            ["ppttouch"] = ("Beta", true),       // 触屏行为跟设备与 PowerPoint 版本有关
            ["ghostfile"] = ("Beta", true),      // 删除不可逆，且部分成因只能刷新视图、清不掉条目

            // New：这一版第一次出现
            ["dictionary"] = ("New", false),
            ["docs"] = ("New", false),
            ["function-plot"] = ("New", false),
            ["announce"] = ("New", false),
            ["disk-map"] = ("New", false),
            ["equation"] = ("New", false),
            ["chemistry"] = ("New", false),
            ["repair"] = ("New", false),
            ["startup"] = ("New", false),
            ["pagefile"] = ("New", false),
            ["sysfeatures"] = ("New", false),
            ["systemtweaks"] = ("New", false),
        };

    /// <summary>
    /// 导航角标挂多久（天）：**本版本第一次运行**起算。改这个数就行。
    /// <para>
    /// 2026-11-07 Nick 定：**3 天**。这一版是转正式版后的第一次大更新，从 1.1.3 升上来的用户
    /// 一口气多了十来个入口 —— 提示要看得见，但不该常驻成装饰，3 天正好。
    /// （上一版是 15 天，2026-11-04 定的；那条已被这次取代，别再改回去。）
    /// </para>
    /// </summary>
    private const int BadgeLifetimeDays = 3;

    /// <summary>本版本还在角标有效期内（构造时算一次，一次运行中不会变）。</summary>
    private bool _badgesFresh;

    /// <summary>
    /// 角标还能不能挂。顺带承担"本版本第一次运行"的打点：版本号跟存档里记的不一样就重新计时。
    /// <para>
    /// ⚠️ 记的是**首次运行**时间，不是安装时间 —— 装完隔几天才打开的情况更常见，按运行时间算才准。
    /// ⚠️ 存档读不出来 / 时间解析失败时返回 true（照常显示）：宁可多提示一次，
    ///    也别因为一个坏字段把功能提示整个吞掉。
    /// </para>
    /// </summary>
    private static bool BadgesAreFresh()
    {
        try
        {
            var settings = App.Settings.Current;
            var version = ShellConfig.ShellVersion;

            if (!string.Equals(settings.BadgeSinceVersion, version, StringComparison.Ordinal))
            {
                settings.BadgeSinceVersion = version;
                settings.BadgeSinceUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                App.Settings.Save();
                AppLog.Step("shell", $"导航角标：新版本 {version} 首次运行，{BadgeLifetimeDays} 天计时从这里开始");
                return true;
            }

            if (!DateTime.TryParse(settings.BadgeSinceUtc, CultureInfo.InvariantCulture,
                                   DateTimeStyles.RoundtripKind, out var since))
                return true;

            // 时钟被往回拨时 age 是负的，仍算"新鲜"（显示），不折腾用户
            var age = DateTime.UtcNow - since.ToUniversalTime();
            var fresh = age < TimeSpan.FromDays(BadgeLifetimeDays);

            AppLog.Step("shell", fresh
                ? $"导航角标：本版本第 {age.TotalDays:0.#} 天，还有 {(BadgeLifetimeDays - age.TotalDays):0.#} 天到期"
                : $"导航角标：本版本已过 {age.TotalDays:0.#} 天，到期不再显示");

            return fresh;
        }
        catch (Exception ex)
        {
            AppLog.Warn("shell", "导航角标计时读取失败（照常显示）：" + ex.Message);
            return true;
        }
    }

    /// <summary>
    /// 导航项原本的文字。角标是把 <see cref="NavigationViewItem.Content"/> 换成「文字 + 角标」的组合，
    /// 换完就取不到原文了；而主题切换时要重造一次（颜色是代码设的具体值，不会自己跟着变），
    /// 所以第一次换之前把原文记下来。
    /// </summary>
    private readonly Dictionary<NavigationViewItem, string> _navLabels = new();

    /// <summary>把 <see cref="NavBadges"/> 挂到对应的导航项上。必须在导航项都建好之后调用。</summary>
    private void ApplyNavBadges()
    {
        foreach (var item in EnumerateNavItems(Nav.MenuItems))
        {
            if (item.Tag is not string tag) continue;

            // 记下原文（第一次进来时 Content 还是 XAML 里写的字符串）
            if (!_navLabels.ContainsKey(item) && item.Content is string label)
                _navLabels[item] = label;

            if (!_navLabels.TryGetValue(item, out var text)) continue;

            // _badgesFresh = false（装好超过 15 天）时就当表是空的：一次也不挂，文字原样还原
            if (_badgesFresh && NavBadges.TryGetValue(tag, out var badge))
            {
                // ⚠️ NavigationViewItem 默认 HorizontalContentAlignment = Left，
                //    不改成 Stretch 的话内容不会被撑开，上面 Grid 的 * 列等于没设。2026-11-04
                item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                item.Content = ComposeLabel(text, badge.Text, badge.Warning);

                // 内容换成组合元素后，列表项自己念不出名字了（UIA 里它退化成「NavigationViewItem」），
                // 显式把名字挂回列表项上。2026-11-04
                AutomationProperties.SetName(item, text);
            }
            else if (item.Content is not string)
            {
                // 从表里删掉这一行 = 角标自动摘掉，文字原样还原
                item.Content = text;
            }
        }
    }

    /// <summary>
    /// 文字和角标并排。用 <see cref="Grid"/> 而不是水平 <see cref="StackPanel"/>：
    /// 面板展开宽度 296（`OpenPaneLength`，2026-11-04 从 252 加宽 —— 252 下
    /// 「PowerPoint 触控优化」作为子项还多一层缩进，加个 Beta 就撑爆，实测会截成「PowerPoint 触控…」），
    /// 再长就让 Grid 的 <c>*</c> 列先省略文字，角标永远完整。2026-11-04。
    ///
    /// <para>
    /// 角标右对齐到内容区右边缘（分组项则是展开箭头左边一格），所以列与列之间看着有空白 ——
    /// 这是刻意的：一列角标对齐成一条竖线，比各自紧贴文字更好扫读。
    /// </para>
    /// </summary>
    private FrameworkElement ComposeLabel(string text, string badgeText, bool warning)
    {
        var label = new TextBlock
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            // 字体不显式设：让它继承导航项自己的字体，跟其它项保持一致
        };

        var badge = MakeTextBadge(badgeText, warning);

        // 文字列用 *（占满剩余宽度、能省略），角标列 Auto（永远完整）。
        // ⚠️ 两列都 Auto 时水平布局**不会**约束文字 —— 文本原样画出去被裁掉，角标一起被挤出可视区
        //    （「PowerPoint 触控优化」是子项还有一层缩进，实测就撑爆了）。2026-11-04。
        var grid = new Grid
        {
            ColumnSpacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            // 帮朗读工具念出导航项名字（内容换成组合元素后，它自己读不出文字了）
            Children = { label, badge },
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        AutomationProperties.SetName(grid, text);

        Grid.SetColumn(label, 0);
        Grid.SetColumn(badge, 1);

        return grid;
    }

    /// <summary>
    /// 文字角标：<see cref="Border"/> + <see cref="TextBlock"/> 两个原生基础控件的组合。
    ///
    /// <para>
    /// ⛔ 不用 <see cref="InfoBadge"/>：它原生只认数字和图标，要塞文字得整个替换 ControlTemplate ——
    /// 那就是"自己画一个控件"，跟"能原生就原生"这条铁律正相反（2026-11-04 Nick 定）。
    /// </para>
    ///
    /// <para>
    /// 2026-11-04 第二次调：Nick 对着 WinUI Gallery 的「Experimental」标签说我们这版
    /// "第三方味道很浓"。差在四处，全部往系统那边靠：
    /// <list type="bullet">
    ///   <item><b>字重</b>：SemiBold → Normal。小字加粗是网页 tag 的做法，系统标签一律常规字重；</item>
    ///   <item><b>内边距</b>：6,1 → 7,1。垂直只留 1 像素容易被压扁，水平给够才不显得挤；</item>
    ///   <item><b>底色</b>：自己算的"语义色 + 15% 透明" → 系统 <c>SystemFillColor*Background</c> 的**实色**
    ///         （深色下 Success 是 #393D1B、Caution 是 #433519）—— InfoBar 的严重性底色就是这对资源。
    ///         叠透明度会跟着窗口背景跑，Mica / 亚克力上发灰，这就是"第三方味"的来源；</item>
    ///   <item><b>圆角</b>：4，跟 InfoBar / 卡片同一档，⛔ 不要做成胶囊。</item>
    /// </list>
    /// 颜色仍取系统语义色（Success / Caution），深浅主题各自适配。
    /// </para>
    /// </summary>
    private Border MakeTextBadge(string text, bool warning)
    {
        var fg = (SolidColorBrush)ThemeBrush.Get(this, warning ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush");

        // 底色用系统那对**调好的实色**（Success/Caution Background），⛔ 不是"语义色压透明度"。
        // 叠透明度会跟着窗口背景走，Mica / 亚克力上发灰发脏 —— 这正是"第三方味"的来源（2026-11-04）。
        // InfoBar 的严重性底色用的也是同一对资源。
        var bg = (SolidColorBrush)ThemeBrush.Get(
            this, warning ? "SystemFillColorCautionBackgroundBrush" : "SystemFillColorSuccessBackgroundBrush");

        return new Border
        {
            Background = bg,
            // 4 = InfoBar / 卡片同一档圆角，跟系统控件对齐（3 会更像网页 chip）
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 1, 7, 1),
            VerticalAlignment = VerticalAlignment.Center,
            // 离导航项右边缘留一档，别真的顶到边 —— 官方那个标签也没贴死
            Margin = new Thickness(0, 0, 2, 0),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.Normal,
                Foreground = fg,
            },
        };
    }

    /// <summary>连子项一起往下走（实验性功能、内置工具这两组下面都是有子项的）。</summary>
    private static IEnumerable<NavigationViewItem> EnumerateNavItems(IList<object> items)
    {
        foreach (var raw in items)
        {
            if (raw is not NavigationViewItem item) continue;

            yield return item;

            foreach (var child in EnumerateNavItems(item.MenuItems)) yield return child;
        }
    }

    /// <summary>「任务进行」上的 InfoBadge = 正在下载的任务数；一个都没有就把徽标整个摘掉。</summary>
    private void UpdateDownloadBadge()
    {
        try
        {
            var active = Services.DownloadManager.Current.ActiveCount;
            DownloadsNav.InfoBadge = active > 0 ? new InfoBadge { Value = active } : null;
        }
        catch
        {
            // 徽标画不出来不值得影响导航
        }
    }

    /// <summary>NavigationView 自带的返回按钮：能退就亮，退到头就灰（原生尺寸和动画）。</summary>
    private void UpdateBackButton()
    {
        Nav.IsBackEnabled = ContentFrame.CanGoBack;
    }

    /// <summary>
    /// 窄窗口（面板显示模式 Minimal，窗口宽 &lt; 820 DIP）下，NavigationView 会把返回按钮和汉堡按钮
    /// **浮在内容区左上角**，不给内容留位置 —— 而每个页面的标题正好也在左上角，于是两样东西叠在一起
    /// （Nick 2026-09-26 反馈「标题和返回、汉堡重叠」）。
    /// 这里按显示模式给内容整体让出头顶那一条：Minimal 让 48（＝按钮那一行的高度）；其余模式不用让
    /// （Compact 时按钮在左侧 48 宽的面板栏里、Expanded 时在展开的面板里，都压不到内容）。
    /// </summary>
    private void ApplyPaneInset()
    {
        var inset = Nav.DisplayMode == NavigationViewDisplayMode.Minimal ? 48d : 0d;
        if (Math.Abs(ContentFrame.Margin.Top - inset) > 0.5)
            ContentFrame.Margin = new Thickness(0, inset, 0, 0);
    }

    private void Nav_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
        UpdateBackButton();
    }

    /// <summary>
    /// 当前所在页面的 tag（<see cref="NavigateTagCore"/> 每次导航时更新）。
    /// 用途：未处理异常写日志时带上它 —— 否则「布局循环」这类只在某一页出现的问题根本没法定位。
    /// </summary>
    public static string CurrentTag { get; private set; } = "home";

    /// <summary>标题栏小图标（MainWindow 把站点图标塞进来）。</summary>
    public void SetIcon(Microsoft.UI.Xaml.Media.ImageSource? source) => TitleIcon.Source = source;

    /// <summary>数据加载完之后调用一次：显示数据来源、进首页。</summary>
    public void Init()
    {
        TitleText.Text = ShellConfig.AppName;

        var start = StartupPageTag();
        SelectTag(start);
        NavigateTag(start);

        // 开发用：--tool=<id> 直接落到某个内置工具（会走载体选择）。
        // 有了它验"Web 版 / 原生版"两条路都只编一趟，不用手点工具列表。
        if (StartupToolId() is { } toolId
            && ToolCatalog.All.FirstOrDefault(t => t.Id == toolId)?.Page is { } page)
        {
            NavigateToTool(page);
        }
    }

    /// <summary><c>--tool=&lt;id&gt;</c>，id 跟 <see cref="ToolCatalog"/> 里的一致。没给返回 null。</summary>
    private static string? StartupToolId()
    {
        try
        {
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (!arg.StartsWith("--tool=", StringComparison.OrdinalIgnoreCase)) continue;
                var id = arg["--tool=".Length..].Trim();
                if (id.Length > 0) return id;
            }
        }
        catch
        {
            // 命令行读不到就当没给
        }

        return null;
    }

    /// <summary>
    /// 启动落在哪一页：<c>--page=&lt;tag&gt;</c>，tag 跟导航项的一样（<c>--page=feedback</c>、
    /// <c>--page=machinecheck</c>、<c>--page=tools</c>…）。没给、给了不认识的、或者参数本身读不到，
    /// 一律回首页。
    ///
    /// 为什么留这个参数：以前想让程序直接落在某一页，只能临时改这个方法，验证完再改回来 ——
    /// 一个页面固定吃掉两趟全量构建。有了它就只编一趟，排障和自动验证都省一半时间。
    /// 正常启动不传这个参数，行为跟以前一模一样。
    /// </summary>
    private static string StartupPageTag()
    {
        try
        {
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (!arg.StartsWith("--page=", StringComparison.OrdinalIgnoreCase)) continue;
                var tag = arg["--page=".Length..].Trim();
                if (tag.Length > 0) return tag;
            }
        }
        catch
        {
            // 命令行读不到就当没给
        }

        return "home";
    }

    /// <summary>外部（首页卡片等）跳转导航用：先高亮导航项，再切页面。</summary>
    public void NavigateTo(string tag)
    {
        SelectTag(tag);
        NavigateTag(tag);
    }

    /// <summary>
    /// 进「设置」并滚到某个分组（需求 1:18 的 <c>csh://settings/&lt;section&gt;</c>）。
    /// <para>
    /// 认不出的分组不报错，停在页面顶部 —— 文章里的链接可能比应用更新，不该因此弹错误。
    /// 映射见 <c>SettingsPage.ActivateSection</c>。
    /// </para>
    /// </summary>
    public void NavigateToSettings(string? section)
    {
        NavigateTo("settings");
        if (ContentFrame.Content is SettingsPage page) page.ActivateSection(section);
    }

    /// <summary>
    /// 从外部（首页卡片等）进「实验性功能」：切到总览页，并把分组展开。
    /// 展开这一步是给"从外面跳进来"补的 —— 不然看不出这一组里还有子项。
    /// ⚠️ 在导航栏里点父项那条路**不走它**：那种情况 NavigationView 自己会 toggle 展开/收起。
    /// </summary>
    public void NavigateToExperimental()
    {
        NavigateTo("experimental");
        ExperimentalNav.IsExpanded = true;
    }

    /// <summary>
    /// 从外部（首页卡片等）进「内置工具」：切到工具索引页，并把分组展开。
    /// 与 <see cref="NavigateToExperimental"/> 对称 —— 从外面跳进来时看不出这一组还有子项。
    /// </summary>
    public void NavigateToTools()
    {
        NavigateTo("tools");
        ClassroomToolsNav.IsExpanded = true;
        SystemToolsNav.IsExpanded = false;
    }

    /// <summary>
    /// 从外部（首页卡片等）进「课堂工具」：切到该组列表页，并把这一组展开、另一组收起。
    /// 2026-10-23 加 —— 首页卡片跟着导航一起拆了（原先只有一张「内置工具」指向总索引）。
    /// </summary>
    public void NavigateToClassroomTools()
    {
        NavigateTo("tools-classroom");
        ClassroomToolsNav.IsExpanded = true;
        SystemToolsNav.IsExpanded = false;
    }

    /// <summary>从外部进「系统工具」：同上，展开的组相反。</summary>
    public void NavigateToSystemTools()
    {
        NavigateTo("tools-system");
        ClassroomToolsNav.IsExpanded = false;
        SystemToolsNav.IsExpanded = true;
    }

    /// <summary>
    /// 跳到「内置工具」里的某个工具页（浮窗里的「详细设置」用）。
    /// 先把工具列表页铺一层，这样工具页左上角的返回按钮能正常退回列表。
    /// </summary>
    public void NavigateToTool(Type pageType) => NavigateToTool(pageType, null, viaList: true);

    /// <summary>
    /// 带参数跳工具页。
    /// <paramref name="viaList"/> = false 时**不铺工具列表**：返回一次就回到来的那一页 ——
    /// 软件详情页点「校验」去哈希工具，用户心理是"看一眼再回来"，不该先退到工具列表。
    /// 两种走法都把导航高亮切到「内置工具」（人确实在工具区里）。
    /// </summary>
    public void NavigateToTool(Type pageType, object? parameter, bool viaList = true)
    {
        SelectTag("tools");
        if (viaList && ContentFrame.CurrentSourcePageType != typeof(ToolsPage))
            ContentFrame.Navigate(typeof(ToolsPage));

        // ⭐ 载体选择（2026-10-23）：这个工具有 Web 版、且本机跑得起来 → 交给宿主页；
        //    否则走原生页。⛔ 别在这里加提示或角标 —— 降级对用户是无感的（Nick 定）。
        var def = ToolCatalog.All.FirstOrDefault(t => t.Page == pageType);
        if (def is not null && ToolCarrier.RequestFor(def.WebToolId, pageType, parameter) is { } request)
        {
            ContentFrame.Navigate(typeof(ToolHostPage), request);
            return;
        }

        ContentFrame.Navigate(pageType, parameter);
    }

    /// <summary>
    /// 进反馈中心的**表单页**（「报告问题」/「提出建议」卡片单击时调）。
    ///
    /// 导航高亮一起切到「反馈中心」—— 人确实还在反馈中心这一区里，只是从"选类型"走到了"填内容"。
    /// ⚠️ 这里**不铺选择页**：用户本来就是从选择页点进来的，它已经在返回栈里，
    ///    导航栏返回按钮（<c>ContentFrame.GoBack()</c>）一按就回去。
    /// </summary>
    public void NavigateToFeedbackForm(string kind)
    {
        SelectTag("feedback");
        ContentFrame.Navigate(typeof(FeedbackFormPage), kind);
    }

    /// <summary>只切页面，不动导航高亮（软件下载页内部按分类切换时用）。</summary>
    public void NavigateTag(string tag)
    {
        try
        {
            NavigateTagCore(tag);
        }
        catch (Exception ex)
        {
            // 某个页面自己加载失败（比如 XAML 里引了不存在的资源键）不该把整个应用带走 ——
            // 之前踩过：应用会记住上次停留的页，页面一坏就变成「一启动就崩」，用户连设置都进不去。
            LogNavFailure(tag, ex);
            if (tag != "home")
            {
                try { NavigateTagCore("home"); } catch { }
            }
        }
    }

    private void NavigateTagCore(string tag)
    {
        CurrentTag = tag;   // 崩溃日志靠它记下"当时在哪一页"（见 App.OnUnhandledException）

        // 内置工具子页：tag 就是 ToolCatalog 里的 Id（导航子项动态生成，见 BuildToolNavItems）。
        // 按 Id 反查拿页面类型，工具清单只有一份，不用给每个工具单独写 case。
        var toolDef = ToolCatalog.All.FirstOrDefault(t => t.Id == tag);
        if (toolDef?.Page is not null)
        {
            // ⭐ 载体选择（2026-10-25 补，与 NavigateToTool 同一套）：有 Web 版、本机跑得起来 → 宿主页；
            //    否则原生页。⛔ 别在这里加提示或角标 —— 降级对用户是无感的（Nick 定）。
            // ⚠️ 为什么补：以前这里直接 Navigate 原生页，漏了载体检查 —— 导航栏点击、--page= 直达、
            //    工具索引页卡片点击三条路全都进不了网页版。2026-10-25 实测 --page=function-plot
            //    落在原生 FunctionPlotPage，而 --tool= 和学科工具浮窗却走 Web，同一工具两副嘴脸。
            if (ToolCarrier.RequestFor(toolDef.WebToolId, toolDef.Page, null) is { } toolRequest)
                ContentFrame.Navigate(typeof(ToolHostPage), toolRequest);
            else
                ContentFrame.Navigate(toolDef.Page);
            return;
        }

        switch (tag)
        {
            case "announce":
                // 教室喊话（2026-10-25 Nick：从工具里提出来单独成项）。纯原生页，不经 ToolCarrier ——
                // 它的本体是「开关 + 后台轮询 + 全屏播报」，不是可替换的网页小工具。
                ContentFrame.Navigate(typeof(Tools.AnnouncePage));
                return;
            case "submit":
                // 2026-10-30：导航项已移除，但这个 case 必须留着 —— 入口还有三处：
                // 软件下载页顶部按钮、首页「提交软件」卡片、命令行 --page=submit。
                // 删了它们会一起掉进 default 直接回首页，而且不报错。
                ContentFrame.Navigate(typeof(SubmitPage));
                return;
            case "feedback":
                ContentFrame.Navigate(typeof(FeedbackPage));
                return;
            case "feedback-form":
                // 反馈中心的表单页。正常流程是从「反馈中心」点类型卡进来（带 kind 参数，
                // 走 NavigateToFeedbackForm）；这个 case 只服务 --page=feedback-form 直达 ——
                // 不带参数，页面自己退回草稿里记着的类型。
                ContentFrame.Navigate(typeof(FeedbackFormPage));
                return;
            case "downloads":
                ContentFrame.Navigate(typeof(DownloadsPage));
                return;
            case "machinecheck":
                ContentFrame.Navigate(typeof(MachineCheckPage));
                return;
            case "procguard":
                ContentFrame.Navigate(typeof(ProcessGuardPage));
                return;
            case "uninstaller":
                // 应用卸载（2026-10-12 加，实验性功能分组下）
                ContentFrame.Navigate(typeof(UninstallPage));
                return;
            case "startup":
                // 开机启动项（2026-10-16 加，实验性功能分组下）
                ContentFrame.Navigate(typeof(StartupPage));
                return;
            // ⚠️ 自动关机（autoshutdown）2026-10-12 转正搬进「内置工具」，走上面 ToolCatalog 那条路，
            //    这里不再单写 case（写了也到不了：工具路径在前面就 return 了）。
            // ⚠️ 白板专杀 / AppData 管理 / 系统修复 同理 —— 2026-10-29 从「实验性功能」转正搬进
            //    ToolCatalog 的 System 组，三个 case 已删。它们的 Id 必须与原来的 tag 一字不差，
            //    否则 page= 启动参数与首页快捷方式会掉到 default（回首页）。
            case "logs":
                ContentFrame.Navigate(typeof(LogViewerPage));
                return;
            // ⚠️ 系统优化 / 系统功能 / 虚拟内存 / PowerPoint 触控优化 / 幽灵文件清理 2026-11-07 从
            //    「实验性功能」转正搬进 ToolCatalog 的 System 组，五个 case 已删 —— 走上面按 Id 反查那条路。
            //    Id 与原 tag 一字不差，所以 page= 启动参数、OOBE 完成页跳转、Hub AI 的动作清单都不用改。
            case "dictionary":
                // 查单词（需求清单第 17 条）：纯原生页，词源调度在 Services/Dictionary 里，
                // 不经 ToolCarrier —— 它不是可替换的网页小工具，也不需要 WebView2。
                ContentFrame.Navigate(typeof(DictionaryPage));
                return;
            case "experimental":
                // 「实验性功能」分组的父项**自己就是总览入口**：点它 = 展开/收起分组 + 进这一页。
                ContentFrame.Navigate(typeof(ExperimentalPage));
                return;
            case "changelog":
                ContentFrame.Navigate(typeof(ChangelogPage));
                return;
            case "settings":
                ContentFrame.Navigate(typeof(SettingsPage));
                return;
            case "sidebar":
                ContentFrame.Navigate(typeof(SidebarLayoutPage));
                return;
            case "tools-classroom":
                ContentFrame.Navigate(typeof(ToolsPage), "classroom");
                return;
            case "tools-system":
                ContentFrame.Navigate(typeof(ToolsPage), "system");
                return;
            case "apps":
                ContentFrame.Navigate(typeof(SoftwarePage), "");
                return;
            case "hubai":
                // Hub AI 对话页（2026-10-18 加，导航上半区，紧跟软件下载）
                ContentFrame.Navigate(typeof(HubAiPage));
                return;
            case "docs":
                // 文档库列表页（需求 1:15）。正文页走 NavigateToDocArticle，不在导航里出现。
                ContentFrame.Navigate(typeof(DocLibraryPage));
                return;
            case "hubai-settings":
                // Hub AI 二级设置页：不在导航栏，入口是「设置 → Hub AI」那张卡
                ContentFrame.Navigate(typeof(HubAiSettingsPage));
                return;
            case "home":
            default:
                if (tag.StartsWith("cat:", StringComparison.Ordinal))
                    ContentFrame.Navigate(typeof(SoftwarePage), tag.Substring(4));
                else
                    ContentFrame.Navigate(typeof(WelcomePage));
                return;
        }
    }

    /// <summary>页面加载失败的兜底日志（和 App 的崩溃日志写同一个文件，事后好查）。</summary>
    private static void LogNavFailure(string tag, Exception ex)
    {
        try
        {
            Core.AppLog.Error("crash", $"导航到「{tag}」失败: {ex}");
        }
        catch
        {
            // 日志写不进去就算了，别在这里再抛
        }
    }

    private void SelectTag(string tag)
    {
        // 分组父项（实验性功能 / 内置工具）**不进 NavigationView 的选中体系**（原因见 ShellPage.xaml 的注释）——
        // 高亮得手动打：先清掉别的选中项，再点亮父项自己。
        // ⚠️ 这里**不碰 IsExpanded**：点父项时 NavigationView 自己会 toggle 展开/收起，
        //    在这儿硬展开的话，分组就再也收不起来了（每次点都被撑开）。
        //    需要"顺带展开"的入口（首页卡片那种）走 NavigateToExperimental / NavigateToTools。
        foreach (var top in AllTopItems())
        {
            if ((top.Tag as string) != tag) continue;

            if (top.MenuItems.Count > 0)
            {
                // 分组父项：手动点亮，不碰 SelectedItem（塞 group header 进去会顺着分组 Invoke 第一个子项）
                Nav.SelectedItem = null;
                foreach (var item in AllNavigationItems()) item.IsSelected = false;
                top.IsSelected = true;
            }
            else
            {
                // 普通项：走 SelectedItem；同时把分组父项残留的手动高亮熄掉
                foreach (var item in AllNavigationItems())
                    if (item.MenuItems.Count > 0) item.IsSelected = false;
                Nav.SelectedItem = top;
            }
            return;
        }

        // 没命中顶层 → 往下一层找（「内置工具 → 随机抽号」这种子项）。
        // 找到子项时顺手把父项**展开** —— 否则高亮藏在折叠的组里，看着像点了没反应。
        foreach (var top in AllTopItems())
        {
            foreach (var child in top.MenuItems.OfType<NavigationViewItem>())
            {
                if ((child.Tag as string) != tag) continue;

                top.IsExpanded = true;
                Nav.SelectedItem = child;
                return;
            }
        }
    }

    /// <summary>导航里的全部项（含一层子项）—— 手动改高亮时用。</summary>
    private IEnumerable<NavigationViewItem> AllNavigationItems()
    {
        foreach (var top in AllTopItems())
        {
            yield return top;
            foreach (var child in top.MenuItems.OfType<NavigationViewItem>()) yield return child;
        }
    }

    /// <summary>导航栏最外层的那批项（上半区 + 下半区，不含子项）。</summary>
    private IEnumerable<NavigationViewItem> AllTopItems()
        => Nav.MenuItems.OfType<NavigationViewItem>()
              .Concat(Nav.FooterMenuItems.OfType<NavigationViewItem>());

    private void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem item) return;

        // ⚠️ 没有 Tag 的项不导航（防止 `?? "home"` 那种兜底把"点分组"变成"跳回首页"）。
        if (item.Tag is not string tag || tag.Length == 0) return;

        // ⚠️ 走 NavigateTo 而不是 NavigateTag：点顶层项时 NavigationView 本来也会自己改高亮，
        //    但**子项的高亮它靠不住**（「实验性功能 → 本机核实」这种嵌套项，选中态经常不跟着走）。
        //    显式 SelectTag 一遍，两条路径都吃到同一套高亮逻辑。
        NavigateTo(tag);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 标题栏全局搜索
    //
    // 匹配规则全在 Data/SearchIndex.cs，这里只管把结果喂给 AutoSuggestBox。
    // ⛔ 2026-10-16 Nick 看过自绘那一版（分组标题 + 自己接管键盘 + 自叠浮层），
    //    要求「用 WinUI 原生的就行，你不需要照抄网页的」—— 所以结果改走
    //    **AutoSuggestBox 自带的建议列表**：下拉定位、滚动、↑↓ 选择、Enter 提交、
    //    点外面自动收起，全部归系统管，这边一行键盘代码都不用写。
    //    代价是没有网页端那种"软件 / 内置工具 / 页面"的组标题，
    //    改由每行右侧那个类型小字顶上（见 SearchHit.Badge）。
    // ════════════════════════════════════════════════════════════════════════════

    private List<SearchHit> _hits = new();
    private string _lastOpenedKey = "";
    private DateTime _lastOpenedAt = DateTime.MinValue;

    private void InitSearch()
    {
        // ⛔ 关掉加速键的**自动提示**：WinUI 默认会在焦点/鼠标经过时把 "Ctrl+K" 做成一个小浮块
        //    弹出来（2026-10-24 Nick 反馈"时不时冒出来"）。PlacementMode.Hidden 只压住这个提示，
        //    加速键本身照常触发 —— 想告诉用户快捷键，靠输入框的 PlaceholderText 就够了。
        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        // Ctrl + K 聚焦搜索框（与站点同）。
        var accel = new KeyboardAccelerator { Key = VirtualKey.K, Modifiers = VirtualKeyModifiers.Control };
        accel.Invoked += (_, e) =>
        {
            e.Handled = true;
            FocusSearch();
        };
        RootGrid.KeyboardAccelerators.Add(accel);
    }

    /// <summary>Ctrl + K：聚焦输入框，并把候选摆出来。</summary>
    private void FocusSearch()
    {
        // AutoSuggestBox 自己不是焦点目标，得往里找那个 TextBox
        if (FindDescendant<TextBox>(SearchBox) is { } input) input.Focus(FocusState.Programmatic);
        else SearchBox.Focus(FocusState.Programmatic);

        RefreshResults();
    }

    /// <summary>
    /// 窗口变窄时收一收搜索框 —— 标题栏左边是图标与名称、右边是外观按钮和系统按钮，中间不能硬撑。
    /// </summary>
    private void TitleBarArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var w = e.NewSize.Width;
        SearchBox.Width = w >= 880 ? 360d : w >= 720 ? 250d : 170d;

        // 宽度改完要等布局跑完才算得出新矩形，排到下一轮空闲再算
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateTitleBarPassthrough);
    }

    /// <summary>
    /// 把搜索框那块从「标题栏拖动区」里挖出来，交还给客户区（2026-11-04 Nick 报）。
    ///
    /// <para>
    /// 症状：在搜索框里按住鼠标往左右拖，想选中已输入的文字，结果整个窗口跟着走。
    /// 原因：整条 48 高的标题栏都交给了 <c>SetTitleBar</c>，系统把它当非客户区 ——
    /// 在非客户区按下鼠标，拖动就是"移动窗口"，输入框根本收不到这次拖拽。
    /// </para>
    ///
    /// <para>
    /// 解法是 WinUI 自己的机制：<see cref="Microsoft.UI.Input.InputNonClientPointerSource"/> 的
    /// <c>Passthrough</c> 区域 —— 命中这些矩形时，指针直接透传给客户区，
    /// 拖动就回到输入框手里（选字），而标题栏其余部分照旧可以拖窗。⛔ 不要自己写 WM_NCHITTEST 钩子。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 矩形用物理像素、以客户区左上角为原点（不是 DIP），所以要乘 <c>RasterizationScale</c>。
    /// ⚠️ 只设一个区域，每次整组覆盖 —— 不追加，避免窗口缩放后留下旧矩形。
    /// </para>
    /// </summary>
    private void UpdateTitleBarPassthrough()
    {
        try
        {
            if (App.MainWindow is not { } main) return;
            if (XamlRoot is not { } root) return;
            if (SearchBox.ActualWidth <= 0 || SearchBox.ActualHeight <= 0) return;

            var bounds = SearchBox
                .TransformToVisual(null)
                .TransformBounds(new Windows.Foundation.Rect(
                    0, 0, SearchBox.ActualWidth, SearchBox.ActualHeight));

            var scale = root.RasterizationScale;
            var rect = new Windows.Graphics.RectInt32(
                (int)Math.Round(bounds.X * scale),
                (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale),
                (int)Math.Round(bounds.Height * scale));

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(main);
            var source = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));

            source.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, new[] { rect });
        }
        catch (Exception ex)
        {
            // 老系统 / API 不可用时退回原行为（搜索框能点、但按住拖动仍会拖窗），不影响正常使用
            AppLog.Warn("shell", "搜索框拖动区透传设置失败：" + ex.Message);
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // ⚠️ 选中建议项之后，系统会把那一项的文本回填进输入框 —— 那不是"有人在打字"，
        //    当成新一次搜索会让列表刚关又开。这一档必须放过。
        if (args.Reason == AutoSuggestionBoxTextChangeReason.SuggestionChosen) return;

        // 其余（用户输入、程序改文本、点内置的清除按钮）都重算一遍。
        // ⛔ 这里绝不去回写 SearchBox.Text，否则会和 TextChanged 互相咬住。
        RefreshResults();
    }

    /// <summary>按当前关键词重算候选，交给系统那个下拉列表。</summary>
    private void RefreshResults()
    {
        var query = SearchBox.Text ?? "";
        _hits = SearchIndex.Query(query).SelectMany(g => g.Hits).ToList();

        SearchBox.ItemsSource = _hits;
        SearchBox.IsSuggestionListOpen = _hits.Count > 0;
    }

    /// <summary>用户从下拉里挑了一项（鼠标点，或键盘选中后回车）。</summary>
    private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not SearchHit hit) return;
        sender.IsSuggestionListOpen = false;
        OpenHit(hit);
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // 下拉里选中后回车 → ChosenSuggestion 有值；直接在输入框里回车 → 打开第一条
        if (args.ChosenSuggestion is SearchHit hit) OpenHit(hit);
        else if (_hits.Count > 0) OpenHit(_hits[0]);
    }

    private void OpenHit(SearchHit hit)
    {
        // 同一次选择可能先来 SuggestionChosen、再来 QuerySubmitted —— 别连着跳两趟，
        // 否则返回栈里会平白多压一层（按一次返回退不出来）。
        var now = DateTime.UtcNow;
        if (hit.Key == _lastOpenedKey && (now - _lastOpenedAt).TotalMilliseconds < 600) return;
        _lastOpenedKey = hit.Key;
        _lastOpenedAt = now;

        switch (hit.Kind)
        {
            case SearchHitKind.App:
                NavigateToApp(hit.Key);
                break;

            case SearchHitKind.Doc:
                NavigateToDocArticle(hit.Key);
                break;

            default:
                NavigateTo(hit.Target);
                break;
        }
    }

    /// <summary>
    /// 进「文档库」里的一篇文章（列表点击、全局搜索、文章内 <c>csh://article/…</c> 链接共用）。
    /// 导航高亮停在「文档库」—— 正文页属于这一区，导航里不给每篇文章单独建项。
    /// </summary>
    public void NavigateToDocArticle(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        SelectTag("docs");
        ContentFrame.Navigate(typeof(DocArticlePage), id);
    }

    /// <summary>
    /// 从搜索结果进软件详情页 —— 与「软件下载」里点卡片落到同一页；
    /// 导航高亮跟着切到「软件下载」（人确实还在软件这一区里）。
    /// </summary>
    private void NavigateToApp(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        SelectTag("apps");
        ContentFrame.Navigate(typeof(DetailPage), id);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindDescendant<T>(child) is { } deep) return deep;
        }
        return null;
    }
}
