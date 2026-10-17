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

/// <summary>任务卡的风险等级。⛔ 只是给用户扫一眼用的分级，别拿它当"可不可以做"的判据。</summary>
public enum RepairRisk
{
    Low,
    Medium,
    High,
}

/// <summary>
/// 一张预设任务卡。
/// <para>
/// ⛔⛔ <b>刻意不给命令输入框</b> —— 界面上只能选这几张卡，用户不能自己敲任意命令。
/// 这个功能的定位是"系统出毛病了修一下"，不是"给个终端"。放开输入等于把风险全推给用户。
/// </para>
/// </summary>
/// <param name="Command">完整命令行。**既要执行、也原样显示给用户看**（确认框里就是它）—— 透明度是这里的安全感来源。</param>
/// <param name="Effect">执行后会发生什么（确认框正文，写实、不美化）。</param>
/// <param name="Caution">高风险卡的额外警告，界面上用醒目色写；空串 = 没有。</param>
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
    /// <summary>卡片上那行灰色元信息。⚠️ 分隔符用全角空格（与 AppData 页的计数行同一套写法）。</summary>
    public string Meta => $"{RiskText}　·　耗时{TimeHint}　·　需要管理员授权";

    public string RiskText => Risk switch
    {
        RepairRisk.Low => "低风险",
        RepairRisk.Medium => "中风险",
        _ => "高风险",
    };
}

/// <summary>一次运行的结局。</summary>
/// <param name="Cancelled">是我们主动中止的（而不是命令自己结束）。</param>
public sealed record RepairOutcome(bool Cancelled, int ExitCode, string Note);

/// <summary>
/// 系统修复指令（1.2.0 第二批第二条）。
///
/// <para>
/// <b>为什么要有提权助手这一层。</b>7 张卡全部要管理员权限（改系统文件、组件存储、网络栈、服务）。
/// 应用本体是**非提权**运行的，所以由非提权的父进程 `runas` 起一个提权的自己，
/// 命令跑在助手里、输出写文件、父进程轮询着读 —— 与「应用卸载」页的残留清理是同一个套路。
/// </para>
/// <para>
/// <b>为什么要走文件而不是管道。</b>提权进程是另一个进程，父进程拿不到它的 stdout。
/// 所以助手把命令输出**按行追加**写进 <c>&lt;清单&gt;.out</c>，进度写 <c>.pct</c>，
/// 心跳写 <c>.beat</c>，结局写 <c>.result</c>；父进程定时读这几个文件。
/// 中止也是反过来的：父进程写 <c>.cancel</c> 哨兵，助手看到就杀子进程树 ——
/// ⛔ 非提权进程**杀不掉**提权进程，所以"中止"必须由助手自己做，父进程直接 Kill 是没用的。
/// </para>
/// </summary>
public static class SystemRepair
{
    /// <summary>提权助手开关。由 <c>App</c> 在启动最前面接管，见 <see cref="RunElevated"/>。</summary>
    public const string AdminSwitch = "--repair-admin";

    private static string RunDir => Path.Combine(SettingsStore.Dir, "repair");

    // ══════════════════════════════════════════════════════════════════════════
    //  7 张任务卡
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ⚠️ 顺序有意义：**从"最安全"排到"最重"**（先只读扫描、再修复、最后动网络与服务）。
    /// 用户从上往下读，自然会先跑扫描再决定要不要修。
    /// ⛔ 图标只用 E7/E8/E9 老码位（ED 那段在 Win10 上是空框），每个都渲图确认过有字形、彼此不撞脸。
    /// </summary>
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

    // ══════════════════════════════════════════════════════════════════════════
    //  父进程一侧：启动 / 轮询 / 中止
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 起一次运行。返回 <c>null</c> = 用户在 UAC 上点了"否"（什么都没做）。
    /// <para>⚠️ 每次运行一个**独立目录**（时间戳 + 任务名），并发的两次运行不会互相踩。</para>
    /// </summary>
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
            Verb = "runas",              // 会弹一次 UAC
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

    /// <summary>
    /// 上一次**没跑完**的运行（用于页面重新加载后接回去）。
    /// ⚠️ 判据是"没有 .result 且心跳文件还新鲜" —— ⛔ 别只看"没有 .result"，
    /// 那样重启电脑之前被掐断的旧任务会被当成还在跑。
    /// </summary>
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

    /// <summary>清掉已经收尾、且超过一天的运行目录（免得越攒越多）。</summary>
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

