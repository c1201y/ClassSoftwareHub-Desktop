using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;



























public static class GhostFile
{
    
    public sealed class GhostEntry
    {
        
        public string Parent { get; init; } = "";

        
        public string Name { get; init; } = "";

        
        public string LongPath { get; init; } = "";

        
        public string ShortName { get; init; } = "";

        public bool IsDirectory { get; init; }

        
        public long Size { get; init; }

        
        public string DisplayName { get; init; } = "";

        
        public string Reason { get; init; } = "";
    }

    
    public sealed class ScanResult
    {
        public List<GhostEntry> Items { get; init; } = new();

        
        public string Error { get; init; } = "";

        
        public int Total { get; init; }
    }

    

    
    
    
    public static ScanResult Scan(string parentDir)
    {
        var items = new List<GhostEntry>();

        try
        {
            var parent = Path.GetFullPath(parentDir.Trim().Trim('"'));
            if (!Directory.Exists(parent))
                return new ScanResult { Error = "这个目录不存在，或者没有访问权限。" };

            var longParent = ToLongPath(parent);
            var pattern = longParent.TrimEnd('\\') + @"\*";

            var total = 0;
            var handle = Native.FindFirstFileW(pattern, out var data);
            if (handle == Native.InvalidHandleValue)
                return new ScanResult { Error = DescribeWin32(Marshal.GetLastWin32Error()) };

            try
            {
                do
                {
                    var name = data.cFileName;
                    if (name is "." or "..") continue;
                    total++;

                    var entry = Inspect(parent, longParent, name, data);
                    if (entry is not null) items.Add(entry);
                }
                while (Native.FindNextFileW(handle, out data));
            }
            finally
            {
                Native.FindClose(handle);
            }

            items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return new ScanResult { Items = items, Total = total };
        }
        catch (Exception ex)
        {
            AppLog.Exception("ghost", ex, "扫描幽灵文件");
            return new ScanResult { Error = "扫描时出错：" + ex.Message };
        }
    }

    
    
    
    public static bool Delete(GhostEntry entry, out string error)
    {
        error = "";

        if (TryDelete(entry.LongPath, entry.IsDirectory, out error)) return true;
        AppLog.Warning("ghost", $"用长路径删不掉「{entry.Name}」：{error}，改用短名再试");

        if (!string.IsNullOrEmpty(entry.ShortName))
        {
            var dirPart = entry.LongPath[..(entry.LongPath.Length - entry.Name.Length)];
            if (TryDelete(dirPart + entry.ShortName, entry.IsDirectory, out error)) return true;

            var plain = Path.Combine(entry.Parent, entry.ShortName);
            if (TryDelete(plain, entry.IsDirectory, out error)) return true;
        }

        return false;
    }

    
    
    
    
    
    
    public static void RefreshExplorer(string parentDir)
    {
        try
        {
            var parent = Path.GetFullPath(parentDir.Trim().Trim('"'));
            Native.SHChangeNotify(Native.ShcneUpdateDir, Native.ShcnfPathW | Native.ShcnfFlush, parent, IntPtr.Zero);
            AppLog.Info("ghost", "已请求资源管理器刷新：" + parent);
        }
        catch (Exception ex)
        {
            AppLog.Warning("ghost", "刷新资源管理器失败：" + ex.Message);
        }
    }

    
    public static string Visualize(string name, out int trailingSpaces, out int trailingDots)
    {
        trailingSpaces = 0;
        trailingDots = 0;

        var end = name.Length;
        while (end > 0 && (name[end - 1] == ' ' || name[end - 1] == '.'))
        {
            if (name[end - 1] == ' ') trailingSpaces++;
            else trailingDots++;
            end--;
        }

        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < end; i++)
        {
            var c = name[i];
            sb.Append(c < 32 || c == 127 ? '·' : c);
        }

