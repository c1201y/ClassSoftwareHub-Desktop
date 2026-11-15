using System;

namespace ClassSoftwareHub.Desktop.Core.Dictionary;


public sealed class TranslationResult
{
    
    public string Source { get; init; } = "";

    
    public string Target { get; init; } = "";

    
    public string From { get; init; } = "";

    
    public string To { get; init; } = "";

    
    public string Provider { get; init; } = "";

    public bool IsEmpty => Target.Length == 0;

    
    public string DirectionLabel => From.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "中 → 英" : "英 → 中";
}









public static class QueryModes
{
    
    public const string Auto = "auto";

    
    public const string Word = "word";

    
    public const string Sentence = "sentence";

    
    
    
    
    public static bool LooksLikeSentence(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return false;

        if (LexiconFormat.HasChinese(t))
        {
            
            if (t.EndsWith('。') || t.EndsWith('！') || t.EndsWith('？') ||
                t.EndsWith('.') || t.EndsWith('!') || t.EndsWith('?')) return true;

            var han = 0;
            foreach (var c in t)
                if (c is >= '\u4E00' and <= '\u9FFF') han++;

            return han >= 8;
        }

        
        if (t.EndsWith('.') || t.EndsWith('!') || t.EndsWith('?')) return true;
        if (t.Length > 24) return true;

        var words = 0;
        foreach (var part in t.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            words++;

        return words >= 3;
    }
}
