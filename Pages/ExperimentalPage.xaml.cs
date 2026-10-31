using System.Collections.Generic;
using System.Linq;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 实验性功能总览页：把「还不成熟、先放着试」的功能列一遍。
/// 版式跟内置工具页一致（内容列 960、两列卡片、页脚说明卡），Nick 2026-09-26 要求。
///
/// <para>
/// 卡片与左侧导航子项**共用 <see cref="ExperimentalFeatureCatalog.All"/> 一份清单**
/// （2026-11-01 收编）。加一项只改那张表 + <c>NavigateTagCore</c> 的 case。
/// ⛔ 别在本页再写一份列表：以前两边各写一份，转正的功能从导航删了、这里还留着三张旧卡。
/// </para>
/// </summary>
public sealed partial class ExperimentalPage : Page
{
    public ExperimentalPage()
    {
        InitializeComponent();

        // 两列板式（见 .xaml：照搬首页「快速开始」）：偶数序号进左列、奇数进右列。
        // ⚠️ 顺序仍是「从上往下读」，跟一个 GridView 两列排是一样的效果。
        var all = ExperimentalFeatureCatalog.All;
        CardColumnLeft.ItemsSource = all.Where((_, i) => i % 2 == 0).ToList();
        CardColumnRight.ItemsSource = all.Where((_, i) => i % 2 == 1).ToList();
    }

    // 卡片整张可点（首页同款：容器 chrome 关掉、反馈由卡片自己给，见 App.xaml 的 CshNavCardStyle）
    private void Card_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // 走导航栏那一套（不是 Frame.Navigate）：左侧「实验性功能」才会跟着高亮
        if ((sender as FrameworkElement)?.DataContext is ExperimentalFeature f && f.Tag.Length > 0)
            App.MainWindow?.Shell.NavigateTo(f.Tag);
    }

    // 卡片自己的悬停反馈（容器 chrome 已关掉，见 App.xaml 里的 CshCardItemStyle）
    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Opacity = 0.88;
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Opacity = 1.0;
    }
}
