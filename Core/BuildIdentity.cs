using System;
using System.Linq;
using System.Reflection;

namespace ClassSoftwareHub.Desktop.Core;





















public static class BuildIdentity
{
    private const string MetadataKey = "CshBuildStamp";

    private static readonly Lazy<Stamp> Cached = new(Read);

    
    public static string BuildId => Cached.Value.BuildId;

    
    public static string Signature => Cached.Value.Signature;

    
    public static bool IsOfficial => Signature.Length > 0;

    
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

            
            var parts = text.Split('|');
            if (parts.Length < 4 || parts[0] != "v1") return fallback;

            var buildId = parts[1].Trim();
            var signature = parts[2].Trim();

            
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
