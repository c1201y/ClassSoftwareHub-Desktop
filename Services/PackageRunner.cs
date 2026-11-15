using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services;


public sealed record ExtractResult(string Directory);


public sealed record InstallResult(bool Started, bool SourceDeleted, string Message = "");













public static class PackageRunner
{
    
    
    
    
    
    public static string TargetDirFor(string archivePath)
    {
        var parent = Path.GetDirectoryName(archivePath);
        if (string.IsNullOrEmpty(parent)) parent = DownloadService.DefaultDir;

        var name = Path.GetFileNameWithoutExtension(archivePath);
        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0) name = "解压结果";

        var target = Path.Combine(parent, name);
        for (var i = 2; i < 1000 && IsOccupied(target); i++)
            target = Path.Combine(parent, $"{name} ({i})");
        return target;
    }

    private static bool IsOccupied(string dir)
    {
        try { return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch { return false; }
    }

    
    
    
    
    public static async Task<ExtractResult> ExtractAsync(
        string archivePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("压缩包不存在（可能已被移动或删除）", archivePath);

        var target = TargetDirFor(archivePath);
        Directory.CreateDirectory(target);

        try
        {
            if (Path.GetExtension(archivePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                await Task.Run(() => ExtractZip(archivePath, target, progress, ct), ct);
            else
                await ExtractWithSystemTarAsync(archivePath, target, ct);

            progress?.Report(1);
            return new ExtractResult(target);
        }
        catch
        {
            TryRemoveDirectory(target);
            throw;
        }
    }

    

    private static void ExtractZip(string zipPath, string target, IProgress<double>? progress, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        long total = 0;
        foreach (var entry in zip.Entries) total += Math.Max(0, entry.Length);

        
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;

        long done = 0;
        var reported = 0.0;

        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(entry.FullName)) continue;

            var outPath = Path.GetFullPath(Path.Combine(target, entry.FullName));
            if (!outPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"压缩包内含越界路径，已中止解压：{entry.FullName}");

            
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(outPath);
                continue;
            }

            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            entry.ExtractToFile(outPath, overwrite: true);

            done += Math.Max(0, entry.Length);
            if (total > 0)
            {
                var pct = Math.Min(1.0, (double)done / total);
                
                if (pct - reported >= 0.01 || pct >= 1.0)
                {
                    reported = pct;
                    progress?.Report(pct);
                }
            }
        }
    }

    

    private static async Task ExtractWithSystemTarAsync(string archivePath, string target, CancellationToken ct)
    {
        var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (!File.Exists(tar))
            throw new NotSupportedException(
                "当前系统未提供内置解压组件 tar.exe（需要 Windows 10 1803 及以上），请安装 7-Zip 或 WinRAR 后手动解压。");

        var psi = new ProcessStartInfo(tar)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        
        psi.ArgumentList.Add("-xf");
        psi.ArgumentList.Add(archivePath);
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(target);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动系统解压组件。");

        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode == 0) return;

        var reason = stderr.Trim();
        if (Path.GetExtension(archivePath).Equals(".rar", StringComparison.OrdinalIgnoreCase))
            throw new IOException(
                "系统内置组件无法解压该 RAR 压缩包（RAR5 需 Windows 11 内置支持）。"
                + "可安装 7-Zip 或 WinRAR 后手动解压。");

        throw new IOException(reason.Length > 0
            ? $"系统解压组件报错：{reason}"
            : $"系统解压组件返回代码 {process.ExitCode}。");
    }

    

    
    
    
    
    
    
    
    public static async Task<InstallResult> InstallAsync(
        string installerPath, bool deleteAfter, CancellationToken ct = default)
    {
        if (!File.Exists(installerPath))
            throw new FileNotFoundException("安装包不存在（可能已被移动或删除）", installerPath);

        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            
            return new InstallResult(false, false, "已取消授权，安装未开始。");
        }

        if (process is null)
            throw new InvalidOperationException("系统未接受该安装程序。");

        if (!deleteAfter)
        {
            process.Dispose();
            return new InstallResult(true, false);
        }

        using (process)
        {
            
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromHours(24));
            try
            {
                await process.WaitForExitAsync(wait.Token);
            }
            catch (OperationCanceledException)
            {
                return new InstallResult(true, false, "安装程序尚未结束，安装包已保留。");
            }
        }

        await Task.Delay(800, CancellationToken.None);   
        var deleted = await TryDeleteAsync(installerPath, CancellationToken.None);
        return new InstallResult(true, deleted, deleted ? "" : "安装包仍被占用，未能删除。");
    }

    

    
    
    
    
    
    
    
    
    
    
    public static async Task RunAsync(DownloadTask task)
    {
        if (task.Path is not { Length: > 0 } path) return;

        if (!File.Exists(path))
        {
            task.SetWork(false, "文件已不在原位置，可能已被移动或删除。");
            return;
        }

        var settings = App.Settings.Current;
        var autoDelete = settings.AutoDeletePackage;
        var autoOpen = settings.AutoOpenExtractFolder;
        var installing = task.Action == PackageAction.Install;

        try
        {
            if (installing)
            {
                task.SetWork(false, "正在启动安装程序…");
                var result = await InstallAsync(path, autoDelete);
                task.SetWork(false, !result.Started
                    ? result.Message
                    : result.SourceDeleted
                        ? "安装程序已结束，安装包已删除。"
                        : result.Message.Length > 0
                            ? result.Message
                            : "安装程序已启动。");

                Core.AppLog.Info("package",
                    $"启动第三方安装程序「{task.Title}」· 自动删除={autoDelete} · 已删除={result.SourceDeleted}");
                return;
            }

            task.SetWork(true, "正在解压…");
            var progress = new Progress<double>(p =>
            {
                if (p < 1) task.WorkText = $"正在解压 {p:P0}";
            });

            var extracted = await ExtractAsync(path, progress, CancellationToken.None);
            task.ExtractedDir = extracted.Directory;

            var deleted = autoDelete && await TryDeleteAsync(path);
            task.SetWork(false, deleted
                ? $"已解压至 {extracted.Directory}（已删除压缩包）"
                : $"已解压至 {extracted.Directory}");

            Core.AppLog.Done("package",
                $"解压完成「{task.Title}」→ {extracted.Directory} · 自动删除={autoDelete} · 已删除={deleted}");

            
            if (autoOpen)
            {
                App.MainWindow?.RevealFile(extracted.Directory);
                Core.AppLog.Info("package", $"已按开关自动打开产物目录：{extracted.Directory}");
            }
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("package", ex, $"{(installing ? "安装" : "解压")}「{task.Title}」");
            task.SetWork(false, $"{(installing ? "启动安装程序失败" : "解压失败")}：{ex.Message}");
        }
    }

    

    
    public static async Task<bool> TryDeleteAsync(string path, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return true;
                File.SetAttributes(path, FileAttributes.Normal);   
                File.Delete(path);
                return !File.Exists(path);
            }
            catch (IOException) {  }
            catch (UnauthorizedAccessException) {  }

            if (attempt < 4)
            {
                try { await Task.Delay(1200, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        try { return !File.Exists(path); } catch { return false; }
    }

    
    private static void TryRemoveDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Core.AppLog.Info("package",
                $"解压失败后清理半截产物未能完成（不影响主流程）：{dir} · {ex.Message}");
        }
    }
}
