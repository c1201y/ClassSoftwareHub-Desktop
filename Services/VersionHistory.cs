using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>一条"这台电脑运行过的版本"记录。</summary>
public sealed class VersionRecord
{
    public string Version { get; set; } = "";
    public string Channel { get; set; } = "";
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
}

/// <summary>
/// 本机版本记录：更新页的「历史版本」读的就是它 —— 不是仓库的 Release 列表。
/// 应用每次启动调一次 Record()：新版本记一条，老版本只刷新"最近一次运行"时间。
/// </summary>
public static class VersionHistory
{
    public static string FilePath => Path.Combine(AppPaths.DataDir, "version-history.json");

    public static List<VersionRecord> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<VersionRecord>();
            var list = JsonSerializer.Deserialize<List<VersionRecord>>(File.ReadAllText(FilePath)) ?? new List<VersionRecord>();
            return list
                .Where(r => !string.IsNullOrWhiteSpace(r.Version))
                .OrderByDescending(r => r.LastSeen)
                .ToList();
        }
        catch
        {
            return new List<VersionRecord>();
        }
    }

    public static void Record(string? version = null, string? channel = null)
    {
        try
        {
            version = string.IsNullOrWhiteSpace(version) ? ShellConfig.ShellVersion : version;
            channel = string.IsNullOrWhiteSpace(channel) ? ShellConfig.DefaultUpdateChannel : channel;

            var list = Load();
            var now = DateTime.Now;
            var hit = list.FirstOrDefault(r => string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
            {
                list.Insert(0, new VersionRecord
                {
                    Version = version!,
                    Channel = channel!,
                    FirstSeen = now,
                    LastSeen = now,
                });
            }
            else
            {
                hit.Channel = channel!;
                hit.LastSeen = now;
            }

            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            /* 记不上不影响使用 */
        }
    }

    /// <summary>界面上显示的版本号：带 dv 前缀。</summary>
    public static string Display(VersionRecord record)
        => ShellConfig.VersionPrefix + record.Version;
}
