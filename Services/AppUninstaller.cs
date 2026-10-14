using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>一次卸载尝试的结果。<see cref="Message"/> 直接摆到界面上，写成给人看的话。</summary>
/// <param name="Started">
/// 卸载程序是否**成功启动**了。⚠️ 这不代表卸载完成 —— 有些卸载器启动后把自己复制到临时目录再跑、
/// 父进程立刻退出，所以真正的结论要看**重新扫描之后那条记录还在不在**。
/// </param>
/// <param name="Message">人话说明。</param>
/// <param name="Finished">
/// 卸载程序是不是**真的退出了**（2026-10-14 加，残留扫描的前提）。
/// ⛔ 只有它为 true 才允许复扫：超时那一条分支里，卸载程序**还在跑**（<c>StartAndWaitAsync</c>
///    放弃等待 ≠ 它停了），这时候抓快照会把"正在删"的过程拍成"还在"，
///    差集里就全是假残留。
/// </param>
public sealed record UninstallOutcome(bool Started, string Message, bool Finished = false);

/// <summary>
/// 一次「卸载 + 残留体检」的完整结果（2026-10-14 加）。
/// <paramref name="Scanned"/> = 有没有真的做过前后快照（卸载没起来 / 商店包 / 超时 都是 false，
/// 那时 <paramref name="Leftovers"/> 是空表 —— 界面要能分清"没有残留"和"没查"）。
/// </summary>
public sealed record UninstallRun(
    UninstallOutcome Outcome,
    IReadOnlyList<LeftoverItem> Leftovers,
    bool Scanned);


/// <summary>
/// 跑一次卸载（2026-10-12 加，「应用卸载」页的后端）。
///
/// 两条路：
///   ① 注册表条目（「程序和功能」里的软件）→ 跑它**自己**的卸载命令，提权启动（UAC 一次）。
///      卸载程序自己有界面（绝大多数都有），我们只负责把它拉起来、等它退出。
///   ② Microsoft Store / MSIX 包 → 没有卸载命令，按包全名走 <c>PackageManager</c>；
///      不成就回落到提权跑 PowerShell 的 <c>Remove-AppxPackage</c>。
///
/// ⚠️ 这套做法的边界（页面上要讲清楚，别让人以为这是"清垃圾"）：
///   · 我们**不删任何文件** —— 删除动作全在厂商自己的卸载程序里。这恰恰是它比"手动删目录"安全的原因；
///   · 卸载程序弹不弹界面、要不要重启，我们管不着；
///   · ⛔ **不能卸自己**（<see cref="IsSelf"/>），页面上那颗按钮会直接禁用。
/// </summary>
public static class AppUninstaller
{
    /// <summary>等卸载程序退出的上限。绝大多数卸载 1 分钟内完事；给宽点，慢机器装大软件的也够。</summary>
    private static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(20);

    /// <summary>这条记录是不是本程序自己 —— 不许在这里卸自己。</summary>
    public static bool IsSelf(InstalledApp app)
        => InstalledApps.Normalize(app.Name).Contains("classsoftwarehub", StringComparison.Ordinal);

    /// <summary>
    /// ⛔⛔ 这条记录是不是**不许动**的 Windows 自带组件（2026-10-14 加）。
    ///
    /// 为什么必须挡：这类包 <see cref="InstalledApp.IsStore"/> 是 true、<c>PackageFullName</c> 也齐，
    /// 于是 <see cref="CanUninstall"/> 的旧判据会对它们返回 true —— 界面上「文件资源管理器」「开始」
    /// 「设置」「讲述人」这些**全都带着可点的卸载按钮**（Nick 2026-10-14 截图实拍报的
    /// 「这些东西是能卸载的吗？？？」）。更糟的是 <see cref="RemoveStorePackageAsync"/> 在 API 失败后
    /// 还会**提权回落**到 <c>Remove-AppxPackage</c>，而管理员身份下那是真能把系统组件拆掉的
    /// —— 轻则开始菜单/资源管理器报废，重得要重装系统组件。
    ///
    /// ⚠️ 判据只看分类：这个分类由 <c>PackageManager.SignatureKind == System</c> 定（见 InstalledApps），
    ///    是系统自己给的权威标记，不是我们猜的。
    /// ⚠️ 光禁用界面按钮**不算数** —— <see cref="RunAsync"/> 里也得拦（见那里的同名判断）。
    /// </summary>
    public static bool IsProtected(InstalledApp app) => app.Category == AppCategory.System;

