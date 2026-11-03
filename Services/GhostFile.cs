using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 「幽灵文件」的检测与清理（实验性功能，2026-11-03）。
///
/// <para>
/// 什么算幽灵文件：**目录能列出来，但按正常路径打不开**。典型来源是名字不合 Win32 规矩 ——
/// 末尾带空格或点、名字里有 <c>&lt; &gt; : " | ? *</c> 这类字符、或者撞上 <c>CON</c> / <c>NUL</c>
/// 这种系统保留名。资源管理器用非规范化的方式列目录，所以照样画出图标；
/// 双击或删除时走的是 Win32 规范化路径（末尾空格被吃掉），于是提示「找不到路径」。
/// </para>
///
/// <para>
/// 判定与清理都靠长路径前缀 <c>\\?\</c>：它让系统**不规范化**路径，原样交给文件系统。
/// 8.3 短名（<c>SHORTN~1</c>）作为第二道兜底 —— 老系统、老文件系统上偶尔只有短名走得通。
/// </para>
///
/// <para>
/// ⚠️ 这里删的东西**进不了回收站**：回收站接口不支持 <c>\\?\</c> 前缀。
/// 所以界面上必须二次确认，且要说清楚是永久删除。
/// </para>
///
/// <para>
/// ⚠️ 另一种「删了还在」是**资源管理器视图缓存**，那种情况文件系统里根本没有这个条目，
/// <see cref="Scan"/> 什么也扫不到（不是漏扫，是真的不存在）。这种情况只能刷新视图，
/// 见 <see cref="RefreshExplorer"/>。
/// </para>
/// </summary>
public static class GhostFile
{
    /// <summary>一个被判定为幽灵的条目。</summary>
    public sealed class GhostEntry
    {
        /// <summary>父目录（用户给的、可直接显示的普通路径）。</summary>
        public string Parent { get; init; } = "";

        /// <summary>真实名字，**原样保留**（可能末尾带空格/点）。</summary>
        public string Name { get; init; } = "";

        /// <summary>带 <c>\\?\</c> 前缀的完整路径，访问与删除都用它。</summary>
        public string LongPath { get; init; } = "";

        /// <summary>8.3 短名。拿不到就是空串。</summary>
        public string ShortName { get; init; } = "";

        public bool IsDirectory { get; init; }

        /// <summary>字节数；目录为 -1。</summary>
        public long Size { get; init; }

        /// <summary>给人看的名字：末尾的空格画成 ␣、末尾的点画成 ·，不然肉眼根本分不出来。</summary>
        public string DisplayName { get; init; } = "";

        /// <summary>为什么判成幽灵，一句话。</summary>
        public string Reason { get; init; } = "";
    }

    /// <summary>扫描结果。</summary>
    public sealed class ScanResult
    {
        public List<GhostEntry> Items { get; init; } = new();

        /// <summary>扫描失败的原因（成功时为空）。</summary>
        public string Error { get; init; } = "";

        /// <summary>父目录下总共列出来多少个条目（含正常的），用来区分「目录是空的」和「都不异常」。</summary>
        public int Total { get; init; }
    }

    // ── 对外 ────────────────────────────────────────────────

    /// <summary>
    /// 扫描指定目录（**只扫这一级**），找出名字不合规矩、正常路径打不开的条目。
    /// </summary>
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

    /// <summary>
    /// 永久删除一个幽灵条目。三层回退：<c>\\?\</c> 真名 → <c>\\?\</c> 短名 → 普通路径短名。
    /// </summary>
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

    /// <summary>
    /// 让资源管理器重新读一遍这个目录（对付「文件其实早没了、图标还挂着」的显示缓存）。
    ///
    /// ⚠️ 这是温和做法，只会让已打开的窗口重绘。真要彻底清掉，得重启资源管理器；
    ///    那会关掉用户所有文件夹窗口，所以不放在这里自动做。
    /// </summary>
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

    /// <summary>把名字里的不可见字符画出来（末尾空格 → ␣，末尾点 → ·）。</summary>
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

    // ── 内部 ────────────────────────────────────────────────

    /// <summary>判断单个条目是不是幽灵；是就返回描述，不是返回 null。</summary>
    private static GhostEntry? Inspect(string parent, string longParent, string name, Native.Win32FindData data)
    {
        var normalPath = Path.Combine(parent, name);

        // 正常路径打得开 → 普通条目，不看
        if (Native.GetFileAttributesW(normalPath) != Native.InvalidFileAttributes) return null;

        // 保留名特殊处理：GetFileAttributesW("C:\dir\NUL") 会去打开设备并"成功"，
        // 不特判的话 NUL / CON 这类会被当成正常文件。
        var reserved = IsReservedName(name);

        // 正常路径打不开，再用长路径确认一次：连 \\?\ 都打不开的，不是幽灵（大概率是权限）
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

    /// <summary>Win32 保留设备名。这类名字正常路径访问会落到设备上，必须单独认。</summary>
    private static bool IsReservedName(string name)
    {
        var dot = name.IndexOf('.');
        var stem = (dot >= 0 ? name[..dot] : name).TrimEnd(' ').ToUpperInvariant();

        if (stem is "CON" or "PRN" or "AUX" or "NUL") return true;

        return stem.Length == 4
               && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
               && stem[3] is >= '1' and <= '9';
    }

    /// <summary>
    /// 真正执行删除。目录走自己写的递归 —— .NET 的递归删除碰到这些怪名字和只读属性容易半途而废，
    /// 而且失败了也说不清是哪一个删不掉。
    /// </summary>
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

            // 目录：先把里面的东西删干净
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

                        TryDelete(childPath, childIsDir, out _);   // 单个删不掉不阻断整体
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

    /// <summary>普通路径 → <c>\\?\</c> 长路径（UNC 走 <c>\\?\UNC\</c>）。</summary>
    private static string ToLongPath(string full)
    {
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + full[2..];
        return @"\\?\" + full;
    }

    /// <summary>把 Win32 错误码翻成能看懂的一句话（常见那几个手写，其余交给系统）。</summary>
    private static string DescribeWin32(int code) => code switch
    {
        2 => "找不到路径",
        3 => "找不到路径",
        5 => "没有权限",
        32 => "文件正被别的程序占用",
        123 => "名字格式不正确",
        _ => new Win32Exception(code).Message,
    };

    // ── 原生 ─────────────────────────────────────────────────

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

        /// <summary>
        /// <c>WIN32_FIND_DATAW</c>。
        ///
        /// <para>
        /// ⚠️ **必须加 <c>Pack = 4</c>**：里面的 FILETIME 是按 4 字节对齐的（两个 DWORD），
        /// 而 C# 的 <c>long</c> 默认按 8 字节对齐 —— 不加的话第一个时间字段就从 offset 8 开始，
        /// 后面 cFileName 全部错位，读出来是空串（2026-11-03 测试就是这么挂的）。
        /// </para>
        /// </summary>
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
