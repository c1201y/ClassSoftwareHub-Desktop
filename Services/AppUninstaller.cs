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
public sealed record UninstallOutcome(bool Started, string Message);

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
    /// 这条记录**有没有**可用的卸载途径。
    /// 没有的话页面上的按钮要禁用并说明原因 —— 比让用户点了没反应强。
    /// </summary>
    public static bool CanUninstall(InstalledApp app)
        => !IsSelf(app) && (app.IsStore || app.UninstallString.Length > 0);

    /// <summary>跑一次卸载。返回值只说明"命令有没有跑起来 / 为什么没跑起来"。</summary>
    public static async Task<UninstallOutcome> RunAsync(InstalledApp app, CancellationToken ct = default)
    {
        if (IsSelf(app)) return new(false, "这是本程序自己，不能在这里卸载。");

        if (app.IsStore) return await RemoveStorePackageAsync(app, ct);

        // QuietUninstallString 优先：厂商特意给的静默版本，少一层交互。
        var raw = app.QuietUninstallString.Length > 0 ? app.QuietUninstallString : app.UninstallString;
        if (raw.Length == 0)
        {
            AppLog.Info("uninstall", $"「{app.Name}」没有 UninstallString，跳过");
            return new(false,
                "这条记录没有提供卸载命令。它多半只做了登记（例如免安装版留下的痕迹），" +
                "需要手动删掉程序目录，或者到系统「设置 → 应用」里找找看。");
        }

        var (exe, args) = SplitCommand(raw);
        if (exe.Length == 0) return new(false, "读不出卸载程序的位置。");

        // ── MSI 的 /I 是安装/修复，卸载要用 /X ──
        // UninstallString 写成 `MsiExec.exe /I{GUID}` 是微软的老习惯，直接跑会弹出
        // "修改 / 修复 / 删除"那个维护对话框，用户还得自己点"删除"。这里给它换过来。
        if (IsMsiExec(exe)) args = MsiInstallToUninstall(args);

        AppLog.Info("uninstall", $"卸载「{app.Name}」：{exe} {args}");
        return await StartAndWaitAsync(exe, args, app.Name, ct);
    }

    // ── 商店 / MSIX ────────────────────────────────────────────────────────────

    private static async Task<UninstallOutcome> RemoveStorePackageAsync(InstalledApp app, CancellationToken ct)
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
                return new(true, $"已请求卸载「{app.Name}」。");
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
        return await StartAndWaitAsync("powershell.exe", psiArgs, app.Name, ct);
    }

    // ── 通用：提权启动 + 等它退出 ───────────────────────────────────────────────

    private static async Task<UninstallOutcome> StartAndWaitAsync(
        string exe, string args, string appName, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = true,   // runas 要求开这个
                Verb = "runas",           // 提权：绝大多数卸载程序要管理员才删得动 Program Files
            };

            // 工作目录跟着卸载程序走 —— 有些老卸载器会把日志/回滚文件写在自己旁边
            try { psi.WorkingDirectory = Path.GetDirectoryName(exe) ?? ""; } catch { /* 路径怪就交给系统定 */ }

            // ⚠️ Process.Start 带 runas 时会**阻塞到用户响应 UAC 弹窗**为止，
            //    所以必须丢后台，⛔ 别让它卡住 UI 线程。
            var p = await Task.Run(() => Process.Start(psi), ct);
            if (p is null) return new(false, $"没能启动「{appName}」的卸载程序。");

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
                    return new(true,
                        $"「{appName}」的卸载程序还在运行（超过 {WaitLimit.TotalMinutes:0} 分钟），本页不再等它。" +
                        "它跑完后点「重新扫描」看结果。");
                }

                return new(true, p.ExitCode == 0
                    ? $"「{appName}」的卸载程序已结束。"
                    : $"「{appName}」的卸载程序已结束（返回码 {p.ExitCode}）。");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = ERROR_CANCELLED：用户在 UAC 弹窗上点了"否"
            return new(false, "已取消：没有通过管理员授权，什么都没做。");
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
