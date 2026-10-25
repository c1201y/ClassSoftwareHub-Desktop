using System;
using System.Collections.Generic;
using System.Linq;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>内置工具索引页：卡片清单来自 <see cref="ToolCatalog.All"/>（与导航子项同一份）。</summary>
public sealed partial class ToolsPage : Page
{
    public ToolsPage()
    {
        InitializeComponent();

        // 卡片列表改到 OnNavigatedTo 里填 —— 需要先知道当前是哪一组（2026-10-22）。

        // 这里的开关（「点关闭时收进托盘」和设置页里是同一个值）；侧边栏/置顶那些挪到「设置 → 常用工具」了
        _loading = true;
        TraySwitch.IsOn = App.Settings.Current.CloseToTray;
        _loading = false;
    }

    private bool _loading;

    /// <summary>
    /// 按导航传进来的参数决定显示哪一组（<c>classroom</c> / <c>system</c>）。
    /// ⚠️ 两个导航父项共用这一个页面：板式、卡片、交互全部一样，只是数据集与标题不同。
    /// 不拆两个页面是为了避免一份板式两处维护。
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var group = string.Equals(e.Parameter as string, "system", StringComparison.OrdinalIgnoreCase)
            ? ToolGroup.System
            : ToolGroup.Classroom;

        var items = ToolCatalog.All.Where(t => t.Group == group).ToList();

        // 两列板式（见 .xaml：照搬首页「快速开始」）：偶数序号进左列、奇数进右列。
        CardColumnLeft.ItemsSource = items.Where((_, i) => i % 2 == 0).ToList();
        CardColumnRight.ItemsSource = items.Where((_, i) => i % 2 == 1).ToList();

        TitleText.Text = group == ToolGroup.System ? "系统工具" : "课堂工具";

        // 「常用工具窗口」那一块讲的是抽号/计时/时钟，只属于课堂组
        // ⚠️ 必须整张 Border（PaletteCard）一起藏，不能只藏 PaletteTitle 的父级那个 StackPanel ——
        //    「打开窗口」按钮和「关闭时收进系统托盘」开关都在 StackPanel 外面，
        //    只藏里面文字会剩下一张空壳卡片（2026-10-24 Nick 截图指出的就是这个）。
        PaletteCard.Visibility = group == ToolGroup.System ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenPalette_Click(object sender, RoutedEventArgs e)
        => Views.ToolPaletteWindow.ShowTool();

    private void TraySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.MainWindow?.SetCloseToTray(TraySwitch.IsOn);
    }

    // 卡片整张可点（首页同款：容器 chrome 关掉、反馈由卡片自己给，见 App.xaml 的 CshNavCardStyle）
    private void Card_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        // 走导航栏那一套（不是 Frame.Navigate）：左侧「内置工具」对应子项才会跟着高亮
        if ((sender as FrameworkElement)?.DataContext is ToolDef def)
            App.MainWindow?.Shell.NavigateTo(def.Id);
    }

    // 卡片自己的悬停反馈（容器 chrome 已关掉，见 App.xaml 里的 CshCardItemStyle）
    private void Card_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Border b) b.Opacity = 0.88;
    }

    private void Card_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Border b) b.Opacity = 1.0;
    }
}
