using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Core.Dictionary;

namespace ClassSoftwareHub.Desktop.Services.Dictionary;

/// <summary>
/// 整句翻译。中 → 英、英 → 中两个方向，自动判断方向。
///
/// <para>
/// <b>为什么用 MyMemory。</b>它有公开的免密钥接口，两个方向都能直接调，返回 JSON。
/// 试过但**不通**的：有道 <c>fanyi.youdao.com/translate</c>（已改版，返回的是网页）、
/// Google <c>translate_a</c>（本机 SSL 不通）、微软 edge 翻译的取 token 端点（404）、Yandex（403）。
/// 2026-11-01 实测只剩这一家用得住。
/// </para>
/// <para>
/// ⚠️ 它是社区翻译记忆库，不是大厂机器翻译：短句、日常表达够用，长句和术语会飘。
///    所以结果卡上必须标出「由谁翻译」，用户看到怪句子时才知道该怪谁、而不是怪应用。
/// </para>
/// <para>
/// ⚠️ 匿名额度有限（约每天五千词），超了返回 429。这里把额度用尽的提示单独写出来，
///    不然用户只会看到"翻译失败"四个字，以为是程序坏了。
/// </para>
/// </summary>
public static class TranslationService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private const string Endpoint = "https://api.mymemory.translated.net/get?q=";

    /// <summary>接口对单次请求的长度有限制，超过就不发请求，直接告诉用户拆短。</summary>
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

        // 服务翻不出来时会原样退回输入，这不是"成功"
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

        // 额度用尽 / 拒绝服务时 responseStatus 不是 200，responseData 里也没有译文
        if (root.TryGetProperty("responseStatus", out var status))
        {
            var code = status.ValueKind == JsonValueKind.Number ? status.GetInt32() : 0;
            if (code is 429 or 403)
                throw new InvalidOperationException("今日翻译额度已用完，明天再试或改用在线词典逐词查。");
        }

        if (!root.TryGetProperty("responseData", out var data)) return "";
        if (!data.TryGetProperty("translatedText", out var t)) return "";

        var raw = t.GetString() ?? "";

        // 服务返回的是 HTML 转义过的文本（&quot; &#39; 之类），直接显示会露出实体
        var clean = WebUtility.HtmlDecode(raw).Trim();

        // 它有时给整句套一对引号（"您今天过得好吗？"），译文外面那层引号没有意义，去掉
        return TrimQuotes(clean);
    }

    private static string TrimQuotes(string text)
    {
        if (text.Length < 2) return text;

        // 成对的外层引号：直接去掉
        var first = text[0];
        var last = text[^1];
        var paired = (first == '"' && last == '"')
                     || (first == '“' && last == '”')
                     || (first == '\'' && last == '\'');
        if (paired) return text[1..^1].Trim();

        // 只剩一个孤零零的收尾引号（实测遇到过 "您今天过得好吗？”"）：它没有配对的开始引号，
        // 说明是服务端自己加的噪声，去掉。⛔ 不能无脑 Trim('”')，正常引用句的收尾引号要去它是有配对的。
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
