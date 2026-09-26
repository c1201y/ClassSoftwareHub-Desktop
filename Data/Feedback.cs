using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ClassSoftwareHub.Desktop.Data;

/// <summary>
/// 反馈中心的纯逻辑（界面在 <c>Pages/FeedbackPage.*</c>）—— 照搬站点
/// <c>FeedbackPage.vue</c> + <c>feedback.ts</c> 那一套做法。
///
/// 桌面版和站点一样**没有后端**，所以「提交」= 拼一个 **GitHub Issue 预填链接**
/// 交给系统浏览器打开，用户点一下 GitHub 上的「Submit new issue」即可。
/// 不走任何网络请求：既不用白等超时，也不受网络环境影响。
/// 没有 GitHub 账号的用户走「复制反馈内容」（拿去 Q 群）。
///
/// ⚠️ 目标仓库是**桌面版自己的仓库**（<see cref="Core.ShellConfig.UpdateRepoName"/>），
///    不是站点仓库 —— Nick 2026-09-26 定：桌面版的问题（侧边栏、音量、下载……）
///    应该进桌面版自己的 Issue 列表，跟发版仓库同一个，维护时不用两头翻。
///
/// ⚠️ <see cref="KindDef.Label"/> / <see cref="SubKindDef.Label"/> 必须与仓库 Labels
///    里的名字**一字不差**（含空格）。对不上的标签会被 GitHub **静默忽略**——
///    不打上也不报错，很难发现。改标签名时两边一起改。
/// </summary>
public static class Feedback
{
    // ════════════════════════════════════════════════════════════════
    // 目标仓库
    // ════════════════════════════════════════════════════════════════

    public static string RepoUrl =>
        $"https://github.com/{Core.ShellConfig.UpdateRepoOwner}/{Core.ShellConfig.UpdateRepoName}";

    /// <summary>公开议题列表（先查重再决定要不要提）。</summary>
    public static string IssueListUrl => RepoUrl + "/issues";

    // ════════════════════════════════════════════════════════════════
    // 分类（两层）
    //   一层 kind    ：report（报告问题） / suggestion（提出建议）
    //   二层 subKind ：只有 report 才有
    // 加/删类型只动下面两个数组 + 页面上的图标口径。
    // ════════════════════════════════════════════════════════════════

    public const string KindReport = "report";
    public const string KindSuggestion = "suggestion";

    public sealed class KindDef
    {
        public string Key { get; init; } = "";
        public string Title { get; init; } = "";
        public string Desc { get; init; } = "";
        /// <summary>卡片上的字形（Segoe Fluent Icons 码位，不是图片）。</summary>
        public string Glyph { get; init; } = "";
        /// <summary>GitHub 标签名（必须与仓库一字不差）。</summary>
        public string Label { get; init; } = "";
        /// <summary>标题前缀，让维护者一眼看出是哪一类（标签万一没了也还能分辨）。</summary>
        public string Tag { get; init; } = "";
    }

    public sealed class SubKindDef
    {
        public string Key { get; init; } = "";
        public string Title { get; init; } = "";
        public string Label { get; init; } = "";
        public string Tag { get; init; } = "";
    }

    public static readonly IReadOnlyList<KindDef> Kinds = new List<KindDef>
    {
        new()
        {
            Key = KindReport, Title = "报告问题",
            // 文案跟网页版「文字设置.ts」的 feedback.kind-*-desc 对齐（2026-09-26）
            Desc = "软件信息有误、下载链接失效，或页面无法正常使用。",
            Glyph = "\uE7BA",                       // Warning（三角感叹号）—— 卡片现在用图片，这个留着兜底
            Label = "报告问题", Tag = "报告问题",
        },
        new()
        {
            Key = KindSuggestion, Title = "提出建议",
            Desc = "希望新增的功能，或对现有功能的改进建议。",
            // 星形（不是灯泡）：灯泡 EA80 已经被「实验性功能」占了，同一页面上重复会串味；
            // 星形在"想要这个功能"的语境里也是通用隐喻，且与警告三角一眼分得开。
            Glyph = "\uE734",
            Label = "提出建议", Tag = "提出建议",
        },
    };

    /// <summary>「报告问题」下面的子类型；建议分支没有子类型（列表为空）。</summary>
    public static readonly IReadOnlyList<SubKindDef> ReportSubKinds = new List<SubKindDef>
    {
        new() { Key = "interaction", Title = "逻辑交互问题", Label = "逻辑交互", Tag = "逻辑交互" },
        new() { Key = "link",        Title = "链接失效问题", Label = "链接失效", Tag = "链接失效" },
        new() { Key = "other",       Title = "其他分类问题", Label = "其他问题", Tag = "其他问题" },
    };

    /// <summary>所有反馈共用的总标签，用来把用户反馈和仓库里其它 Issue 分开。</summary>
    public const string CommonLabel = "用户反馈";

