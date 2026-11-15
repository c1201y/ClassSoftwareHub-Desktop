using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;



















public static class LeftoverDoctor
{
    
    public static string ReportPath => Path.Combine(SettingsStore.Dir, "leftover-doctor.txt");

    public const string FullSwitch = "--leftover-doctor";
    public const string ScanSwitch = "--leftover-doctor-scan";

    
    private const bool Elevate = false;

    
    
    
    
    
    public static async Task<int> RunAsync(string keyword, bool scanOnly)
    {
        var log = new StringBuilder();
        var failed = 0;

        void Say(string line)
        {
            log.AppendLine(line);
            AppLog.Info("doctor", line);
        }

        try
        {
            Say($"═══ ClassSoftwareHub 残留清理体检 · {DateTime.Now:yyyy-MM-dd HH:mm:ss} ═══");
            Say($"模式：{(scanOnly ? "只体检" : "完整链路（卸载 + 清理）")}   提权：{Elevate}");
            Say($"关键字：{keyword}");
            Say($"数据目录：{SettingsStore.Dir}");
            Say("");

            
            var all = InstalledApps.Enumerate();
            all.AddRange(InstalledApps.EnumerateStorePackages());

            var hits = all.Where(a => a.Name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase))
                          .ToList();

            Say($"── 已安装清单里匹配「{keyword}」：{hits.Count} 条 ──");
            foreach (var h in hits)
                Say($"   · {h.Name}  {h.Version}  分类={h.Category}  位置={Blank(h.Location)}");
            Say("");

            if (hits.Count == 0)
            {
                
                if (!scanOnly)
                {
                    Say("!! 找不到这个软件，完整链路没法跑。");
                    return 2;
                }
            }

            var app = hits.FirstOrDefault();

            var probe = app ?? new InstalledApp(keyword, "", "", "", Category: AppCategory.Desktop);

            
            
            
            
            if (scanOnly)
            {
                var a = FootprintSnapshot.Capture("体检-1");
                await Task.Delay(300);
                var b = FootprintSnapshot.Capture("体检-2");

                var items = LeftoverResolver.Resolve(a, b, probe);
                DumpItems(Say, items);
                Say("");
                Say($"快照条目数：{a.Count} / {b.Count}   耗时 {a.ElapsedMs} ms / {b.ElapsedMs} ms");
                return 0;
            }

            
            Say("── 开始卸载（含前后快照）──");
            var run = await AppUninstaller.UninstallAndScanAsync(app!, default, Elevate);

            Say($"启动：{run.Outcome.Started}   跑完：{run.Outcome.Finished}   做了体检：{run.Scanned}");
            Say($"说明：{run.Outcome.Message}");
            Say("");

            if (!run.Scanned)
            {
                Say("!! 没做体检（启动失败 / 超时）。链路到此为止。");
                return 3;
            }

            DumpItems(Say, run.Leftovers);

            
            var after = InstalledApps.Enumerate()
                .Where(a => a.Name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
            Say("");
            Say(after.Count == 0
                ? "卸载后清单里已经找不到它了。"
                : $"!! 卸载后清单里**仍有** {after.Count} 条（界面在这种情况下不会进残留状态）。");
            Say("");

            
            var chosen = run.Leftovers.Where(i => i.Selected).ToList();
            Say($"── 开始清理：默认勾选 {chosen.Count} 条 / 共 {run.Leftovers.Count} 条 ──");

            if (chosen.Count == 0)
            {
                Say("没有勾选项，跳过清理。");
                return 0;
            }

            var report = await LeftoverCleaner.CleanAsync(chosen, app!.Name);

            Say($"隔离区：{Blank(report.QuarantineDir)}");
            Say($"注册表备份：{Blank(report.RegistryBackup)}");
            Say($"清理记录：{Blank(report.LogFile)}");
            Say($"成功 {report.RemovedTotal} 条（其中未备份 {report.Count(CleanStatus.RemovedNoBackup)}）、" +
                $"缺权限 {report.Count(CleanStatus.NeedsAdmin)}、" +
                $"失败 {report.Count(CleanStatus.Failed)}、" +
                $"被拦 {report.Count(CleanStatus.Blocked)}");
            Say("");

            Say("── 逐条结果（含**复核**结论：StillThere 才是真相）──");
            foreach (var r in report.Results)
            {
                var flag = r.Item.StillThere ? "仍在" : "已清";
                if (r.Status == CleanStatus.Failed || r.Item.StillThere) failed++;
                Say($"   [{Tag(r.Status)}/{flag}] {r.Item.DisplayPath}");
                if (r.Note.Length > 0) Say($"         {r.Note}");
            }

            
            
            
            Say("");
            Say("── 独立复核（重新读一次磁盘/注册表）──");
            foreach (var i in chosen)
            {
                var stillThere = Exists(i);
                Say($"   {(stillThere ? "**仍在**" : "已清")}  {i.DisplayPath}");
                if (stillThere) failed++;
            }

            
            Say("");
            if (report.RegistryBackup.Length > 0 && File.Exists(report.RegistryBackup))
            {
                var text = File.ReadAllLines(report.RegistryBackup);
                Say($"注册表备份 {new FileInfo(report.RegistryBackup).Length} 字节，{text.Length} 行，前几行：");
                foreach (var line in text.Take(8)) Say("   | " + line);
            }

            Say("");
            Say(failed == 0 ? "═══ 结论：全部清干净了。 ═══" : $"═══ 结论：还有 {failed} 项没搞定（见上面标 **仍在** 的）。 ═══");
            return failed == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            AppLog.Exception("doctor", ex, "残留清理体检");
            log.AppendLine("!! 体检崩了：" + ex);
            return 9;
        }
        finally
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
                File.WriteAllText(ReportPath, log.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                AppLog.Exception("doctor", ex, "写体检报告");
            }
        }
    }

    private static void DumpItems(Action<string> say, IReadOnlyList<LeftoverItem> items)
    {
        say($"── 残留判定：{items.Count} 条（确定 {items.Count(i => i.Evidence == LeftoverEvidence.Strong)} · " +
            $"很可能 {items.Count(i => i.Evidence == LeftoverEvidence.Medium)} · " +
            $"不确定 {items.Count(i => i.Evidence == LeftoverEvidence.Weak)}）──");

        foreach (var i in items)
        {
            var mark = i.Selected ? "[勾]" : "[  ]";
            say($"   {mark} {i.EvidenceText,-4} {i.KindText,-5} {i.DisplayPath}");
            say($"         理由：{i.Reason}   体积：{(i.SizeText.Length > 0 ? i.SizeText : "—")}   体检点：{i.Probe}");
        }

        if (items.Count == 0) say("   （一条都没有）");
    }

    private static bool Exists(LeftoverItem item)
    {
        try
        {
            if (item.Kind == FootprintKind.Path)
                return File.Exists(item.Path) || Directory.Exists(item.Path);

            if (!RegistryPath.TryParse(item.Path, out var hive, out var subPath)) return true;

            using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, Microsoft.Win32.RegistryView.Default);
            if (item.Kind == FootprintKind.RegistryKey)
            {
                using var k = root.OpenSubKey(subPath, writable: false);
                return k is not null;
            }

            using var key = root.OpenSubKey(subPath, writable: false);
            return key is not null && Array.IndexOf(key.GetValueNames(), item.ValueName) >= 0;
        }
        catch
        {
            return true;
        }
    }

    private static string Tag(CleanStatus s) => s switch
    {
        CleanStatus.Removed => "已删",
        CleanStatus.RemovedNoBackup => "已删(无备份)",
        CleanStatus.NeedsAdmin => "缺权限",
        CleanStatus.Failed => "失败",
        _ => "跳过",
    };

    private static string Blank(string s) => s.Length == 0 ? "（无）" : s;
}
