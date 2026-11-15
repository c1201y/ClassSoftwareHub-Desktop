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

/// <summary>某个目录的直属文件。<see cref="DiskNode"/> 只存名字与大小，完整路径由上级拼。</summary>
public readonly record struct DiskFile(string Name, long Bytes);

/// <summary>
/// 扫描树节点。**只给目录建节点**，文件按「直属本层」记在节点上。
///
/// <para>
/// 为什么要建树（2026-11-08）：原来只返回当前层的汇总，点进任何一层都得把整个子树重新数一遍，
/// 一次下钻等于重扫一次磁盘。建树之后下钻、上钻、点面包屑全是内存操作，不再读盘。
/// </para>
/// <para>
/// ⛔ 不要给文件也建节点：全盘几十万个文件，每个节点都要带上自己的路径与父子关系，
/// 内存会翻好几倍，而树图最多画 90 块、列表只显示前 5 项，根本用不上。
/// </para>
/// </summary>
public sealed class DiskNode
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";

    /// <summary>上级目录。根节点为 <c>null</c>。面包屑上钻直接沿它往回走。</summary>
    public DiskNode? Parent { get; internal set; }

    /// <summary>本节点连同全部后代的总字节。</summary>
    public long Bytes { get; internal set; }

    /// <summary>本节点连同全部后代的文件数。</summary>
    public int FileCount { get; internal set; }

    /// <summary>直属本层的文件合计（不含子目录）。饼图里「文件」那一块用它。</summary>
    public long DirectFileBytes { get; internal set; }

    public int DirectFileCount { get; internal set; }

    /// <summary>子目录，按占用从大到小排好。</summary>
    public List<DiskNode> Children { get; } = new();

    /// <summary>直属本层的文件，按大小从大到小排好（超过上限的部分被并进下面两个数）。</summary>
    public List<DiskFile> DirectFiles { get; } = new();

    /// <summary>按 <see cref="DiskUsage.MaxFilesPerNode"/> 截掉的小文件合计，渲染时并成「其他文件」。</summary>
    public long TrimmedFileBytes { get; internal set; }

    public int TrimmedFileCount { get; internal set; }

    /// <summary>这一支在扫描时被时间预算截断，数字偏小。</summary>
    public bool Truncated { get; internal set; }
}

public sealed class DiskScanResult
{
    public string Root { get; init; } = "";

    /// <summary>整棵树。下钻用它的 <see cref="DiskNode.Children"/>，不必再扫。</summary>
    public DiskNode? RootNode { get; init; }

    public List<DiskEntry> Children { get; init; } = new();
    public long TotalBytes { get; init; }
    public int FileCount { get; init; }

    /// <summary>没有权限、读不了的目录数（界面上要说出来，否则数字对不上会让人以为漏算）。</summary>
    public int SkippedDirectories { get; init; }

    /// <summary>
    /// 扫描到了时间预算就停止，**结果是不完整的**（2026-10-18 加）。
    /// ⚠️ 用它的地方必须把这件事告诉用户/模型 —— 不标注的话，"总占用"看着像个准确数。
    /// </summary>
    public bool Truncated { get; init; }

    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// 磁盘占用扫描：把某个目录整棵树数出来，每一层都能直接取用。
///
/// <para>
/// ⛔ 性能是这一块的命门（需求评估里就写了）：全盘几十万个文件，
/// 用 <c>DirectoryInfo</c> 逐个 <c>GetFiles()</c> 会慢一倍以上。
/// 这里每一层只调一次 <see cref="DirectoryInfo.GetFileSystemInfos()"/>，
/// 文件直接用枚举回来的 <c>Length</c>（不额外 stat），目录压栈继续。
/// </para>
/// <para>
/// ⛔ 必须跳过重解析点（junction / 符号链接）：<c>C:\Users\All Users</c> 这种指向别处，
/// 跟进去要么重复计数、要么无限递归。
/// </para>
/// </summary>
public static class DiskUsage
{
    /// <summary>
    /// 单个目录最多保留多少个直属文件条目。
    ///
    /// <para>
    /// 树图最多画 90 块、列表只显示 5 项，留几千条已经远超需要；
    /// 但扫整个 C 盘时某些目录确实有上万个小文件，全留会把内存推到几十 MB 以上。
    /// 超出部分不丢字节数，只是并成一块「其他文件」。
    /// </para>
    /// </summary>
    private const int MaxFilesPerNode = 2000;

    public static IReadOnlyList<DriveInfo> FixedDrives() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady)
            .ToList();