                    // ⛔ 只删"有结局"的 —— 没结局的可能是还在跑，删了就把正在读的文件抽走了
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

    // ══════════════════════════════════════════════════════════════════════════
    //  提权助手（由 App 在启动最前面调用，跑完 Environment.Exit）
    // ══════════════════════════════════════════════════════════════════════════

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

        // ── 起子进程 ──
        // ⚠️ 统一走 cmd.exe /c：卡里的命令要么是单条，要么用 & 串了几条，交给 cmd 解释最省事。
        //    末尾补 `2>&1` 把 stderr 并进 stdout —— 两条流分开读的话日志顺序会错乱，
        //    而这些命令的报错恰恰是最该看到的。
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

                        // CRLF 的 \n 会切出一个空段，丢掉它（那是换行符自己，不是内容）
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

            // ⛔⛔ 必须单开一条线程专门抽数据：`Stream.Read` 会阻塞到有输出为止，
            //    而 DISM 联网下载那一段可能好几分钟一声不吭。要是就在主循环里同步 Read，
            //    心跳和中止哨兵会跟着一起卡住 —— 界面会以为助手已经死了、中止按钮也按不动。
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
                // ── 心跳（2 秒一次）──
                if ((DateTime.UtcNow - lastBeat).TotalSeconds >= 2)
                {
                    lastBeat = DateTime.UtcNow;
                    try { File.WriteAllText(beatPath, DateTime.UtcNow.Ticks.ToString()); } catch { }
                }

                // ── 中止哨兵 ──
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

                // ── 把这一拍拿到的数据切成行 ──
                var drained = false;
                while (queue.TryDequeue(out var chunk))
                {
                    drained = true;
                    Feed(chunk);
                }

