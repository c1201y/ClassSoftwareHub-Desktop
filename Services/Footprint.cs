using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>快照里一条记录是什么东西。</summary>
public enum FootprintKind
{
    /// <summary>文件夹或文件（<see cref="FootprintNode.Path"/> 是完整路径）。</summary>
    Path = 0,

    /// <summary>注册表项（<see cref="FootprintNode.Path"/> 是完整键路径，如 <c>HKCU\Software\Foo</c>）。</summary>
    RegistryKey = 1,

    /// <summary>注册表里的一个值（<see cref="FootprintNode.ValueName"/> 才是名字）。</summary>
    RegistryValue = 2,
}

/// <summary>
/// 快照里的一条「活动痕迹」。
/// <paramref name="Probe"/> 是它从哪个体检点抓到的（给人看的位置说明，如「用户数据」），
/// <paramref name="Path"/> 是能直接动手的完整路径（目录 / 注册表键路径）。
/// </summary>
public sealed record FootprintNode(
    FootprintKind Kind,
    string Probe,
    string Path,
    string Name,
    string ValueName = "");

/// <summary>
/// 某一时刻本机的「浅层痕迹快照」（2026-10-14 加，卸载残留清理的地基）。
///
/// ⛔ 为什么是**浅层**：只列一批"软件最爱拉屎的地方"的**一级**内容（目录名 / 键名），
///    不递归。理由有三：
///      ① 递归遍历 <c>%LOCALAPPDATA%</c> 这类目录要几十万次 IO，卸载流程里等不起；
///      ② 软件留下的痕迹**绝大多数就是一级目录 / 一级键**（<c>%LOCALAPPDATA%\某软件</c>、
///         <c>HKCU\Software\某软件</c>），深处的文件本来就在那个一级目录里面，删掉一级就一起没了；
///      ③ 浅层集合只有几千条字符串，前后两份快照求差集是**毫秒级**的事。
///
/// 用法就是「卸载前抓一份、卸载后再抓一份、两边求差集」—— 这是唯一一种**不需要用户提前做任何准备**
/// 就能确定"这些痕迹是这个软件留下的"的办法（Geek / HiBit 那套事后瞎猜的路子，本程序不采用；
/// HiBit 的"安装监控"倒是准，但要用户装之前就记着去开，现实中没人会配合）。
///
/// ⚠️ 边界（界面上要如实说）：
///   · 只覆盖**一级**。软件把痕迹藏在 <c>%LOCALAPPDATA%\SomeVendor\SomeApp</c> 这种两层结构里时，
///     抓到的会是 <c>SomeVendor</c> 这一层 —— 名字对不上，会被判成"弱证据"（默认不勾）。
///   · <c>%TEMP%</c> 故意**不抓**：装什么都会在里面留目录，噪声大到没用。
///   · <c>HKLM</c> 只是**读**，不需要管理员（写才需要）。
/// </summary>
public sealed class FootprintSnapshot
{
    private readonly Dictionary<string, FootprintNode> _nodes;

    private FootprintSnapshot(DateTime takenAt, Dictionary<string, FootprintNode> nodes,
                              int elapsedMs, bool truncated)
    {
        TakenAt = takenAt;
        _nodes = nodes;
        ElapsedMs = elapsedMs;
        Truncated = truncated;
    }

    public DateTime TakenAt { get; }

    /// <summary>抓这一份花了多少毫秒（自测用，会写进日志）。</summary>
    public int ElapsedMs { get; }

    /// <summary>
    /// 有没有因为超过时间预算而中途放弃。
    /// ⚠️ 一旦为 true，两份快照的覆盖范围就可能不一致 ⇒ 差集里会混进"假新记录"。
    ///    处理方式见 <see cref="LeftoverResolver"/>：整体降级，只保留名字对得上的那些候选。
    /// </summary>
    public bool Truncated { get; }

    public int Count => _nodes.Count;

    internal IReadOnlyDictionary<string, FootprintNode> Nodes => _nodes;

