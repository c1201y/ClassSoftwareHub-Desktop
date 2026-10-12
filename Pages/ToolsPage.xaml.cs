using System;
using System.Collections.Generic;
using System.Linq;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>内置工具索引页：卡片清单来自 <see cref="ToolCatalog.All"/>（与导航子项同一份）。</summary>
public sealed partial class ToolsPage : Page
{
    public ToolsPage()
    {
        InitializeComponent();

        // 两列板式（见 .xaml：照搬首页「快速开始」）：偶数序号进左列、奇数进右列。
        CardColumnLeft.ItemsSource = ToolCatalog.All.Where((_, i) => i % 2 == 0).ToList();
        CardColumnRight.ItemsSource = ToolCatalog.All.Where((_, i) => i % 2 == 1).ToList();

        // 这里的开关（「点关闭时收进托盘」和设置页里是同一个值）；侧边栏/置顶那些挪到「设置 → 常用工具」了
        _loading = true;
        TraySwitch.IsOn = App.Settings.Current.CloseToTray;
        _loading = false;
    }

    private bool _loading;

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
