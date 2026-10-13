using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 从一个文件取出能直接在界面上显示的图标。
/// 给音量合成器（一行一个进程、要显示它自己的图标）和「应用卸载」页用。
///
/// 两条路（2026-10-13 分的）：
///   · exe / ico / dll → 走 Win32（<c>SHGetFileInfo</c> 拿 HICON → 复制成 32bpp ARGB → 拼成 WriteableBitmap），
///     不引 System.Drawing；
///   · png / jpg 等图片 → 直接交给 <see cref="BitmapImage"/> 按 <c>file://</c> 异步解码
///     （Microsoft Store 应用的图标就是包目录里的一张 png，不是 exe）。
/// 提取结果按路径缓存，同一个文件反复问也不重复掏。
///
/// ⚠️ <see cref="Get"/> 必须在 **UI 线程**上调用：两条路都会创建 XAML 对象（WriteableBitmap / BitmapImage）。
///    界面上是懒加载的 —— 只有真正滚到的那些行才会问一次。
///    想在列表出来之前就把贵的那一段跑掉，在**后台线程**上调 <see cref="Prewarm"/>。
/// </summary>
public static class AppIconService
{
    private static readonly ConcurrentDictionary<string, ImageSource?> _cache = new();
    private static readonly ConcurrentDictionary<string, Pixels?> _pixels = new();

    /// <summary>提取出来的原始像素（32bpp BGRA、行序自上而下）。</summary>
    private sealed record Pixels(int W, int H, byte[] Bgra);

    /// <summary>
    /// 后台预热：先把**慢的那一半**（读 exe 图标资源 → 拷成像素）跑掉。可在任意线程调。
    ///
    /// 为什么要有它（2026-10-13 加）：
    /// XAML 对象（<see cref="WriteableBitmap"/>）只能在 UI 线程建，所以"掏图标"整件事
    /// 天生被绑在 UI 线程上 —— 而 <c>SHGetFileInfo</c> 是要碰文件系统的，冷路径上几十毫秒，
    /// 列表一滚就是一片迟滞（「应用卸载」页几百个应用就是这情形）。
    /// 拆开之后：慢的一半丢后台预热，UI 线程上只剩「拷一块内存 + 建位图」。
    ///
    /// ⚠️ 预热只是优化，不是正确性依赖 —— 没预热到的话 <see cref="Get"/> 自己同步补上。
    /// </summary>
    public static void Prewarm(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (IsImageFile(path)) return;   // png 那条走 BitmapImage 异步解码，本来就不占 UI 线程
        _pixels.GetOrAdd(path, ExtractPixels);
    }

    /// <summary>拿某个文件的图标；拿不到（路径空 / 文件没了 / 提取失败）返回 null，调用方自会显示占位。</summary>
    public static ImageSource? Get(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        return _cache.GetOrAdd(exePath, path =>
        {
            if (IsImageFile(path)) return FromImageFile(path);

            var px = _pixels.GetOrAdd(path, ExtractPixels);
            return px is null ? null : ToBitmap(px);
        });
    }

    private static bool IsImageFile(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif";

    /// <summary>
    /// 图片文件（商店应用的 logo png）直接交给 BitmapImage。
    /// ⚠️ 用 <c>UriSource</c> 而不是读字节：解码在后台线程做，不卡 UI；
    ///    而且 unpackaged 的进程有完整文件访问权，<c>file://</c> 读得到。
    /// </summary>
    private static ImageSource? FromImageFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return new BitmapImage(new Uri("file:///" + path.Replace('\\', '/')));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把预热好的像素变成能绑到界面上的位图。⚠️ 只能在 UI 线程调。</summary>
    private static ImageSource ToBitmap(Pixels px)
    {
        var wb = new WriteableBitmap(px.W, px.H);
        using (var stream = wb.PixelBuffer.AsStream())
            stream.Write(px.Bgra, 0, px.Bgra.Length);
        wb.Invalidate();
        return wb;
    }

    private static Pixels? ExtractPixels(string exePath)
    {
        var hIcon = IntPtr.Zero;
        try
        {
            var shfi = new SHFILEINFO();
            // SHGFI_ICON | SHGFI_LARGEICON：拿 32×32 大图标。拿不到 hIcon 就放弃（不抛异常）。
            var res = SHGetFileInfo(exePath, 0, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(),
                                    SHGFI_ICON | SHGFI_LARGEICON);
            if (res == 0 || shfi.hIcon == IntPtr.Zero) return null;
            hIcon = shfi.hIcon;

            if (!GetIconInfo(hIcon, out var ii)) return null;
            try
            {
                if (ii.hbmColor == IntPtr.Zero) return null;   // 罕见：只有掩码没有彩色位图

                if (GetObject(ii.hbmColor, Marshal.SizeOf<BITMAP>(), out var bmp) == 0) return null;
                int w = bmp.bmWidth, h = bmp.bmHeight;
                if (w <= 0 || h <= 0) return null;

                // 图标位图是 32bpp DIB section，直接按 BGRA 读出来。
                // ⚠️ 负高度 = 行序自上而下，免去自己翻一遍（Windows 位图默认自下而上）。
                var pixels = new byte[w * h * 4];
                var bih = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,          // BI_RGB
                };

                var hdc = GetDC(IntPtr.Zero);
                if (hdc == IntPtr.Zero) return null;
                try
                {
                    if (GetDIBits(hdc, ii.hbmColor, 0, (uint)h, pixels, ref bih, DIB_RGB_COLORS) == 0)
                        return null;
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, hdc);
                }

                return new Pixels(w, h, pixels);
            }
            finally
            {
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            }
        }
        catch
        {
            return null;                       // 任何一步失败都当「没图标」，别把浮窗带崩
        }
        finally
        {
            if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
        }
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
                                               ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hObject, int nCount, out BITMAP lpObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
                                        byte[] lpvBits, ref BITMAPINFOHEADER lpbi, uint uUsage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
}
