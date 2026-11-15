namespace ClassSoftwareHub.Desktop.Core.Dictionary;





public sealed class DictSense
{
    
    public string Pos { get; init; } = "";

    
    public string Text { get; init; } = "";
}


public sealed class DictExample
{
    public string En { get; init; } = "";
    public string Zh { get; init; } = "";
}


public static class DictDirections
{
    
    public const string EnToZh = "en-zh";

    
    public const string ZhToEn = "zh-en";
}





public sealed class DictEntry
{
    public string Word { get; init; } = "";

    
    public string Phonetic { get; init; } = "";

    
    public string Source { get; init; } = "";

    
    public string Direction { get; init; } = DictDirections.EnToZh;

    
    public bool IsZhToEn => Direction == DictDirections.ZhToEn;

    public IReadOnlyList<DictSense> Senses { get; init; } = System.Array.Empty<DictSense>();

    public IReadOnlyList<DictExample> Examples { get; init; } = System.Array.Empty<DictExample>();

    
    public string SourceLabel => Source switch
    {
        DictSources.Offline => "本地词库",
        DictSources.Youdao => "有道词典",
        DictSources.FreeDictionary => "Free Dictionary",
        _ => Source
    };

    public bool IsEmpty => Senses.Count == 0;
}








public sealed class DictResult
{
    public DictEntry? Entry { get; init; }

    
    public TranslationResult? Translation { get; init; }

    
    public string Note { get; init; } = "";

    
    public string Error { get; init; } = "";

    public bool Found => Entry is { IsEmpty: false } || Translation is { IsEmpty: false };
}
