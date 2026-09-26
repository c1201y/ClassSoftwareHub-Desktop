using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>一块抓下来的屏幕像素（BGRA，行自上而下）。</summary>
public sealed class ScreenFrame
{
    public byte[] Bgra = Array.Empty<byte>();

    /// <summary>这块像素宽度（**物理像素**）。</summary>
    public int Width;

    /// <summary>这块像素高度（物理像素）。</summary>
    public int Height;

    /// <summary>它贴在整个虚拟桌面上的原点（物理像素）。</summary>
    public int X;

    public int Y;
}

/// <summary>
/// 「截屏贴图」的地基：抓屏 → 图片 → 剪贴板。
///
/// 为什么全用 Win32：本工程**没引 System.Drawing / WinForms / WPF**（csproj 连 UseWPF 都没有），
/// 所以不能 `Graphics.CopyFromScreen`。这里用 `CreateDIBSection` 拿裸像素，
/// 再手搓 54 字节 BMP 头拼成图片流 —— 够 BitmapImage 显示，也够塞剪贴板给课件 Ctrl+V。
///
/// 用法（框选层的套路）：**先把整屏抓成 Frame 冻住**（窗口背景就贴这张图，看起来跟没动过一样），
/// 用户框完再从 Frame 上裁 → 省掉"覆盖层会不会把自己照进去"那一堆麻烦。
/// </summary>
public static class ScreenCapture
{
    private const int SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;     // 连分层窗口一起抓（不然某些窗口是黑的）
    private const uint DibRgbColors = 0;

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    /// <summary>解码中的图片流要有人"扶着"，不然 GC 可能把流收走、图变空白。</summary>
    private static readonly List<InMemoryRandomAccessStream> KeepAlive = new();

    /// <summary>
    /// 这些流最多共占多少字节。
    ///
    /// ⚠️ 原来按**张数**封顶（24 张）。但抓的是整个虚拟桌面：4K 单屏一张约 33MB，
    /// 双 4K 约 66MB —— 24 张就是 0.8～1.6GB 常驻，教学机 8G 内存直接顶不住。
    /// 改成按总字节数封顶，跟屏幕多大无关。
    /// </summary>
    private const long KeepAliveBudgetBytes = 256L * 1024 * 1024;

    /// <summary>
    /// 扶住一个刚解码好的流，并把超预算的老流**释放掉**。
    /// 只从列表里移除是不够的：流的位图要等 GC 才回收，而几十 MB 的大对象 GC 并不积极。
    /// 最保守也留最新的一张（正在显示的那张绝不能被释放）。
    /// </summary>
    private static void Keep(InMemoryRandomAccessStream stream)
    {
        KeepAlive.Add(stream);

        while (KeepAlive.Count > 1 && TotalKeptBytes() > KeepAliveBudgetBytes)
        {
            var oldest = KeepAlive[0];
            KeepAlive.RemoveAt(0);
            try { oldest.Dispose(); } catch { /* 释放失败也不能影响截屏 */ }
        }
    }

    private static long TotalKeptBytes()
    {
        long total = 0;
        foreach (var s in KeepAlive)
        {
            try { total += (long)s.Size; } catch { /* 流已失效就按 0 算 */ }
        }
        return total;
    }

    /// <summary>整个虚拟桌面（多屏也覆盖）的物理像素范围。</summary>
    public static (int X, int Y, int W, int H) VirtualScreen() => (
        GetSystemMetrics(SmXVirtualScreen),
        GetSystemMetrics(SmYVirtualScreen),
        Math.Max(1, GetSystemMetrics(SmCxVirtualScreen)),
        Math.Max(1, GetSystemMetrics(SmCyVirtualScreen)));

    /// <summary>抓一块屏幕区域（**物理像素**坐标，允许负值 = 副屏）。</summary>
    public static ScreenFrame? Grab(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0 || w > 20000 || h > 20000) return null;

