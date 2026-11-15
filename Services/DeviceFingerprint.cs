using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 设备指纹：给 <c>/api/oss-sign</c> 报一个「同一台机器长期不变、换机器就变」的短标识。
/// 服务端只在登记表里存它的哈希（用来按人追溯与清理孤儿对象），**不存原始信号**。
///
/// 组成：本机持久随机 ID（存数据目录，删掉就换一个新的 —— 与网页端 localStorage 里那个
/// 32 位随机 ID 同思路）＋ 机器名 / 用户名 / CPU 核数 / 系统版本。
///
/// ⚠️ 不需要跟网页端算出同一个值：服务端只把它当不透明标识，不跨端比对。
/// </summary>
public static class DeviceFingerprint
{
    private static string? _cached;
    private static readonly object Gate = new();

    /// <summary>32 位十六进制以内的稳定标识（进程内只算一次）。</summary>
    public static string Value
    {
        get
        {
            lock (Gate)
            {
                return _cached ??= Compute();
            }
        }
    }

    private static string Compute()
    {
        var parts = string.Join('|',
            PersistedId(),
            Environment.MachineName,
            Environment.UserName,
            Environment.ProcessorCount.ToString(),
            Environment.OSVersion.VersionString);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(parts));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string PersistedId()
    {
        var path = Path.Combine(Core.AppPaths.DataDir, "device-id.txt");
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length == 32 && existing.All(Uri.IsHexDigit)) return existing;
            }

            var fresh = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            Directory.CreateDirectory(Core.AppPaths.DataDir);
            File.WriteAllText(path, fresh);
            return fresh;
        }
        catch
        {
            // 写不进去（只读盘 / 权限不足）就退回「本次运行内有效」——少一点稳定性，不影响上传
            return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        }
    }
}
