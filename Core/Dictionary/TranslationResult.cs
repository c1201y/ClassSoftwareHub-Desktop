using System;

namespace ClassSoftwareHub.Desktop.Core.Dictionary;

/// <summary>一次句子翻译的结果。</summary>
public sealed class TranslationResult
{
    /// <summary>原文。</summary>
    public string Source { get; init; } = "";

    /// <summary>译文。空 = 没翻出来。</summary>
    public string Target { get; init; } = "";

    /// <summary>源语言代码（<c>zh-CN</c> / <c>en</c>）。</summary>
    public string From { get; init; } = "";

    /// <summary>目标语言代码。</summary>
    public string To { get; init; } = "";

    /// <summary>翻译服务名，界面上要标出来（不同服务质量差得远）。</summary>
    public string Provider { get; init; } = "";

    public bool IsEmpty => Target.Length == 0;

    /// <summary>方向标签，例如「中 → 英」。</summary>
    public string DirectionLabel => From.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "中 → 英" : "英 → 中";
}

/// <summary>
/// 一次查询该怎么解释：按单词查、按整句翻译，还是让程序自己判断。
///
/// <para>
/// 界面上不摆"词 / 句"开关 —— 绝大多数时候用户不用想这件事：
/// 打一个词就是查词，打一句话就是翻译。判错了在结果上方给一条链接，点一下换个方式重来即可。
/// </para>
/// </summary>
public static class QueryModes
{
    /// <summary>自动判断（默认）。</summary>
    public const string Auto = "auto";

    /// <summary>强制按单词查。</summary>
    public const string Word = "word";

    /// <summary>强制按整句翻译。</summary>
    public const string Sentence = "sentence";

    /// <summary>
    /// 看起来像一句话吗。判定刻意宽松 —— 判成句子最坏是"把一个短语整句翻了"，
    /// 判成单词最坏是"查不到，用户还得自己再点一次翻译"。
    /// </summary>
    public static bool LooksLikeSentence(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return false;

        if (LexiconFormat.HasChinese(t))
        {
            // 中文：句末标点、或一口气八个汉字以上，基本不是"一个词"
            if (t.EndsWith('。') || t.EndsWith('！') || t.EndsWith('？') ||
                t.EndsWith('.') || t.EndsWith('!') || t.EndsWith('?')) return true;

            var han = 0;
            foreach (var c in t)
                if (c is >= '\u4E00' and <= '\u9FFF') han++;

            return han >= 8;
        }

        // 英文：带句末标点、或三个词以上、或明显超长
        if (t.EndsWith('.') || t.EndsWith('!') || t.EndsWith('?')) return true;
        if (t.Length > 24) return true;

        var words = 0;
        foreach (var part in t.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            words++;

        return words >= 3;
    }
}
