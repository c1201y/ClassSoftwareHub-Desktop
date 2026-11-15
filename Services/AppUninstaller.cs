using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;













public sealed record UninstallOutcome(bool Started, string Message, bool Finished = false);






public sealed record UninstallRun(
    UninstallOutcome Outcome,
    IReadOnlyList<LeftoverItem> Leftovers,
    bool Scanned);
















public static class AppUninstaller
{
    
    private static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(20);

    
    public static bool IsSelf(InstalledApp app)
        => InstalledApps.Normalize(app.Name).Contains("classsoftwarehub", StringComparison.Ordinal);

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    public static bool IsProtected(InstalledApp app) => app.Category == AppCategory.System;

    
    
    
    
    public static bool CanUninstall(InstalledApp app)
        => !IsSelf(app) && !IsProtected(app) && (app.IsStore || app.UninstallString.Length > 0);

    
    
    
    
    
    
    public static async Task<UninstallOutcome> RunAsync(
        InstalledApp app, CancellationToken ct = default, bool elevate = true)
    {
        if (IsSelf(app)) return new(false, "这是本程序自己，不能在这里卸载。");

        
        
        if (IsProtected(app))
        {
            AppLog.Warning("uninstall", $"拒绝卸载 Windows 自带组件：{app.Name}（{app.PackageFullName}）");
            return new(false,
                $"「{app.Name}」为 Windows 系统组件，移除后将导致对应系统功能失效，本程序不予执行。" +
                "如确需移除，请使用系统自带工具操作。");
        }

        if (app.IsStore) return await RemoveStorePackageAsync(app, ct, elevate);

        
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

        
        
        
        if (IsMsiExec(exe)) args = MsiInstallToUninstall(args);

        AppLog.Info("uninstall", $"卸载「{app.Name}」：{exe} {args}");
        return await StartAndWaitAsync(exe, args, app.Name, ct, elevate);
    }

    

    
    
    
    
    
    
    
    
    private const int RescanDelayMs = 2000;

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    public static async Task<UninstallRun> UninstallAndScanAsync(
        InstalledApp app, CancellationToken ct = default, bool elevate = true)
    {
        
        if (app.IsStore || IsSelf(app) || IsProtected(app))
            return new UninstallRun(await RunAsync(app, ct, elevate), Array.Empty<LeftoverItem>(), false);

        var before = FootprintSnapshot.Capture("卸载前");

        var outcome = await RunAsync(app, ct, elevate);

        
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

    

    private static async Task<UninstallOutcome> RemoveStorePackageAsync(
        InstalledApp app, CancellationToken ct, bool elevate = true)
    {
        if (app.PackageFullName.Length == 0)
            return new(false, "拿不到这个商店应用的包标识，没法卸载。");

        
        try
        {
            var pm = new Windows.Management.Deployment.PackageManager();
            var result = await pm.RemovePackageAsync(app.PackageFullName);
            if (result is not null && result.ExtendedErrorCode is null)
            {
                AppLog.Info("uninstall", $"商店应用「{app.Name}」已按 API 移除：{app.PackageFullName}");
                
                return new(true, $"已请求卸载「{app.Name}」。", Finished: true);
            }
            AppLog.Warning("uninstall",
                $"商店应用「{app.Name}」走 API 没成功（{result?.ErrorText ?? "无错误文本"}），改用 PowerShell");
        }
        catch (Exception ex)
        {
            
            AppLog.Exception("uninstall", ex, $"PackageManager.RemovePackageAsync（{app.Name}）");
        }

        
        var script = $"Remove-AppxPackage -Package '{app.PackageFullName}'";
        var psiArgs = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"";
        return await StartAndWaitAsync("powershell.exe", psiArgs, app.Name, ct, elevate);
    }

    

    private static async Task<UninstallOutcome> StartAndWaitAsync(
        string exe, string args, string appName, CancellationToken ct, bool elevate = true)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = true,   
            };

            
            
            
            if (elevate) psi.Verb = "runas";

            
            try { psi.WorkingDirectory = Path.GetDirectoryName(exe) ?? ""; } catch {  }

            
            
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
            
            return new(false, "已取消：未通过管理员授权，未执行任何操作。");
        }
        catch (Exception ex)
        {
            AppLog.Exception("uninstall", ex, $"启动卸载程序（{appName}）");
            return new(false, $"启动卸载程序失败：{ex.Message}");
        }
    }

    

    
    
    
    
    internal static (string Exe, string Args) SplitCommand(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0) return ("", "");

        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            if (end > 1) return (s[1..end], s[(end + 1)..].Trim());
        }

        
        
        var m = Regex.Match(s, @"^(?<exe>.+?\.exe)(?:\s+(?<args>.*))?$",
                            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (m.Success) return (m.Groups["exe"].Value.Trim(), m.Groups["args"].Value.Trim());

        
        var i = s.IndexOf(' ');
        return i > 0 ? (s[..i], s[(i + 1)..].Trim()) : (s, "");
    }

    private static bool IsMsiExec(string exe)
    {
        var name = Path.GetFileName(exe);
        return name.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("msiexec", StringComparison.OrdinalIgnoreCase);
    }

    
    
    
    
    internal static string MsiInstallToUninstall(string args)
    {
        const string pattern = @"(?<pre>^|\s)(?<sl>-|/)[iI](?=\s*\{|\s|$)";
        return new Regex(pattern).Replace(args, "${pre}${sl}X", 1);
    }
}
