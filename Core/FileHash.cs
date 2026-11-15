using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ClassSoftwareHub.Desktop.Core;
















public static class FileHash
{
    
    public static readonly string[] Algorithms = { "MD5", "SHA-1", "SHA-256", "SHA-512" };

    
    private static readonly Dictionary<string, int> HexLengths = new()
    {
        ["MD5"] = 32,
        ["SHA-1"] = 40,
        ["SHA-256"] = 64,
        ["SHA-512"] = 128,
    };

    
    
    
    
    public static Dictionary<string, string> Compute(string path, System.Threading.CancellationToken token = default)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);

        var buffer = new byte[1 << 20];

        
        
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

    
    
    
    
    public static string? GuessAlgorithm(string? raw)
    {
        var value = Normalize(raw);
        if (value.Length == 0) return null;

        
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
