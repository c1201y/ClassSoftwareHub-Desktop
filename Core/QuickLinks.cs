using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>首页 / 设置页那一行快捷入口的数据。</summary>
public sealed class QuickLink : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Glyph { get; set; } = "";

    /// <summary>点了用系统浏览器打开的外部链接。空 = 不是外链（看 <see cref="Tag"/>）。</summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// 点了在**应用内**跳转的导航 tag（如 <c>changelog</c>）。
    /// ⚠️ 只要这个非空就走应用内导航，<see cref="Url"/> 会被忽略 —— 别再写成"顺手也留个网址"，
    ///    那会让"更新日志"又跳回网站（Nick 2026-09-26 明确要求改成应用内）。
    /// </summary>
    public string Tag { get; set; } = "";

    /// <summary>SVG 图标资源文件名（如 <c>github.svg</c>）。非空时模板改用这张图、隐藏 FontIcon。</summary>
    public string? IconSvg { get; set; }

    /// <summary>按当前主题色染好的 SVG 图标源（由页面在 Build 后填充，主题切换时刷新）。</summary>
    public ImageSource? IconSource
    {
        get => _iconSource;
        set { _iconSource = value; OnChanged(nameof(IconSource)); }
    }
    private ImageSource? _iconSource;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>快捷入口清单（两处页面共用，改一处两边都变）。</summary>
public static class QuickLinks
{
    public static List<QuickLink> Build(Data.UiText ui)
    {
        // 「项目仓库」= 桌面版自己的仓库（跟更新通道同一个仓库）
        var repoUrl = $"https://github.com/{ShellConfig.UpdateRepoOwner}/{ShellConfig.UpdateRepoName}";
        if (ShellConfig.UpdateRepoOwner.Length == 0 || ShellConfig.UpdateRepoName.Length == 0)
            repoUrl = ui.T("about.repository-url", "https://github.com/c1201y/ClassSoftwareHub-Desktop");

        return new List<QuickLink>
        {
            new() { Name = "项目仓库", Glyph = "\uE943", IconSvg = "GITHUB.svg", Url = repoUrl },
            new()
            {
                Name = "作者主页", Glyph = "\uE77B", IconSvg = "主页.svg",
                Url = ui.T("about.author-home-url", "https://space.bilibili.com/1274920807"),
            },
            // ⚠️ 这颗原来带 Accent = true（走"主题色底 + 反白字"那套模板单独突出），
            //    2026-10-12 Nick 嫌那个底色越看越丑 ⇒ 去掉。`Accent` 属性与 QuickLinkSelector 一并删除，
            //    首页与设置页的五颗现在共用同一套模板。
            new()
            {
                Name = "赞助作者", Glyph = "\uEB51",
                Url = ui.T("about.reward-url", "https://ifdian.net/a/TinyNickCSHub"),
            },
            new()
            {
                Name = "加入Q群", Glyph = "\uE8BD", IconSvg = "QQ.svg",
                Url = ui.T("about.qq-group-url", "https://qm.qq.com/q/wByO7XG8Wk"),
            },
            // ⚠️ 更新日志走**应用内**页面（tag changelog），不是仓库的 releases 网页 ——
            //    Nick 2026-09-26 明确要求"不在导航到网站"。所以这里只给 Tag、不给 Url。
            new() { Name = "更新日志", Glyph = "\uE72C", Tag = "changelog" },
        };
    }
}
