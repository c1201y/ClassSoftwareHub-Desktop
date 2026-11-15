using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;


public sealed class VersionRecord
{
    public string Version { get; set; } = "";
    public string Channel { get; set; } = "";
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
}





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
            
        }
    }

    
    public static string Display(VersionRecord record)
        => ShellConfig.VersionPrefix + record.Version;
}