        var hdcScreen = GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero) return null;

        var hdcMem = IntPtr.Zero;
        var hbmp = IntPtr.Zero;
        var hOld = IntPtr.Zero;
        try
        {
            hdcMem = CreateCompatibleDC(hdcScreen);
            if (hdcMem == IntPtr.Zero) return null;

            var bmi = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = w,
                    Height = -h,                    // 负 = 自上而下，读出来就是正常顺序
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,                // BI_RGB
                    SizeImage = (uint)(w * h * 4),
                },
            };

            hbmp = CreateDIBSection(hdcMem, ref bmi, DibRgbColors, out var bits, IntPtr.Zero, 0);
            if (hbmp == IntPtr.Zero || bits == IntPtr.Zero) return null;

            hOld = SelectObject(hdcMem, hbmp);
            if (!BitBlt(hdcMem, 0, 0, w, h, hdcScreen, x, y, SrcCopy | (int)CaptureBlt)) return null;

            var pixels = new byte[w * h * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            // ⚠️ 屏幕像素其实不透明，但 DIB 的 alpha 是 0 —— 有的解码器见 alpha=0 就当全透明、
            //    贴出来一片空白。统一刷成 255。
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 0xFF;

            return new ScreenFrame { Bgra = pixels, Width = w, Height = h, X = x, Y = y };
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hOld != IntPtr.Zero && hdcMem != IntPtr.Zero) SelectObject(hdcMem, hOld);
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            if (hdcMem != IntPtr.Zero) DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    /// <summary>整帧 → BMP 字节（当"冻屏"背景图用）。</summary>
    public static byte[]? ToBmp(ScreenFrame frame) => ToBmp(frame, 0, 0, frame.Width, frame.Height);

    /// <summary>从帧上裁一块（坐标是**帧内**的物理像素）→ BMP 字节。越界自动夹住。</summary>
    public static byte[]? ToBmp(ScreenFrame frame, int cropX, int cropY, int cropW, int cropH)
    {
        if (frame.Bgra.Length == 0) return null;
        if (cropW <= 0 || cropH <= 0) return null;

        var x = Math.Clamp(cropX, 0, frame.Width - 1);
        var y = Math.Clamp(cropY, 0, frame.Height - 1);
        var w = Math.Clamp(cropW, 1, frame.Width - x);
        var h = Math.Clamp(cropH, 1, frame.Height - y);
        var stride = frame.Width * 4;

        var outStride = w * 4;
        const int headerSize = 14 + 40;
        var size = headerSize + outStride * h;

        using var ms = new MemoryStream(size);
        using var bw = new BinaryWriter(ms);

        bw.Write((byte)'B');
        bw.Write((byte)'M');
        bw.Write(size);                              // 文件总大小
        bw.Write(0);                                 // 保留
        bw.Write(headerSize);                        // 像素数据偏移

        bw.Write(40);                                // 信息头大小
        bw.Write(w);
        bw.Write(h);                                 // 正数 = 行自下而上
        bw.Write((short)1);                          // planes
        bw.Write((short)32);                          // bpp
        bw.Write(0);                                 // BI_RGB
        bw.Write(outStride * h);                     // 像素数据大小
        bw.Write(2835);                              // 水平分辨率（72dpi）
        bw.Write(2835);                              // 垂直分辨率
        bw.Write(0);                                 // 调色板
        bw.Write(0);                                 // 重要颜色数

        for (var row = h - 1; row >= 0; row--)       // BMP 行自下而上
            bw.Write(frame.Bgra, (y + row) * stride + x * 4, outStride);

        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>BMP 字节 → 可显示的图片（"冻屏"背景、贴图窗口都用它）。</summary>
    public static async Task<BitmapImage?> ToImageAsync(byte[] bmp)
    {
        try
        {
            var ras = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(ras))
            {
                writer.WriteBytes(bmp);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            ras.Seek(0);
            var img = new BitmapImage();
            await img.SetSourceAsync(ras);

            Keep(ras);
            return img;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把图片塞进剪贴板（老师截完直接 Ctrl+V 粘进课件）。
    /// ⚠️ 要在 UI 线程调；`Flush()` 让内容在本程序退出后也留在剪贴板里。
    /// </summary>
    public static async Task CopyToClipboardAsync(byte[] bmp)
    {
        var ras = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(ras))
        {
            writer.WriteBytes(bmp);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        ras.Seek(0);
        var dp = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        dp.SetBitmap(RandomAccessStreamReference.CreateFromStream(ras));
        Clipboard.SetContent(dp);
        Clipboard.Flush();
    }

    // ── 找"光标底下那个窗口"（「窗口」方式用） ────────────────

    /// <summary>
    /// 光标物理坐标底下那个**别人家的**顶级窗口的框（物理像素）。
    ///
    /// ⚠️ 不能用 `WindowFromPoint`：框选层的窗口铺满全屏、还置顶，`WindowFromPoint` 只会返回我们自己。
    /// 所以按 **z 序从上往下枚举**（`EnumWindows` 就是 z 序），取第一个"包含这个点、且不是我们进程"的窗口。
    /// 顺带排掉系统壳 / 工具窗 / 有 owner 的浮层 / 没标题的。
    /// </summary>
    public static (int X, int Y, int W, int H)? WindowRectUnderPoint(int px, int py)
    {
        var myPid = (uint)Environment.ProcessId;
        var hit = IntPtr.Zero;
        var hitBounds = default(Rect);

        try
        {
            _ = EnumWindows((hwnd, _) =>
            {
                if (hit != IntPtr.Zero) return false;
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;

                var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
                if ((style & WsExToolWindow) != 0) return true;              // 工具窗（含贴图那些）
                if (GetWindow(hwnd, GwOwner) != IntPtr.Zero) return true;     // 有 owner 的多半是对话框/浮层

                if (ShellClassNames.Contains(ClassNameOf(hwnd))) return true;
                if (TitleOf(hwnd).Length == 0) return true;
                if (!GetWindowRect(hwnd, out var r)) return true;
                if (px < r.Left || px >= r.Right || py < r.Top || py >= r.Bottom) return true;

                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == myPid) return true;                               // 自己家的窗口跳过

                // 阴影/可缩放边框不算进去，取 DWM 的"真实可视边界"，失败就退回 GetWindowRect
                var b = r;
                try
                {
                    if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var dwm, Marshal.SizeOf<Rect>()) == 0)
                        b = dwm;
                }
                catch { }

                if (b.Right <= b.Left || b.Bottom <= b.Top) b = r;

                hit = hwnd;
                hitBounds = b;
                return false;
            }, IntPtr.Zero);
        }
        catch
        {
            return null;
        }

        if (hit == IntPtr.Zero) return null;

        // 夹进虚拟桌面（有些窗口边界能跑到屏幕外）
        var (vx, vy, vw, vh) = VirtualScreen();
        var l = Math.Clamp(hitBounds.Left, vx, vx + vw - 1);
        var t = Math.Clamp(hitBounds.Top, vy, vy + vh - 1);
        var rr = Math.Clamp(hitBounds.Right, l + 1, vx + vw);
        var bb = Math.Clamp(hitBounds.Bottom, t + 1, vy + vh);
        return (l, t, rr - l, bb - t);
    }

    private static readonly HashSet<string> ShellClassNames = new()
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "Windows.UI.Composition.DesktopWindowContentBridge",
        "MSCTFIME UI", "IME", "Default IME",
    };

    private static string ClassNameOf(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        _ = GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string TitleOf(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(276);
        _ = GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>「截屏贴图」的自报日志（跟其它模块一个目录）。</summary>
    public static void Log(string msg)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(SettingsStore.Dir, "snip.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\r\n",
                System.Text.Encoding.UTF8);
        }
        catch { }
    }

    // ── Win32 ────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        // 本该跟一张调色板，32bpp 用不着，只声明头
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const uint GwOwner = 4;
    private const int DwmwaExtendedFrameBounds = 9;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern int GetClassNameW(IntPtr hwnd, System.Text.StringBuilder buffer, int max);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextW(IntPtr hwnd, System.Text.StringBuilder buffer, int max);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out Rect value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h, IntPtr hdcSrc, int xSrc, int ySrc, int rop);
}
