using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;

namespace ClassSoftwareHub.Desktop.Services.Docs;


public sealed class DocArticle
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Contact { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Date { get; set; } = "";

    
    public string Type { get; set; } = "";

    
    public string File { get; set; } = "";

    public int Order { get; set; } = 1000;
    public List<string> Tags { get; set; } = new();

    
    public List<string> Files { get; set; } = new();

    
    public Dictionary<string, string> FileShas { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    
    public string MetaSha { get; set; } = "";

    
    [JsonIgnore]
    public string TypeLabel => Type switch
    {
        "markdown" => "Markdown",
        "txt" => "纯文本",
        "docx" => "Word 文档",
        "pdf" => "PDF",
        _ => "文档",
    };

    
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

    
    [JsonIgnore]
    public string RepoArticleDir => $"文档库/{Id}";

    [JsonIgnore]
    public string RepoFilePath => string.IsNullOrEmpty(File) ? "" : $"{RepoArticleDir}/{File}";

    [JsonIgnore]
    public DateTime? ParsedDate =>
        DateTime.TryParse(Date, out var value) ? value.Date : null;
}


public sealed class DocContent
{
    public required DocArticle Article { get; init; }

    
    public required string FilePath { get; init; }

    
    public required string Type { get; init; }

    
    public required IReadOnlyDictionary<string, string> Resources { get; init; }

    
    public string? ResolveResource(string relative)
    {
        var key = NormalizeRelative(relative);
        if (key.Length == 0) return null;

        if (Resources.TryGetValue(key, out var path)) return path;

        
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


public sealed record DocSyncResult(bool Ok, bool Updated, int Count, string Message);


public sealed class CachedDoc
{
    public string Fingerprint { get; set; } = "";
    public string File { get; set; } = "";
    public Dictionary<string, string> Resources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string CachedAt { get; set; } = "";

    public IEnumerable<string> AllFiles =>
        string.IsNullOrEmpty(File) ? Resources.Values : Resources.Values.Append(File);

    
    
    
    
    public bool Exists(Func<string, bool> fileExists) =>
        !string.IsNullOrEmpty(File) && fileExists(File);
}
