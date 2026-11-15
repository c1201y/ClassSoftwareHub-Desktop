namespace ClassSoftwareHub.Desktop.Core.Dictionary;

/// <summary>
/// 一条释义。中文词典习惯把词性写在释义最前面（"n. 招呼，问候"），
/// 这里把它拆出来单独存，界面上词性排成一个小标签，正文只剩中文。
/// </summary>
public sealed class DictSense
{
    /// <summary>词性，如 <c>n.</c> <c>v.</c> <c>adj.</c>；拆不出来就是空串。</summary>
    public string Pos { get; init; } = "";

    /// <summary>释义正文（不含词性）。</summary>
    public string Text { get; init; } = "";
}

/// <summary>一条例句：原文 + 译文（译文没有就是空串）。</summary>
public sealed class DictExample
{
    public string En { get; init; } = "";
    public string Zh { get; init; } = "";
}

/// <summary>词条的方向：英译中 / 中译英。同一个 <see cref="DictEntry"/> 两种情况都用。</summary>
public static class DictDirections
{
    /// <summary>英文单词 → 中文释义（默认）。</summary>
    public const string EnToZh = "en-zh";

    /// <summary>中文词 → 英文译词。</summary>
    public const string ZhToEn = "zh-en";
}

/// <summary>
/// 一次查询查到的一个词条。<see cref="Source"/> 说明它从哪儿来 —— 界面上要明确标出来，
/// 用户才知道挡住他的那条是"本地词库没这词"还是"在线查失败"。
/// </summary>
public sealed class DictEntry
{
    public string Word { get; init; } = "";

    /// <summary>音标，带斜杠（<c>/həˈləʊ/</c>）；没有就是空串。</summary>
    public string Phonetic { get; init; } = "";

    /// <summary>词条来自哪个源，见 <see cref="DictSources"/> 里的常量。</summary>
    public string Source { get; init; } = "";

    /// <summary>方向，见 <see cref="DictDirections"/>。</summary>
    public string Direction { get; init; } = DictDirections.EnToZh;

    /// <summary>中译英时每个 <see cref="DictSense.Text"/> 是英文译词，其余字段用法不变。</summary>
    public bool IsZhToEn => Direction == DictDirections.ZhToEn;

    public IReadOnlyList<DictSense> Senses { get; init; } = System.Array.Empty<DictSense>();

    public IReadOnlyList<DictExample> Examples { get; init; } = System.Array.Empty<DictExample>();

    /// <summary>给界面看的来源标签。</summary>
    public string SourceLabel => Source switch
    {
        DictSources.Offline => "本地词库",
        DictSources.Youdao => "有道词典",
        DictSources.FreeDictionary => "Free Dictionary",
        _ => Source
    };

    public bool IsEmpty => Senses.Count == 0;
}

/// <summary>
/// 一次查询的完整结果。
///
/// ⚠️ 这里刻意把「没查到」和「查询失败」分成两个字段：前者是词库里确实没有这个词
/// （换个拼写或改用在线源就能解决），后者是网络/接口出问题（重试才有意义）。
/// 界面上的提示语完全不同，混成一个 bool 就没法给用户可操作的下一步。
/// </summary>
public sealed class DictResult
{
    public DictEntry? Entry { get; init; }

    /// <summary>整句翻译的结果（<see cref="Entry"/> 为空时才可能有值）。</summary>
    public TranslationResult? Translation { get; init; }

    /// <summary>一句话说明，例如「本地词库中没有这个词，已改用在线词典」；没有要说的就是空串。</summary>
    public string Note { get; init; } = "";

    /// <summary>查询失败的原因（网络不通、接口改版…）；没失败就是空串。</summary>
    public string Error { get; init; } = "";

    public bool Found => Entry is { IsEmpty: false } || Translation is { IsEmpty: false };
}
