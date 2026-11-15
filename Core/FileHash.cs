using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 文件哈希的算法与比对规则（纯函数，不碰界面）。
///
/// <para>
/// 2026-10-30 从 <c>Pages/Tools/EncodingToolPage</c> 抽出来：那边原本有一个私有的
/// <c>HashFile</c>，新的「哈希校验」悬浮窗要算同一套值，再复制一份就会出现两处实现
/// （改一处忘一处，正是代码审计里列的那类问题）。抽成这里之后两处共用，
/// 而且纯逻辑能直接挂进 E 盘测试工程跑。
/// </para>
///
/// <para>
/// ⚠️ 一次读完四种算法、**流式**处理：1 MB 一块，多大的文件都不吃内存。
///    分开算四遍会把文件读四遍，机械盘上很慢，所以采用 IncrementalHash 并行喂数据。
/// </para>
/// </summary>
public static class FileHash
{
    /// <summary>算哪几种。顺序 = 界面上从上到下的顺序，也是比对时优先命中的顺序（越靠前越短）。</summary>
    public static readonly string[] Algorithms = { "MD5", "SHA-1", "SHA-256", "SHA-512" };

    /// <summary>算法名 → 十六进制位数。用来按长度猜「用户粘的是哪一种」。</summary>
    private static readonly Dictionary<string, int> HexLengths = new()
    {
        ["MD5"] = 32,
        ["SHA-1"] = 40,
        ["SHA-256"] = 64,
        ["SHA-512"] = 128,
    };

    /// <summary>
    /// 算出四种哈希，键是算法名、值是**小写十六进制**。
    /// <paramref name="token"/> 用于「用户换了个文件」时作废上一轮计算。
    /// </summary>
    public static Dictionary<string, string> Compute(string path, System.Threading.CancellationToken token = default)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);

        var buffer = new byte[1 << 20];

        // FileShare.ReadWrite：别的程序正开着这个文件（比如刚下载完还在被安全软件扫）也能读。
        // FileOptions.SequentialScan：告诉系统「顺序读」，大文件能拿到预读。
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                       buffer.Length, FileOptions.SequentialScan))
        {
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                var span = buffer.AsSpan(0, read);
                md5.AppendData(span);
                sha1.AppendData(span);
                sha256.AppendData(span);
                sha512.AppendData(span);
            }
        }

        return new Dictionary<string, string>
        {
            ["MD5"] = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant(),
            ["SHA-1"] = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(),
            ["SHA-256"] = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant(),
            ["SHA-512"] = Convert.ToHexString(sha512.GetHashAndReset()).ToLowerInvariant(),
        };
    }

    /// <summary>
    /// 归一化用户粘进来的校验值：去掉空白、连字符、冒号，统一小写。
    ///
    /// <para>
    /// ⚠️ 只做这几样，**不要**再「聪明」下去：不能去掉所有非十六进制字符，
    ///    否则一段错得离谱的文本会被归一化成空串，界面就会显示「请先填入校验值」，
    ///    用户以为程序没读到他的输入。宁可让它显示「不一致」。
    /// </para>
    /// <para>
    /// 网页上抄下来的校验值经常带 <c>-</c> 分组（<c>ab12-cd34-…</c>）或 <c>:</c>
    /// （<c>ab:12:cd:…</c>），手工比对时最容易被这些分隔符坑。
    /// </para>
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch) || ch == '-' || ch == ':') continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 拿用户填的值去比对四种哈希，命中就返回算法名（"SHA-256" 这种），一个都不中返回 null。
    /// <paramref name="hashes"/> 为 null 或空表示文件还没算完，同样返回 null。
    /// </summary>
    public static string? Match(string? expected, IReadOnlyDictionary<string, string>? hashes)
    {
        var want = Normalize(expected);
        if (want.Length == 0 || hashes is null || hashes.Count == 0) return null;

        foreach (var name in Algorithms)
        {
            if (!hashes.TryGetValue(name, out var got)) continue;
            if (got.Length > 0 && string.Equals(Normalize(got), want, StringComparison.Ordinal)) return name;
        }
        return null;
    }

    /// <summary>
    /// 按十六进制位数猜这段校验值属于哪种算法（用于界面提示「看起来这是 SHA-256」）。
    /// 认不出返回 null —— 长度对不上任何一类，通常意味着用户粘错了东西。
    /// </summary>
    public static string? GuessAlgorithm(string? raw)
    {
        var value = Normalize(raw);
        if (value.Length == 0) return null;

        // 长度对但混了非十六进制字符：不是哈希，别硬猜
        foreach (var ch in value)
        {
            var hex = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f');
            if (!hex) return null;
        }

        foreach (var pair in HexLengths)
            if (pair.Value == value.Length) return pair.Key;

        return null;
    }
}