    /// <summary>
    /// 这条记录**有没有**可用的卸载途径。
    /// 没有的话页面上的按钮要禁用并说明原因 —— 比让用户点了没反应强。
    /// </summary>
    public static bool CanUninstall(InstalledApp app)
        => !IsSelf(app) && !IsProtected(app) && (app.IsStore || app.UninstallString.Length > 0);

    /// <summary>跑一次卸载。返回值只说明"命令有没有跑起来 / 为什么没跑起来"。</summary>
    /// <param name="elevate">
    /// 要不要用 <c>runas</c> 强制提权。默认 true（与历史行为一致）。
    /// ⚠️ 只有"已知这个卸载器不需要管理员"时才传 false —— 主要给 <see cref="LeftoverDoctor"/> 用，
    ///    它要在无人值守的情况下跑完整条链路，弹一次 UAC 就把自动验证打断了。
    /// </param>
    public static async Task<UninstallOutcome> RunAsync(
        InstalledApp app, CancellationToken ct = default, bool elevate = true)
    {
        if (IsSelf(app)) return new(false, "这是本程序自己，不能在这里卸载。");

        // ⛔ 第二道闸（第一道在 CanUninstall）。界面状态可能过期、也可能以后有人新加一条入口，
        //    所以真正动手之前必须再拦一次 —— 这类包放出去点了就是拆系统。
        if (IsProtected(app))
        {
            AppLog.Warning("uninstall", $"拒绝卸载 Windows 自带组件：{app.Name}（{app.PackageFullName}）");
            return new(false,
                $"「{app.Name}」为 Windows 系统组件，移除后将导致对应系统功能失效，本程序不予执行。" +
                "如确需移除，请使用系统自带工具操作。");
        }

        if (app.IsStore) return await RemoveStorePackageAsync(app, ct, elevate);

        // QuietUninstallString 优先：厂商特意给的静默版本，少一层交互。
        var raw = app.QuietUninstallString.Length > 0 ? app.QuietUninstallString : app.UninstallString;
        if (raw.Length == 0)
        {
            AppLog.Info("uninstall", $"「{app.Name}」没有 UninstallString，跳过");
            return new(false,
                "该记录未提供卸载命令。此类条目通常只有安装登记（例如免安装版留下的痕迹），" +
                "需手动删除程序目录，或前往系统「设置 → 应用」查找。");
        }

        var (exe, args) = SplitCommand(raw);
        if (exe.Length == 0) return new(false, "读不出卸载程序的位置。");

        // ── MSI 的 /I 是安装/修复，卸载要用 /X ──
        // UninstallString 写成 `MsiExec.exe /I{GUID}` 是微软的老习惯，直接跑会弹出
        // "修改 / 修复 / 删除"那个维护对话框，用户还得自己点"删除"。这里给它换过来。
        if (IsMsiExec(exe)) args = MsiInstallToUninstall(args);

        AppLog.Info("uninstall", $"卸载「{app.Name}」：{exe} {args}");
        return await StartAndWaitAsync(exe, args, app.Name, ct, elevate);
    }

    // ── 卸载 + 残留体检（2026-10-14 加） ───────────────────────────────────────

    /// <summary>卸载程序退出之后，等这么久再复扫。</summary>
    /// <remarks>
    /// ⚠️ 这个延时不是"求稳"，是**必须的**：卸载器退出的那一瞬间，它的收尾动作可能还在别的进程里
    /// （不少卸载器会拉起一个"重启后继续删"的 <c>RunOnce</c> 项、或者把删除动作丢给
    /// <c>cmd /c ping 127.0.0.1 -n 3 &amp; rd /s /q ...</c> 那种延迟批处理）。
    /// 立刻复扫的话，差集里会出现一堆"以为还在、其实马上就没"的假残留。
    /// 2 秒是折中：绝大多数卸载器的收尾在 1 秒内做完，而界面上也多等得起 2 秒。
    /// </remarks>
    private const int RescanDelayMs = 2000;