    public static KindDef? FindKind(string key) =>
        Kinds.FirstOrDefault(k => k.Key == key);

    public static SubKindDef? FindSubKind(string key) =>
        ReportSubKinds.FirstOrDefault(s => s.Key == key);

    // ════════════════════════════════════════════════════════════════
    // 长度上限
    // ════════════════════════════════════════════════════════════════

    /// <summary>标题软上限：超过就拦下来让用户自己压（标题太长 Issue 列表里会很难看）。</summary>
    public const int TitleMax = 80;

    /// <summary>描述软上限（超过会在拼 URL 时被截断）。</summary>
    public const int DetailMax = 1800;

    /// <summary>
    /// 整个预填 URL 的长度上限（保守值）。
    /// ⚠️ 卡这个不是因为浏览器装不下，而是中间链路（代理、聊天软件转发、手工复制粘贴）
    ///    会在某个长度上开始截断 —— 一旦截断，Issue 正文就是半句话，比主动截断更糟。
    /// </summary>
    public const int UrlMax = 7000;

    // ════════════════════════════════════════════════════════════════
    // 草稿
    // ════════════════════════════════════════════════════════════════

    public sealed class Draft
    {
        public string Kind { get; set; } = "";
        public string SubKind { get; set; } = "";
        /// <summary>涉及的软件 id；空串 = 桌面版本身 / 未指定。</summary>
        public string AppId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        /// <summary>联系方式：选填，会公开显示在 Issue 里。</summary>
        public string Contact { get; set; } = "";
        /// <summary>是否在正文里附带本机环境信息（桌面版比网页版多的这一项）。</summary>
        public bool IncludeEnv { get; set; } = true;
    }

    /// <summary>一行环境信息（标签 + 值）。</summary>
    public sealed record EnvRow(string Label, string Value);

    /// <summary>取「大类 + 子类型」的中文标签，用于标题前缀与正文表格。</summary>
    public static string KindTag(Draft draft)
    {
        var kind = FindKind(draft.Kind);
        if (kind is null) return "";
        if (draft.Kind != KindReport) return kind.Tag;

        var sub = FindSubKind(draft.SubKind);
        return sub is null ? kind.Tag : $"{kind.Tag} · {sub.Tag}";
    }

    /// <summary>这条反馈要用到的全部 GitHub 标签。</summary>
    public static List<string> Labels(Draft draft)
    {
        var labels = new List<string> { CommonLabel };
        var kind = FindKind(draft.Kind);
        if (kind is not null) labels.Add(kind.Label);
        if (draft.Kind == KindReport)
        {
            var sub = FindSubKind(draft.SubKind);
            if (sub is not null) labels.Add(sub.Label);
        }
        return labels;
    }

    /// <summary>把多行文本转成 Markdown 引用块（每行前加 `&gt; `），空行留空。</summary>
    private static string Quote(string text) =>
        string.Join("\n", text.Split('\n').Select(line => line.Trim().Length > 0 ? "> " + line : ">"));

    /// <summary>拼 Issue 正文（Markdown）。</summary>
    public static string BuildBody(Draft draft, SoftwareApp? app, IReadOnlyList<EnvRow>? env)
    {
        var sb = new StringBuilder();

        if (draft.Detail.Trim().Length > 0)
            sb.Append(Quote(draft.Detail.Trim())).Append("\n\n");

        sb.Append("---\n\n");
        sb.Append("| 项目 | 内容 |\n");
        sb.Append("| --- | --- |\n");
        sb.Append($"| 类型 | {(KindTag(draft).Length > 0 ? KindTag(draft) : "（未选择）")} |\n");

        if (app is not null)
        {
            var site = Core.ShellConfig.SiteUrl.TrimEnd('/');
            sb.Append($"| 涉及软件 | {app.Name}（`{app.Id}`）· [详情页]({site}/#/download/{app.Id}) |\n");
        }
        else
        {
            sb.Append("| 涉及软件 | 桌面版本体 / 未指定 |\n");
        }

        if (draft.Contact.Trim().Length > 0)
            sb.Append($"| 联系方式 | {draft.Contact.Trim()} |\n");

        // 环境信息（可在表单里取消）：排查问题时最想知道的就是"你什么版本、什么系统"
        if (env is not null)
        {
            foreach (var row in env)
                sb.Append($"| {row.Label} | {row.Value} |\n");
        }

        sb.Append('\n');
        sb.Append("<!-- 由桌面版「反馈中心」生成。若标题或正文有误，直接改这里也行。 -->");

        return sb.ToString();
    }

    /// <summary>Issue 标题：<c>[报告问题 · 链接失效] 7-Zip 下载链接 404</c></summary>
    public static string BuildTitle(Draft draft)
    {
        var tag = KindTag(draft);
        var subject = draft.Title.Trim().Length > 0 ? draft.Title.Trim() : "（未填写标题）";
        return tag.Length > 0 ? $"[{tag}] {subject}" : subject;
    }

