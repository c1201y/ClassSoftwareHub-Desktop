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
        new ExperimentalFeature
        {
            Id = "easiguard", Name = "白板专杀",
            Desc = "到达设定时间点后，结束希沃白板5 后台滞留的残留进程",
            Glyph = "\uEA99", Tag = "easiguard"
        },
        new ExperimentalFeature
        {
            Id = "procguard", Name = "程序专杀",
            Desc = "为指定程序单独设定结束时间点，到点结束其后驻留进程",
            Glyph = "\uE7E8", Tag = "procguard"
        },
        new ExperimentalFeature
        {
            Id = "uninstaller", Name = "应用卸载",
            Desc = "扫描本机已安装的软件，可直接卸载其中的任意一款",
            Glyph = "\uE74D", Tag = "uninstaller"
        },
        new ExperimentalFeature
        {
            Id = "appdata", Name = "AppData 管理",
            Desc = "先看清是哪些文件夹在占地方，再把能安全清理的缓存挑出来",
            Glyph = "\uE8B7", Tag = "appdata"
        },
        // ⚠️ 原来是四项，第三项是「虚拟键盘」—— 该功能 2026-10-09 整体下线，卡片一并删除
        //    （导航分组里的子项、NavigateTagCore 的 case 也同步删了）。
        // ⚠️「自动关机」2026-10-12 转正搬去「内置工具」（Nick 定）——
        //    入口改由 Data/ToolDef.cs 的 ToolCatalog 提供，这里连同导航子项、case 一起删掉。
        // 日志查看已不算实验功能（Nick 2026-10-02）：入口挪到 设置 → 诊断。
    };

    public ExperimentalPage()
    {
        InitializeComponent();

        // 两列板式（见 .xaml：照搬首页「快速开始」）：偶数序号进左列、奇数进右列。
        // ⚠️ 顺序仍是「从上往下读」，跟一个 GridView 两列排是一样的效果。
        CardColumnLeft.ItemsSource = All.Where((_, i) => i % 2 == 0).ToList();
        CardColumnRight.ItemsSource = All.Where((_, i) => i % 2 == 1).ToList();
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
