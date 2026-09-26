using System.Collections.Generic;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 实验性功能总览页：把「还不成熟、先放着试」的功能列一遍。
/// 版式跟内置工具页一致（内容列 960、两列卡片、页脚说明卡），Nick 2026-09-26 要求。
///
/// ⚠️ 往这儿加功能要动三处：① 下面 <see cref="All"/> 加一条；② <c>ShellPage.xaml</c> 的
///    「实验性功能」分组里加一个子项（带同一个 tag）；③ <c>ShellPage.NavigateTagCore</c> 加一个 case。
///    少一处就会出现「卡片点不动」或「点进去没高亮」。
/// </summary>
public sealed partial class ExperimentalPage : Page
{
    private static readonly List<ExperimentalFeature> All = new()
    {
        new ExperimentalFeature
        {
            Id = "machinecheck", Name = "本机核实",
            Desc = "对照 Windows 的已安装程序记录，看清单里的软件本机装没装",
            Glyph = "\uE7F4", Tag = "machinecheck"
        },
    };

    public ExperimentalPage()
    {
        InitializeComponent();
        FeatureGrid.ItemsSource = All;
    }

    private void FeatureGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        // 走导航栏那一套（不是 Frame.Navigate）：左侧「实验性功能」才会跟着高亮
        if (e.ClickedItem is ExperimentalFeature f && f.Tag.Length > 0)
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