        sb.Append('␣', trailingSpaces);
        sb.Append('·', trailingDots);
        return sb.ToString();
    }

    

    
    private static GhostEntry? Inspect(string parent, string longParent, string name, Native.Win32FindData data)
    {
        var normalPath = Path.Combine(parent, name);

        
        if (Native.GetFileAttributesW(normalPath) != Native.InvalidFileAttributes) return null;

        
        
        var reserved = IsReservedName(name);

        
        var longPath = longParent.TrimEnd('\\') + "\\" + name;
        var attr = Native.GetFileAttributesW(longPath);
        if (attr == Native.InvalidFileAttributes && !reserved) return null;

        var isDir = attr != Native.InvalidFileAttributes
                    && (attr & Native.FileAttributeDirectory) != 0;

        var size = isDir ? -1L : ((long)data.FileSizeHigh << 32) | data.FileSizeLow;

        var display = Visualize(name, out var spaces, out var dots);

        var reasons = new List<string>();
        if (spaces > 0) reasons.Add($"末尾有 {spaces} 个空格");
        if (dots > 0) reasons.Add($"末尾有 {dots} 个点");
        if (reserved) reasons.Add("是系统保留名");
        if (reasons.Count == 0) reasons.Add("名字含系统不接受的字符");

        return new GhostEntry
        {
            Parent = parent,
            Name = name,
            LongPath = longPath,
            ShortName = data.AlternateFileName ?? "",
            IsDirectory = isDir,
            Size = size,
            DisplayName = display,
            Reason = string.Join("、", reasons),
        };
    }

    
    private static bool IsReservedName(string name)
    {
        var dot = name.IndexOf('.');
        var stem = (dot >= 0 ? name[..dot] : name).TrimEnd(' ').ToUpperInvariant();

        if (stem is "CON" or "PRN" or "AUX" or "NUL") return true;

        return stem.Length == 4
               && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
               && stem[3] is >= '1' and <= '9';
    }

    
    
    
    
    private static bool TryDelete(string path, bool isDirectory, out string error)
    {
        error = "";

        try
        {
            if (!isDirectory)
            {
                MakeWritable(path);
                if (Native.DeleteFileW(path)) return true;

                var code = Marshal.GetLastWin32Error();
                error = DescribeWin32(code);
                return false;
            }

            
            var pattern = path.TrimEnd('\\') + @"\*";
            var handle = Native.FindFirstFileW(pattern, out var data);
            if (handle != Native.InvalidHandleValue)
            {
                try
                {
                    do
                    {
                        var child = data.cFileName;
                        if (child is "." or "..") continue;

                        var childPath = path.TrimEnd('\\') + "\\" + child;
                        var childIsDir = (data.FileAttributes & Native.FileAttributeDirectory) != 0
                                         && (data.FileAttributes & Native.FileAttributeReparsePoint) == 0;

                        TryDelete(childPath, childIsDir, out _);   
                    }
                    while (Native.FindNextFileW(handle, out data));
                }
                finally
                {
                    Native.FindClose(handle);
                }
            }

            if (Native.RemoveDirectoryW(path)) return true;

            error = DescribeWin32(Marshal.GetLastWin32Error());
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void MakeWritable(string path)
    {
        var attr = Native.GetFileAttributesW(path);
        if (attr == Native.InvalidFileAttributes) return;
        if ((attr & Native.FileAttributeReadOnly) == 0) return;

        Native.SetFileAttributesW(path, Native.FileAttributeNormal);
    }

    
    private static string ToLongPath(string full)
    {
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + full[2..];
        return @"\\?\" + full;
    }

    
    private static string DescribeWin32(int code) => code switch
    {
        2 => "找不到路径",
        3 => "找不到路径",
        5 => "没有权限",
        32 => "文件正被别的程序占用",
        123 => "名字格式不正确",
        _ => new Win32Exception(code).Message,
    };

    

    private static class Native
    {
        public static readonly IntPtr InvalidHandleValue = new(-1);
        public const uint InvalidFileAttributes = 0xFFFFFFFF;

        public const uint FileAttributeReadOnly = 0x0001;
        public const uint FileAttributeDirectory = 0x0010;
        public const uint FileAttributeNormal = 0x0080;
        public const uint FileAttributeReparsePoint = 0x0400;

        public const int ShcneUpdateDir = 0x1000;
        public const uint ShcnfPathW = 0x0005;
        public const uint ShcnfFlush = 0x1000;

        
        
        
        
        
        
        
        
        
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
        public struct Win32FindData
        {
            public uint FileAttributes;
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint Reserved0;
            public uint Reserved1;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string AlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindFirstFileW(string pattern, out Win32FindData data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindNextFileW(IntPtr handle, out Win32FindData data);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindClose(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetFileAttributesW(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetFileAttributesW(string path, uint attributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteFileW(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RemoveDirectoryW(string path);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);
    }
}
