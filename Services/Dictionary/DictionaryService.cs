using System;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core.Dictionary;

namespace ClassSoftwareHub.Desktop.Services.Dictionary;

/// <summary>
/// 查阅的总入口：决定这次是"查一个词"还是"翻一整句"，再按用户在设置里选的词源优先级去查。
///
/// <para>
/// 三条路：
/// ① <see cref="QueryModes.Word"/> —— 只查词；
/// ② <see cref="QueryModes.Sentence"/> —— 只翻译；
/// ③ <see cref="QueryModes.Auto"/> —— 先看输入像不像一句话（<see cref="QueryModes.LooksLikeSentence"/>），
///    像就翻译；不像就查词，**查不到时再退回翻译一次**（"a bit" 这种两词短语查不到、翻出来反而有用）。
/// </para>
/// <para>
/// 词源四档优先级（<see cref="DictSourceModes"/>）：离线优先 / 在线优先 / 仅离线 / 仅在线。默认离线优先 ——
/// 查词是高频、短平快的动作，先问本地能立刻出结果；而且教室里网络时好时坏，先本地最稳。
/// </para>
/// <para>
/// ⚠️ 离线优先时，如果在线查到了本地没有的词，会顺手补进用户词库（<see cref="LocalLexicon.Remember"/>）。
///    这样"离线"这一档不是死词表，用户查过的词会沉淀下来。
/// </para>
/// <para>
/// ⚠️ 句子翻译**只能联网**：本地词库只有词条，没有翻译模型。「仅离线」时直接说清楚，
///    而不是让用户等一个空结果。
/// </para>
/// </summary>
public static class DictionaryService
{
    /// <summary>查一个词或翻一句话。<paramref name="mode"/> 取 <see cref="QueryModes"/> 里的值。</summary>
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

        // 词没查到：多词短语或中文短句，翻一下往往就有答案
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

    // ─────────────────────────── 查词 ───────────────────────────

    private static async Task<DictResult> LookupWordAsync(string word)
    {
        var mode = DictSourceModes.Normalize(App.Settings.Current.DictionarySource);

        // 中译英：内置词库只存"英文词 → 中文释义"，做不了反向查（中文要先分词，靠词库解不了）。
        // 所以这一支**永远走在线**，「仅离线」时直接说清楚，而不是让用户等一个空结果。
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
            // 在线查到了、本地还没有 —— 存一份，下次断网也查得到
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
