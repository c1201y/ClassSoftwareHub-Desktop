using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 屏幕亮度（**内建显示屏**专用）+ 自动亮度开关。
///
/// 跟音频那边一个原则：**读不到就老实说读不到**，绝不抛异常、绝不假装 0%。
///
/// ⚠️ 亮度有**两条完全不同的路**，用错路的现象就是"明明能调却说读不到"（2026-09-26 踩过）：
///   ① **WMI**（<c>root\wmi:WmiMonitorBrightness / WmiMonitorBrightnessMethods</c>）
///      —— 笔记本内屏走这条。**Windows 自己的亮度滑块也是走它**。
///   ② **dxva2.dll**（<c>Get/SetMonitorBrightness</c>）—— 那是 **DDC/CI（MCCS 0x10）**，
///      归**外接显示器**用。内屏**根本不支持 DDC/CI**，拿 HMONITOR 或 physical monitor 句柄去问
///      都是一路失败（错误码是 0xC02xxxxx 那类图形错误）。
///      一开始只写了这条 → 内屏上永远"读不到设备"，这就是那个 bug。
/// 所以现在是 **WMI 优先、dxva2 兜底**：内屏走 WMI，外接显示器（支持 DDC/CI 的）走 dxva2。
///
/// 自动亮度（跟随环境光）又是另一回事：得有**环境光传感器（ALS）**。
/// 没传感器的机器上 Windows 设置里那个开关也是摆设 —— 所以先探传感器，没有就把开关置灰。
/// </summary>
public static class BrightnessService
{
    // ════════════════════════════════════════════════════════════════
    // 读
    // ════════════════════════════════════════════════════════════════

    /// <summary>读当前亮度（0–100）。两条路都读不到才返回 false（比如纯外接显示器且不支持 DDC/CI）。</summary>
    public static bool TryGet(out int percent)
    {
        if (WmiRead(out percent)) return true;
        if (Dxva2Read(out percent)) return true;
        percent = 0;
        return false;
    }

    // ════════════════════════════════════════════════════════════════
    // 写
    // ════════════════════════════════════════════════════════════════

    private static readonly object Gate = new();
    private static int _want = -1;          // 攒着的最新目标值
    private static bool _armed;             // 合并窗口已经排上了
    private static int _applied = -1;       // 上次真设下去的值

    /// <summary>
    /// 设亮度（0–100）。
    /// ⚠️ 拖滑块一秒钟能出几十个值，而 **WMI 一次要十几到几十毫秒** → 逐个设会越拖越落后（手感变"糊"）。
    ///    所以这里做个 50ms 的**合并窗口**：窗口内连续调用只留最后一次，最多 20 次/秒，松手一定落在最终值上。
    /// </summary>
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

    // ════════════════════════════════════════════════════════════════
    // ① WMI（内建显示屏）
    // ════════════════════════════════════════════════════════════════

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

                args["Timeout"] = (uint)3;              // 秒
                args["Brightness"] = (byte)percent;     // 0–100

                using var result = mo.InvokeMethod("WmiSetBrightness", args, null);

                // ⚠️ 用 Properties[…] 而不是 result["ReturnValue"]：
                //    这个方法的 CIM 映射里**没有 ReturnValue 这一项**（实测：Invoke-CimMethod 返回的对象是空的），
                //    用索引器取会直接抛 ManagementException → 日志被刷屏、还误判成失败。
                var rc = result?.Properties["ReturnValue"]?.Value;
                if (rc is not null && Convert.ToInt32(rc) != 0) continue;   // 0 = 成功

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

    // ════════════════════════════════════════════════════════════════
    // ② dxva2 / DDC-CI（外接显示器）
    // ════════════════════════════════════════════════════════════════

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

    /// <summary>
    /// 拿**物理显示器句柄** —— dxva2 这套是要这个，不是 HMONITOR（给 HMONITOR 会一直失败）。
    /// </summary>
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

        hPhysicalMonitor = arr[0].hPhysicalMonitor;      // 多显示器也先管主显示器
        return hPhysicalMonitor != IntPtr.Zero;
    }

    // ════════════════════════════════════════════════════════════════
    // 自动亮度（环境光传感器）
    // ════════════════════════════════════════════════════════════════

    private static int _alsProbe = -1;      // -1 = 还没探过，0 = 没传感器，1 = 有

    /// <summary>这台机器有没有环境光传感器（= 自动亮度有没有意义）。探一次就记住。</summary>
    public static bool AdaptiveSupported
    {
        get
        {
            if (_alsProbe < 0)
            {
                try
                {
                    // WinRT 这条最省事：没有传感器就是 null（拿不到传感器服务时也会抛，一起当"没有"）
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

    /// <summary>自动亮度现在开着吗（读电源设置里 ADAPTBRIGHT 的交流索引）。</summary>
    public static bool TryGetAdaptive(out bool on)
    {
        on = false;
        try
        {
            var text = Run("powercfg", $"/query SCHEME_CURRENT {SubVideo} {AdaptBright}");
            if (text is null) return false;

            // ⚠️ 别去匹配 powercfg 输出里的中文（"当前交流电源设置索引"这类字样是**跟系统语言走**的，
            //    英文系统上就全对不上了）。按位置取：输出里最后一对 0x 值 = 当前交流 / 当前直流。
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

    /// <summary>开关自动亮度（交流 + 直流都设，跟系统设置里那个开关等价）。成功返回 true。</summary>
    public static bool SetAdaptive(bool on)
    {
        try
        {
            var v = on ? "1" : "0";
            var a = Run("powercfg", $"/setacvalueindex SCHEME_CURRENT {SubVideo} {AdaptBright} {v}");
            var b = Run("powercfg", $"/setdcvalueindex SCHEME_CURRENT {SubVideo} {AdaptBright} {v}");
            var c = Run("powercfg", "/setactive SCHEME_CURRENT");
            if (a is null || b is null || c is null) return false;

            // 复核一次：powercfg 偶尔"命令成功但没落到当前方案"，别让界面骗人
            return TryGetAdaptive(out var now) && now == on;
        }
        catch
        {
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // 底下这些小东西
    // ════════════════════════════════════════════════════════════════

    /// <summary>跑一条命令，成功（退出码 0）返回输出文本，否则 null。</summary>
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

            // 中文字节会被按非 UTF8 读成乱码，无所谓 —— 我们只要里面的 0x 十六进制
            var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(4000);
            return p.HasExited && p.ExitCode == 0 ? text : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>亮度这条路的日志（万一又出"读不到"，看这个就知道卡在哪一步）。</summary>
    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Dir);
            File.AppendAllText(Path.Combine(SettingsStore.Dir, "brightness.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch
        {
            // 日志写不进去就算了
        }
    }

    private const string SubVideo = "7516b95f-f776-4464-8c53-06167f40cc99";      // SUB_VIDEO（显示）
    private const string AdaptBright = "fbd9aa66-9553-4097-ba44-ed6e9d65eab8";   // ADAPTBRIGHT（自适应亮度）
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
