using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>解压收尾：产物目录在哪。</summary>
public sealed record ExtractResult(string Directory);

/// <summary>安装收尾：有没有起来、源安装包删没删、有没有话要带给用户。</summary>
public sealed record InstallResult(bool Started, bool SourceDeleted, string Message = "");

/// <summary>
/// 下载完之后"再走一步"的两个动作：解压压缩包、启动安装程序。
///
/// ⛔ 不引第三方压缩库（SharpCompress 之类），两个通道就够了：
///   · <b>.zip</b> → BCL 的 <see cref="ZipFile"/>，逐条目解压，能报进度，也能自己拦 Zip Slip；
///   · <b>其余格式</b>（7z / tar / tar.gz / tar.xz / bz2 / zst / rar…）→ 系统自带的
///     <c>%SystemRoot%\System32\tar.exe</c>（bsdtar + libarchive，Windows 10 1803 起就有）。
///
/// 覆盖度是量过的：站点 61 个压缩包里 .zip 占 52（85%），其余是 3 个 .tar.xz、3 个 .tar.gz、
/// 2 个 .7z、1 个 .rar —— 前四类两个通道都能吃下；.rar 只有 1 个，且能否解开取决于系统
/// libarchive 版本（Windows 11 可以，Windows 10 不行），解不开就如实报错，不装作支持。
/// </summary>
public static class PackageRunner
{
    /// <summary>
    /// 解压到哪儿：与压缩包**同级**的同名文件夹（<c>D:\下载\foo.zip</c> → <c>D:\下载\foo\</c>）。
    /// 同名目录已存在且非空时顺延 <c>foo (2)</c>、<c>foo (3)</c> —— 绝不往一个已经有东西的目录里倒。
    /// <c>.tar.gz</c> 这类复合后缀连剥两层，免得解出个 <c>foo.tar</c> 文件夹。
    /// </summary>
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

    /// <summary>
    /// 解压到 <see cref="TargetDirFor"/>。失败时把半截产物清掉 —— 留着一堆解了一半的文件，
    /// 用户既看不出哪些是完整的，也没法「重来一遍」。
    /// </summary>
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

    // ── .zip：BCL ────────────────────────────────────────────────────

    private static void ExtractZip(string zipPath, string target, IProgress<double>? progress, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        long total = 0;
        foreach (var entry in zip.Entries) total += Math.Max(0, entry.Length);

        // 防 Zip Slip：条目名里带 ..\ 或绝对路径时，拼出来的落点会跑到目标目录外面
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

            // 目录条目（名字以分隔符结尾）
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
                // 条目多的时候逐条报会刷爆 UI 线程（Progress<T> 每次都 Post），按 1% 节流
                if (pct - reported >= 0.01 || pct >= 1.0)
                {
                    reported = pct;
                    progress?.Report(pct);
                }
            }
        }
    }

    // ── 其余格式：系统 bsdtar ────────────────────────────────────────

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
        // ArgumentList 会自己处理引号与空格 —— 中文路径、带空格的路径都不会被拆开
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

    // ── 安装 ────────────────────────────────────────────────────────

    /// <summary>
    /// 启动安装程序（UAC 由系统自己弹，不归我们管）。
    ///
    /// <paramref name="deleteAfter"/> 为真时**等安装程序自己退出**再删安装包 ——
    /// 早删会删到安装程序正在读的文件，只能等它走完。等待期间应用被关掉 ⇒ 等待中断，
    /// 安装包留在原处（少删一次而已，不会损坏任何东西）。
    /// </summary>
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
            // 1223 = 用户在 UAC 上点了「否」
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
            // 上限 24 小时纯粹是防僵尸；正常情况就是等用户装完
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

        await Task.Delay(800, CancellationToken.None);   // 句柄未必立刻放开，稍等一下再删
        var deleted = await TryDeleteAsync(installerPath, CancellationToken.None);
        return new InstallResult(true, deleted, deleted ? "" : "安装包仍被占用，未能删除。");
    }

    // ── 编排：对一条已完成的任务执行「安装 / 解压」────────────────────

    /// <summary>
    /// 按任务自己的类型执行那一步（安装 / 解压），把过程写回卡片、按开关删源文件、按开关打开产物目录。
    ///
    /// ⚠️ 任务页那颗按钮和「下载完成」弹窗都走这里 —— 两边各写一套的话，
    ///    进度文案、失败说法、删除时机迟早各说各的。
    /// ⚠️ 两个开关都**在方法内部读存档**，不由调用方传：弹窗允许用户当场改勾选，
    ///    传参就得保证两边都把最新值传进来，读存档没这个隐患。
    /// ⚠️ 必须在 UI 线程调（<see cref="DownloadTask.SetWork"/> 会刷绑定；
    ///    <see cref="Progress{T}"/> 也只有在 UI 线程建才捕获得到同步上下文）。
    /// </summary>
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

            // 解压本来就是为了去那个目录拿东西，所以默认直接把它打开（可在任务页取消勾选）
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

    // ── 小工具 ──────────────────────────────────────────────────────

    /// <summary>删一个文件，被占用就重试几次（安装程序/压缩工具常常晚一步才松手）。</summary>
    public static async Task<bool> TryDeleteAsync(string path, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return true;
                File.SetAttributes(path, FileAttributes.Normal);   // 只读属性会让 Delete 直接抛
                File.Delete(path);
                return !File.Exists(path);
            }
            catch (IOException) { /* 被占用：等一下再试 */ }
            catch (UnauthorizedAccessException) { /* 同上 */ }

            if (attempt < 4)
            {
                try { await Task.Delay(1200, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        try { return !File.Exists(path); } catch { return false; }
    }

    /// <summary>只删空目录树（解压失败后的清理；有东西就说明不是我们建的，别动）。</summary>
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
