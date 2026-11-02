using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;

namespace ClassSoftwareHub.Desktop.Services.Docs;

/// <summary>文档库里的一篇文章（来自桌面端仓库「文档库/&lt;id&gt;/meta.json」）。</summary>
public sealed class DocArticle
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Contact { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Date { get; set; } = "";

    /// <summary>markdown / txt / docx / pdf。</summary>
    public string Type { get; set; } = "";

    /// <summary>正文文件（相对文章目录），例如 <c>article.md</c>。</summary>
    public string File { get; set; } = "";

    public int Order { get; set; } = 1000;
    public List<string> Tags { get; set; } = new();

    /// <summary>文章目录下的全部文件（相对路径，不含 meta.json）。同步清单时填入。</summary>
    public List<string> Files { get; set; } = new();

    /// <summary>文件相对路径 → 仓库 blob sha，用来判断"内容有没有变过"。</summary>
    public Dictionary<string, string> FileShas { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>meta.json 的 blob sha：没变就不必重新下载元信息。</summary>
    public string MetaSha { get; set; } = "";

    /// <summary>供界面直接显示的格式名。</summary>
    [JsonIgnore]
    public string TypeLabel => Type switch
    {
        "markdown" => "Markdown",
        "txt" => "纯文本",
        "docx" => "Word 文档",
        "pdf" => "PDF",
        _ => "文档",
    };

    /// <summary>卡片副标题下面那行：作者 · 日期 · 标签。</summary>
    [JsonIgnore]
    public string ByLine
    {
        get
        {
            var parts = new List<string>();
            if (Author.Length > 0) parts.Add(Author);
            if (DisplayDate.Length > 0) parts.Add(DisplayDate);
            if (Tags.Count > 0) parts.Add(string.Join(" / ", Tags));
            return string.Join(" · ", parts);
        }
    }

    [JsonIgnore]
    public string DisplayDate =>
        ParsedDate?.ToString("yyyy-MM-dd") ?? Date;

    [JsonIgnore]
    public Visibility SummaryVisibility =>
        Summary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Visibility ByLineVisibility =>
        ByLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>正文文件在仓库里的完整路径。</summary>
    [JsonIgnore]
    public string RepoArticleDir => $"文档库/{Id}";

    [JsonIgnore]
    public string RepoFilePath => string.IsNullOrEmpty(File) ? "" : $"{RepoArticleDir}/{File}";

    [JsonIgnore]
    public DateTime? ParsedDate =>
        DateTime.TryParse(Date, out var value) ? value.Date : null;
}

/// <summary>已经落到本机缓存、可以直接交给渲染器的一篇文章。</summary>
public sealed class DocContent
{
    public required DocArticle Article { get; init; }

    /// <summary>正文缓存文件的绝对路径（无扩展名）。</summary>
    public required string FilePath { get; init; }

    /// <summary>正文格式（以正文文件扩展名为准，meta 里的 type 只是提示）。</summary>
    public required string Type { get; init; }

    /// <summary>文章目录内的相对路径 → 本机缓存文件的绝对路径。</summary>
    public required IReadOnlyDictionary<string, string> Resources { get; init; }

    /// <summary>把正文里的相对图片路径换成本机缓存路径。找不到返回 null。</summary>
    public string? ResolveResource(string relative)
    {
        var key = NormalizeRelative(relative);
        if (key.Length == 0) return null;

        if (Resources.TryGetValue(key, out var path)) return path;

        // Markdown 里常见 ./images/a.png 与 images/a.png 两种写法，映射表只存后者
        foreach (var pair in Resources)
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;

        return null;
    }

    public static string NormalizeRelative(string value)
    {
        var text = (value ?? "").Trim().Replace('\\', '/');
        if (text.StartsWith("./", StringComparison.Ordinal)) text = text[2..];
        text = text.TrimStart('/');
        try { text = Uri.UnescapeDataString(text); } catch { }
        return text;
    }
}

/// <summary>同步结果（不抛异常，失败给人话）。</summary>
public sealed record DocSyncResult(bool Ok, bool Updated, int Count, string Message);

/// <summary>本机缓存索引里的一篇（保存在 docs\cache.json）。</summary>
public sealed class CachedDoc
{
    public string Fingerprint { get; set; } = "";
    public string File { get; set; } = "";
    public Dictionary<string, string> Resources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string CachedAt { get; set; } = "";

    public IEnumerable<string> AllFiles =>
        string.IsNullOrEmpty(File) ? Resources.Values : Resources.Values.Append(File);

    /// <summary>
    /// 只要求正文在：缺一张图片不该让整篇每次打开都重新联网下载
    /// （离线时那张图本来就永远补不上，正文却还能读）。
    /// </summary>
    public bool Exists(Func<string, bool> fileExists) =>
        !string.IsNullOrEmpty(File) && fileExists(File);
}
