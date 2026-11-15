using System;
using System.Collections.Generic;
using System.Text;

namespace ClassSoftwareHub.Desktop.Core;















public static class PinyinTable
{
    
    private const int MaxWordLength = 4;

    private static Dictionary<char, string>? _chars;
    private static Dictionary<string, string>? _words;
    private static readonly object Gate = new();

    
    public static string Of(char ch)
    {
        Ensure();
        return _chars!.TryGetValue(ch, out var pinyin) ? pinyin : "";
    }

    
    
    
    
    public static string Translate(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        Ensure();

        var sb = new StringBuilder(text.Length * 3);

        for (var i = 0; i < text.Length;)
        {
            var matched = false;
            var max = Math.Min(MaxWordLength, text.Length - i);

            for (var length = max; length >= 2; length--)
            {
                if (!_words!.TryGetValue(text.Substring(i, length), out var word)) continue;

                sb.Append(word);
                i += length;
                matched = true;
                break;
            }

            if (matched) continue;

            if (_chars!.TryGetValue(text[i], out var single)) sb.Append(single);
            i++;
        }

        return sb.ToString();
    }

    
    public static bool IsHan(char ch) => ch >= 0x4E00 && ch <= 0x9FFF;

    private static void Ensure()
    {
        if (_chars is not null) return;

        lock (Gate)
        {
            if (_chars is not null) return;

            var chars = new Dictionary<char, string>(7000);
            var words = new Dictionary<string, string>(900);

            try
            {
                var text = Services.EmbeddedAssets.ReadText("pinyin.tsv");

                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    var tab = line.IndexOf('\t');
                    if (tab <= 0) continue;

                    var key = line[..tab];
                    var value = line[(tab + 1)..].Trim();

                    if (key.Length == 1) chars[key[0]] = value;
                    else if (key.Length <= MaxWordLength) words[key] = value;
                }
            }
            catch (Exception ex)
            {
                
                AppLog.Warn("submit", "拼音表读取失败：" + ex.Message);
            }

            _words = words;
            _chars = chars;
        }
    }
}
