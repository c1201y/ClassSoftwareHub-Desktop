using System;
using System.IO;
using System.Text.Json;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.License;













public sealed record LicenseTicket(string Token, long ExpiresAt, string Device, long ActivatedAt = 0)
{
    
    public bool IsExpired => ExpiresAt > 0
        && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= ExpiresAt;

    
    public DateTimeOffset? ExpiresAtLocal => ExpiresAt > 0
        ? DateTimeOffset.FromUnixTimeSeconds(ExpiresAt).ToLocalTime()
        : null;

    
    public DateTimeOffset? ActivatedAtLocal => ActivatedAt > 0
        ? DateTimeOffset.FromUnixTimeSeconds(ActivatedAt).ToLocalTime()
        : null;
}



















public static class LicenseStore
{
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string Path => System.IO.Path.Combine(AppPaths.DataDir, "license.json");

    
    
    
    public static LicenseTicket? Current
    {
        get
        {
            lock (Gate)
            {
                var ticket = ReadRaw();
                if (ticket is null) return null;

                if (!string.Equals(ticket.Device, DeviceFingerprint.Value, StringComparison.Ordinal))
                {
                    AppLog.Info("license", "本机保存的令牌是别的设备签发的，已忽略");
                    return null;
                }

                if (ticket.IsExpired)
                {
                    AppLog.Info("license", $"令牌已于 {ticket.ExpiresAtLocal:yyyy-MM-dd HH:mm} 过期");
                    return null;
                }

                return ticket;
            }
        }
    }

    
    public static bool HasValid => Current is not null;

    
    public static bool Save(string? token, long expiresAt, string? device = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        lock (Gate)
        {
            try
            {
                var trimmed = token.Trim();

                
                
                var activatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var previous = ReadRaw();
                if (previous is not null
                    && string.Equals(previous.Token, trimmed, StringComparison.Ordinal)
                    && previous.ActivatedAt > 0)
                {
                    activatedAt = previous.ActivatedAt;
                }

                var ticket = new LicenseTicket(
                    trimmed,
                    expiresAt,
                    string.IsNullOrWhiteSpace(device) ? DeviceFingerprint.Value : device.Trim(),
                    activatedAt);

                Directory.CreateDirectory(AppPaths.DataDir);

                var temp = Path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(ticket, Json));

                
                File.Move(temp, Path, overwrite: true);

                AppLog.Info("license", $"令牌已保存（到期 {ticket.ExpiresAtLocal:yyyy-MM-dd HH:mm}）");
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Exception("license", ex, "保存授权令牌");
                return false;
            }
        }
    }

    
    public static void Clear()
    {
        lock (Gate)
        {
            try { if (File.Exists(Path)) File.Delete(Path); }
            catch (Exception ex) { AppLog.Exception("license", ex, "清除授权令牌"); }
        }
    }

    private static LicenseTicket? ReadRaw()
    {
        try
        {
            if (!File.Exists(Path)) return null;

            var text = File.ReadAllText(Path);
            if (string.IsNullOrWhiteSpace(text)) return null;

            var ticket = JsonSerializer.Deserialize<LicenseTicket>(text);

            
            if (ticket is null || string.IsNullOrWhiteSpace(ticket.Token)) return null;

            return ticket;
        }
        catch (Exception ex)
        {
            
            AppLog.Exception("license", ex, "读取授权令牌");
            try { File.Delete(Path); } catch { }
            return null;
        }
    }
}
