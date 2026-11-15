using System;
using System.Collections.Generic;
using System.Text;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 汉字 → 拼音（无声调）对照表，用于把软件名生成软件 ID。
///
/// <para>
/// 数据是内嵌资源 <c>Assets\Pinyin\pinyin.tsv</c>：6,763 个 GB2312 常用字 + 853 个多音字词
/// （`音乐` → yinyue、`银行` → yinhang），每行「字或词 \t 拼音」。
/// 源 pinyin-data（ISC 协议），生成方式见同目录 README.md。
/// </para>
///
/// <para>
/// 先按最长四字词匹配、再退回单字：多音字只按单字取首读音会把「音乐」读成 yinle，
/// 词表就是为了修这一类。⚠️ 懒加载一次、之后只读，多页面并发读不需要额外加锁。
/// </para>
/// </summary>
public static class PinyinTable
{
    /// <summary>词组最长几个字。词表里最长的是四字词。</summary>
    private const int MaxWordLength = 4;

    private static Dictionary<char, string>? _chars;
    private static Dictionary<string, string>? _words;
    private static readonly object Gate = new();

    /// <summary>取单字拼音；表里没有返回空串。</summary>
    public static string Of(char ch)
    {
        Ensure();
        return _chars!.TryGetValue(ch, out var pinyin) ? pinyin : "";
    }

    /// <summary>
    /// 把一段**纯汉字**转成拼音，词表能匹配的先按词走，其余逐字。
    /// 表里查不到的字直接跳过（生僻字不该让整个 ID 生成失败）。
    /// </summary>
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

    /// <summary>是否是需要转拼音的汉字（基本区，够覆盖软件名）。</summary>
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
                // 读不出来就当表是空的：ID 生成退化成「只取英文数字」，不影响提交
                AppLog.Warn("submit", "拼音表读取失败：" + ex.Message);
            }

            _words = words;
            _chars = chars;
        }
    }
}
