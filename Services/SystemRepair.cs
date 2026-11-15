using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;


public enum RepairRisk
{
    Low,
    Medium,
    High,
}











public sealed record RepairTask(
    string Id,
    string Name,
    string Glyph,
    string Desc,
    string Command,
    string TimeHint,
    RepairRisk Risk,
    string Effect,
    string Caution)
{
    
    public string Meta => $"{RiskText}　·　耗时{TimeHint}　·　需要管理员授权";

    public string RiskText => Risk switch
    {
        RepairRisk.Low => "低风险",
        RepairRisk.Medium => "中风险",
        _ => "高风险",
    };
}



public sealed record RepairOutcome(bool Cancelled, int ExitCode, string Note);

















public static class SystemRepair
{
    
    public const string AdminSwitch = "--repair-admin";

    private static string RunDir => Path.Combine(SettingsStore.Dir, "repair");

    
    
    

    
    
    
    
    
    public static readonly IReadOnlyList<RepairTask> Tasks = new[]
    {
        new RepairTask(
            "chkdsk-scan", "磁盘错误扫描", "\uE9F3",
            "只读扫描系统盘的文件系统结构，看有没有错误",
            "chkdsk C: /scan",
            "1~10 分钟", RepairRisk.Low,
            "只读扫描，不会修改磁盘上的任何数据，也不需要重启。发现错误只会报告出来，不会自动改动。",
            ""),

        new RepairTask(
            "dism-scan", "组件存储健康扫描", "\uE9D9",
            "检查系统组件存储有没有损坏，只读扫描",
            "dism /Online /Cleanup-Image /ScanHealth",
            "2~8 分钟", RepairRisk.Low,
            "只读扫描，不修改任何文件。它回答的是「组件存储本身坏没坏」——如果这一步就报告没坏，"
            + "下面那条修复通常也没有必要跑。",
            ""),

        new RepairTask(
            "sfc", "系统文件检查与修复", "\uE7EF",
            "扫描受保护的系统文件，发现被损坏或篡改的用官方副本替换",
            "sfc /scannow",
            "5~15 分钟", RepairRisk.Low,
            "逐个校验系统文件与官方副本是否一致。发现不一致时用本机缓存里的官方副本替换掉被改动的那一份；"
            + "本机缓存里也没有的，会从系统组件存储里取（组件存储坏了就会取不到，那时要先跑组件存储修复）。",
            ""),

        new RepairTask(
            "dism-restore", "组件存储修复", "\uE90F",
            "修复系统组件存储，让系统文件检查能取到官方副本",
            "dism /Online /Cleanup-Image /RestoreHealth",
            "10~30 分钟", RepairRisk.Low,
            "从 Windows 更新服务器下载缺失或损坏的组件并替换进去（本机已有缓存时不联网）。"
            + "这一步失败时，系统文件检查往往也修不好。",
            "过程中可能长时间停在同一个百分比不动 —— 那是在联网下载，属于正常现象，不要中途关掉。"),

        new RepairTask(
            "dism-cleanup", "组件存储清理", "\uE7B8",
            "清掉组件存储里被新版本取代的旧组件，通常能腾出几 GB",
            "dism /Online /Cleanup-Image /StartComponentCleanup",
            "5~20 分钟", RepairRisk.Medium,
            "删除 WinSxS 目录里已被新版本取代的旧组件。这些旧版本留着的作用只有一个："
            + "卸载近期安装的更新时能回退回去。删掉之后，回退就不再可行。",
            "此操作不可撤销。如果近期装过系统更新、而且可能想卸载它，请先不要执行。"),

        new RepairTask(
            "net-reset", "网络栈重置", "\uE895",
            "把网络协议栈恢复成默认设置，用于网络彻底抽风时",
            "netsh winsock reset & netsh int ip reset & ipconfig /flushdns",
            "几秒", RepairRisk.High,
            "重置 Winsock 目录与 TCP/IP 协议栈。第三方网络组件（部分 VPN、代理客户端、安全软件的网络过滤驱动）"
            + "会从协议栈里被摘掉；如果本机用的是静态 IP 或自定义 DNS，这些设置会被清掉、需要重新填。"
            + "Wi-Fi 密码不受影响。",
            "这是网络问题排查的最后一招，不是常规清理。执行后需要重启电脑，相关软件可能要重新配置。"),

        new RepairTask(
            "wu-reset", "Windows 更新组件重置", "\uE8F3",
            "重置更新组件，用于更新卡住、反复报错时",
            "net stop wuauserv & net stop bits & net stop cryptsvc & " +
            "ren \"%windir%\\SoftwareDistribution\" SoftwareDistribution.bak & " +
            "net start cryptsvc & net start bits & net start wuauserv",
            "1~2 分钟", RepairRisk.High,
            "停止三个更新相关服务，把更新缓存目录 SoftwareDistribution 改名为 SoftwareDistribution.bak，"
            + "再把服务启动回去。已下载但还没安装的更新会丢失，需要重新下载。原来的缓存目录没有被删除，"
            + "改名可以恢复回去。",
            "执行时会短暂中断 Windows 更新服务。如果系统正在安装更新，请不要执行。"),
    };

