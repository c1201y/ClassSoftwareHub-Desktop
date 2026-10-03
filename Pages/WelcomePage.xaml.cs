using System;
using System.Collections.Generic;
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
        Loaded += (_, _) => { Populate(); RefreshUpdateReadyBar(); };
    }

    // ── 更新待装横幅 ────────────────────────────────────────────────

    /// <summary>
    /// 后台下载完成 → 存档里有待装标记 → 顶部 InfoBar 提醒（用户选了「稍后安装」或没点通知都算）。
    /// 装上新版本再进来时，待装 tag 与当前版本一致 → 自检清档；安装包被清理了也顺手清，不摆死横幅。
    /// </summary>
    private void RefreshUpdateReadyBar()
    {
        var s = App.Settings.Current;
        var path = s.UpdatePendingPath ?? "";

        if (path.Length == 0 || !System.IO.File.Exists(path))
        {
            // 标记悬空（包被清了 / 档是手抄的）：清掉别让它永久挂着
            if (path.Length > 0)
            {
                s.UpdatePendingPath = "";
                s.UpdatePendingTag = "";
                App.Settings.Save();
            }
            UpdateReadyBar.IsOpen = false;
            return;
        }

        // 待装 tag == 当前版本（VersionPrefix + ShellVersion）= 已经装上了，待装周期结束
        if (s.UpdatePendingTag == ShellConfig.VersionPrefix + ShellConfig.ShellVersion)
        {
            s.UpdatePendingPath = "";
            s.UpdatePendingTag = "";
            App.Settings.Save();
            UpdateReadyBar.IsOpen = false;
            return;
        }

        UpdateReadyBar.Title = $"{(s.UpdatePendingTag.Length > 0 ? s.UpdatePendingTag : "新版本")} 已下载就绪";
        UpdateReadyBar.IsOpen = true;
    }

    private async void InstallPending_Click(object sender, RoutedEventArgs e)
    {
        // 防连点：安装会退出应用，多点只会并发起安装器
        if (sender is Button b) b.IsEnabled = false;
        await Services.Updating.UpdateFlow.InstallPendingNowAsync();
    }

    private async void Populate()
    {
        var ui = App.Content.Ui;

        var title = ui.AppTitle.Length > 0 ? ui.AppTitle : "电教委员常用软件下载站";
        HomeTitleText.Text = title + " • 桌面版";
        // 只显示软件自己的版本（前缀 VersionPrefix + ShellVersion，如 dv1.1.0-insider1.0 或正式版 dv1.1.0）。
        // ⚠️ 别再往这里挂"网站版本"——软件是独立发布物，不摆成网站版本的附属品（2026-09-26 删）。
        ShellVersionText.Text = ShellConfig.VersionPrefix + ShellConfig.ShellVersion;

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
        }
    }
}