    /// <summary>
    /// 扫描 <paramref name="root"/> 整棵树。
    /// </summary>
    /// <param name="onProgress">扫描进度（当前目录路径、已累计字节、已数文件数）。</param>
    /// <param name="budget">
    /// 时间预算（2026-10-18）。到点就停止递归，返回**已统计到的部分**并置 <see cref="DiskScanResult.Truncated"/>。
    /// ⚠️ 整盘扫描动辄上百万文件，靠"超时就整条失败"体验极差 —— 让用户/模型先拿到部分结果，
    /// 比等一分半然后什么也没有强。传 <c>null</c> 表示不设限（老行为）。
    /// </param>
    public static Task<DiskScanResult> ScanAsync(
        string root,
        CancellationToken token,
        Action<string, long, int>? onProgress = null,
        TimeSpan? budget = null)
    {
        return Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            var skipped = 0;

            var rootInfo = new DirectoryInfo(root);
            if (!rootInfo.Exists) throw new DirectoryNotFoundException(root);

            var rootNode = new DiskNode { Name = rootInfo.Name, FullPath = rootInfo.FullName };

            // 显式栈，不用递归：全盘目录深度不可控，递归写法遇到极深的路径会爆栈
            var stack = new Stack<(DiskNode Node, DirectoryInfo Dir)>();
            stack.Push((rootNode, rootInfo));

            var visited = 0;
            var lastReport = 0L;
            long seenBytes = 0;
            var seenFiles = 0;
            var truncated = false;

            while (stack.Count > 0)
            {
                var (node, dir) = stack.Pop();
                visited++;

                // 每 64 个目录看一次取消/进度/预算，太频繁反而拖慢
                if ((visited & 0x3F) == 0)
                {
                    token.ThrowIfCancellationRequested();

                    var now = watch.ElapsedMilliseconds;
                    if (onProgress is not null && now - lastReport > 250)
                    {
                        lastReport = now;
                        onProgress(dir.FullName, seenBytes, seenFiles);
                    }

                    if (budget is { } limit && watch.Elapsed >= limit)
                    {
                        truncated = true;
                        break;
                    }
                }

                foreach (var entry in SafeEntries(dir, ref skipped))
                {
                    if (entry is FileInfo file)
                    {
                        node.DirectFileBytes += file.Length;
                        node.DirectFileCount++;
                        node.DirectFiles.Add(new DiskFile(file.Name, file.Length));
                        seenBytes += file.Length;
                        seenFiles++;
                        continue;
                    }

                    if (entry is DirectoryInfo sub && !IsReparse(sub))
                    {
                        var child = new DiskNode
                        {
                            Name = sub.Name,
                            FullPath = sub.FullName,
                            Parent = node,
                        };
                        node.Children.Add(child);
                        stack.Push((child, sub));
                    }
                }
            }

            Aggregate(rootNode, truncated);

            watch.Stop();

            return new DiskScanResult
            {
                Root = root,
                RootNode = rootNode,
                Children = ToEntries(rootNode),
                TotalBytes = rootNode.Bytes,
                FileCount = rootNode.FileCount,
                SkippedDirectories = skipped,
                Truncated = truncated,
                Elapsed = watch.Elapsed,
            };
        }, token);
    }

    /// <summary>
    /// 把某个节点的下一层摊平成 <see cref="DiskEntry"/>：子目录 + 直属文件，按大小降序。
    ///
    /// <para>
    /// 下钻、上钻、点面包屑都走它 —— 只读内存，不碰磁盘。
    /// </para>
    /// </summary>
    public static List<DiskEntry> ToEntries(DiskNode node)
    {
        var list = new List<DiskEntry>(node.Children.Count + node.DirectFiles.Count);

        foreach (var child in node.Children)
            list.Add(new DiskEntry(child.Name, child.FullPath, true, child.Bytes, child.FileCount));

        // 直属文件的完整路径在这里现拼，节点里不存，省一份字符串
        foreach (var file in node.DirectFiles)
            list.Add(new DiskEntry(file.Name, Path.Join(node.FullPath, file.Name), false, file.Bytes, 1));

        // 被截掉的小文件合成一块，否则这块占用会在树图上凭空消失
        if (node.TrimmedFileCount > 0)
        {
            list.Add(new DiskEntry(
                $"其他 {node.TrimmedFileCount:N0} 个文件",
                node.FullPath,
                false,
                node.TrimmedFileBytes,
                node.TrimmedFileCount));
        }

        // 0 字节的东西在树图上没有面积，画不出来；文件列表里留着也只是噪音
        list.RemoveAll(e => e.Bytes <= 0);
        list.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

        return list;
    }

    /// <summary>按完整路径在树里找节点，找不到返回 <c>null</c>（调用方回退到真正扫描）。</summary>
    public static DiskNode? Find(DiskNode root, string path)
    {
        if (string.Equals(root.FullPath, path, StringComparison.OrdinalIgnoreCase)) return root;

        if (!path.StartsWith(root.FullPath, StringComparison.OrdinalIgnoreCase)) return null;

        var rest = path[root.FullPath.Length..].TrimStart(Path.DirectorySeparatorChar);
        if (rest.Length == 0) return root;

        var current = root;
        foreach (var part in rest.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            DiskNode? next = null;
            foreach (var child in current.Children)
            {
                if (string.Equals(child.Name, part, StringComparison.OrdinalIgnoreCase))
                {
                    next = child;
                    break;
                }
            }

            if (next is null) return null;
            current = next;
        }

        return current;
    }

    /// <summary>
    /// 自底向上汇总（显式后序，不递归）：
    /// 先算出每个节点自身与全部后代的总和，再按占用给子项排好序。
    /// </summary>
    private static void Aggregate(DiskNode root, bool truncated)
    {
        var stack = new Stack<(DiskNode Node, bool Expanded)>();
        stack.Push((root, false));

        while (stack.Count > 0)
        {
            var (node, expanded) = stack.Pop();

            if (!expanded)
            {
                stack.Push((node, true));
                foreach (var child in node.Children) stack.Push((child, false));
                continue;
            }

            var bytes = node.DirectFileBytes;
            var files = node.DirectFileCount;

            foreach (var child in node.Children)
            {
                bytes += child.Bytes;
                files += child.FileCount;
            }

            node.Bytes = bytes;
            node.FileCount = files;
            node.Truncated = truncated;

            node.Children.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            node.DirectFiles.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

            if (node.DirectFiles.Count > MaxFilesPerNode)
            {
                for (var i = MaxFilesPerNode; i < node.DirectFiles.Count; i++)
                {
                    node.TrimmedFileBytes += node.DirectFiles[i].Bytes;
                    node.TrimmedFileCount++;
                }

                node.DirectFiles.RemoveRange(
                    MaxFilesPerNode, node.DirectFiles.Count - MaxFilesPerNode);
            }
        }
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
