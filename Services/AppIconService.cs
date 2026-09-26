using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 从 exe 提取软件图标，转成 XAML 能直接显示的 <see cref="ImageSource"/>。
/// 给音量合成器这类「一行一个进程、要显示它自己的图标」的地方用。
///
/// 走 Win32（<c>SHGetFileInfo</c> 拿 HICON → 复制成 32bpp ARGB → 拼成 WriteableBitmap），
/// 不引 System.Drawing。提取结果按 exe 路径缓存，同一个进程反复刷新时不再重复掏图标。
/// </summary>
public static class AppIconService
{
    private static readonly ConcurrentDictionary<string, ImageSource?> _cache = new();

    /// <summary>拿某个 exe 的图标；拿不到（路径空 / 文件没了 / 提取失败）返回 null，调用方自会显示占位。</summary>
    public static ImageSource? Get(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        return _cache.GetOrAdd(exePath, path => Extract(path));
    }

    private static ImageSource? Extract(string exePath)
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

                var wb = new WriteableBitmap(w, h);
                using (var stream = wb.PixelBuffer.AsStream())
                    stream.Write(pixels, 0, pixels.Length);
                wb.Invalidate();
                return wb;
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