                // ⛔ 不能只看 HasExited：退出那一瞬间管道里可能还剩数据没读完
                if (process.HasExited && !drained && queue.IsEmpty) break;
                if (!drained) Thread.Sleep(200);
            }

            // 收尾：把还没成行的尾巴写出去（等一小会儿让抽数线程把最后一段送进队列）
            Thread.Sleep(150);
            while (queue.TryDequeue(out var tail)) Feed(tail);
            if (pending.Count > 0) HandleLine(Decode(pending), writer, ref percent, ref lastPct, pctPath);

            process.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            AppLog.Exception("repair", ex, $"读取命令输出（{task.Name}）");
        }

        // ── 收尾 ──
        int exitCode;
        try { exitCode = process.HasExited ? process.ExitCode : -1; }
        catch { exitCode = -1; }

        try { File.WriteAllText(beatPath, DateTime.UtcNow.Ticks.ToString()); } catch { }

        var note = cancelled ? "已中止" : (exitCode == 0 ? "" : $"命令返回代码 {exitCode}");
        AppLog.Info("repair", $"「{task.Name}」结束：{(cancelled ? "已中止" : $"退出码 {exitCode}")}");

        return new RepairOutcome(cancelled, exitCode, note);
    }

    /// <summary>处理一行输出。返回 true = 这行是进度条（已单独记进 .pct，不用写日志）。</summary>
    private static bool HandleLine(
        string text, StreamWriter writer, ref int percent, ref int lastPct, string pctPath)
    {
        if (ProgressLine.IsMatch(text))
        {
            var m = PercentValue.Match(text);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var pct))
            {
                // 进度只增不减 —— 有些命令会分阶段从 0 重来，界面上倒退看着像出错了
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

    // ══════════════════════════════════════════════════════════════════════════
    //  输出解码
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 进度整行的形状。两种都要认，缺一种日志就会被进度行灌满：
    /// <list type="bullet">
    ///   <item>方括号进度条 —— <c>[====25.0%====]</c>（DISM）、<c>[    20.0%    ]</c>；</item>
    ///   <item>文字进度 —— <c>验证 25% 完成。</c>（**中文版 sfc 就是这个形式，没有方括号**）、<c>Verification 25% complete.</c>。</item>
    /// </list>
    /// ⛔ 必须**整行**匹配，别拿"含百分号"当判据 —— 结束摘要里也带百分号，那样会被当进度吞掉。
    /// </summary>
    private static readonly Regex ProgressLine = new(
        @"^(?:[\[\s=]*\d{1,3}(?:[.,]\d+)?%[\s=\]]*|.{0,24}?\d{1,3}(?:[.,]\d+)?%\s*(?:完成|complete)[.。]?)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PercentValue =
        new(@"(\d{1,3})(?:[.,]\d+)?%", RegexOptions.Compiled);

    /// <summary>
    /// 按控制台代码页解码。<br/>
    /// ⛔⛔ <b>不能用 <c>Encoding.GetEncoding(936)</c></b>：.NET 的代码页表在
    /// <c>System.Text.Encoding.CodePages</c> 那个包里，本项目没引用它，直接调会抛
    /// <c>NotSupportedException</c>（实测本机输出目录里确实没有这个程序集）。
    /// ⛔ 也不能指望 <c>chcp 65001</c> 让子进程改吐 UTF-8 —— 实测只对一部分工具有效，
    /// <c>net.exe</c> 完全不理它，会当场变成乱码。走系统 API 拿同一张表最稳。
    /// <para>
    /// ⛔⛔ <b><c>ExactSpelling = true</c> 一个都不能少</b>：.NET 默认（ExactSpelling 为 false）
    /// 会按 CharSet 去猜导出名 —— <c>CharSet.Unicode</c> 下会去找 <c>MultiByteToWideCharW</c>，
    /// 而 kernel32 里的真实导出名就是 <c>MultiByteToWideChar</c>（没有 A/W 后缀）。
    /// 猜不到就抛 EntryPointNotFoundException，于是整条链路**静默退回 UTF-8**，
    /// 中文输出全变成「��」—— 2026-10-16 实测踩到过，症状极隐蔽。
    /// </para>
    /// </summary>
    [DllImport("kernel32.dll", EntryPoint = "MultiByteToWideChar",
        CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int MultiByteToWideChar(
        uint codePage, uint dwFlags, byte[]? lpMultiByteStr, int cbMultiByte,
        [Out] char[]? lpWideCharStr, int cchWideChar);

    private static uint _codePage;

    /// <summary>
    /// 系统级的 OEM 代码页（中文系统 = 936）。
    /// <para>
    /// ⛔⛔ <b>为什么不去问 Win32 的 <c>GetACP</c> / <c>GetOEMCP</c>：</b>这两个 API 报的是
    /// **本进程**的活动代码页，而本应用的 <c>app.manifest</c> 里有
    /// <c>&lt;activeCodePage&gt;UTF-8&lt;/activeCodePage&gt;</c> —— 实测在本进程里它们都返回
    /// <b>65001</b>，而 <c>cmd.exe</c> 子进程吐出来的仍然是系统 GBK。
    /// 照 65001 解就是满屏「��」（2026-10-16 实测踩到过，症状是"输出全是乱码但程序不报错"）。
    /// </para>
    /// <para>
    /// ⛔ 也别指望给子进程 <c>chcp 65001</c>：实测只对一部分工具有效，
    /// <c>net.exe</c> 完全不理它，会当场变成乱码。权威来源是注册表里那条系统设置。
    /// </para>
    /// </summary>
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

    /// <summary>控制台输出的代码页。注册表 → 托管区域设置 → UTF-8 依次兜底，并把探测值打进日志。</summary>
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

    /// <summary>⛔ 只报一次 —— 输出可能上千行，每行都记会把日志淹掉。</summary>
    private static void WarnDecodeOnce(string reason)
    {
        if (_decodeWarned) return;
        _decodeWarned = true;
        AppLog.Warning("repair", $"{reason}，本次运行的输出已退回 UTF-8（中文会变乱码）");
    }
}

/// <summary>
/// 一次运行的句柄：父进程拿它轮询输出 / 进度 / 结局，也可以请求中止。
/// ⚠️ 全部状态都在文件里，所以页面重新加载之后新建一个也能接上（见 <see cref="SystemRepair.FindUnfinished"/>）。
/// </summary>
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

    /// <summary>心跳还新鲜 = 助手进程还活着。</summary>
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

    /// <summary>增量取新输出（每次只拿上次之后新增的部分）。</summary>
    public string ReadNewOutput()
    {
        try
        {
            if (!File.Exists(OutPath)) return "";

            using var stream = new FileStream(OutPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < _outPos) _outPos = 0;      // 文件被换过
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

    /// <summary>当前进度百分比；-1 = 还没拿到（界面用不确定进度条）。</summary>
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

    /// <summary>结局；null = 还在跑。</summary>
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

    /// <summary>
    /// 请求中止。
    /// ⛔ 只是**写一个哨兵文件** —— 真正杀子进程的是提权助手那边（普通权限杀不掉提权进程），
    /// 所以这里不能直接 Kill，也别以为写完就立刻停了。
    /// </summary>
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
