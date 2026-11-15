using System;
using System.Linq;
using System.Reflection;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 构建身份：本程序是「官方流水线打出来的包」还是「自己编译的社区版」。
///
/// <para>
/// 官方包的构建号与签名由打包流水线在**编译期**写进程序集元数据
/// （<c>-p:CshBuildStampB64=&lt;base64("v1|buildId|sig|issuedAt")&gt;</c>），
/// 运行时从 <c>AssemblyMetadata</c> 读回来。签名密钥只在流水线的 Secret 里，
/// **不在仓库、也不在客户端** —— 客户端只负责把签名原样带给服务端。
/// </para>
///
/// <para>
/// 读不到就是社区版：不带签名，服务端会按「需要激活」处理（见后端《防滥用接入指南》§2.4）。
/// 这条路径不报错、不打扰用户，界面上只体现为"未激活 / 待激活"。
/// </para>
///
/// <para>
/// ⛔ 不要把签名另存成随包分发的明文文件：放进程序集只是抬高复制门槛，
/// 它的价值在于"流水线签过"，不在于"藏得住"。2026-11-04。
/// </para>
/// </summary>
public static class BuildIdentity
{
    private const string MetadataKey = "CshBuildStamp";

    private static readonly Lazy<Stamp> Cached = new(Read);

    /// <summary>构建号，放在 <c>X-CSH-Build</c> 头里。官方包来自签名，社区版是本地拼的。</summary>
    public static string BuildId => Cached.Value.BuildId;

    /// <summary>构建签名（64 位十六进制）。空字符串 = 社区版，不带这个头。</summary>
    public static string Signature => Cached.Value.Signature;

    /// <summary>这个包是不是官方流水线产出的。</summary>
    public static bool IsOfficial => Signature.Length > 0;

    /// <summary>签名签发时间（UTC），读不到返回 null。</summary>
    public static DateTimeOffset? IssuedAt => Cached.Value.IssuedAt;

    private readonly record struct Stamp(string BuildId, string Signature, DateTimeOffset? IssuedAt);

    private static Stamp Read()
    {
        var fallback = new Stamp($"local-{ShellConfig.ShellVersion}", "", null);

        try
        {
            var raw = typeof(BuildIdentity).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => string.Equals(a.Key, MetadataKey, StringComparison.Ordinal))
                ?.Value;

            if (string.IsNullOrWhiteSpace(raw)) return fallback;

            var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(raw.Trim()));

            // "v1|buildId|sig|issuedAt"
            var parts = text.Split('|');
            if (parts.Length < 4 || parts[0] != "v1") return fallback;

            var buildId = parts[1].Trim();
            var signature = parts[2].Trim();

            // 构建号为空或含竖线的包一律按社区版处理：服务端那边也过不了
            if (buildId.Length == 0 || buildId.Length > 96 || buildId.Contains('|')) return fallback;

            DateTimeOffset? issued = null;
            if (DateTimeOffset.TryParse(parts[3].Trim(), out var at)) issued = at;

            return new Stamp(buildId, signature, issued);
        }
        catch (Exception ex)
        {
            AppLog.Warn("license", $"读取构建签名失败，按社区版处理：{ex.Message}");
            return fallback;
        }
    }
}
