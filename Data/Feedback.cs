using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ClassSoftwareHub.Desktop.Data;























public static class Feedback
{
    
    
    

    
    public static string RepoUrl =>
        $"https://github.com/{Core.ShellConfig.SiteRepoOwner}/{Core.ShellConfig.SiteRepoName}";

    
    
    
    
    
    public static string IssueListUrl =>
        RepoUrl + "/issues?q=" + Uri.EscapeDataString($"is:issue label:\"{DesktopLabel}\"");

    
    
    
    
    
    

    public const string KindReport = "report";
    public const string KindSuggestion = "suggestion";

    public sealed class KindDef
    {
        public string Key { get; init; } = "";
        public string Title { get; init; } = "";
        public string Desc { get; init; } = "";
        
        public string Glyph { get; init; } = "";
        
        public string Label { get; init; } = "";
        
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
            
            Desc = "软件信息有误、下载链接失效，或页面无法正常使用。",
            Glyph = "\uE7BA",                       
            Label = "报告问题", Tag = "报告问题",
        },
        new()
        {
            Key = KindSuggestion, Title = "提出建议",
            Desc = "希望新增的功能，或对现有功能的改进建议。",
            
            
            Glyph = "\uE734",
            Label = "提出建议", Tag = "提出建议",
        },
    };

    
    public static readonly IReadOnlyList<SubKindDef> ReportSubKinds = new List<SubKindDef>
    {
        new() { Key = "interaction", Title = "逻辑交互问题", Label = "逻辑交互", Tag = "逻辑交互" },
        new() { Key = "link",        Title = "链接失效问题", Label = "链接失效", Tag = "链接失效" },
        new() { Key = "other",       Title = "其他分类问题", Label = "其他问题", Tag = "其他问题" },
    };

    
    public const string CommonLabel = "用户反馈";

    
    
    
    
    public const string DesktopLabel = "桌面版";

    public static KindDef? FindKind(string key) =>
        Kinds.FirstOrDefault(k => k.Key == key);

    public static SubKindDef? FindSubKind(string key) =>
        ReportSubKinds.FirstOrDefault(s => s.Key == key);

    
    
    

    
    public const int TitleMax = 80;

    
    public const int DetailMax = 1800;

    
    
    
    
    
    public const int UrlMax = 7000;

    
    
    

    public sealed class Draft
    {
        public string Kind { get; set; } = "";
        public string SubKind { get; set; } = "";
        
        public string AppId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        
        
        
        
        public string Contact { get; set; } = "";
        
        public bool IncludeEnv { get; set; } = true;

        
        
        
        
        
        
        public void CopyFrom(Draft other)
        {
            Kind = other.Kind;
            SubKind = other.SubKind;
            AppId = other.AppId;
            Title = other.Title;
            Detail = other.Detail;
            Contact = other.Contact;
            IncludeEnv = other.IncludeEnv;
        }
    }

    
    public sealed record EnvRow(string Label, string Value);

    
    public static string KindTag(Draft draft)
    {
        var kind = FindKind(draft.Kind);
        if (kind is null) return "";
        if (draft.Kind != KindReport) return kind.Tag;

        var sub = FindSubKind(draft.SubKind);
        return sub is null ? kind.Tag : $"{kind.Tag} · {sub.Tag}";
    }

    
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
        labels.Add(DesktopLabel);      
        return labels;
    }

    
    private static string Quote(string text) =>
        string.Join("\n", text.Split('\n').Select(line => line.Trim().Length > 0 ? "> " + line : ">"));

    
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

        
        if (env is not null)
        {
            foreach (var row in env)
                sb.Append($"| {row.Label} | {row.Value} |\n");
        }

        sb.Append('\n');

        
        
        
        if (draft.Contact.Trim().Length > 0)
        {
            sb.Append("### 联系方式（已在本地加密，仅维护者可用私钥解密）\n\n");
            sb.Append("```\n");
            sb.Append(draft.Contact.Trim()).Append('\n');
            sb.Append("```\n\n");
        }

        sb.Append("<!-- 由桌面版「反馈中心」生成。标题或正文如有错误，可直接在此修改。 -->");

        return sb.ToString();
    }

    
    
    
    
    
    
    
    
    public static string BuildSubmitDetail(Draft draft, IReadOnlyList<EnvRow>? env)
    {
        var sb = new StringBuilder();
        sb.Append(draft.Detail.Trim());

        if (env is not null && env.Count > 0)
        {
            sb.Append("\n\n——\n\n本机信息：");
            sb.Append(string.Join(" · ", env.Select(r => $"{r.Label} {r.Value}")));
        }

        return sb.ToString();
    }

    
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

    
    
    
    
    
    
    
    public static IssueLink BuildIssueLink(Draft draft, SoftwareApp? app, IReadOnlyList<EnvRow>? env)
    {
        var title = BuildTitle(draft);
        var labels = Labels(draft);

        var body = BuildBody(draft, app, env);
        var url = MakeUrl(title, body, labels);

        if (url.Length <= UrlMax)
            return new IssueLink(url, body, false);

        const string note = "\n\n（描述过长，已截断；完整内容请使用页面上的「在 Q 群中反馈」。）";
        var detail = draft.Detail.Trim();

        
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

    
    
    

    
    public static string Validate(Draft draft)
    {
        if (draft.Kind.Length == 0) return "反馈类型为必填项。";
        if (draft.Kind == KindReport && draft.SubKind.Length == 0)
            return "问题类型为必填项。";
        if (draft.Title.Trim().Length == 0 || draft.Detail.Trim().Length == 0)
            return "标有 * 的必填项尚未填写完整。";
        if (draft.Title.Trim().Length > TitleMax)
            return $"标题超出 {TitleMax} 字上限，需精简后重试（当前 {draft.Title.Trim().Length} 字）。";
        return "";
    }
}











public static class FeedbackDraftStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    
    
    
    
    
    
    
    public static Feedback.Draft Current { get; } = new();

    public static string FilePath { get; } =
        Path.Combine(Core.AppPaths.DataDir, "feedback-draft.json");

    public static void Save(Feedback.Draft draft)
    {
        try
        {
            Directory.CreateDirectory(Core.AppPaths.DataDir);
            
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(draft, JsonOpts));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null, true);
            else File.Move(tmp, FilePath);
        }
        catch
        {
            
        }
    }

    public static Feedback.Draft? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var draft = JsonSerializer.Deserialize<Feedback.Draft>(File.ReadAllText(FilePath), JsonOpts);
            
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

    
    
    
    
    
    
    
    public static void Discard()
    {
        Current.CopyFrom(new Feedback.Draft());
        Clear();
    }
}
