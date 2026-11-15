using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services;


















public static class BrightnessService
{
    
    
    

    
    public static bool TryGet(out int percent)
    {
        if (WmiRead(out percent)) return true;
        if (Dxva2Read(out percent)) return true;
        percent = 0;
        return false;
    }

    
    
    

    private static readonly object Gate = new();
    private static int _want = -1;          
    private static bool _armed;             
    private static int _applied = -1;       

    
    
    
    
    
    public static void SetPercent(int percent)
    {
        var v = Math.Clamp(percent, 0, 100);

        lock (Gate)
        {
            _want = v;
            if (_armed) return;
            _armed = true;
        }

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(50).ConfigureAwait(false); } catch { }

            int target;
            lock (Gate)
            {
                target = _want;
                _want = -1;
                _armed = false;
            }

            if (target < 0 || target == _applied) return;
            if (WmiWrite(target) || Dxva2Write(target)) _applied = target;
        });
    }

    
    
    

    private static bool WmiRead(out int percent)
    {
        percent = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");

            foreach (var o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                var v = mo["CurrentBrightness"];
                if (v is null) continue;

                percent = Math.Clamp(Convert.ToInt32(v), 0, 100);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log("WMI 读亮度失败: " + ex.Message);
        }
        return false;
    }

    private static bool WmiWrite(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");

            var any = false;
            foreach (var o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                using var args = mo.GetMethodParameters("WmiSetBrightness");
                if (args is null) continue;

                args["Timeout"] = (uint)3;              
                args["Brightness"] = (byte)percent;     

                using var result = mo.InvokeMethod("WmiSetBrightness", args, null);

                
                
                
                var rc = result?.Properties["ReturnValue"]?.Value;
                if (rc is not null && Convert.ToInt32(rc) != 0) continue;   

                any = true;
            }
            return any;
        }
        catch (Exception ex)
        {
            Log("WMI 设亮度失败: " + ex.Message);
            return false;
        }
    }

    
    
    

    private static bool Dxva2Read(out int percent)
    {
        percent = 0;
        try
        {
            if (!TryGetPhysicalMonitor(out var hMonitor, out var err))
            {
                Log("dxva2 取显示器句柄失败: " + err);
                return false;
            }

            if (!GetMonitorBrightness(hMonitor, out var min, out var cur, out var max)) return false;

            var span = Math.Max(1u, max - min);
            percent = (int)Math.Round((cur - min) * 100.0 / span);
            percent = Math.Clamp(percent, 0, 100);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool Dxva2Write(int percent)
    {
        try
        {
            if (!TryGetPhysicalMonitor(out var hMonitor, out _)) return false;
            if (!GetMonitorBrightness(hMonitor, out var min, out _, out var max)) return false;

            var target = min + (uint)Math.Round(Math.Clamp(percent, 0, 100) * (max - min) / 100.0);
            return SetMonitorBrightness(hMonitor, target);
        }
        catch
        {
            return false;
        }
    }

    
    
    
    private static bool TryGetPhysicalMonitor(out IntPtr hPhysicalMonitor, out string error)
    {
        hPhysicalMonitor = IntPtr.Zero;
        error = "";

        var h = MonitorFromPoint(new POINT(), MonitorDefaultToPrimary);
        if (h == IntPtr.Zero) { error = "MonitorFromPoint 没给句柄"; return false; }

        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(h, out var n) || n < 1)
        {
            error = "GetNumberOfPhysicalMonitorsFromHMONITOR 失败 err=" + Marshal.GetLastWin32Error();
            return false;
        }

        var arr = new PHYSICAL_MONITOR[n];
        if (!GetPhysicalMonitorsFromHMONITOR(h, n, arr))
        {
            error = "GetPhysicalMonitorsFromHMONITOR 失败 err=" + Marshal.GetLastWin32Error();
            return false;
        }

        hPhysicalMonitor = arr[0].hPhysicalMonitor;      
        return hPhysicalMonitor != IntPtr.Zero;
    }

    
    
    

    private static int _alsProbe = -1;      

    
    public static bool AdaptiveSupported
    {
        get
        {
            if (_alsProbe < 0)
            {
                try
                {
                    
                    _alsProbe = Windows.Devices.Sensors.LightSensor.GetDefault() is null ? 0 : 1;
                }
                catch
                {
                    _alsProbe = 0;
                }
            }
            return _alsProbe == 1;
        }
    }

    
    public static bool TryGetAdaptive(out bool on)
    {
        on = false;
        try
        {
            var text = Run("powercfg", $"/query SCHEME_CURRENT {SubVideo} {AdaptBright}");
            if (text is null) return false;

            
            
            var hex = Regex.Matches(text, @"0x([0-9a-fA-F]{1,8})");
            if (hex.Count < 2) return false;

            on = Convert.ToInt32(hex[hex.Count - 2].Groups[1].Value, 16) != 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    
    public static bool SetAdaptive(bool on)
    {
        try
        {
            var v = on ? "1" : "0";
            var a = Run("powercfg", $"/setacvalueindex SCHEME_CURRENT {SubVideo} {AdaptBright} {v}");
            var b = Run("powercfg", $"/setdcvalueindex SCHEME_CURRENT {SubVideo} {AdaptBright} {v}");
            var c = Run("powercfg", "/setactive SCHEME_CURRENT");
            if (a is null || b is null || c is null) return false;

            
            return TryGetAdaptive(out var now) && now == on;
        }
        catch
        {
            return false;
        }
    }

    
    
    

    
    private static string? Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;

            
            var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(4000);
            return p.HasExited && p.ExitCode == 0 ? text : null;
        }
        catch
        {
            return null;
        }
    }

    
    private static void Log(string message)
    {
        try
        {
            Core.AppLog.Info("brightness", message);
        }
        catch
        {
            
        }
    }

    private const string SubVideo = "7516b95f-f776-4464-8c53-06167f40cc99";      
    private const string AdaptBright = "fbd9aa66-9553-4097-ba44-ed6e9d65eab8";   
    private const uint MonitorDefaultToPrimary = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] arr);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint min, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint newBrightness);
}
