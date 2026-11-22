using System;
using System.Runtime.InteropServices;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Services;


public sealed record DisplayInfo(double Scale, double WidthRaw, double HeightRaw,
                                 string ColorKind, bool IsHdr, bool IsWideGamut)
{
    public string Summary => $"缩放 {Scale:0%} · {WidthRaw}×{HeightRaw} · {ColorKind}"
                             + (IsHdr ? " · HDR" : "");
}







public static class DisplayCapabilities
{
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint QdcOnlyActivePaths = 0x00000002;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;

    private const uint DeviceInfoGetSourceName = 1;
    private const uint DeviceInfoGetAdvancedColorInfo = 9;

    public static DisplayInfo Current { get; private set; } = new(1.0, 0, 0, "Unknown", false, false);

    public static void Refresh(Window window)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);

            var scale = 1.0;
            var dpi = GetDpiForWindow(hwnd);
            if (dpi > 0) scale = dpi / 96.0;

            var width = 0;
            var height = 0;
            var deviceName = string.Empty;
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero)
            {
                var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    width = info.rcMonitor.Right - info.rcMonitor.Left;
                    height = info.rcMonitor.Bottom - info.rcMonitor.Top;
                    deviceName = info.szDevice;
                }
            }

            var color = QueryAdvancedColor(deviceName);
            Current = new DisplayInfo(scale, width, height, color.Kind, color.IsHdr, color.IsWideGamut);
        }
        catch (Exception ex)
        {
            AppLog.Warn("display", "显示器信息读取失败：" + ex.Message);
        }
    }

    private static (string Kind, bool IsHdr, bool IsWideGamut) QueryAdvancedColor(string deviceName)
    {
        var kind = "Unknown";
        var isHdr = false;
        var isWide = false;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint pathCount = 0;
            uint modeCount = 0;
            var error = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out pathCount, out modeCount);
            if (error != ErrorSuccess || pathCount == 0) return (kind, isHdr, isWide);

            var pathSize = Marshal.SizeOf<PathInfo>();
            var modeSize = Marshal.SizeOf<ModeInfoBuffer>();
            var paths = Marshal.AllocHGlobal((int)pathCount * pathSize);
            var modes = Marshal.AllocHGlobal((int)modeCount * modeSize);

            try
            {
                error = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths,
                                           ref modeCount, modes, IntPtr.Zero);
                if (error == ErrorInsufficientBuffer) continue;
                if (error != ErrorSuccess) return (kind, isHdr, isWide);

                for (var i = 0; i < pathCount; i++)
                {
                    var path = Marshal.PtrToStructure<PathInfo>(paths + i * pathSize);
                    if (path.target.targetAvailable == 0) continue;

                    var sourceName = new SourceDeviceName
                    {
                        header = new DeviceInfoHeader
                        {
                            type = DeviceInfoGetSourceName,
                            size = (uint)Marshal.SizeOf<SourceDeviceName>(),
                            adapterId = path.source.adapterId,
                            id = path.source.id,
                        },
                    };
                    if (GetSourceName(ref sourceName) != ErrorSuccess) continue;
                    if (!string.Equals(sourceName.viewGdiDeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var colorInfo = new AdvancedColorInfo
                    {
                        header = new DeviceInfoHeader
                        {
                            type = DeviceInfoGetAdvancedColorInfo,
                            size = (uint)Marshal.SizeOf<AdvancedColorInfo>(),
                            adapterId = path.target.adapterId,
                            id = path.target.id,
                        },
                    };
                    if (GetAdvancedColor(ref colorInfo) != ErrorSuccess) continue;

                    var supported = (colorInfo.value & 0x1) != 0;
                    var enabled = (colorInfo.value & 0x2) != 0;
                    if (!supported)
                    {
                        kind = "SDR";
                    }
                    else if (!enabled)
                    {
                        kind = "SDR";
                    }
                    else if (colorInfo.colorEncoding == 0)
                    {
                        kind = "HDR";
                        isHdr = true;
                    }
                    else
                    {
                        kind = "宽色域";
                        isWide = true;
                    }

                    return (kind, isHdr, isWide);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(paths);
                Marshal.FreeHGlobal(modes);
            }
        }

        return (kind, isHdr, isWide);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSourceInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        public PathSourceInfo source;
        public PathTargetInfo target;
        public uint flags;
    }

    
    [StructLayout(LayoutKind.Sequential)]
    private struct ModeInfoBuffer
    {
        public int infoType;
        public uint id;
        public Luid adapterId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)]
        public uint[] reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public uint type;
        public uint size;
        public Luid adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorInfo
    {
        public DeviceInfoHeader header;
        public uint value;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, IntPtr pathArray,
                                                 ref uint numModeArrayElements, IntPtr modeArray, IntPtr currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetSourceName(ref SourceDeviceName requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetAdvancedColor(ref AdvancedColorInfo requestPacket);
}