    private static string MakeUrl(string title, string body, IEnumerable<string> labels)
    {
        var url = $"{RepoUrl}/issues/new?title={Uri.EscapeDataString(title)}" +
                  $"&body={Uri.EscapeDataString(body)}";
        var labelText = string.Join(",", labels);
        if (labelText.Length > 0)
            url += $"&labels={Uri.EscapeDataString(labelText)}";
        return url;
    }

    public sealed record IssueLink(string Url, string Body, bool Truncated);

    /// <summary>
    /// 构造 GitHub Issue 新建页的预填链接。
    /// 返回实际用到的正文和「有没有被截断」—— 界面要据此显示提示条，
    /// **绝不能悄悄截断**：维护者看到半句话比看到完整内容还难办。
    ///
    /// 截断策略（与站点一致）：优先砍描述，保留标题、类型、涉及软件 —— 这三样是分流必需的。
    /// </summary>
    public static IssueLink BuildIssueLink(Draft draft, SoftwareApp? app, IReadOnlyList<EnvRow>? env)
    {
        var title = BuildTitle(draft);
        var labels = Labels(draft);

        var body = BuildBody(draft, app, env);
        var url = MakeUrl(title, body, labels);

        if (url.Length <= UrlMax)
            return new IssueLink(url, body, false);

        const string note = "\n\n（描述过长，已截断；完整内容请点页面上的「复制反馈内容」。）";
        var detail = draft.Detail.Trim();

        // 二分找「砍到多少字能让 URL 落进上限」，比逐字减快得多
        var lo = 0;
        var hi = detail.Length;
        while (lo < hi)
        {
            var mid = (int)Math.Ceiling((lo + hi) / 2.0);
            var trial = new Draft
            {
                Kind = draft.Kind, SubKind = draft.SubKind, AppId = draft.AppId,
                Title = draft.Title, Contact = draft.Contact,
                Detail = detail[..mid] + note,
            };
            if (MakeUrl(title, BuildBody(trial, app, env), labels).Length <= UrlMax) lo = mid;
            else hi = mid - 1;
        }

        var cut = new Draft
        {
            Kind = draft.Kind, SubKind = draft.SubKind, AppId = draft.AppId,
            Title = draft.Title, Contact = draft.Contact,
            Detail = detail[..lo] + note,
        };
        var cutBody = BuildBody(cut, app, env);
        return new IssueLink(MakeUrl(title, cutBody, labels), cutBody, true);
    }

    // ════════════════════════════════════════════════════════════════
    // 校验
    // ════════════════════════════════════════════════════════════════

    /// <summary>校验：返回第一条不满足的提示文案，全部通过返回空串。</summary>
    public static string Validate(Draft draft)
    {
        if (draft.Kind.Length == 0) return "请选择反馈类型。";
        if (draft.Kind == KindReport && draft.SubKind.Length == 0)
            return "请选择问题类型。";
        if (draft.Title.Trim().Length == 0 || draft.Detail.Trim().Length == 0)
            return "请填写所有标有 * 的必填项。";
        if (draft.Title.Trim().Length > TitleMax)
            return $"标题超出 {TitleMax} 字上限，请精简后重试（现在 {draft.Title.Trim().Length} 字）。";
        return "";
    }
}

/// <summary>
/// 反馈草稿的本地持久化（%LOCALAPPDATA%\ClassSoftwareHub\feedback-draft.json）。
/// ⚠️ 与站点一样**故意不提供「提交成功后清草稿」**：打开的是浏览器里的 GitHub 页面，
///    跨进程读不到那边到底提交没有。清掉反而危险 —— 用户以为提交了、其实没点，内容却没了。
///    宁可下次进来多问一句「恢复上次填写」。
/// </summary>
public static class FeedbackDraftStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string FilePath { get; } =
        Path.Combine(Core.AppPaths.DataDir, "feedback-draft.json");

    public static void Save(Feedback.Draft draft)
    {
        try
        {
            Directory.CreateDirectory(Core.AppPaths.DataDir);
            // 原子替换：先写临时文件再整体换过去，避免写到一半留下半截 JSON
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(draft, JsonOpts));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null, true);
            else File.Move(tmp, FilePath);
        }
        catch
        {
            // 草稿存不下来不影响提交本身
        }
    }

    public static Feedback.Draft? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var draft = JsonSerializer.Deserialize<Feedback.Draft>(File.ReadAllText(FilePath), JsonOpts);
            // 只有「填过东西」的草稿才值得恢复，空草稿等于没有
            if (draft is null) return null;
            if (draft.Kind.Length == 0 && draft.Title.Trim().Length == 0 && draft.Detail.Trim().Length == 0)
                return null;
            return draft;
        }
        catch
        {
            return null;
        }
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }
}
