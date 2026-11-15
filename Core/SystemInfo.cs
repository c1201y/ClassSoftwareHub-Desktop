using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Core;





public static class SystemInfo
{
    
    public sealed record InfoLine(string Label, string Value);

    
    public sealed record QuickInfo(IReadOnlyList<InfoLine> Hardware, IReadOnlyList<InfoLine> System);

    private const string Dash = "—";

    public static QuickInfo Gather()
    {
        var hw = new List<InfoLine>
        {
            new("处理器", CpuText()),
            new("内存", MemoryText()),
            new("硬盘", DiskText()),
            new("触摸", TouchText()),
            new("显卡", GpuText()),
            new("显存", VramText()),
            new("显示器", MonitorText()),
        };

        var sys = new List<InfoLine>
        {
            new("操作系统", OsNameText()),
            new("系统版本", OsVersionText()),
            new("安装日期", InstallDateText()),
            new("虚拟内存", PageFileText()),
            new("虚拟化", VirtualizationText()),
            new("DirectX", DirectXText()),
        };

        return new QuickInfo(Clean(hw), Clean(sys));
    }

    private static IReadOnlyList<InfoLine> Clean(List<InfoLine> list) =>
        list.Select(x => new InfoLine(x.Label, Blank(x.Value) ? Dash : x.Value.Trim())).ToList();

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);

    

    
    private static string CpuText()
    {
        try
        {
            string name = "";
            int mhz = 0;
            using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            {
                name = (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
                if (k?.GetValue("~MHz") is int i && i > 0) mhz = i;
            }

            name = name.Replace("(R)", "").Replace("(TM)", "").Replace("CPU", "").Replace("  ", " ").Trim();
            
            name = System.Text.RegularExpressions.Regex.Replace(name, @"@\s*[\d.]+\s*GHz", "").Trim();

            var (cores, threads) = CpuCoreCounts();
            if (threads <= 0) threads = Environment.ProcessorCount;

            var parts = new List<string>();
            if (name.Length > 0) parts.Add(name);
            if (cores > 0) parts.Add($"{cores} 核 / {threads} 线程");
            else if (threads > 0) parts.Add($"{threads} 线程");
            if (mhz > 0) parts.Add((mhz / 1000d).ToString("0.00") + " GHz");
            return string.Join(" · ", parts);
        }
        catch { return ""; }
    }

    
    private static (int cores, int threads) CpuCoreCounts()
    {
        try
        {
            uint len = 0;
            GetLogicalProcessorInformation(IntPtr.Zero, ref len);
            if (len == 0) return (0, 0);

            var ptr = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformation(ptr, ref len)) return (0, 0);
                var size = Marshal.SizeOf<SlpiEntry>();
                if (size <= 0) return (0, 0);
                var count = (int)(len / (uint)size);
                var cores = 0;
                for (var i = 0; i < count; i++)
                {
                    var e = Marshal.PtrToStructure<SlpiEntry>(ptr + i * size);
                    if (e.Relationship == 0) cores++;   
                }
                return (cores, Environment.ProcessorCount);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch { return (0, 0); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CacheDescriptor
    {
        public byte Level;
        public byte Associativity;
        public ushort LineSize;
        public uint CacheSize;
        public int Type;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct SlpiUnion
    {
        [FieldOffset(0)] public CacheDescriptor Cache;
        [FieldOffset(0)] public uint NodeNumber;
        [FieldOffset(0)] public ulong Reserved0;
        [FieldOffset(8)] public ulong Reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SlpiEntry
    {
        public UIntPtr ProcessorMask;
        public int Relationship;
        public SlpiUnion U;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint length);

    
    private static string MemoryText()
    {
        try
        {
            var modules = MemoryModules();
            var used = modules.Where(m => m.SizeMb > 0).ToList();

            if (used.Count > 0)
            {
                var totalMb = used.Sum(m => m.SizeMb);
                var distinct = used.Select(m => m.SizeMb).Distinct().OrderBy(x => x).ToList();
                var head = distinct.Count == 1 && used.Count > 1
                    ? $"{Mb(distinct[0])} × {used.Count}"
                    : Mb(totalMb);

                var parts = new List<string> { head };

                var type = used.Select(m => m.TypeName).FirstOrDefault(t => t.Length > 0) ?? "";
                var speed = used.Select(m => m.Speed).FirstOrDefault(s => s > 0);
                var ts = new List<string>();
                if (type.Length > 0) ts.Add(type);
                if (speed > 0) ts.Add(speed + " MT/s");
                if (ts.Count > 0) parts.Add(string.Join(" ", ts));

                var withBrand = used.FirstOrDefault(m => m.Manufacturer.Length > 0 || m.PartNumber.Length > 0);
                if (withBrand is not null)
                {
                    var brand = (VendorZh(withBrand.Manufacturer) + " " + withBrand.PartNumber).Trim();
                    if (brand.Length > 0) parts.Add(brand);
                }

                if (modules.Count > 0) parts.Add($"插槽 {used.Count}/{modules.Count}");

                return string.Join(" · ", parts);
            }

            
            var mstat = new MemoryStatus();
            if (GlobalMemoryStatusEx(mstat) && mstat.TotalPhys > 0) return Size((long)mstat.TotalPhys);
            return "";
        }
        catch { return ""; }
    }

    private sealed record MemoryModule(long SizeMb, int Speed, string TypeName, string Manufacturer, string PartNumber);

    private static List<MemoryModule> MemoryModules()
    {
        var list = new List<MemoryModule>();
        var table = RawSmbios();
        if (table is null) return list;

        var pos = 8;   
        while (pos + 4 <= table.Length)
        {
            var type = table[pos];
            var length = table[pos + 1];
            if (length < 4) break;
            if (type == 127) break;   

            if (type == 17 && length >= 0x15 && pos + length <= table.Length)
            {
                var sizeRaw = (ushort)(table[pos + 0x0C] | (table[pos + 0x0D] << 8));
                long sizeMb;
                if ((sizeRaw & 0x8000) != 0) sizeMb = (sizeRaw & 0x7FFF) / 1024;          
                else if (sizeRaw == 0 && length >= 0x20)                                   
                    sizeMb = BitConverter.ToUInt32(table, pos + 0x1C) & 0x7FFFFFFF;
                else sizeMb = sizeRaw;

                var memType = table[pos + 0x12];

                int speed = length >= 0x17 ? (table[pos + 0x15] | (table[pos + 0x16] << 8)) : 0;
                if (speed == 0 && length >= 0x58)
                    speed = (int)(BitConverter.ToUInt32(table, pos + 0x54) & 0x7FFFFFFF);   

                var manufacturer = length > 0x17 ? SmbiosString(table, pos, length, table[pos + 0x17]) : "";
                var partNumber = length > 0x1A ? SmbiosString(table, pos, length, table[pos + 0x1A]) : "";

                list.Add(new MemoryModule(sizeMb, speed, MemoryTypeName(memType), Clean(manufacturer), Clean(partNumber)));
            }

            
            pos += length;
            while (pos + 1 < table.Length && !(table[pos] == 0 && table[pos + 1] == 0)) pos++;
            pos += 2;
        }
        return list;
    }

    private static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (c >= 0x20 && c < 0x7F) sb.Append(c);
        return sb.ToString().Trim();
    }

    
    private static string SmbiosString(byte[] table, int formattedStart, int formattedLen, int index)
    {
        if (index <= 0) return "";
        var pos = formattedStart + formattedLen;
        var current = 1;
        while (pos < table.Length && table[pos] != 0)
        {
            var end = pos;
            while (end < table.Length && table[end] != 0) end++;
            if (current == index) return Encoding.ASCII.GetString(table, pos, end - pos);
            current++;
            pos = end + 1;
        }
        return "";
    }

    
    private static string MemoryTypeName(byte code) => code switch
    {
        0x03 => "DRAM",
        0x0F => "SDRAM",
        0x11 => "RDRAM",
        0x12 => "DDR",
        0x13 => "DDR2",
        0x14 => "DDR2 FB-DIMM",
        0x18 => "DDR3",
        0x19 => "FBD2",
        0x1A => "DDR4",
        0x1B => "LPDDR",
        0x1C => "LPDDR2",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x1F => "逻辑非易失内存",
        0x20 => "HBM",
        0x21 => "HBM2",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        0x24 => "HBM3",
        0x25 => "LPDDR5X",
        _ => "",
    };

    private static string VendorZh(string v) => v switch
    {
        "Samsung" => "三星",
        "SK Hynix" => "SK 海力士",
        "Hynix" => "海力士",
        "Hynix Semiconductor" => "海力士",
        "Micron" => "美光",
        "Micron Technology" => "美光",
        "Crucial" => "英睿达",
        "Kingston" => "金士顿",
        "Corsair" => "海盗船",
        "ADATA" => "威刚",
        "Nanya" => "南亚",
        "Ramaxel" => "记忆科技",
        "Transcend" => "创见",
        "G.Skill" => "芝奇",
        "Kingbank" => "金百达",
        _ => v,
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint signature, uint tableId, IntPtr buffer, uint size);

    private static byte[]? RawSmbios()
    {
        try
        {
            const uint RSMB = 0x52534D42;   
            var size = GetSystemFirmwareTable(RSMB, 0, IntPtr.Zero, 0);
            if (size == 0 || size > 8 * 1024 * 1024) return null;
            var buf = new byte[size];
            var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                if (GetSystemFirmwareTable(RSMB, 0, handle.AddrOfPinnedObject(), size) == 0) return null;
            }
            finally { handle.Free(); }
            return buf;
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private class MemoryStatus
    {
        public uint Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
        public uint Load;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus buffer);

    
    private static string DiskText()
    {
        try
        {
            var models = PhysicalDiskModels();

            long total = 0, free = 0;
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    total += d.TotalSize;
                    free += d.TotalFreeSpace;
                }
                catch { }
            }
            if (total <= 0) return models.Count > 0 ? string.Join(" + ", models) : "";

            var modelPart = models.Count > 0 ? string.Join(" + ", models.Take(2)) + " · " : "";
            return $"{modelPart}已用 {Size(total - free)} / 共 {Size(total)}";
        }
        catch { return ""; }
    }

    
    
    
    
    private static List<string> PhysicalDiskModels()
    {
        var result = new List<string>();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\disk\Enum");
            if (k is null) return result;
            foreach (var name in k.GetValueNames().Where(n => n.All(char.IsDigit)).OrderBy(n => n.Length).ThenBy(n => n))
            {
                var raw = k.GetValue(name) as string;
                if (Blank(raw)) continue;

                var ven = Token(raw!, "Ven_");
                var prod = Token(raw!, "Prod_");
                var bus = new[] { "NVMe", "ATA", "SCSI", "USB", "SATA", "RAID", "Virtual", "Msft" };
                string model;
                if (bus.Contains(ven, StringComparer.OrdinalIgnoreCase) || ven.Length == 0)
                    model = prod;
                else
                    model = (ven + " " + prod).Trim();

                if (model.Length > 0 && !result.Contains(model)) result.Add(model);
            }
        }
        catch { }
        return result;
    }

    private static string Token(string s, string marker)
    {
        var i = s.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        i += marker.Length;
        var e = s.IndexOfAny(new[] { '&', '\\' }, i);
        var v = e < 0 ? s.Substring(i) : s.Substring(i, e - i);
        return v.Replace('_', ' ').Trim();
    }

    
    private static string TouchText()
    {
        try
        {
            var n = GetSystemMetrics(95);   
            return n > 0 ? $"{n} 点触控" : "不支持触控";
        }
        catch { return ""; }
    }

    
    
    
    
    
    
    
    private static List<(string Name, string Pnp)> RealAdapters()
    {
        var list = new List<(string, string)>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, ConfigManagerErrorCode FROM Win32_VideoController");

            foreach (ManagementObject item in searcher.Get())
            {
                using var _ = item;

                var name = (item["Name"] as string)?.Trim() ?? "";
                if (Blank(name)) continue;

                
                var error = item["ConfigManagerErrorCode"] is uint code ? code : 0u;
                if (error != 0) continue;

                if (IsVirtualAdapter(name)) continue;
                if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)) continue;

                var pnp = (item["PNPDeviceID"] as string)?.Trim() ?? "";
                list.Add((name, pnp));
            }
        }
        catch {  }

        return list;
    }

    private static string GpuText()
    {
        try
        {
            var real = RealAdapters();
            if (real.Count > 0)
                return string.Join(" ｜ ", real.Select(r => r.Name).Distinct().Take(2));

            
            using var cls = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return "";

            var names = new List<string>();
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)).OrderBy(n => n))
            {
                using var k = cls.OpenSubKey(sub);
                var d = (k?.GetValue("DriverDesc") as string)?.Trim();
                if (!Blank(d) && !names.Contains(d!)) names.Add(d!);
            }

            var filtered = names.Where(n => !IsVirtualAdapter(n)).ToList();
            return string.Join(" ｜ ", (filtered.Count > 0 ? filtered : names).Take(2));
        }
        catch { return ""; }
    }

    private static bool IsVirtualAdapter(string name) =>
        name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Mirror", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Remote", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Parsec", StringComparison.OrdinalIgnoreCase);

    
    
    
    
    
    private static string VramText()
    {
        try
        {
            var real = RealAdapters().Select(a => a.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            using var cls = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return "";

            long best = 0;

            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                if (k is null) continue;

                
                var desc = (k.GetValue("DriverDesc") as string)?.Trim();
                if (real.Count > 0 && (desc is null || !real.Contains(desc))) continue;

                long v = k.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    int i => (uint)i,
                    _ => 0,
                };

                if (v <= 0 && k.GetValue("HardwareInformation.MemorySize") is byte[] b && b.Length >= 4)
                    v = BitConverter.ToUInt32(b, 0);

                if (v > best) best = v;
            }

            return best > 0 ? Size(best) : "";
        }
        catch { return ""; }
    }

    
    private static string MonitorText()
    {
        try
        {
            var name = MonitorModel();

            var devName = PrimaryDisplayName();
            var dm = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>() };
            if (devName.Length == 0 || !EnumDisplaySettings(devName, -1, ref dm))
                return name;

            var parts = new List<string>();
            if (name.Length > 0) parts.Add(name);
            if (dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0) parts.Add($"{dm.dmPelsWidth} × {dm.dmPelsHeight}");
            if (dm.dmDisplayFrequency > 1) parts.Add(dm.dmDisplayFrequency + " Hz");
            return string.Join(" · ", parts);
        }
        catch { return ""; }
    }

    private static string PrimaryDisplayName()
    {
        var dd = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            const int attachedToDesktop = 0x00000001, primaryDevice = 0x00000004;
            if ((dd.StateFlags & attachedToDesktop) != 0 && (dd.StateFlags & primaryDevice) != 0)
                return dd.DeviceName ?? "";
            dd = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        }
        return @"\\.\DISPLAY1";
    }

    
    private static string MonitorModel()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY");
            if (root is null) return "";
            foreach (var mfg in root.GetSubKeyNames())
            {
                using var mk = root.OpenSubKey(mfg);
                if (mk is null) continue;
                foreach (var inst in mk.GetSubKeyNames())
                {
                    using var ik = mk.OpenSubKey(inst + @"\Device Parameters");
                    if (ik?.GetValue("EDID") is not byte[] edid || edid.Length < 128) continue;

                    var vendor = EdidVendor(edid);
                    var model = EdidModel(edid);
                    if (model.Length == 0) model = mfg;   

                    var text = (vendor + " " + model).Trim();
                    if (text.Length > 0) return text;
                }
            }
        }
        catch { }
        return "";
    }

    private static readonly Dictionary<string, string> MonitorVendorZh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DEL"] = "Dell", ["AUS"] = "ASUS", ["SAM"] = "Samsung", ["ACR"] = "Acer",
        ["LEN"] = "Lenovo", ["HPN"] = "HP", ["HWP"] = "HP", ["GSM"] = "LG",
        ["BNQ"] = "BenQ", ["MSI"] = "MSI", ["PHL"] = "Philips", ["VSC"] = "ViewSonic",
        ["HKC"] = "HKC", ["AOC"] = "AOC", ["CMN"] = "京东方", ["BOE"] = "京东方",
        ["AUO"] = "友达光电", ["INL"] = "InnoLux", ["SDC"] = "三星显示", ["LGD"] = "LG Display",
        ["CSO"] = "华星光电", ["HEC"] = "HEC", ["SHP"] = "夏普",
    };

    private static string EdidVendor(byte[] edid)
    {
        var v = (edid[8] << 8) | edid[9];
        var c1 = (char)(((v >> 10) & 0x1F) + 'A' - 1);
        var c2 = (char)(((v >> 5) & 0x1F) + 'A' - 1);
        var c3 = (char)((v & 0x1F) + 'A' - 1);
        var code = new string(new[] { c1, c2, c3 });
        return MonitorVendorZh.TryGetValue(code, out var zh) ? zh : code;
    }

    private static string EdidModel(byte[] edid)
    {
        foreach (var off in new[] { 54, 72, 90, 108 })
        {
            if (off + 18 > edid.Length) continue;
            if (edid[off] != 0 || edid[off + 1] != 0 || edid[off + 3] != 0xFC) continue;
            var sb = new StringBuilder();
            for (var i = off + 5; i < off + 18; i++)
            {
                if (edid[i] == 0x0A) break;
                if (edid[i] >= 0x20 && edid[i] < 0x7F) sb.Append((char)edid[i]);
            }
            var s = sb.ToString().Trim();
            if (s.Length > 0) return s;
        }
        return "";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType;
        public uint dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DevMode lpDevMode);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    

    

    
    
    
    
    public static string CpuModel()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
            
            name = System.Text.RegularExpressions.Regex.Replace(name, @"\s*@\s*[\d.]+\s*GHz", "").Trim();
            
            
            
            name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+CPU$", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            return name;
        }
        catch { return ""; }
    }

    
    public static string OsDisplay() => OsNameText();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    
    
    
    
    
    
    public static string DisplaySummary()
    {
        try
        {
            var monitors = GetSystemMetrics(80);   
            var w = GetSystemMetrics(0);           
            var h = GetSystemMetrics(1);           
            var dpi = GetDpiForSystem();
            var scale = dpi > 0 ? dpi * 100 / 96 : 0;
            return $"{monitors} 台／{w}x{h}／缩放 {scale}%";
        }
        catch { return "—"; }
    }

    
    
    
    
    
    public static string OsBuildEx()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = (k?.GetValue("CurrentBuildNumber") as string)?.Trim() ?? "";
            if (build.Length == 0) return "";

            
            var ubr = (k?.GetValue("UBR"))?.ToString()?.Trim() ?? "";
            return ubr.Length > 0 ? $"{build}.{ubr}" : build;
        }
        catch { return ""; }
    }

    private static int? _osBuild;

    
    
    
    
    
    
    
    
    
    public static int OsBuild()
    {
        if (_osBuild.HasValue) return _osBuild.Value;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var s = (k?.GetValue("CurrentBuildNumber") as string)?.Trim() ?? "";
            _osBuild = int.TryParse(s, out var b) ? b : 0;
        }
        catch { _osBuild = 0; }
        return _osBuild.Value;
    }

    
    
    
    
    public static bool IsWin11 => OsBuild() >= 22000;

    
    private static string OsNameText()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = (k?.GetValue("ProductName") as string)?.Trim() ?? "Windows";
            var build = k?.GetValue("CurrentBuildNumber") as string ?? "";
            var display = (k?.GetValue("DisplayVersion") as string)?.Trim()
                          ?? (k?.GetValue("ReleaseId") as string)?.Trim() ?? "";
            var editionId = (k?.GetValue("EditionID") as string)?.Trim() ?? "";

            
            var win = int.TryParse(build, out var b) && b >= 22000 ? "Windows 11" : "Windows 10";

            var edition = EditionZh(editionId);
            if (edition.Length == 0)   
                edition = product.Replace("Windows 10", "").Replace("Windows 11", "").Trim();

            return $"{win} {edition} {display}".Trim();
        }
        catch { return ""; }
    }

    
    private static string EditionZh(string id) => id switch
    {
        "Professional" => "专业版",
        "ProfessionalN" => "专业版 N",
        "Core" => "家庭版",
        "CoreN" => "家庭版 N",
        "CoreSingleLanguage" => "家庭单语言版",
        "CoreCountrySpecific" => "家庭中文版",
        "Enterprise" => "企业版",
        "EnterpriseN" => "企业版 N",
        "EnterpriseS" => "企业版 LTSC",
        "EnterpriseSN" => "企业版 LTSC N",
        "Education" => "教育版",
        "EducationN" => "教育版 N",
        "ProfessionalEducation" => "专业教育版",
        "ProfessionalWorkstation" => "专业工作站版",
        "ProfessionalWorkstationN" => "专业工作站版 N",
        "IoTEnterprise" => "IoT 企业版",
        "IoTEnterpriseS" => "IoT 企业版 LTSC",
        "Starter" => "入门版",
        _ => "",
    };

    
    private static string OsVersionText()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var major = k?.GetValue("CurrentMajorVersionNumber")?.ToString() ?? "10";
            var minor = k?.GetValue("CurrentMinorVersionNumber")?.ToString() ?? "0";
            var build = k?.GetValue("CurrentBuildNumber") as string ?? "";
            var ubr = k?.GetValue("UBR")?.ToString() ?? "";
            var version = $"{major}.{minor}.{build}";
            if (ubr.Length > 0) version += "." + ubr;
            var bits = Environment.Is64BitOperatingSystem ? "64 位" : "32 位";
            return $"{version}（{bits}）";
        }
        catch { return ""; }
    }

    
    private static string InstallDateText()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (k?.GetValue("InstallDate") is int secs && secs > 0)
                return DateTimeOffset.FromUnixTimeSeconds(secs).ToLocalTime().ToString("yyyy-MM-dd");
            if (k?.GetValue("InstallTime") is long ft && ft > 0)
                return DateTime.FromFileTime(ft).ToString("yyyy-MM-dd");
        }
        catch { }
        return "";
    }

    
    
    private static string PageFileText()
    {
        try
        {
            if (!GetPerformanceInfo(out var pi, (uint)Marshal.SizeOf<PerformanceInformation>())) return "";
            var page = (long)pi.PageSize;
            if (page <= 0) return "";

            var limitBytes = (long)pi.CommitLimit * page;                  
            var pageFileBytes = ((long)pi.CommitLimit - (long)pi.PhysicalTotal) * page;   

            var parts = new List<string> { $"共 {Size(limitBytes)}" };
            parts.Add(pageFileBytes > 0 ? $"分页文件 {Size(pageFileBytes)}" : "分页文件 未启用");
            return string.Join(" · ", parts);
        }
        catch { return ""; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint cb;
        public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable;
        public UIntPtr SystemCache, KernelTotal, KernelPaged, KernelNonPaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetPerformanceInfo(out PerformanceInformation info, uint size);

    
    
    
    
    
    private static string VirtualizationText()
    {
        try
        {
            if (IsProcessorFeaturePresent(21)) return "已启用";                     
            if (VbsEnabled() || HypervisorServicePresent()) return "已启用（Hyper-V 运行中）";
            return "未启用";
        }
        catch { return ""; }
    }

    private static bool VbsEnabled()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard");
            if (k is null) return false;
            if (k.GetValue("EnableVirtualizationBasedSecurity") is int v && v == 1) return true;

            using var s = k.OpenSubKey(@"Scenarios\HypervisorEnforcedCodeIntegrity");
            return s?.GetValue("Enabled") is int e && e == 1;
        }
        catch { return false; }
    }

    
    private static bool HypervisorServicePresent()
    {
        try
        {
            foreach (var name in new[] { "VMBus", "vmbusr", "HvHost" })
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
                if (k is not null) return true;
            }
        }
        catch { }
        return false;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessorFeaturePresent(uint feature);

    
    private static string DirectXText()
    {
        try
        {
            var dir = Environment.SystemDirectory;
            if (File.Exists(Path.Combine(dir, "d3d12.dll")) || File.Exists(Path.Combine(dir, "d3d12core.dll")))
                return "DirectX 12";
            if (File.Exists(Path.Combine(dir, "d3d11.dll")))
                return "DirectX 11";

            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\DirectX");
            var v = (k?.GetValue("Version") as string)?.Trim();
            return Blank(v) ? "" : "DirectX " + v;
        }
        catch { return ""; }
    }

    
    private static string Mb(long mb) => mb >= 1024 ? (mb / 1024d).ToString("0.#") + " GB" : mb + " MB";

    private static string Size(long bytes)
    {
        const double gb = 1024d * 1024d * 1024d;
        var g = bytes / gb;
        return g >= 1000 ? (g / 1024d).ToString("0.##") + " TB" : g.ToString("0.#") + " GB";
    }
}