    public static RepairTask? TaskOf(string id) => Tasks.FirstOrDefault(t => t.Id == id);

    
    
    

    
    
    
    
    public static async Task<RepairSession?> StartAsync(RepairTask task, CancellationToken ct)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            AppLog.Warning("repair", "拿不到本程序路径，无法提权");
            return null;
        }

        Directory.CreateDirectory(RunDir);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dir = Path.Combine(RunDir, $"{stamp}-{task.Id}");
        Directory.CreateDirectory(dir);

        var manifest = Path.Combine(dir, "run.json");
        await File.WriteAllTextAsync(manifest,
            JsonSerializer.Serialize(new RepairManifest(task.Id), Json), ct);

        var session = new RepairSession(task, manifest);
        AppLog.Info("repair", $"开始执行「{task.Name}」：{task.Command}");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"\"{AdminSwitch}\" \"{manifest}\"",
            UseShellExecute = true,
            Verb = "runas",              
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            var process = await Task.Run(() => Process.Start(psi), ct);
            process?.Dispose();
            return session;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            AppLog.Info("repair", "用户取消了提权，任务未开始");
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Exception("repair", ex, $"启动提权助手（{task.Name}）");
            return null;
        }
    }

    
    
    
    
    
    public static RepairSession? FindUnfinished()
    {
        try
        {
            if (!Directory.Exists(RunDir)) return null;

            var manifests = Directory.GetFiles(RunDir, "run.json", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTime)
                .Take(8);

            foreach (var manifest in manifests)
            {
                if (File.Exists(manifest + ".result")) continue;

                var taskId = ReadTaskId(manifest);
                var task = taskId is null ? null : TaskOf(taskId);
                if (task is null) continue;

                var session = new RepairSession(task, manifest);
                if (session.IsAlive) return session;
            }

            return null;
        }
        catch (Exception ex)
        {
            AppLog.Info("repair", $"查未收尾的运行失败：{ex.GetType().Name}");
            return null;
        }
    }

    private static string? ReadTaskId(string manifestPath)
    {
        try
        {
            return JsonSerializer.Deserialize<RepairManifest>(File.ReadAllText(manifestPath))?.TaskId;
        }
        catch (Exception ex)
        {
            AppLog.Info("repair", $"读运行清单失败（{manifestPath}）：{ex.GetType().Name}");
            return null;
        }
    }

    
    public static void CleanupOldRuns()
    {
        try
        {
            if (!Directory.Exists(RunDir)) return;

            var cutoff = DateTime.Now.AddDays(-1);
            foreach (var dir in Directory.GetDirectories(RunDir))
            {
                try
                {
                    if (Directory.GetLastWriteTime(dir) > cutoff) continue;

                    
                    if (!File.Exists(Path.Combine(dir, "run.json.result"))) continue;

                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex)
                {
                    AppLog.Info("repair", $"清旧运行目录失败（{dir}）：{ex.GetType().Name}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Info("repair", $"清旧运行目录失败：{ex.GetType().Name}");
        }
    }

    
    
    

    public static void RunElevated(string manifestPath)
    {
        AppLog.Info("repair", $"提权助手启动，清单：{manifestPath}");

        var outcome = new RepairOutcome(false, -1, "未执行");

        try
        {
            var taskId = ReadTaskId(manifestPath);
            var task = taskId is null ? null : TaskOf(taskId);

            if (task is null)
            {
                Outcome(manifestPath, new RepairOutcome(false, -1, "清单里的任务不认识"));
                return;
            }

            outcome = Execute(manifestPath, task);
        }
        catch (Exception ex)
        {
            AppLog.Exception("repair", ex, "提权助手执行");
            outcome = new RepairOutcome(false, -1, ex.Message);
        }

        Outcome(manifestPath, outcome);
    }

    private static RepairOutcome Execute(string manifestPath, RepairTask task)
    {
        var outPath = manifestPath + ".out";
        var pctPath = manifestPath + ".pct";
        var beatPath = manifestPath + ".beat";
        var cancelPath = manifestPath + ".cancel";

        SafeDelete(cancelPath);
        SafeDelete(outPath);
        SafeDelete(pctPath);
        SafeDelete(beatPath);

        
        
        
        
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(task.Command + " 2>&1");

        using var process = Process.Start(psi);
        if (process is null)
        {
            return new RepairOutcome(false, -1, "命令没能启动");
        }

        var cancelled = false;
        var lastBeat = DateTime.MinValue;
        var lastPct = -1;
        var percent = -1;

        using var writer = new StreamWriter(outPath, append: false, new UTF8Encoding(false))
        {
            AutoFlush = true,
        };

        try
        {
            var stream = process.StandardOutput.BaseStream;

            var pending = new List<byte>(8192);
            var lastWasCr = false;

            void Feed(byte[] chunk)
            {
                foreach (var b in chunk)
                {
                    if (b is (byte)'\n' or (byte)'\r')
                    {
                        var text = Decode(pending);
                        pending.Clear();

                        
                        if (!(text.Length == 0 && b == (byte)'\n' && lastWasCr))
                            HandleLine(text, writer, ref percent, ref lastPct, pctPath);

                        lastWasCr = b == (byte)'\r';
                    }
                    else
                    {
                        pending.Add(b);
                    }
                }
            }

            
            
            
            var queue = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
            var reader = new Thread(() =>
            {
                try
                {
                    var buf = new byte[8192];
                    int n;
                    while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                    {
                        var chunk = new byte[n];
                        Array.Copy(buf, chunk, n);
                        queue.Enqueue(chunk);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Info("repair", $"读命令输出中断：{ex.GetType().Name}");
                }
            })
            {
                IsBackground = true,
                Name = "repair-pump",
            };
            reader.Start();

            while (true)
            {
                
                if ((DateTime.UtcNow - lastBeat).TotalSeconds >= 2)
                {
                    lastBeat = DateTime.UtcNow;
                    try { File.WriteAllText(beatPath, DateTime.UtcNow.Ticks.ToString()); } catch { }
                }

                
                try
                {
                    if (File.Exists(cancelPath))
                    {
                        cancelled = true;
                        AppLog.Info("repair", "收到中止请求，结束子进程树");
                        KillTree(process);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Info("repair", $"读中止哨兵失败：{ex.GetType().Name}");
                }

                
                var drained = false;
                while (queue.TryDequeue(out var chunk))
                {
                    drained = true;
                    Feed(chunk);
                }

                
                if (process.HasExited && !drained && queue.IsEmpty) break;
                if (!drained) Thread.Sleep(200);
            }

            
            Thread.Sleep(150);
            while (queue.TryDequeue(out var tail)) Feed(tail);
            if (pending.Count > 0) HandleLine(Decode(pending), writer, ref percent, ref lastPct, pctPath);

            process.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            AppLog.Exception("repair", ex, $"读取命令输出（{task.Name}）");
        }

        
        int exitCode;
        try { exitCode = process.HasExited ? process.ExitCode : -1; }
        catch { exitCode = -1; }

        try { File.WriteAllText(beatPath, DateTime.UtcNow.Ticks.ToString()); } catch { }

        var note = cancelled ? "已中止" : (exitCode == 0 ? "" : $"命令返回代码 {exitCode}");
        AppLog.Info("repair", $"「{task.Name}」结束：{(cancelled ? "已中止" : $"退出码 {exitCode}")}");

        return new RepairOutcome(cancelled, exitCode, note);
    }

    
    private static bool HandleLine(
        string text, StreamWriter writer, ref int percent, ref int lastPct, string pctPath)
    {
        if (ProgressLine.IsMatch(text))
        {
            var m = PercentValue.Match(text);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var pct))
            {
                
                if (pct > percent) percent = pct;

                if (percent != lastPct)
                {
                    lastPct = percent;
                    try { File.WriteAllText(pctPath, percent.ToString()); } catch { }
                }
            }

            return true;
        }

        if (text.Length > 0)
        {
            writer.WriteLine(text);
        }

        return false;
    }

    private static void Outcome(string manifestPath, RepairOutcome outcome)
    {
        try
        {
            File.WriteAllText(manifestPath + ".result", JsonSerializer.Serialize(outcome, Json));
        }
        catch (Exception ex)
        {
            AppLog.Exception("repair", ex, "写运行结局");
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            AppLog.Info("repair", $"结束子进程树失败：{ex.GetType().Name}");
        }
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { AppLog.Info("repair", $"删旧文件失败（{path}）：{ex.GetType().Name}"); }
    }

    private sealed record RepairManifest(string TaskId);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    
    
    

    
    
    
    
    
    
    
    
    private static readonly Regex ProgressLine = new(
        @"^(?:[\[\s=]*\d{1,3}(?:[.,]\d+)?%[\s=\]]*|.{0,24}?\d{1,3}(?:[.,]\d+)?%\s*(?:完成|complete)[.。]?)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PercentValue =
        new(@"(\d{1,3})(?:[.,]\d+)?%", RegexOptions.Compiled);

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    [DllImport("kernel32.dll", EntryPoint = "MultiByteToWideChar",
        CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int MultiByteToWideChar(
        uint codePage, uint dwFlags, byte[]? lpMultiByteStr, int cbMultiByte,
        [Out] char[]? lpWideCharStr, int cchWideChar);

    private static uint _codePage;

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private static uint ReadSystemOemCodePage()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Nls\CodePage", writable: false);

            if (key?.GetValue("OEMCP") is string text
                && uint.TryParse(text, out var codePage) && codePage > 0)
            {
                return codePage;
            }
        }
        catch (Exception ex)
        {
            AppLog.Info("repair", $"读系统 OEM 代码页失败：{ex.GetType().Name}");
        }

        return 0;
    }

    
    private static uint ConsoleCodePage
    {
        get
        {
            if (_codePage != 0) return _codePage;

            var fromRegistry = ReadSystemOemCodePage();

            var fromCulture = 0;
            try
            {
                fromCulture = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            }
            catch (Exception ex)
            {
                AppLog.Info("repair", $"取 CurrentCulture.OEMCodePage 失败：{ex.GetType().Name}");
            }

            AppLog.Info("repair", $"代码页探测：注册表 OEMCP={fromRegistry} 托管 OEMCodePage={fromCulture}");

            _codePage = fromRegistry != 0 ? fromRegistry
                      : fromCulture > 0 ? (uint)fromCulture
                      : 65001;

            AppLog.Info("repair", $"控制台输出按代码页 {_codePage} 解码");
            return _codePage;
        }
    }

    private static bool _decodeWarned;

    private static string Decode(List<byte> bytes)
    {
        if (bytes.Count == 0) return "";

        var raw = bytes.ToArray();

        try
        {
            var need = MultiByteToWideChar(ConsoleCodePage, 0, raw, raw.Length, null, 0);
            if (need > 0)
            {
                var chars = new char[need];
                var got = MultiByteToWideChar(ConsoleCodePage, 0, raw, raw.Length, chars, need);
                if (got > 0) return new string(chars, 0, got);
            }

            WarnDecodeOnce($"MultiByteToWideChar 返回 {need}");
        }
        catch (Exception ex)
        {
            WarnDecodeOnce($"解码输出失败（{ex.GetType().Name}）");
        }

        return Encoding.UTF8.GetString(raw);
    }

    
    private static void WarnDecodeOnce(string reason)
    {
        if (_decodeWarned) return;
        _decodeWarned = true;
        AppLog.Warning("repair", $"{reason}，本次运行的输出已退回 UTF-8（中文会变乱码）");
    }
}





public sealed class RepairSession
{
    private long _outPos;

    internal RepairSession(RepairTask task, string manifestPath)
    {
        Task = task;
        ManifestPath = manifestPath;
        StartedAt = File.GetLastWriteTime(manifestPath);
    }

    public RepairTask Task { get; }

    public string ManifestPath { get; }

    public DateTime StartedAt { get; }

    private string OutPath => ManifestPath + ".out";

    private string PctPath => ManifestPath + ".pct";

    private string BeatPath => ManifestPath + ".beat";

    private string ResultPath => ManifestPath + ".result";

    
    public bool IsAlive
    {
        get
        {
            try
            {
                return File.Exists(BeatPath)
                       && DateTime.UtcNow - File.GetLastWriteTimeUtc(BeatPath) < TimeSpan.FromSeconds(30);
            }
            catch
            {
                return false;
            }
        }
    }

    
    public string ReadNewOutput()
    {
        try
        {
            if (!File.Exists(OutPath)) return "";

            using var stream = new FileStream(OutPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < _outPos) _outPos = 0;      
            if (stream.Length == _outPos) return "";

            stream.Seek(_outPos, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            var text = reader.ReadToEnd();
            _outPos = stream.Position;
            return text;
        }
        catch (Exception ex)
        {
            AppLog.Info("repair", $"读运行输出失败：{ex.GetType().Name}");
            return "";
        }
    }

    
    public int ReadPercent()
    {
        try
        {
            if (!File.Exists(PctPath)) return -1;
            return int.TryParse(File.ReadAllText(PctPath).Trim(), out var pct) ? pct : -1;
        }
        catch
        {
            return -1;
        }
    }

    
    public RepairOutcome? ReadOutcome()
    {
        try
        {
            if (!File.Exists(ResultPath)) return null;
            return System.Text.Json.JsonSerializer.Deserialize<RepairOutcome>(File.ReadAllText(ResultPath));
        }
        catch (Exception ex)
        {
            AppLog.Info("repair", $"读运行结局失败：{ex.GetType().Name}");
            return null;
        }
    }

    
    
    
    
    
    public void RequestCancel()
    {
        try
        {
            File.WriteAllText(ManifestPath + ".cancel", DateTime.UtcNow.Ticks.ToString());
            AppLog.Info("repair", $"已请求中止「{Task.Name}」");
        }
        catch (Exception ex)
        {
            AppLog.Exception("repair", ex, "写中止哨兵");
        }
    }
}
