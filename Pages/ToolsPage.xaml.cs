using System;
using System.Collections.Generic;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Pages.Tools;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>内置工具索引页：只放已经在桌面版做完的工具（其余先不上）。</summary>
public sealed partial class ToolsPage : Page
{
    private static readonly List<ToolDef> All = new()
    {
        new ToolDef
        {
            Id = "image-color", Name = "图片取色",
            Desc = "丢一张图片进来，自动提取一组柔和的配色（种子色 + 7 档明暗变体），点色块即可复制。",
            Glyph = "\uE790", Page = typeof(ImageColorToolPage)
        },
        new ToolDef
        {
            Id = "pick-number", Name = "随机抽号",
            Desc = "输入号码范围（比如学号 1~50）就能抽号，支持一次抽多个、抽过不重复。",
            Glyph = "\uE716", Page = typeof(PickNumberToolPage)
        },
        new ToolDef
        {
            Id = "timer", Name = "课堂计时器",
            Desc = "倒计时 / 秒表，大字号方便投影，到点响铃。",
            Glyph = "\uE916", Page = typeof(TimerToolPage)
        },
        new ToolDef
        {
            Id = "clock", Name = "全屏时钟",
            Desc = "把屏幕变成一面大钟：可放背景图、调蒙版，全屏显示看时间。",
            Glyph = "\uE740", Page = typeof(ClockToolPage)
        },
        new ToolDef
        {
            Id = "encoding", Name = "编码 / 哈希工具",
            Desc = "Base64、URL 编解码，以及 MD5 / SHA-1 / SHA-256 / SHA-512 哈希。",
            Glyph = "\uE943", Page = typeof(EncodingToolPage)
        },
        new ToolDef
        {
            Id = "mirror-download", Name = "系统镜像下载",
            Desc = "Windows 等系统镜像的官方 / 可信第三方入口，点一行直接跳转（本站不存镜像）。",
            Glyph = "\uE896", Page = typeof(MirrorToolPage)
        },
    };

    public ToolsPage()
    {
        InitializeComponent();
        ToolGrid.ItemsSource = All;
    }

    private void ToolGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ToolDef def && def.Page is not null)
            Frame.Navigate(def.Page);
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