    /// <summary>
    /// 「卸载 + 残留体检」的完整链路（2026-10-14 加）。
    ///
    /// 六步：
    ///   ① 抓「卸载前」浅层快照 → ② 跑厂商自己的卸载器（含 UAC） → ③ 退出后等 2 秒
    ///   → ④ 抓「卸载后」快照 → ⑤ 求差集 → ⑥ 按名字/路径判归属三级证据。
    ///
    /// ⛔ 为什么"卸载前"那份**必须**在 UAC 之前抓：<c>Process.Start(Verb=runas)</c> 会**阻塞到
    ///    用户响应 UAC 弹窗**为止，而用户完全可能在弹窗上思考十几秒 —— 那段空白期里
    ///    卸载器还没跑，快照是干净的。要是把抓取放在 <c>Process.Start</c> 之后，就等于
    ///    把"卸载器已经删了一半"的状态当成基线，差集会缺一大块。
    ///
    /// ⛔ 商店包（MSIX）**不抓快照**：它的文件都在包容器里、由系统统一收，注册表也不写
    ///    <c>Uninstall</c> 键，扫一遍只是白花时间。所以直接返回 <c>Scanned=false</c>。
    /// </summary>
    public static async Task<UninstallRun> UninstallAndScanAsync(
        InstalledApp app, CancellationToken ct = default, bool elevate = true)
    {
        // 商店包 / 被拦住的（自己、系统组件）：不抓快照，行为与以前完全一致
        if (app.IsStore || IsSelf(app) || IsProtected(app))
            return new UninstallRun(await RunAsync(app, ct, elevate), Array.Empty<LeftoverItem>(), false);

        var before = FootprintSnapshot.Capture("卸载前");

        var outcome = await RunAsync(app, ct, elevate);

        // ⛔ 超时分支（Finished=false）：卸载程序**还在跑**，这会儿复扫毫无意义
        if (!outcome.Finished)
            return new UninstallRun(outcome, Array.Empty<LeftoverItem>(), false);

        try
        {
            await Task.Delay(RescanDelayMs, ct);
        }
        catch (OperationCanceledException)
        {
            return new UninstallRun(outcome, Array.Empty<LeftoverItem>(), false);
        }

        var after = FootprintSnapshot.Capture("卸载后");
        var leftovers = LeftoverResolver.Resolve(before, after, app);

        return new UninstallRun(outcome, leftovers, true);
    }

    // ── 商店 / MSIX ────────────────────────────────────────────────────────────

    private static async Task<UninstallOutcome> RemoveStorePackageAsync(
        InstalledApp app, CancellationToken ct, bool elevate = true)
    {
        if (app.PackageFullName.Length == 0)
            return new(false, "拿不到这个商店应用的包标识，没法卸载。");

        // ① 先走系统 API：跟「设置 → 应用」里点卸载是同一个效果，运气好不用弹 UAC。
        try
        {
            var pm = new Windows.Management.Deployment.PackageManager();
            var result = await pm.RemovePackageAsync(app.PackageFullName);
            if (result is not null && result.ExtendedErrorCode is null)
            {
                AppLog.Info("uninstall", $"商店应用「{app.Name}」已按 API 移除：{app.PackageFullName}");
                // 商店包由系统统一回收，跑完就是跑完了 —— 但没有"残留体检"这回事（见 UninstallAndScanAsync）
                return new(true, $"已请求卸载「{app.Name}」。", Finished: true);
            }
            AppLog.Warning("uninstall",
                $"商店应用「{app.Name}」走 API 没成功（{result?.ErrorText ?? "无错误文本"}），改用 PowerShell");
        }
        catch (Exception ex)
        {
            // 无包身份 / 权限不够是常见情形，不算异常情况，记一条就往下走回落路
            AppLog.Exception("uninstall", ex, $"PackageManager.RemovePackageAsync（{app.Name}）");
        }

        // ② 回落：提权跑 PowerShell。管理员身份下 Remove-AppxPackage 一定能动。
        var script = $"Remove-AppxPackage -Package '{app.PackageFullName}'";
        var psiArgs = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"";
        return await StartAndWaitAsync("powershell.exe", psiArgs, app.Name, ct, elevate);
    }

