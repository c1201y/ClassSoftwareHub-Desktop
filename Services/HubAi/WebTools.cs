using System;
using ClassSoftwareHub.Desktop.Core;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services.HubAi;





















public static class WebTools
{
    private static readonly HttpClient Http = CreateClient();

    
    private const int MaxResults = 5;

    
    private const int MaxPageChars = 3500;

    
    private static string ReadArg(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");

        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");

        return client;
    }

    
    
    

    public static async Task<string> SearchAsync(string argumentsJson, CancellationToken ct)
    {
        var query = ReadArg(argumentsJson, "query");

        if (string.IsNullOrWhiteSpace(query))
            return "缺少 query 参数。请给出要检索的关键词。";

        try
        {
            var url = "https://lite.duckduckgo.com/lite/?q=" + Uri.EscapeDataString(query);

            using var response = await Http.GetAsync(url, ct);
            var html = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return $"检索未成功（HTTP {(int)response.StatusCode}）。请如实告知用户本次没有查到，不要编造。";

            var hits = ParseLite(html);

            if (hits.Count == 0)
                return $"没有检索到与「{query}」相关的结果。请如实告知用户，不要编造。";

            var builder = new StringBuilder();
            builder.AppendLine($"检索「{query}」，共 {hits.Count} 条：");

            for (var i = 0; i < hits.Count; i++)
            {
                builder.AppendLine($"{i + 1}. {hits[i].Title}");
                if (!string.IsNullOrWhiteSpace(hits[i].Snippet))
                    builder.AppendLine($"   摘要：{hits[i].Snippet}");
                if (!string.IsNullOrWhiteSpace(hits[i].Url))
                    builder.AppendLine($"   链接：{hits[i].Url}");
            }

            builder.AppendLine();
            builder.AppendLine("提示：这些是网页摘要，不是实时核实过的事实。引用时请说明来源，不要当作官方结论。");

            return builder.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn("hubai-web", "检索失败：" + ex.Message);
            return "检索失败（网络不可达或被限流）。请如实告知用户本次没有查到，不要编造。";
        }
    }

    
    
    
    
    
    private static List<(string Title, string Url, string Snippet)> ParseLite(string html)
    {
        var results = new List<(string, string, string)>();

        var linkRegex = new Regex(
            "<a[^>]*class=\"result-link\"[^>]*href=\"(?<url>[^\"]+)\"[^>]*>(?<title>.*?)</a>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var snippetRegex = new Regex(
            "<td[^>]*class=\"result-snippet\"[^>]*>(?<text>.*?)</td>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var links = linkRegex.Matches(html);
        var snippets = snippetRegex.Matches(html);

        for (var i = 0; i < links.Count && results.Count < MaxResults; i++)
        {
            var title = Clean(links[i].Groups["title"].Value);
            var url = links[i].Groups["url"].Value;

            
            url = UnwrapRedirect(url);

            var snippet = i < snippets.Count ? Clean(snippets[i].Groups["text"].Value) : "";

            if (title.Length == 0) continue;

            results.Add((title, url, snippet));
        }

        return results;
    }

    
    private static string UnwrapRedirect(string url)
    {
        if (url.StartsWith("//", StringComparison.Ordinal)) url = "https:" + url;

        var marker = "uddg=";
        var index = url.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return url;

        var start = index + marker.Length;
        var end = url.IndexOf('&', start);
        var encoded = end < 0 ? url[start..] : url[start..end];

        try { return Uri.UnescapeDataString(encoded); }
        catch { return url; }
    }

    
    
    

    public static async Task<string> FetchAsync(string argumentsJson, CancellationToken ct)
    {
        var raw = ReadArg(argumentsJson, "url");

        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return "url 参数无效，需要完整的 http(s) 地址。";

        if (uri.Scheme != "http" && uri.Scheme != "https")
            return "只支持 http / https 地址。";

        try
        {
            using var response = await Http.GetAsync(uri, ct);

            if (!response.IsSuccessStatusCode)
                return $"打开页面失败（HTTP {(int)response.StatusCode}）。请如实告知用户，不要据此推测页面内容。";

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

            if (!contentType.Contains("text", StringComparison.OrdinalIgnoreCase)
                && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                && !contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
            {
                return $"该地址不是文本内容（{contentType}），无法读取正文。";
            }

            var html = await response.Content.ReadAsStringAsync(ct);
            var text = ToPlainText(html);

            if (text.Length == 0)
                return "页面没有可读正文（可能是纯脚本渲染的页面）。";

            if (text.Length > MaxPageChars)
                text = text[..MaxPageChars] + "\n（正文过长，已截断）";

            return $"页面：{uri}\n\n{text}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn("hubai-web", "抓取失败：" + ex.Message);
            return "打开页面失败（网络不可达或站点拒绝访问）。请如实告知用户，不要编造页面内容。";
        }
    }

    
    
    

    
    private static string ToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        html = Regex.Replace(html, "<script[^>]*>.*?</script>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "<style[^>]*>.*?</style>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "<noscript[^>]*>.*?</noscript>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "<svg[^>]*>.*?</svg>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        
        html = Regex.Replace(html, "<(br|/p|/div|/li|/h[1-6]|/tr)[^>]*>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "<[^>]+>", "", RegexOptions.Singleline);

        html = DecodeEntities(html);
        html = Regex.Replace(html, "[ \t\u00a0]+", " ");
        html = Regex.Replace(html, "\n{3,}", "\n\n");

        return html.Trim();
    }

    private static string Clean(string fragment)
    {
        if (string.IsNullOrEmpty(fragment)) return "";

        var text = Regex.Replace(fragment, "<[^>]+>", " ", RegexOptions.Singleline);
        text = DecodeEntities(text);
        text = Regex.Replace(text, "\\s+", " ");

        return text.Trim();
    }

    private static string DecodeEntities(string text)
    {
        return text
            .Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'")
            .Replace("&apos;", "'")
            .Replace("&middot;", "·")
            .Replace("&hellip;", "…")
            .Replace("&mdash;", "—")
            .Replace("&ndash;", "–");
    }
}