    /// <summary>抓一份当前快照。<paramref name="phase"/> 只用于日志（如「卸载前」）。</summary>
    public static FootprintSnapshot Capture(string phase)
    {
        var sw = Stopwatch.StartNew();
        var nodes = new Dictionary<string, FootprintNode>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;

        foreach (var probe in Probes)
        {
            if (sw.ElapsedMilliseconds > BudgetMs)
            {
                truncated = true;
                AppLog.Warning("footprint", $"快照（{phase}）超过 {BudgetMs} ms 预算，剩下的体检点已跳过");
                break;
            }

            try
            {
                if (probe.Kind == ProbeKind.Directory) ScanDirectory(probe, nodes);
                else ScanRegistry(probe, nodes);
            }
            catch (Exception ex)
            {
                // 单个体检点读不动（权限 / 已被删）不影响别的 —— 但必须留痕，⛔ 别写裸 catch
                AppLog.Info("footprint", $"体检点「{probe.Label}」读取失败：{ex.GetType().Name} {ex.Message}");
            }
        }

        sw.Stop();
        AppLog.Info("footprint",
            $"快照（{phase}）：{nodes.Count} 条痕迹，用时 {sw.ElapsedMilliseconds} ms" +
            (truncated ? "（已截断）" : ""));

        return new FootprintSnapshot(DateTime.Now, nodes, (int)sw.ElapsedMilliseconds, truncated);
    }

    /// <summary>两份快照里"后一份有、前一份没有"的那些记录（即卸载过程中新出现的）。</summary>
    public IReadOnlyList<FootprintNode> NewSince(FootprintSnapshot before)
    {
        var list = new List<FootprintNode>();
        foreach (var (key, node) in _nodes)
            if (!before._nodes.ContainsKey(key))
                list.Add(node);
        return list;
    }

    /// <summary>这一条在另一份快照里是不是**已经存在**（用来区分"新冒出来的"和"老早就在那儿的"）。</summary>
    public bool Has(FootprintNode node) => _nodes.ContainsKey(KeyOf(node));

    // ══════════════════════════════════════════════════════════════════════════
    //  体检点清单
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>整份快照的时间预算。真机上这套浅层枚举实测在 100 ms 量级，2 秒是留给"注册表被安全软件挂钩"的极端情况。</summary>
    private const int BudgetMs = 2000;

    private enum ProbeKind { Directory, Registry }

    /// <summary>
    /// 一个体检点。<paramref name="Depth"/> 对目录无意义；对注册表 = 往下走几层（1 = 只看直接子键）。
    /// <paramref name="ValuesOnly"/> = 不列子键、只列这个键下的**值名**（<c>Run</c> 这种键的用法）。
    /// </summary>
    private sealed record Probe(
        ProbeKind Kind,
        string Label,
        string Base,
        int Depth = 1,
        bool ValuesOnly = false,
        bool IncludeFiles = true);

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string ProgramDataDir => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string UserProfileDir => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>开始菜单 / 桌面这两个地方，装什么都会掉快捷方式，是最直观的"残留"。</summary>
    private static string StartMenuAllUsers => Path.Combine(ProgramDataDir,
        @"Microsoft\Windows\Start Menu\Programs");

    private static string StartMenuCurrentUser => Path.Combine(RoamingAppData,
        @"Microsoft\Windows\Start Menu\Programs");

