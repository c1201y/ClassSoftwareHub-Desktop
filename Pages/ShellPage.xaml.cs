using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 原生外壳：顶部标题栏 + 左侧导航 + 页面容器。
/// 导航：上半区「首页 / 软件下载 / 内置工具 / 侧边布局 / 实验性功能」，
/// 下半区「任务进行 / 提交软件 / 反馈中心 / 更新日志 / 设置」。
/// ⚠️「实验性功能」是分组父项，点它=进总览页，子项「本机核实」可直达（见 ShellPage.xaml 的注释）。
/// ⚠️ 所有页面（含「提交软件」）都是 WinUI3 自绘；只有详情页那种「在应用内看一眼网页」的浮层（MainWindow.WebSheet）会用到 WebView2。
/// </summary>
public sealed partial class ShellPage : UserControl
{
    /// <summary>给 MainWindow 用：SetTitleBar 需要这个元素（原生页面的拖动区）。</summary>
    public Grid TitleBarElement => TitleBarArea;

    public ShellPage()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => UpdateThemeButton();

        // 下载任务数变了 → 同步「任务进行」上的徽标（在下载就不占着界面，但得让人随时看见有几个在下）
        Services.DownloadManager.Current.Changed += UpdateDownloadBadge;
        UpdateDownloadBadge();

        // 面板显示模式变了（宽 ↔ 窄）→ 重新算内容区要不要给导航按钮让位
        Nav.DisplayModeChanged += (_, _) => ApplyPaneInset();
        ApplyPaneInset();

        // 每次切页停稳之后收一次内存（页面本身不缓存，这里再把工作集还给系统）
        ContentFrame.Navigated += (_, _) =>
        {
            Services.MemoryTrimmer.TrimLater(3000);
            UpdateBackButton();
        };
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

    /// <summary>标题栏那个太阳/月亮按钮：图标显示"点了会切到的那一边"。</summary>
    public void UpdateThemeButton()
    {
        var dark = ActualTheme == ElementTheme.Dark;
        ThemeIcon.Glyph = dark ? "\uE706" : "\uE708";   // 深色时显示太阳（点了变浅色），反之显示月亮
        ToolTipService.SetToolTip(ThemeButton, dark ? "切换到浅色模式" : "切换到深色模式");
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        var next = ActualTheme == ElementTheme.Dark ? "light" : "dark";
        App.Settings.Current.Theme = next;
        App.MainWindow?.SetTheme(next);
        UpdateThemeButton();
    }

    /// <summary>标题栏小图标（MainWindow 把站点图标塞进来）。</summary>
    public void SetIcon(Microsoft.UI.Xaml.Media.ImageSource? source) => TitleIcon.Source = source;

    /// <summary>数据加载完之后调用一次：显示数据来源、进首页。</summary>
    public void Init()
    {
        TitleText.Text = ShellConfig.AppName;

        SelectTag("home");
        NavigateTag("home");
        UpdateThemeButton();
    }

    /// <summary>外部（首页卡片等）跳转导航用：先高亮导航项，再切页面。</summary>
    public void NavigateTo(string tag)
    {
        SelectTag(tag);
        NavigateTag(tag);
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
    /// 跳到「内置工具」里的某个工具页（浮窗里的「详细设置」用）。
    /// 先把工具列表页铺一层，这样工具页左上角的返回按钮能正常退回列表。
    /// </summary>
    public void NavigateToTool(Type pageType)
    {
        SelectTag("tools");
        if (ContentFrame.CurrentSourcePageType != typeof(ToolsPage))
            ContentFrame.Navigate(typeof(ToolsPage));
        ContentFrame.Navigate(pageType);
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
        switch (tag)
        {
            case "submit":
                ContentFrame.Navigate(typeof(SubmitPage));
                return;
            case "feedback":
                ContentFrame.Navigate(typeof(FeedbackPage));
                return;
            case "downloads":
                ContentFrame.Navigate(typeof(DownloadsPage));
                return;
            case "machinecheck":
                ContentFrame.Navigate(typeof(MachineCheckPage));
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
            case "tools":
                ContentFrame.Navigate(typeof(ToolsPage));
                return;
            case "apps":
                ContentFrame.Navigate(typeof(SoftwarePage), "");
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
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassSoftwareHub");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 导航到「{tag}」失败: {ex}{Environment.NewLine}");
        }
        catch
        {
            // 日志写不进去就算了，别在这里再抛
        }
    }

    private void SelectTag(string tag)
    {
        // 「实验性功能」是个分组父项，它**不进 NavigationView 的选中体系**（原因见 ShellPage.xaml 的注释）——
        // 高亮得手动打：先清掉别的选中项，再点亮父项自己。
        // ⚠️ 这里**不碰 IsExpanded**：点父项时 NavigationView 自己会 toggle 展开/收起，
        //    在这儿硬展开的话，分组就再也收不起来了（每次点都被撑开）。
        //    需要"顺带展开"的入口（首页卡片那种）走 NavigateToExperimental。
        if (tag == "experimental")
        {
            SelectExperimentalHighlight();
            return;
        }

        ExperimentalNav.IsSelected = false;      // 走别的页时，手动把父项的高亮熄掉

        foreach (var top in AllTopItems())
        {
            if ((top.Tag as string) == tag)
            {
                Nav.SelectedItem = top;
                return;
            }

            // 往下一层找（「实验性功能 → 本机核实」这种子项）。
            // 找到子项时顺手把父项**展开** —— 否则高亮藏在折叠的组里，看着像点了没反应。
            foreach (var child in top.MenuItems.OfType<NavigationViewItem>())
            {
                if ((child.Tag as string) != tag) continue;

                top.IsExpanded = true;
                Nav.SelectedItem = child;
                return;
            }
        }
    }

    /// <summary>
    /// 把导航高亮打在「实验性功能」父项上。
    /// ⚠️ 父项**不能塞进 Nav.SelectedItem**：NavigationView 拿到一个"带子项的分组头"时会把选中
    ///    转给第一个子项 —— 这正是当年"点实验性功能却跳到本机核实"的根因。所以只能手动点亮。
    /// ⚠️ 也**不能只设 SelectedItem = null**：那样上一个选中的项还会留着选中底色
    ///    （实测：进总览页后「首页」那块底色还在），得挨个熄掉。
    /// </summary>
    private void SelectExperimentalHighlight()
    {
        Nav.SelectedItem = null;
        foreach (var item in AllNavigationItems()) item.IsSelected = false;
        ExperimentalNav.IsSelected = true;
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
}
