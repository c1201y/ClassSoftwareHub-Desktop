using System;
using System.IO;
using System.Text.Json;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.License;

/// <summary>本机的授权凭证。</summary>
/// <param name="Token">服务端签发的授权令牌，原样带在 <c>X-CSH-Lic</c> 头里。</param>
/// <param name="ExpiresAt">到期时间（Unix 秒，0 = 长期）。</param>
/// <param name="Device">签发时用的设备码。**换了设备码这份就作废**。</param>
public sealed record LicenseTicket(string Token, long ExpiresAt, string Device)
{
    /// <summary>过期了没有。0 表示长期有效。</summary>
    public bool IsExpired => ExpiresAt > 0
        && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= ExpiresAt;

    /// <summary>到期时间，0 给 null。</summary>
    public DateTimeOffset? ExpiresAtLocal => ExpiresAt > 0
        ? DateTimeOffset.FromUnixTimeSeconds(ExpiresAt).ToLocalTime()
        : null;
}

/// <summary>
/// 授权令牌的本地存放（<c>%LOCALAPPDATA%\ClassSoftwareHub\license.json</c>）。
///
/// <para>
/// 两条规矩，都是从后端文档抄回来的：
/// </para>
/// <list type="number">
///   <item><b>令牌和设备码绑死</b>：服务端验签时会比对设备码，换了机器令牌就无效。
///         所以读的时候一定要核对，不能只看有没有文件 —— 否则换机之后会一直拿着废令牌去撞 403。</item>
///   <item><b>写入要原子</b>：文档的故障表里有一条「官方包偶发 403，重启后恢复 —— 令牌被旧值覆盖」。
///         先写临时文件再整体替换，避免半个 JSON 落盘。</item>
/// </list>
///
/// <para>
/// ⛔ 这里存的是**服务端给的凭证**，不是密钥，没有加密的必要 —— 加密了也挡不住本机用户，
/// 反而会让"换机重新激活"这种事变得没法排查。2026-11-04。
/// </para>
/// </summary>
public static class LicenseStore
{
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string Path => System.IO.Path.Combine(AppPaths.DataDir, "license.json");

    /// <summary>
    /// 当前可用的令牌；没有、过期、或设备码对不上都返回 null。
    /// </summary>
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

    /// <summary>有没有一份**能用的**令牌。</summary>
    public static bool HasValid => Current is not null;

    /// <summary>保存服务端下发的令牌。空值直接忽略（服务端没下发就别动已有的）。</summary>
    public static bool Save(string? token, long expiresAt, string? device = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        lock (Gate)
        {
            try
            {
                var ticket = new LicenseTicket(
                    token.Trim(),
                    expiresAt,
                    string.IsNullOrWhiteSpace(device) ? DeviceFingerprint.Value : device.Trim());

                Directory.CreateDirectory(AppPaths.DataDir);

                var temp = Path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(ticket, Json));

                // 整体替换：写到一半崩了也不会留下半个文件
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

    /// <summary>清掉本地凭证（换激活码、或用户主动退出授权时用）。</summary>
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

            // 字段缺失的旧文件当成没有
            if (ticket is null || string.IsNullOrWhiteSpace(ticket.Token)) return null;

            return ticket;
        }
        catch (Exception ex)
        {
            // 文件坏了不该让整个功能不可用，删掉重来
            AppLog.Exception("license", ex, "读取授权令牌");
            try { File.Delete(Path); } catch { }
            return null;
        }
    }
}
