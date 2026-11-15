using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ClassSoftwareHub.Desktop.Services;










public static class DeviceFingerprint
{
    private static string? _cached;
    private static readonly object Gate = new();

    
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
            
            return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        }
    }
}
