using System.Collections.Generic;

namespace ClassSoftwareHub.Desktop.Data;

/// <summary>内容包 text/ui.json 里桌面版要用到的文字（标题 / 版本号 / 详情页标签等）。</summary>
public sealed class UiText
{
    /// <summary>整份 text/ui.json 原样存一份，界面按 key 取（取不到就用硬编码兜底）。</summary>
    public Dictionary<string, string> All { get; } = new();

    /// <summary>按 key 取站点文字，取不到（或为空）就用 fallback。</summary>
    public string T(string key, string fallback) =>
        All.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    /// <summary>站点大标题（app.title，例如「电教委员常用软件下载站」）。</summary>
    public string AppTitle { get; set; } = "";

    /// <summary>站点首页大标题（home.title，例如「ClassSoftwareHub」）。</summary>
    public string HomeTitle { get; set; } = "";

    /// <summary>站点首页副标题（home.subtitle）。</summary>
    public string HomeSubtitle { get; set; } = "";

    /// <summary>站点版本号全文（app.version，例如「v2.3.2 - Tangram (20260919PR01)」）。</summary>
    public string AppVersion { get; set; } = "";

    /// <summary>取短版本号：只留开头的 vX.Y.Z，给首页图片旁边用。</summary>
    public string ShortVersion
    {
        get
        {
            var v = AppVersion.Trim();
            if (v.Length == 0) return Core.ShellConfig.SiteVersionTarget;
            var end = v.IndexOfAny(new[] { ' ', '-', '(' });
            return end > 0 ? v.Substring(0, end).Trim() : v;
        }
    }
}
