using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services;


public sealed record DiskEntry(string Name, string FullPath, bool IsDirectory, long Bytes, int FileCount);


public readonly record struct DiskFile(string Name, long Bytes);













public sealed class DiskNode
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";

    
    public DiskNode? Parent { get; internal set; }

    
    public long Bytes { get; internal set; }

    
    public int FileCount { get; internal set; }

    
    public long DirectFileBytes { get; internal set; }

    public int DirectFileCount { get; internal set; }

    
    public List<DiskNode> Children { get; } = new();

    
    public List<DiskFile> DirectFiles { get; } = new();

    
    public long TrimmedFileBytes { get; internal set; }

    public int TrimmedFileCount { get; internal set; }

    
    public bool Truncated { get; internal set; }
}

public sealed class DiskScanResult
{
    public string Root { get; init; } = "";

    
    public DiskNode? RootNode { get; init; }

    public List<DiskEntry> Children { get; init; } = new();
    public long TotalBytes { get; init; }
    public int FileCount { get; init; }

    
    public int SkippedDirectories { get; init; }

    
    
    
    
    public bool Truncated { get; init; }

    public TimeSpan Elapsed { get; init; }
}















public static class DiskUsage
{
    
    
    
    
    
    
    
    
    
    private const int MaxFilesPerNode = 2000;

    public static IReadOnlyList<DriveInfo> FixedDrives() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady)
            .ToList();

    
    
    
    
    
    
    
    
    
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

    
    
    
    
    
    
    
    public static List<DiskEntry> ToEntries(DiskNode node)
    {
        var list = new List<DiskEntry>(node.Children.Count + node.DirectFiles.Count);

        foreach (var child in node.Children)
            list.Add(new DiskEntry(child.Name, child.FullPath, true, child.Bytes, child.FileCount));

        
        foreach (var file in node.DirectFiles)
            list.Add(new DiskEntry(file.Name, Path.Join(node.FullPath, file.Name), false, file.Bytes, 1));

        
        if (node.TrimmedFileCount > 0)
        {
            list.Add(new DiskEntry(
                $"其他 {node.TrimmedFileCount:N0} 个文件",
                node.FullPath,
                false,
                node.TrimmedFileBytes,
                node.TrimmedFileCount));
        }

        
        list.RemoveAll(e => e.Bytes <= 0);
        list.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

        return list;
    }

    
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
            return true;   
        }
    }

    
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
