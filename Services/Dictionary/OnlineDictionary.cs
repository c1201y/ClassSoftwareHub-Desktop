using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Core.Dictionary;

namespace ClassSoftwareHub.Desktop.Services.Dictionary;















public static class OnlineDictionary
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private const string YoudaoUrl = "https://dict.youdao.com/jsonapi?q=";
    private const string FreeUrl = "https://api.dictionaryapi.dev/api/v2/entries/en/";

    
    
    
    
    
    public static Task<DictResult> LookupAsync(string word) =>
        LexiconFormat.HasChinese(word) ? LookupChineseAsync(word) : LookupEnglishAsync(word);

    
    private static async Task<DictResult> LookupEnglishAsync(string word)
    {
        var note = "";

        
        try
        {
            var entry = await QueryYoudaoAsync(word);
            if (entry is { IsEmpty: false }) return new DictResult { Entry = entry };
            note = "有道词典未收录该词";
        }
        catch (Exception ex)
        {
            AppLog.Warning("dict", $"有道查询失败：{ex.Message}");
            note = "有道词典暂时连不上";
        }

        
        try
        {
            var entry = await QueryFreeAsync(word);
            if (entry is { IsEmpty: false })
                return new DictResult { Entry = entry, Note = note.Length > 0 ? note + "，已改用英文词典" : "" };
            note = note.Length > 0 ? note + "；英文词典也未收录" : "英文词典未收录该词";
        }
        catch (Exception ex)
        {
            AppLog.Warning("dict", $"Free Dictionary 查询失败：{ex.Message}");
            note = note.Length > 0 ? note + "；英文词典也连不上" : "英文词典暂时连不上";
        }

        return new DictResult { Note = note, Error = note };
    }

    
    
    
    
    
    
    
    
    
    private static async Task<DictResult> LookupChineseAsync(string word)
    {
        string note;

        try
        {
            var entry = await QueryYoudaoChineseAsync(word);
            if (entry is { IsEmpty: false }) return new DictResult { Entry = entry };
            note = "没有查到这个词的英文译法";
        }
        catch (Exception ex)
        {
            AppLog.Warning("dict", $"中译英查询失败：{ex.Message}");
            note = "词典服务暂时连不上";
        }

        return new DictResult { Note = note, Error = note };
    }

    private static async Task<DictEntry?> QueryYoudaoChineseAsync(string word)
    {
        var json = await Http.GetStringAsync(YoudaoUrl + Uri.EscapeDataString(word));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var words = new List<string>();

        
        if (root.TryGetProperty("ce", out var ce)
            && ce.TryGetProperty("word", out var ceWords)
            && ceWords.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in ceWords.EnumerateArray())
            {
                if (!w.TryGetProperty("trs", out var trs) || trs.ValueKind != JsonValueKind.Array) continue;

                foreach (var trsItem in trs.EnumerateArray())
                {
                    if (!trsItem.TryGetProperty("tr", out var trList) || trList.ValueKind != JsonValueKind.Array) continue;

                    foreach (var tr in trList.EnumerateArray())
                    {
                        if (!tr.TryGetProperty("l", out var l)) continue;
                        if (!l.TryGetProperty("i", out var items) || items.ValueKind != JsonValueKind.Array) continue;

                        foreach (var item in items.EnumerateArray())
                        {
                            var text = item.ValueKind switch
                            {
                                JsonValueKind.String => item.GetString(),
                                JsonValueKind.Object when item.TryGetProperty("#text", out var t) => t.GetString(),
                                _ => null
                            };

                            if (string.IsNullOrWhiteSpace(text)) continue;
                            text = text!.Trim();

                            
                            if (!ContainsAsciiLetter(text)) continue;
                            if (words.Contains(text, StringComparer.OrdinalIgnoreCase)) continue;

                            words.Add(text);
                            if (words.Count >= 8) break;
                        }
                    }
                }
            }
        }

        
        if (words.Count < 3 && root.TryGetProperty("web_trans", out var web)
            && web.TryGetProperty("web-translation", out var webList)
            && webList.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in webList.EnumerateArray())
            {
                if (!item.TryGetProperty("trans", out var trans) || trans.ValueKind != JsonValueKind.Array) continue;

                var taken = 0;
                foreach (var t in trans.EnumerateArray())
                {
                    if (taken >= 2 || words.Count >= 8) break;
                    if (!t.TryGetProperty("value", out var v)) continue;

                    var text = v.GetString();
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    text = text!.Trim();
                    if (!ContainsAsciiLetter(text)) continue;
                    if (words.Contains(text, StringComparer.OrdinalIgnoreCase)) continue;

                    words.Add(text);
                    taken++;
                }
            }
        }

        if (words.Count == 0) return null;

        var senses = new List<DictSense>(words.Count);
        foreach (var w in words) senses.Add(new DictSense { Text = w });

        return new DictEntry
        {
            Word = word,
            Source = DictSources.Youdao,
            Direction = DictDirections.ZhToEn,
            Senses = senses
        };
    }

    private static bool ContainsAsciiLetter(string text)
    {
        foreach (var c in text)
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') return true;

        return false;
    }

    

    private static async Task<DictEntry?> QueryYoudaoAsync(string word)
    {
        var json = await Http.GetStringAsync(YoudaoUrl + Uri.EscapeDataString(word));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var phonetic = "";
        var senses = new List<DictSense>();

        
        if (root.TryGetProperty("ec", out var ec)
            && ec.TryGetProperty("word", out var ecWords)
            && ecWords.ValueKind == JsonValueKind.Array
            && ecWords.GetArrayLength() > 0)
        {
            var w = ecWords[0];

            try
            {
                
                if (w.TryGetProperty("usphone", out var us) && us.GetString() is { Length: > 0 } usText)
                    phonetic = usText;
                else if (w.TryGetProperty("ukphone", out var uk) && uk.GetString() is { Length: > 0 } ukText)
                    phonetic = ukText;
            }
            catch
            {
                
            }

            try
            {
                if (w.TryGetProperty("trs", out var trs) && trs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var trsItem in trs.EnumerateArray())
                    {
                        if (!trsItem.TryGetProperty("tr", out var trList) || trList.ValueKind != JsonValueKind.Array) continue;

                        foreach (var tr in trList.EnumerateArray())
                        {
                            if (!tr.TryGetProperty("l", out var l)) continue;
                            if (!l.TryGetProperty("i", out var items) || items.ValueKind != JsonValueKind.Array) continue;

                            foreach (var item in items.EnumerateArray())
                            {
                                var text = item.GetString();
                                if (string.IsNullOrWhiteSpace(text)) continue;
                                senses.Add(LexiconFormat.SplitPos(text));
                            }
                        }
                    }
                }
            }
            catch
            {
                
            }
        }

        
        if (phonetic.Length == 0
            && root.TryGetProperty("simple", out var simple)
            && simple.TryGetProperty("word", out var simpleWords)
            && simpleWords.ValueKind == JsonValueKind.Array
            && simpleWords.GetArrayLength() > 0)
        {
            var w = simpleWords[0];
            try
            {
                if (w.TryGetProperty("usphone", out var us) && us.GetString() is { Length: > 0 } usText) phonetic = usText;
                else if (w.TryGetProperty("ukphone", out var uk) && uk.GetString() is { Length: > 0 } ukText) phonetic = ukText;
            }
            catch
            {
                
            }
        }

        if (senses.Count == 0) return null;

        var examples = ReadYoudaoSentences(root);

        return new DictEntry
        {
            Word = word,
            Phonetic = phonetic.Length > 0 ? "/" + phonetic.Trim('/') + "/" : "",
            Source = DictSources.Youdao,
            Senses = senses,
            Examples = examples
        };
    }

    
    private static List<DictExample> ReadYoudaoSentences(JsonElement root)
    {
        var list = new List<DictExample>();
        try
        {
            if (!root.TryGetProperty("blng_sents_part", out var part)) return list;
            if (!part.TryGetProperty("sentence-pair", out var pairs) || pairs.ValueKind != JsonValueKind.Array) return list;

            foreach (var pair in pairs.EnumerateArray())
            {
                if (list.Count >= 5) break;

                var en = pair.TryGetProperty("sentence", out var s) ? s.GetString() : null;
                var zh = pair.TryGetProperty("sentence-translation", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(en)) continue;

                list.Add(new DictExample
                {
                    En = StripTags(en!).Trim(),
                    Zh = StripTags(zh ?? "").Trim()
                });
            }
        }
        catch
        {
            
        }

        return list;
    }

    
    private static string StripTags(string text) => text.Replace("<b>", "", StringComparison.OrdinalIgnoreCase)
                                                        .Replace("</b>", "", StringComparison.OrdinalIgnoreCase);

    

    private static async Task<DictEntry?> QueryFreeAsync(string word)
    {
        var json = await Http.GetStringAsync(FreeUrl + Uri.EscapeDataString(word));
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return null;

        var first = doc.RootElement[0];
        var phonetic = "";
        var senses = new List<DictSense>();

        try
        {
            if (first.TryGetProperty("phonetic", out var ph) && ph.GetString() is { Length: > 0 } phText)
            {
                phonetic = phText;
            }
            else if (first.TryGetProperty("phonetics", out var phonetics) && phonetics.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in phonetics.EnumerateArray())
                {
                    if (p.TryGetProperty("text", out var t) && t.GetString() is { Length: > 0 } text)
                    {
                        phonetic = text;
                        break;
                    }
                }
            }
        }
        catch
        {
            
        }

        try
        {
            if (first.TryGetProperty("meanings", out var meanings) && meanings.ValueKind == JsonValueKind.Array)
            {
                foreach (var meaning in meanings.EnumerateArray())
                {
                    var pos = meaning.TryGetProperty("partOfSpeech", out var p) ? p.GetString() ?? "" : "";
                    if (!meaning.TryGetProperty("definitions", out var defs) || defs.ValueKind != JsonValueKind.Array) continue;

                    var taken = 0;
                    foreach (var def in defs.EnumerateArray())
                    {
                        if (taken >= 3) break;
                        var text = def.TryGetProperty("definition", out var d) ? d.GetString() : null;
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        senses.Add(new DictSense { Pos = pos.Length > 0 ? pos + "." : "", Text = text!.Trim() });
                        taken++;
                    }
                }
            }
        }
        catch
        {
            
        }

        if (senses.Count == 0) return null;

        return new DictEntry
        {
            Word = word,
            Phonetic = phonetic.Length > 0 ? "/" + phonetic.Trim('/') + "/" : "",
            Source = DictSources.FreeDictionary,
            Senses = senses
        };
    }
}
