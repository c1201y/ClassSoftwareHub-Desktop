using System;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core.Dictionary;

namespace ClassSoftwareHub.Desktop.Services.Dictionary;
























public static class DictionaryService
{
    
    public static async Task<DictResult> LookupAsync(string? query, string mode = QueryModes.Auto)
    {
        var raw = (query ?? "").Trim();
        if (raw.Length == 0) return new DictResult { Note = "请输入要查询的单词，或要翻译的句子。" };

        if (mode == QueryModes.Sentence) return await TranslationService.TranslateAsync(raw);

        if (mode == QueryModes.Auto && QueryModes.LooksLikeSentence(raw))
            return await TranslationService.TranslateAsync(raw);

        var word = LexiconFormat.NormalizeQuery(raw);
        var result = await LookupWordAsync(word);
        if (result.Found) return result;

        
        if (mode == QueryModes.Auto && (LexiconFormat.HasChinese(word) || word.Contains(' ')))
        {
            var translated = await TranslationService.TranslateAsync(raw);
            if (translated.Found)
            {
                translated = new DictResult
                {
                    Translation = translated.Translation,
                    Note = "词库里没有这一条，以下是整句翻译的结果。"
                };
                return translated;
            }
        }

        return result;
    }

    

    private static async Task<DictResult> LookupWordAsync(string word)
    {
        var mode = DictSourceModes.Normalize(App.Settings.Current.DictionarySource);

        
        
        if (LexiconFormat.HasChinese(word))
        {
            if (mode == DictSourceModes.OfflineOnly)
                return new DictResult { Note = "中译英需要联网，当前的词源优先级是「仅离线」。可以改成「离线优先」或「在线优先」。" };

            return await OnlineDictionary.LookupAsync(word);
        }

        return mode switch
        {
            DictSourceModes.OfflineOnly => await LookupOfflineOnlyAsync(word),
            DictSourceModes.OnlineOnly => await LookupOnlineOnlyAsync(word),
            DictSourceModes.OnlineFirst => await LookupOnlineFirstAsync(word),
            _ => await LookupOfflineFirstAsync(word)
        };
    }

    private static async Task<DictResult> LookupOfflineOnlyAsync(string word)
    {
        await LocalLexicon.EnsureLoadedAsync();

        var hit = LocalLexicon.Find(word);
        if (hit is not null) return new DictResult { Entry = hit };

        return new DictResult { Note = "本地词库中没有这个词。可以改用在线词典，或把优先级切到「在线优先」。" };
    }

    private static async Task<DictResult> LookupOnlineOnlyAsync(string word)
    {
        var result = await OnlineDictionary.LookupAsync(word);
        if (result.Found) return new DictResult { Entry = result.Entry };

        return new DictResult { Note = result.Note, Error = result.Error };
    }

    private static async Task<DictResult> LookupOnlineFirstAsync(string word)
    {
        var online = await OnlineDictionary.LookupAsync(word);
        if (online.Found)
        {
            
            LocalLexicon.Remember(online.Entry!);
            return new DictResult { Entry = online.Entry, Note = online.Note };
        }

        await LocalLexicon.EnsureLoadedAsync();
        var local = LocalLexicon.Find(word);
        if (local is not null)
            return new DictResult { Entry = local, Note = "在线词典没有结果，以下为本地词库的释义。" };

        return new DictResult { Note = online.Note, Error = online.Error };
    }

    private static async Task<DictResult> LookupOfflineFirstAsync(string word)
    {
        await LocalLexicon.EnsureLoadedAsync();

        var local = LocalLexicon.Find(word);
        if (local is not null) return new DictResult { Entry = local };

        var online = await OnlineDictionary.LookupAsync(word);
        if (online.Found)
        {
            LocalLexicon.Remember(online.Entry!);
            return new DictResult { Entry = online.Entry, Note = "本地词库中没有这个词，以下为在线词典的释义（已存入本地）。" };
        }

        return new DictResult
        {
            Note = online.Note.Length > 0 ? online.Note : "没有查到这个词，请检查拼写。",
            Error = online.Error
        };
    }
}
