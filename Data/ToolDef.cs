using System;
using System.Collections.Generic;
using ClassSoftwareHub.Desktop.Pages.Tools;

namespace ClassSoftwareHub.Desktop.Data;

/// <summary>内置工具卡片定义（工具索引页用）。</summary>
public sealed class ToolDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Desc { get; init; } = "";
    /// <summary>SEGOEICONS 字形码。</summary>
    public string Glyph { get; init; } = "";
    /// <summary>点开要导航到的页面。</summary>
    public Type? Page { get; init; }

    /// <summary>无障碍 / 调试用：列表项名字就是工具名。</summary>
    public override string ToString() => Name;
}

/// <summary>一个系统镜像下载入口（对应内容包 text/mirror-sites.json）。</summary>
public sealed class MirrorSite
{
    public string Name { get; set; } = "";
    public string Desc { get; set; } = "";
    public string Url { get; set; } = "";
    public string Color { get; set; } = "";
    public string Icon { get; set; } = "";

    /// <summary>显示域名（去掉 https:// 与路径）。</summary>
    public string Host
    {
        get
        {
            try { return new Uri(Url).Host; }
            catch { return ""; }
        }
    }

    /// <summary>没有图标时兜底：名字首字。</summary>
    public string Badge => Name.Length > 0 ? Name.Substring(0, 1).ToUpperInvariant() : "?";

    /// <summary>站点有没有自带图标（内容包里的 icon 字段）。</summary>
    public Microsoft.UI.Xaml.Visibility IconVisibility
        => Icon.Length > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility BadgeVisibility
        => Icon.Length > 0 ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>首字方块底色（站点色的淡染）；站点没写 color 就用主题蓝。</summary>
    public Microsoft.UI.Xaml.Media.Brush BadgeBackground => Tint(0.12);

    public Microsoft.UI.Xaml.Media.Brush BadgeBorder => Tint(0.40);

    private Microsoft.UI.Xaml.Media.Brush Tint(double alpha)
    {
        var hex = Color.TrimStart('#');
        if (hex.Length != 6)
        {
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(0, 0, 0, 0));
        }
        try
        {
            var r = Convert.ToByte(hex.Substring(0, 2), 16);
            var g = Convert.ToByte(hex.Substring(2, 2), 16);
            var b = Convert.ToByte(hex.Substring(4, 2), 16);
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb((byte)(Math.Clamp(alpha, 0, 1) * 255), r, g, b));
        }
        catch
        {
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(0, 0, 0, 0));
        }
    }
}

/// <summary>系统镜像下载页的整页文字 + 站点清单。</summary>
public sealed class MirrorInfo
{
    public string Title { get; set; } = "系统镜像下载";
    public string Subtitle { get; set; } = "";
    public string Disclaimer { get; set; } = "";
    public string Note { get; set; } = "";
    public List<MirrorSite> Sites { get; } = new();
}

/// <summary>
/// 内置工具清单 —— 导航栏「内置工具」分组的子项、工具索引页的卡片，共用这一份。
/// <para>⚠️ 往这儿加工具：① 下面 <see cref="All"/> 加一条（Id/Name/Desc/Glyph/Page）；
/// ② 导航子项是 <c>ShellPage</c> 按 <see cref="ToolDef.Id"/> 动态生成的，<c>NavigateTagCore</c>
///    也是按 Id 反查 <see cref="All"/>，所以**不用再改导航**；③ 若首页卡片 / 浮窗有直达入口，另加。</para>
/// </summary>
public static class ToolCatalog
{
    public static readonly List<ToolDef> All = new()
    {
        new ToolDef
        {
            Id = "image-color", Name = "图片取色",
            Desc = "导入图片后自动提取一组柔和的配色（种子色 + 7 档明暗变体），单击色块即可复制。",
            Glyph = "\uE790", Page = typeof(ImageColorToolPage)
        },
        new ToolDef
        {
            Id = "pick-number", Name = "随机抽号",
            Desc = "按学号范围或班级名单随机抽取，可一次抽多个、抽过不重复，也能一键随机分组。",
            Glyph = "\uE716", Page = typeof(PickNumberToolPage)
        },
        new ToolDef
        {
            Id = "timer", Name = "课堂计时器",
            Desc = "倒计时 / 秒表，大字号便于投影，到达设定时间响铃。",
            Glyph = "\uE916", Page = typeof(TimerToolPage)
        },
        new ToolDef
        {
            Id = "clock", Name = "全屏时钟",
            Desc = "将屏幕变为大型时钟：可设置背景图与蒙版，全屏显示查看时间。",
            Glyph = "\uE740", Page = typeof(ClockToolPage)
        },
        new ToolDef
        {
            Id = "encoding", Name = "编码 / 哈希工具",
            Desc = "校验文件：拖入即得 MD5 / SHA-1 / SHA-256 / SHA-512，可粘贴官方值自动核对；也可做 Base64、URL 编解码。",
            Glyph = "\uE943", Page = typeof(EncodingToolPage)
        },
        new ToolDef
        {
            Id = "mirror-download", Name = "系统镜像下载",
            Desc = "Windows 等系统镜像的官方 / 可信第三方入口，单击任一行即跳转（本站不存储镜像）。",
            Glyph = "\uE896", Page = typeof(MirrorToolPage)
        },
    };
}
