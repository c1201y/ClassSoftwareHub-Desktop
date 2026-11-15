using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Core.Dictionary;

namespace ClassSoftwareHub.Desktop.Services.Dictionary;



















public static class TranslationService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private const string Endpoint = "https://api.mymemory.translated.net/get?q=";

    
    private const int MaxLength = 480;

    public static async Task<DictResult> TranslateAsync(string text)
    {
        var source = (text ?? "").Trim();
        if (source.Length == 0) return new DictResult { Note = "请输入要翻译的句子。" };

        if (source.Length > MaxLength)
            return new DictResult { Note = $"一次最多翻译 {MaxLength} 个字符，请把句子拆短一些。" };

        var toEnglish = LexiconFormat.HasChinese(source);
        var from = toEnglish ? "zh-CN" : "en";
        var to = toEnglish ? "en" : "zh-CN";

        string target;
        try
        {
            target = await RequestAsync(source, from, to);
        }
        catch (Exception ex)
        {
            AppLog.Warning("dict", $"翻译失败：{ex.Message}");
            return new DictResult { Note = "翻译服务暂时连不上。", Error = ex.Message };
        }

        if (target.Length == 0)
            return new DictResult { Note = "翻译服务没有返回结果。" };

        
        if (string.Equals(target.Trim(), source, StringComparison.OrdinalIgnoreCase))
            return new DictResult { Note = "没有翻出结果，可以换个说法或缩短句子。" };

        return new DictResult
        {
            Translation = new TranslationResult
            {
                Source = source,
                Target = target.Trim(),
                From = from,
                To = to,
                Provider = "MyMemory"
            }
        };
    }

    private static async Task<string> RequestAsync(string text, string from, string to)
    {
        var url = Endpoint + Uri.EscapeDataString(text) + "&langpair=" + Uri.EscapeDataString(from + "|" + to);
        var json = await Http.GetStringAsync(url);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        
        if (root.TryGetProperty("responseStatus", out var status))
        {
            var code = status.ValueKind == JsonValueKind.Number ? status.GetInt32() : 0;
            if (code is 429 or 403)
                throw new InvalidOperationException("今日翻译额度已用完，明天再试或改用在线词典逐词查。");
        }

        if (!root.TryGetProperty("responseData", out var data)) return "";
        if (!data.TryGetProperty("translatedText", out var t)) return "";

        var raw = t.GetString() ?? "";

        
        var clean = WebUtility.HtmlDecode(raw).Trim();

        
        return TrimQuotes(clean);
    }

    private static string TrimQuotes(string text)
    {
        if (text.Length < 2) return text;

        
        var first = text[0];
        var last = text[^1];
        var paired = (first == '"' && last == '"')
                     || (first == '“' && last == '”')
                     || (first == '\'' && last == '\'');
        if (paired) return text[1..^1].Trim();

        
        
        if (last == '”' && Count(text, '”') > Count(text, '“')) return text[..^1].Trim();
        if (first == '“' && Count(text, '“') > Count(text, '”')) return text[1..].Trim();

        return text;
    }

    private static int Count(string text, char c)
    {
        var n = 0;
        foreach (var ch in text) if (ch == c) n++;
        return n;
    }
}
