using System.Collections.Generic;

namespace ClassSoftwareHub.Desktop.Data;

/// <summary>
/// 站点文字（可选来源 <c>text/ui.json</c>）里桌面版要用到的文字（站点大标题等）。
///
/// ⚠️ **现实是这份文件当前根本不存在，所有取值都走硬编码兜底**（2026-10-05）：
///   安装包不再自带内容包之后，桌面端的内容只从站点仓库的「软件数据/」同步，
///   而那里**没有放 ui.json**（原先一起同步的 <c>text/mirror-sites.json</c> 是给已下线的
///   「系统镜像下载」用的，2026-10-09 起也不再需要）。
///   原因：桌面端真正用到的只有 4 个 key（app.title / detail.pending / detail.hash-copied /
///   about.qq-group-url），兜底值本来就写死在调用点，而"抄一份站点文案进仓库"会随站点改文字而
///   悄悄过期 —— 2026-10-04 正是因为这个（读到的 app.version 停在 v2.3.2）才把站点版本号改成编译期常量。
///
/// 所以：新增用字**直接在调用点写兜底值**，别指望这份 JSON 会有人喂。
/// 真要恢复"站点改文案桌面端跟着变"，得先在站点仓库放一份 ui.json 并想清楚怎么不跑偏。
/// </summary>
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
