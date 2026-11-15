using System;
using System.Collections.Generic;
using System.Text;

namespace ClassSoftwareHub.Desktop.Core.Dictionary;

/// <summary>词条来自哪个源。存进设置的值也是这几个，改字面量等于改存档。</summary>
public static class DictSources
{
    /// <summary>本地词库（随应用分发的内置词库）。</summary>
    public const string Offline = "offline";

    /// <summary>有道词典（<c>dict.youdao.com/jsonapi</c>），中文释义最全，在线首选。</summary>
    public const string Youdao = "youdao";

    /// <summary>Free Dictionary（<c>api.dictionaryapi.dev</c>），只有英文释义，作为在线兜底。</summary>
    public const string FreeDictionary = "freedict";
}

/// <summary>用户选的词典源优先级（存进 <c>AppSettings.DictionarySource</c>）。</summary>
public static class DictSourceModes
{
    /// <summary>先查本地，没有再去在线。默认。</summary>
    public const string OfflineFirst = "offline-first";

    /// <summary>先查在线，失败或查不到再用本地。</summary>
    public const string OnlineFirst = "online-first";

    /// <summary>只查本地，完全不联网。</summary>
    public const string OfflineOnly = "offline-only";

    /// <summary>只查在线。</summary>
    public const string OnlineOnly = "online-only";

    public static bool IsValid(string? mode) =>
        mode is OfflineFirst or OnlineFirst or OfflineOnly or OnlineOnly;

    /// <summary>老存档、手改坏了的值一律归到默认档。</summary>
    public static string Normalize(string? mode) => IsValid(mode) ? mode! : OfflineFirst;

    public static bool UsesOnline(string mode) => mode is OnlineFirst or OnlineOnly;

    public static bool UsesOffline(string mode) => mode is OfflineFirst or OfflineOnly;
}

/// <summary>
/// 本地词库的行格式：<c>单词 \t 音标 \t 释义1 \t 释义2 …</c>
///
/// <para>
/// 为什么用制表符而不是 JSON：这份词库有几万行、每行一条，JSON 光是括号和引号就要多出三成体积，
/// 而它要跟着安装包一起分发。解析也只是 <c>Split('\t')</c>，几万行在手机上都能瞬间跑完。
/// </para>
/// <para>
/// 为什么释义要按字段拆开而不是塞一个长串：一个词常常有五六条不同词性的释义，
/// 拆成字段后界面可以逐条排版（词性一个标签、正文一段），不用在显示时再去猜哪里该断开。
/// </para>
/// <para>
/// ⚠️ 纯函数、不碰文件也不碰 WinUI —— 测试工程直接链接这个文件（见 <c>E:\Classsoftwarehub测试</c>）。
/// </para>
/// </summary>
public static class LexiconFormat
{
    /// <summary>
    /// 解析一行。空行、注释行（<c>#</c> 开头）、字段不足的行一律返回 null —— 词库是外部数据，
    /// 遇到坏行只能跳过，不能让整份词库因此打不开。
    /// </summary>
    public static DictEntry? ParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        if (line[0] == '#') return null;

        var parts = line.Split('\t');
        if (parts.Length < 2) return null;

        var word = parts[0].Trim();
        if (word.Length == 0) return null;

        var phonetic = parts[1].Trim();
        if (phonetic.Length > 0 && !phonetic.StartsWith('/')) phonetic = "/" + phonetic + "/";

        var senses = new List<DictSense>(parts.Length - 2);
        for (var i = 2; i < parts.Length; i++)
        {
            var raw = parts[i].Trim();
            if (raw.Length == 0) continue;
            senses.Add(SplitPos(raw));
        }

        if (senses.Count == 0) return null;

        return new DictEntry
        {
            Word = word,
            Phonetic = phonetic,
            Source = DictSources.Offline,
            Senses = senses
        };
    }

    /// <summary>
    /// 把 <c>"n. 招呼，问候"</c> 拆成词性 <c>n.</c> + 正文。
    ///
    /// <para>
    /// 只认「英文字母点 + 空格」这一种形态（n. / vt. / adj. / abbr.），而且点后面必须真的跟了空格，
    /// 否则像 <c>e.g. </c> 这类正文里的缩写会被误当成词性吃掉。拆不出来就整串当正文。
    /// </para>
    /// </summary>
    public static DictSense SplitPos(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return new DictSense();

        var dot = trimmed.IndexOf(". ", StringComparison.Ordinal);
        if (dot is > 0 and <= 8)
        {
            var pos = trimmed[..(dot + 1)];
            if (IsPosToken(pos))
            {
                var body = trimmed[(dot + 2)..].Trim();
                if (body.Length > 0) return new DictSense { Pos = pos, Text = body };
            }
        }

        return new DictSense { Text = trimmed };
    }

    /// <summary>词性标记只允许 1~5 个小写字母加点（含 <c>abbr.</c> <c>vt.</c> 这种）。</summary>
    private static bool IsPosToken(string token)
    {
        if (token.Length is < 2 or > 6) return false;
        if (token[^1] != '.') return false;

        for (var i = 0; i < token.Length - 1; i++)
        {
            var c = token[i];
            if (c is < 'a' or > 'z') return false;
        }

        return true;
    }

    /// <summary>查询词归一：去首尾空白、小写。词库里全小写存，大小写不同的同一个词不该查两次。</summary>
    public static string NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "";
        return query.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// 查询串里有没有汉字 —— 有就走中译英，没有就走英译中。
    ///
    /// <para>
    /// 只认基本汉字区（U+4E00–U+9FFF）与扩展 A（U+3400–U+4DBF）。够用且不会误判：
    /// 全角标点、日文假名、韩文都不在这个区间里，混进来也不会被当成中文词。
    /// </para>
    /// </summary>
    public static bool HasChinese(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        foreach (var c in text)
        {
            if (c is >= '\u4E00' and <= '\u9FFF') return true;
            if (c is >= '\u3400' and <= '\u4DBF') return true;
        }

        return false;
    }

    /// <summary>一行写回词库（日后要做"把在线查到的词补进本地"时用）。</summary>
    public static string BuildLine(DictEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append(entry.Word).Append('\t');
        sb.Append(entry.Phonetic.Trim('/')).Append('\t');
        for (var i = 0; i < entry.Senses.Count; i++)
        {
            if (i > 0) sb.Append('\t');
            var s = entry.Senses[i];
            if (s.Pos.Length > 0) sb.Append(s.Pos).Append(' ');
            sb.Append(s.Text);
        }

        return sb.ToString();
    }
}
