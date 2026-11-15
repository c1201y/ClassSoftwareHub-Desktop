using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClassSoftwareHub.Desktop.Services;
















public static class AppIconService
{
    private static readonly ConcurrentDictionary<string, ImageSource?> _cache = new();
    private static readonly ConcurrentDictionary<string, Pixels?> _pixels = new();

    
    private sealed record Pixels(int W, int H, byte[] Bgra);

    
    
    
    
    
    
    
    
    
    
    
    public static void Prewarm(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (IsImageFile(path)) return;   
        _pixels.GetOrAdd(path, ExtractPixels);
    }

    
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
            
            var res = SHGetFileInfo(exePath, 0, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(),
                                    SHGFI_ICON | SHGFI_LARGEICON);
            if (res == 0 || shfi.hIcon == IntPtr.Zero) return null;
            hIcon = shfi.hIcon;

            if (!GetIconInfo(hIcon, out var ii)) return null;
            try
            {
                if (ii.hbmColor == IntPtr.Zero) return null;   

                if (GetObject(ii.hbmColor, Marshal.SizeOf<BITMAP>(), out var bmp) == 0) return null;
                int w = bmp.bmWidth, h = bmp.bmHeight;
                if (w <= 0 || h <= 0) return null;

                
                
                var pixels = new byte[w * h * 4];
                var bih = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,          
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
            return null;                       
        }
        finally
        {
            if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
        }
    }

    

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
