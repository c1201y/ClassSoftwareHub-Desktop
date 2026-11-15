using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 残留清理链路的**无人值守体检**（2026-10-14 加，诊断用）。
///
/// ⛔ 为什么非要这么一个东西：这条链路的每一环都要在**真机上**才跑得动
///    （要真的卸载一个软件、真的看注册表和几个数据目录变成了什么样）。
///    而界面那条路要在 WinUI 上点按钮 —— 合成点击在这个项目里被证明不可靠
///    （见记忆里"合成鼠标点击在用户打游戏时不可靠"那条），而且会抢前台。
///    ⇒ 把同一套服务从命令行拉起来跑一遍，报告写进文件，人再去看文件。
///    ⚠️ 这里调用的就是界面上那几个**同一个方法**，没有复制任何逻辑。
///
/// 两个子命令（由 App 在启动最前面接管，跑完直接退出）：
///   <c>--leftover-doctor &lt;关键字&gt;</c>      完整链路：卸载 → 复扫 → 判归属 → 清理 → 复核
///   <c>--leftover-doctor-scan &lt;关键字&gt;</c> 只体检不动作：抓快照 → 判归属 → 打印清单
///
/// ⛔ 提权：完整链路默认<b>不提权</b>跑（<c>elevate: false</c>）——
///    无人值守的验证最怕半路弹一个 UAC 把整条流程挂在等人点确认上。
///    要验提权那条路得用界面（或者把 <see cref="Elevate"/> 翻成 true 手动跑一次）。
/// </summary>
public static class LeftoverDoctor
{
    /// <summary>诊断报告落盘的位置（放数据目录根，不进 cache\ —— 那个目录会被自动回收）。</summary>
    public static string ReportPath => Path.Combine(SettingsStore.Dir, "leftover-doctor.txt");

    public const string FullSwitch = "--leftover-doctor";
    public const string ScanSwitch = "--leftover-doctor-scan";

    /// <summary>完整链路要不要提权。⛔ 默认 false：见类型注释。</summary>
    private const bool Elevate = false;

    /// <summary>
    /// 跑一轮并把报告写进 <see cref="ReportPath"/>。返回进程退出码（0 = 一切正常）。
    /// </summary>
    /// <param name="keyword">用来在已装软件列表里找目标的**显示名关键字**（不区分大小写）。</param>
    /// <param name="scanOnly">true = 只体检（不卸载、不清理）。</param>
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

            // ── ① 找目标 ──
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
                // 只体检模式允许"已经卸载过了"——那时候清单里没有它是正常的
                if (!scanOnly)
                {
                    Say("!! 找不到这个软件，完整链路没法跑。");
                    return 2;
                }
            }

            var app = hits.FirstOrDefault();

            var probe = app ?? new InstalledApp(keyword, "", "", "", Category: AppCategory.Desktop);

            // ── ② 只体检：拿当前状态和"刚才那一份"比是没有意义的，
            //        所以这里改成"跑完一整轮快照 → 判归属 → 打印"，用来复现某次卸载之后的状态。
            //        做法：抓两份相邻快照（中间隔几百毫秒），差集只会有极少数噪声 ——
            //        真正的价值在于把**名字/位置吻合**那一档全部列出来。 ──
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

            // ── ③ 完整链路 ──
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

            // 卸载之后软件还在不在清单里
            var after = InstalledApps.Enumerate()
                .Where(a => a.Name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
            Say("");
            Say(after.Count == 0
                ? "卸载后清单里已经找不到它了。"
                : $"!! 卸载后清单里**仍有** {after.Count} 条（界面在这种情况下不会进残留状态）。");
            Say("");

            // ── ④ 清理（只清默认勾选的，和界面上按"清理选中"是一回事） ──
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

            // ── ⑤ 兜底复核：不依赖 Cleaner 内部那次自查，这里**重新读一遍磁盘/注册表** ──
            //     ⚠️ 两边是独立的判据（Cleaner 复算用的是它自己删完后立刻看的结果，
            //        这里是从报告往回看）—— 不一致本身就是有价值的信号。
            Say("");
            Say("── 独立复核（重新读一次磁盘/注册表）──");
            foreach (var i in chosen)
            {
                var stillThere = Exists(i);
                Say($"   {(stillThere ? "**仍在**" : "已清")}  {i.DisplayPath}");
                if (stillThere) failed++;
            }

            // 备份文件是不是真的写出来了
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