    // ── 通用：提权启动 + 等它退出 ───────────────────────────────────────────────

    private static async Task<UninstallOutcome> StartAndWaitAsync(
        string exe, string args, string appName, CancellationToken ct, bool elevate = true)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = true,   // runas 要求开这个
            };

            // 提权：绝大多数卸载程序要管理员才删得动 Program Files。
            // ⚠️ elevate=false 时不设 Verb —— 卸载器自己的清单里要是写了 requireAdministrator，
            //    Windows 照样会弹 UAC；写了 asInvoker 的（per-user 安装）就一次都不弹。
            if (elevate) psi.Verb = "runas";

            // 工作目录跟着卸载程序走 —— 有些老卸载器会把日志/回滚文件写在自己旁边
            try { psi.WorkingDirectory = Path.GetDirectoryName(exe) ?? ""; } catch { /* 路径怪就交给系统定 */ }

            // ⚠️ Process.Start 带 runas 时会**阻塞到用户响应 UAC 弹窗**为止，
            //    所以必须丢后台，⛔ 别让它卡住 UI 线程。
            var p = await Task.Run(() => Process.Start(psi), ct);
            if (p is null) return new(false, $"无法启动「{appName}」的卸载程序。");

            using (p)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(WaitLimit);
                try
                {
                    await p.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // ⛔ Finished 必须给 false：我们**放弃等待** ≠ 卸载程序停了。
                    //    它多半还在那儿慢慢删，这时候复扫快照只会拍到"删到一半"，
                    //    差集里全是假残留（见 UninstallOutcome.Finished 的注释）。
                    return new(true,
                        $"「{appName}」的卸载程序仍在运行（已超过 {WaitLimit.TotalMinutes:0} 分钟），本页不再等待。" +
                        "待其运行结束后，请点击「重新扫描」查看结果。");
                }

                return new(true, p.ExitCode == 0
                    ? $"「{appName}」的卸载程序已结束。"
                    : $"「{appName}」的卸载程序已结束（返回码 {p.ExitCode}）。",
                    Finished: true);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = ERROR_CANCELLED：用户在 UAC 弹窗上点了"否"
            return new(false, "已取消：未通过管理员授权，未执行任何操作。");
        }
        catch (Exception ex)
        {
            AppLog.Exception("uninstall", ex, $"启动卸载程序（{appName}）");
            return new(false, $"启动卸载程序失败：{ex.Message}");
        }
    }

    // ── 命令解析 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 把 <c>"C:\path with space\uninst.exe" /S</c> 拆成「程序」+「参数」。
    /// 注册表里带引号/不带引号两种写法都有，都得认。
    /// </summary>
    internal static (string Exe, string Args) SplitCommand(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0) return ("", "");

        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            if (end > 1) return (s[1..end], s[(end + 1)..].Trim());
        }

        // 没引号：一路吃到 .exe 结尾。用**懒惰**匹配认第一个 .exe，
        // 这样"路径里带 .exe 字样的目录名"也不会被吃过头。
        var m = Regex.Match(s, @"^(?<exe>.+?\.exe)(?:\s+(?<args>.*))?$",
                            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (m.Success) return (m.Groups["exe"].Value.Trim(), m.Groups["args"].Value.Trim());

        // 最后手段：按第一个空格拆（有些卸载命令是 `cmd /c ...` 这类）
        var i = s.IndexOf(' ');
        return i > 0 ? (s[..i], s[(i + 1)..].Trim()) : (s, "");
    }

    private static bool IsMsiExec(string exe)
    {
        var name = Path.GetFileName(exe);
        return name.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("msiexec", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 把 MSI 的 <c>/I{...}</c>（安装/修复）换成 <c>/X{...}</c>（卸载）。
    /// 只动**第一处** /I 或 -I，后面的 /qb、/norestart 之类原样保留。
    /// </summary>
    internal static string MsiInstallToUninstall(string args)
    {
        const string pattern = @"(?<pre>^|\s)(?<sl>-|/)[iI](?=\s*\{|\s|$)";
        return new Regex(pattern).Replace(args, "${pre}${sl}X", 1);
    }
}
