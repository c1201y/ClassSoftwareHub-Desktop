using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>磁盘可视化里的一块：一个子项及其占用。</summary>
public sealed record DiskEntry(string Name, string FullPath, bool IsDirectory, long Bytes, int FileCount);

public sealed class DiskScanResult
{
    public string Root { get; init; } = "";
    public List<DiskEntry> Children { get; init; } = new();
    public long TotalBytes { get; init; }
    public int FileCount { get; init; }
    /// <summary>没有权限、读不了的目录数（界面上要说出来，否则数字对不上会让人以为漏算）。</summary>
    public int SkippedDirectories { get; init; }
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// 磁盘占用扫描：把某个目录的**直接子项**各自递归求和。
///
/// <para>
/// ⛔ 性能是这一块的命门（需求评估里就写了）：全盘几十万个文件，
/// 用 <c>DirectoryInfo</c> 逐个 <c>GetFiles()</c> 会慢一倍以上。
/// 这里每一层只调一次 <see cref="DirectoryInfo.EnumerateFileSystemInfos()"/>，
/// 文件直接用枚举回来的 <c>Length</c>（不额外 stat），目录压栈继续。
/// </para>
/// <para>
/// ⛔ 必须跳过重解析点（junction / 符号链接）：<c>C:\Users\All Users</c> 这种指向别处，
/// 跟进去要么重复计数、要么无限递归。
/// </para>
/// </summary>
public static class DiskUsage
{
    public static IReadOnlyList<DriveInfo> FixedDrives() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady)
            .ToList();

    /// <summary>
    /// 扫描 <paramref name="root"/> 的每个直接子项。
    /// </summary>
    /// <param name="onProgress">扫描进度（当前目录路径、已累计字节、已数文件数）。</param>
    public static Task<DiskScanResult> ScanAsync(
        string root,
        CancellationToken token,
        Action<string, long, int>? onProgress = null)
    {
        return Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            var children = new List<DiskEntry>();
            var skipped = 0;
            long total = 0;
            var files = 0;

            var rootInfo = new DirectoryInfo(root);
            if (!rootInfo.Exists) throw new DirectoryNotFoundException(root);

            foreach (var child in SafeEntries(rootInfo, ref skipped))
            {
                token.ThrowIfCancellationRequested();

                if (child is FileInfo file)
                {
                    children.Add(new DiskEntry(file.Name, file.FullName, false, file.Length, 1));
                    total += file.Length;
                    files++;
                    continue;
                }

                if (child is not DirectoryInfo dir) continue;

                if (IsReparse(dir)) continue;   // junction：跳过，否则重复计数

                var (bytes, count) = Measure(dir, token, ref skipped, watch, onProgress);
                children.Add(new DiskEntry(dir.Name, dir.FullName, true, bytes, count));
                total += bytes;
                files += count;
            }

            children.RemoveAll(c => c.Bytes <= 0);   // 0 字节的东西在树图上没有面积，画不出来
            children.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

            watch.Stop();

            return new DiskScanResult
            {
                Root = root,
                Children = children,
                TotalBytes = total,
                FileCount = files,
                SkippedDirectories = skipped,
                Elapsed = watch.Elapsed,
            };
        }, token);
    }

    /// <summary>递归求一个目录的大小（显式栈，不用递归调用以防深目录爆栈）。</summary>
    private static (long Bytes, int Files) Measure(
        DirectoryInfo start,
        CancellationToken token,
        ref int skipped,
        Stopwatch watch,
        Action<string, long, int>? onProgress)
    {
        long bytes = 0;
        var files = 0;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(start);

        var lastReport = 0L;
        var visited = 0;
        var localSkipped = 0;

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            visited++;

            // 每 64 个目录看一次取消/进度，太频繁反而拖慢
            if ((visited & 0x3F) == 0)
            {
                token.ThrowIfCancellationRequested();

                var now = watch.ElapsedMilliseconds;
                if (onProgress is not null && now - lastReport > 250)
                {
                    lastReport = now;
                    onProgress(dir.FullName, bytes, files);
                }
            }

            foreach (var entry in SafeEntries(dir, ref localSkipped))
            {
                if (entry is FileInfo file)
                {
                    bytes += file.Length;
                    files++;
                    continue;
                }

                if (entry is DirectoryInfo sub && !IsReparse(sub))
                    stack.Push(sub);
            }
        }

        skipped += localSkipped;
        return (bytes, files);
    }

    /// <summary>
    /// 枚举一层，出异常（无权限、设备未就绪）就当这一层读不到：
    /// 记一笔跳过，不让整次扫描崩掉。
    /// </summary>
    private static IEnumerable<FileSystemInfo> SafeEntries(DirectoryInfo dir, ref int skipped)
    {
        FileSystemInfo[] items;
        try
        {
            items = dir.GetFileSystemInfos();
        }
        catch
        {
            skipped++;
            return Array.Empty<FileSystemInfo>();
        }

        return items;
    }

    private static bool IsReparse(DirectoryInfo dir)
    {
        try
        {
            return (dir.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;   // 读不到属性就当它是，别跟进去
        }
    }

    /// <summary>把字节数写成 "12.3 GB" 这种（磁盘容量按 1024 进制，和资源管理器一致）。</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
