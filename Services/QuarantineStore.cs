using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;


public sealed record QuarantineEntry(
    string Dir,
    string Stamp,
    DateTime Time,
    string App,
    int Removed,
    int Failed,
    bool HasManifest)
{
    
    public string Title => App.Length > 0 ? App : Stamp;

    
    public string SubText => HasManifest
        ? $"已移除 {Removed} 项" + (Failed > 0 ? $"　·　失败 {Failed} 项" : "")
        : "早期记录（不含清单）";

    public string TimeText => Time.ToString("yyyy-MM-dd HH:mm");
}














public static class QuarantineStore
{
    
    public static string Root => LeftoverCleaner.QuarantineRoot;

    
    public static IReadOnlyList<QuarantineEntry> Scan()
    {
        var root = Root;
        if (!Directory.Exists(root)) return Array.Empty<QuarantineEntry>();

        var list = new List<QuarantineEntry>();

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                var stamp = Path.GetFileName(dir);
                var manifest = Path.Combine(dir, "manifest.json");
                var hasManifest = File.Exists(manifest);

                var app = "";
                var removed = 0;
                var failed = 0;

                if (hasManifest)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                    var r = doc.RootElement;

                    if (r.TryGetProperty("app", out var a)) app = a.GetString() ?? "";
                    if (r.TryGetProperty("removed", out var m) && m.TryGetInt32(out var mv)) removed = mv;
                    if (r.TryGetProperty("failed", out var f) && f.TryGetInt32(out var fv)) failed = fv;
                }

                list.Add(new QuarantineEntry(dir, stamp, ParseStamp(stamp, dir), app, removed, failed, hasManifest));
            }
            catch (Exception ex)
            {
                
                AppLog.Exception("quarantine", ex, "读取隔离区记录：" + dir);
            }
        }

        return list.OrderByDescending(e => e.Time).ToList();
    }

    
    
    
    
    private static DateTime ParseStamp(string stamp, string dir)
    {
        if (DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", null,
                System.Globalization.DateTimeStyles.None, out var parsed))
            return parsed;

        try
        {
            return Directory.GetCreationTime(dir);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    
    public static bool OpenInExplorer(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Exception("quarantine", ex, "打开隔离区目录：" + dir);
            return false;
        }
    }
}