    /// <summary>
    /// 体检点。⚠️ 顺序有意义（预算耗尽时是"从后往前砍"），所以把最要紧的放前面：
    /// 用户数据 → 用户级程序目录 → 公共数据 → 程序目录 → 开始菜单 → 注册表。
    /// </summary>
    private static readonly Probe[] Probes =
    {
        // ── 文件系统（浅层：只看一级） ──
        new(ProbeKind.Directory, "用户数据",      LocalAppData),
        new(ProbeKind.Directory, "漫游数据",      RoamingAppData),
        new(ProbeKind.Directory, "用户级程序",    Path.Combine(LocalAppData, "Programs")),
        new(ProbeKind.Directory, "低完整性数据",  Path.Combine(LocalAppData, "..", "LocalLow")),
        new(ProbeKind.Directory, "公共数据",      ProgramDataDir),
        new(ProbeKind.Directory, "程序目录",      Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
        new(ProbeKind.Directory, "程序目录(32位)", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
        new(ProbeKind.Directory, "开始菜单(全部用户)", StartMenuAllUsers),
        new(ProbeKind.Directory, "开始菜单(当前用户)", StartMenuCurrentUser),
        new(ProbeKind.Directory, "用户目录",      UserProfileDir),

        // ── 注册表（浅层：只看一级子键） ──
        new(ProbeKind.Registry, "用户软件键",   @"HKCU\SOFTWARE"),
        new(ProbeKind.Registry, "机器软件键",   @"HKLM\SOFTWARE"),
        new(ProbeKind.Registry, "机器软件键(32位)", @"HKLM\SOFTWARE\WOW6432Node"),

        // × 卸载登记：卸载干净的话这几条会**消失**（不是新增），所以它们的作用是"反证"——
        //   如果卸载后这里还多出一条，说明卸载器自己又写了一遍。
        new(ProbeKind.Registry, "卸载登记(当前用户)", @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        new(ProbeKind.Registry, "卸载登记(机器)",     @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        new(ProbeKind.Registry, "卸载登记(机器32位)", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),

        // × 开机自启：软件最爱留的一处，而且删错了最直观地"能看出来"
        new(ProbeKind.Registry, "开机自启(当前用户)", @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", ValuesOnly: true),
        new(ProbeKind.Registry, "开机自启(机器)",     @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", ValuesOnly: true),
        new(ProbeKind.Registry, "一次性自启(当前用户)", @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", ValuesOnly: true),
        new(ProbeKind.Registry, "一次性自启(机器)",   @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", ValuesOnly: true),

        // × 程序路径登记（App Paths）：很多安装器会写一条 <软件名>.exe 在这里
        new(ProbeKind.Registry, "路径登记(当前用户)", @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"),
        new(ProbeKind.Registry, "路径登记(机器)",     @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"),
        new(ProbeKind.Registry, "路径登记(机器32位)", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths"),

        // × 文件关联 / ProgID：自定义后缀的软件会在这儿留一串
        new(ProbeKind.Registry, "文件关联(当前用户)", @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FileExts", Depth: 2),
        new(ProbeKind.Registry, "文件夹联(机器)",     @"HKLM\SOFTWARE\Classes", Depth: 1),
        new(ProbeKind.Registry, "文件夹联(当前用户)", @"HKCU\SOFTWARE\Classes", Depth: 1),

        // × 「按程序关联」入口（Windows 自己在"打开方式"里用的那套）
        new(ProbeKind.Registry, "程序关联(当前用户)", @"HKCU\SOFTWARE\Classes\Applications"),
        new(ProbeKind.Registry, "程序关联(机器)",     @"HKLM\SOFTWARE\Classes\Applications"),

        // × 共享组件引用计数：老的安装器（InstallShield 一族）用它记"这个 DLL 还有谁在用"，
        //   卸载器经常漏删。值名就是 DLL 的完整路径。
        new(ProbeKind.Registry, "共享组件登记", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs", ValuesOnly: true),
        new(ProbeKind.Registry, "共享组件登记(32位)", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs", ValuesOnly: true),
    };

    // ══════════════════════════════════════════════════════════════════════════
    //  枚举
    // ══════════════════════════════════════════════════════════════════════════

    private static void ScanDirectory(Probe probe, Dictionary<string, FootprintNode> sink)
    {
        var baseDir = Path.GetFullPath(probe.Base);
        if (!Directory.Exists(baseDir)) return;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(baseDir))
                Add(sink, FootprintKind.Path, probe.Label, dir, Path.GetFileName(dir));

            if (probe.IncludeFiles)
            {
                foreach (var file in Directory.EnumerateFiles(baseDir))
                    Add(sink, FootprintKind.Path, probe.Label, file, Path.GetFileName(file));
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 少数目录（系统级的）普通账户列不动 —— 正常的，不当异常报
        }
    }

    private static void ScanRegistry(Probe probe, Dictionary<string, FootprintNode> sink)
    {
        if (!RegistryPath.TryParse(probe.Base, out var hive, out var subPath)) return;

        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var key = root.OpenSubKey(subPath, writable: false);
        if (key is null) return;

        if (probe.ValuesOnly)
        {
            // Run / SharedDLLs 这类：键本身是系统固定的，软件留下的是**值**
            foreach (var name in key.GetValueNames())
            {
                if (name.Length == 0) continue;   // "(默认)"值不算
                Add(sink, FootprintKind.RegistryValue, probe.Label, probe.Base, name, name);
            }
            return;
        }

        WalkRegistry(key, probe.Base, probe.Label, probe.Depth, sink);
    }

    private static void WalkRegistry(RegistryKey key, string keyPath, string label, int depth,
                                     Dictionary<string, FootprintNode> sink)
    {
        if (depth <= 0) return;

        foreach (var name in key.GetSubKeyNames())
        {
            var childPath = keyPath + "\\" + name;
            Add(sink, FootprintKind.RegistryKey, label, childPath, name);

            if (depth <= 1) continue;

            try
            {
                using var child = key.OpenSubKey(name, writable: false);
                if (child is not null) WalkRegistry(child, childPath, label, depth - 1, sink);
            }
            catch (Exception ex)
            {
                AppLog.Info("footprint", $"读注册表子键失败（{childPath}）：{ex.GetType().Name}");
            }
        }
    }

    /// <summary>
    /// 落进字典。键 = 种类 + 路径 + 值名，全小写 —— 注册表和文件系统在 Windows 上都不区分大小写，
    /// 前后两份快照必须用同一套口径比，否则会出现"明明没变却报新增"。
    /// </summary>
    private static void Add(Dictionary<string, FootprintNode> sink, FootprintKind kind, string label,
                            string path, string name, string valueName = "")
    {
        sink.TryAdd(KeyOf(kind, path, valueName), new FootprintNode(kind, label, path, name, valueName));
    }

    private static string KeyOf(FootprintKind kind, string path, string valueName)
        => kind + "|" + path.ToLowerInvariant() + "|" + valueName.ToLowerInvariant();

    private static string KeyOf(FootprintNode node) => KeyOf(node.Kind, node.Path, node.ValueName);

    /// <summary>给外部（残留判定）用的同一套键算法 —— 必须与快照内部一致，否则比不出来。</summary>
    internal static string KeyOfPublic(FootprintNode node) => KeyOf(node);
}

/// <summary>
/// <c>HKCU\SOFTWARE\Foo</c> ↔ <see cref="RegistryHive"/> + 子路径 的互转。
/// 快照、备份、清理三处都要用，单独拎出来免得各写一份（写岔了就是删错键）。
/// </summary>
internal static class RegistryPath
{
    /// <summary>把 <c>HKLM\SOFTWARE\X</c> 拆成 <c>(RegistryHive.LocalMachine, "SOFTWARE\X")</c>。</summary>
    public static bool TryParse(string fullPath, out RegistryHive hive, out string subPath)
    {
        hive = RegistryHive.CurrentUser;
        subPath = "";

        if (string.IsNullOrWhiteSpace(fullPath)) return false;

        var s = fullPath.Trim();
        var slash = s.IndexOf('\\');
        var root = slash < 0 ? s : s[..slash];
        subPath = slash < 0 ? "" : s[(slash + 1)..];

        switch (root.ToUpperInvariant())
        {
            case "HKCU":
            case "HKEY_CURRENT_USER":
                hive = RegistryHive.CurrentUser;
                return true;

            case "HKLM":
            case "HKEY_LOCAL_MACHINE":
                hive = RegistryHive.LocalMachine;
                return true;

            case "HKCR":
            case "HKEY_CLASSES_ROOT":
                hive = RegistryHive.ClassesRoot;
                return true;

            case "HKU":
            case "HKEY_USERS":
                hive = RegistryHive.Users;
                return true;

            default:
                return false;
        }
    }

    /// <summary>反过来：把 hive + 子路径拼回 <c>HKLM\SOFTWARE\X</c> 这种完整写法。</summary>
    public static string Combine(RegistryHive hive, string subPath)
    {
        var head = hive switch
        {
            RegistryHive.LocalMachine => "HKLM",
            RegistryHive.CurrentUser => "HKCU",
            RegistryHive.ClassesRoot => "HKCR",
            RegistryHive.Users => "HKU",
            _ => "HKLM",
        };

        return subPath.Length == 0 ? head : head + "\\" + subPath;
    }
}
