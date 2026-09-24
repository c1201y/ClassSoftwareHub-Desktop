using System;
using System.Linq;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 原生外壳：顶部标题栏 + 左侧导航 + 页面容器。
/// 导航：上半区「首页 / 软件下载 / 内置工具」，下半区「提交软件 / 设置」。
/// ⚠️ 只有「提交软件」页会请求打开网页（WebView2 小窗口），其余页面全部 WinUI3 自绘。
/// </summary>
public sealed partial class ShellPage : UserControl
{
    /// <summary>给 MainWindow 用：SetTitleBar 需要这个元素（原生页面的拖动区）。</summary>
    public Grid TitleBarElement => TitleBarArea;

    public ShellPage()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => UpdateThemeButton();

        // 每次切页停稳之后收一次内存（页面本身不缓存，这里再把工作集还给系统）
        ContentFrame.Navigated += (_, _) =>
        {
            Services.MemoryTrimmer.TrimLater(3000);
            UpdateBackButton();
        };
    }

    /// <summary>NavigationView 自带的返回按钮：能退就亮，退到头就灰（原生尺寸和动画）。</summary>
    private void UpdateBackButton()
    {
        Nav.IsBackEnabled = ContentFrame.CanGoBack;
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
    public void NavigateTag(string tag)    {
        switch (tag)
        {
            case "submit":
                ContentFrame.Navigate(typeof(SubmitPage));
                return;
            case "settings":
                ContentFrame.Navigate(typeof(SettingsPage));
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

    private void SelectTag(string tag)
    {
        var item = Nav.MenuItems.OfType<NavigationViewItem>()
            .Concat(Nav.FooterMenuItems.OfType<NavigationViewItem>())
            .FirstOrDefault(i => (i.Tag as string) == tag);
        if (item is not null) Nav.SelectedItem = item;
    }

    private void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        var tag = (args.InvokedItemContainer as NavigationViewItem)?.Tag as string ?? "home";
        NavigateTag(tag);
    }
}
