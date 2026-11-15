using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>隔离区里的一条记录（一次清理对应一条）。</summary>
public sealed record QuarantineEntry(
    string Dir,
    string Stamp,
    DateTime Time,
    string App,
    int Removed,
    int Failed,
    bool HasManifest)
{
    /// <summary>列表上那一行标题：有软件名就用它，没有（旧记录）退回时间戳。</summary>
    public string Title => App.Length > 0 ? App : Stamp;

    /// <summary>副标题：清了什么。旧记录没有清单，只如实说"这是早期记录"。</summary>
    public string SubText => HasManifest
        ? $"已移除 {Removed} 项" + (Failed > 0 ? $"　·　失败 {Failed} 项" : "")
        : "早期记录（不含清单）";

    public string TimeText => Time.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>
/// 隔离区读取（2026-10-16 加）。
///
/// 为什么会需要它：清理残留时删掉的东西**先统统备份进隔离区**，但在此之前的入口只有
/// "清理完成"那一刻弹窗里的一颗按钮 —— 弹窗一关，用户就再也找不到自己的后悔药了，
/// 而界面上却一直写着"可随时还原"（Nick 2026-10-16 点出来的）。
/// 「隔离区」界面要有地方列出这些记录，这个类就是给它读数据用的。
///
/// ⛔ 只做**读**：这里不删任何东西、也不还原任何东西。隔离区目录永不自动清理
///    （原因见 <see cref="LeftoverCleaner"/> 的类注释：放 <c>cache\</c> 里会被按版本回收掉）。
/// ⚠️ 体积**故意不算**：隔离区里可能有成千上万个文件，递归统计会让这个界面卡住。
///    用户想知道占多大，打开文件夹自己看即可。
/// </summary>
public static class QuarantineStore
{
    /// <summary>隔离区根目录（与清理器同源）。</summary>
    public static string Root => LeftoverCleaner.QuarantineRoot;

    /// <summary>列出全部记录，新的在前。目录不存在返回空表。</summary>
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
                // 某条记录读坏了不该连累整个列表
                AppLog.Exception("quarantine", ex, "读取隔离区记录：" + dir);
            }
        }

        return list.OrderByDescending(e => e.Time).ToList();
    }

    /// <summary>
    /// 目录名就是时间戳（<c>yyyyMMdd-HHmmss</c>）。解析不出来（用户手改过名字之类）
    /// 就退回目录的创建时间 —— 总得能排序，别让一条怪名字把列表顺序搅乱。
    /// </summary>
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

    /// <summary>在资源管理器里打开某条记录（用户要自己看文件、自己取回时用）。</summary>
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
