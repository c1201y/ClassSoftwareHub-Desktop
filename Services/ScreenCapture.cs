using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace ClassSoftwareHub.Desktop.Services;


public sealed class ScreenFrame
{
    public byte[] Bgra = Array.Empty<byte>();

    
    public int Width;

    
    public int Height;

    
    public int X;

    public int Y;
}











public static class ScreenCapture
{
    private const int SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;     
    private const uint DibRgbColors = 0;

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    
    private static readonly List<InMemoryRandomAccessStream> KeepAlive = new();

    
    
    
    
    
    
    
    private const long KeepAliveBudgetBytes = 256L * 1024 * 1024;

    
    
    
    
    
    private static void Keep(InMemoryRandomAccessStream stream)
    {
        KeepAlive.Add(stream);

        while (KeepAlive.Count > 1 && TotalKeptBytes() > KeepAliveBudgetBytes)
        {
            var oldest = KeepAlive[0];
            KeepAlive.RemoveAt(0);
            try { oldest.Dispose(); } catch {  }
        }
    }

    private static long TotalKeptBytes()
    {
        long total = 0;
        foreach (var s in KeepAlive)
        {
            try { total += (long)s.Size; } catch {  }
        }
        return total;
    }

    
    public static (int X, int Y, int W, int H) VirtualScreen() => (
        GetSystemMetrics(SmXVirtualScreen),
        GetSystemMetrics(SmYVirtualScreen),
        Math.Max(1, GetSystemMetrics(SmCxVirtualScreen)),
        Math.Max(1, GetSystemMetrics(SmCyVirtualScreen)));

    
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
                    Height = -h,                    
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,                
                    SizeImage = (uint)(w * h * 4),
                },
            };

            hbmp = CreateDIBSection(hdcMem, ref bmi, DibRgbColors, out var bits, IntPtr.Zero, 0);
            if (hbmp == IntPtr.Zero || bits == IntPtr.Zero) return null;

            hOld = SelectObject(hdcMem, hbmp);
            if (!BitBlt(hdcMem, 0, 0, w, h, hdcScreen, x, y, SrcCopy | (int)CaptureBlt)) return null;

            var pixels = new byte[w * h * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            
            
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

    
    public static byte[]? ToBmp(ScreenFrame frame) => ToBmp(frame, 0, 0, frame.Width, frame.Height);

    
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
        bw.Write(size);                              
        bw.Write(0);                                 
        bw.Write(headerSize);                        

        bw.Write(40);                                
        bw.Write(w);
        bw.Write(h);                                 
        bw.Write((short)1);                          
        bw.Write((short)32);                          
        bw.Write(0);                                 
        bw.Write(outStride * h);                     
        bw.Write(2835);                              
        bw.Write(2835);                              
        bw.Write(0);                                 
        bw.Write(0);                                 

        for (var row = h - 1; row >= 0; row--)       
            bw.Write(frame.Bgra, (y + row) * stride + x * 4, outStride);

        bw.Flush();
        return ms.ToArray();
    }

    
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
                if ((style & WsExToolWindow) != 0) return true;              
                if (GetWindow(hwnd, GwOwner) != IntPtr.Zero) return true;     

                if (ShellClassNames.Contains(ClassNameOf(hwnd))) return true;
                if (TitleOf(hwnd).Length == 0) return true;
                if (!GetWindowRect(hwnd, out var r)) return true;
                if (px < r.Left || px >= r.Right || py < r.Top || py >= r.Bottom) return true;

                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == myPid) return true;                               

                
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

    
    public static void Log(string msg)
    {
        try
        {
            Core.AppLog.Info("snip", msg);
        }
        catch { }
    }

    

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
