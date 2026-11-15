using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 「应用卸载」页上的分类（2026-10-13 加）。
///
/// 为什么要分：这个列表一口气能出几百条，里面有商店应用、有 VC++ 运行库、
/// 还有 Windows 自带的组件包，混在一起根本没法用。Nick 的原话是
/// 「分类可以做好一下，运行库、桌面应用、还有 Microsoft Store」。
/// </summary>
public enum AppCategory
{
    /// <summary>传统安装包 —— 在「程序和功能」（注册表 Uninstall 键）里登记的软件。</summary>
    Desktop = 0,

    /// <summary>运行库 / 运行时 —— VC++、.NET、DirectX、Java 这类「别的软件依赖的东西」。</summary>
    Runtime = 1,

    /// <summary>当前账户注册的 Microsoft Store / MSIX 应用。</summary>
    Store = 2,

    /// <summary>
    /// Windows 自带的系统组件包（商店仓储里 System 签名的）。
    /// ⚠️ 界面上**默认不勾** —— 这些既卸不掉也不该卸，混进来只会淹掉真东西（Nick 2026-10-13 报的"无关的太多"）。
    /// </summary>
    System = 3,
}

/// <summary>
/// 本机装的一个软件（从注册表 Uninstall 键读出来的，或一个 Microsoft Store / MSIX 包）。
///
/// ⚠️ 后四个字段是 2026-10-12 为「应用卸载」功能补的，都带默认值 ——
///    「本机核实」只用前四个，老代码 <c>new InstalledApp(n, v, p, l)</c> 照样编译，不用改。
///    加在这里而不是另开一个类，是为了**不重复读一遍注册表**（那活儿在本机挺贵）。
/// </summary>
/// <param name="UninstallString">注册表里的卸载命令原文（如 <c>"C:\...\uninst.exe" /S</c> 或 <c>MsiExec.exe /I{GUID}</c>）。</param>
/// <param name="QuietUninstallString">厂商额外给的静默卸载命令，有就优先用它（少一次交互）。</param>
/// <param name="IsStore">true = Microsoft Store / MSIX 包 —— 它不写 Uninstall 键，卸载要走另一条路。</param>
/// <param name="PackageFullName">MSIX 的包全名（<c>Name_Version_Arch__PublisherHash</c>），卸载时按它定位。</param>
/// <param name="Category">界面上的分类（2026-10-13 加），决定它进哪颗筛选芯片。</param>
/// <param name="IconPath">
/// 取图标用的**文件路径**（2026-10-13 加）—— 桌面应用指向 exe/ico，商店应用指向包里的 logo png。
/// 空 = 拿不到图标，界面显示分类占位字形。⚠️ 只是个路径，真正的图标在界面线程上按需掏（见 AppIconService）。
/// </param>
public sealed record InstalledApp(
    string Name,
    string Version,
    string Publisher,
    string Location,
    string UninstallString = "",
    string QuietUninstallString = "",
    bool IsStore = false,
    string PackageFullName = "",
    AppCategory Category = AppCategory.Desktop,
    string IconPath = "");

/// <summary>
/// 枚举本机已安装的桌面软件。
///
/// 两个来源：
///   ①「程序和功能」的安装记录（注册表 Uninstall 键）—— 传统安装包的软件都在这里
///   ② Microsoft Store / MSIX 应用（按用户的包仓库）—— **不写** Uninstall 键，只在 ① 里找就是"没装"
///
/// 只读注册表，不需要管理员，不联网，不落盘。
/// ① 覆盖三个位置（少一个就会漏一半软件）：
///   · HKLM 64 位视图  —— 绝大多数 64 位程序
///   · HKLM 32 位视图  —— 32 位程序（在 64 位系统上被重定向到 WOW6432Node）
///   · HKCU            —— 只给当前用户装的程序（很多国产软件走这条路）
///
/// ⚠️ 已知边界（界面上要如实说明，别让用户以为这里是权威清单）：
///   · 免安装（绿色版）软件不写任何注册表 —— 查不到，不是没装
///   · MSIX 只覆盖**当前用户**注册的包；别的账户装的店铺版应用读不到
/// </summary>
public static class InstalledApps
{
    /// <summary>抓一份本机已装软件列表（按名称去重）。</summary>
    public static List<InstalledApp> Enumerate()
    {
        var found = new List<InstalledApp>();

        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Registry64),
                     (RegistryHive.CurrentUser, RegistryView.Registry32),
                 })
        {
            ReadHive(hive, view, found);
        }

        // 同一款软件可能在 64/32 两个视图里各出现一次（甚至多个版本并存）→ 按「名称+版本」去重。
        // ⚠️ 去重时**优先留带卸载命令的那条**（2026-10-12 加）：两个视图里常常一只有 UninstallString、
        //    另一只没有（比如 32 位残留键），随便留一条会让「应用卸载」页上这颗按钮点了没反应。
        return found
            .GroupBy(a => (a.Name.ToLowerInvariant(), a.Version.ToLowerInvariant()))
            .Select(g => g.OrderByDescending(a => a.UninstallString.Length > 0).First())
            .OrderBy(a => a.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// 抓一份**当前账户**注册的 Microsoft Store / MSIX 应用。
    ///
    /// ⚠️ 2026-10-13 重写（Nick：「无关的太多了」的**主因**就在这儿）。
    ///
    /// 原来读的是注册表仓库键，而那个键是**机器级**的 ——
    /// 本机实测：里面躺着 790 个包，只有 188 个属于当前账户。
    /// 剩下 600 个是别的账户、以及 Windows 自己的组件包，它们的 DisplayName 又都是
    /// 「@…normal_neutral_cw5n1h2txyewy/ms-resource://…」这种没展开的引用，
    /// 于是整屏都是天书（Nick 截图里那一片）。
    /// 而且注册表里**没有**任何值能区分"这个包属于谁" —— 当前账户的
    /// <c>Microsoft.WindowsCalculator</c> 和别家的包，键结构一模一样。
    ///
    /// 所以改用 WinRT 的 <c>PackageManager</c>：系统「设置 → 应用」和
    /// <c>Get-AppxPackage</c> 用的就是它，拿到的天然只有**当前账户**的包。
    /// 顺带白拿三样东西：
    ///   · <c>DisplayName</c> 由系统解析资源引用 —— 不用我们自己拼 ms-resource；
    ///   · <c>SignatureKind</c> 直接区分"商店应用"和"Windows 自带组件"（注册表里没有这个值）；
    ///   · <c>Logo</c> 就是包里那张方形图标。
    ///
    /// 跳过框架包与资源包 —— 那些不是"软件"，列出来只会淹掉真东西。
    /// </summary>
    public static List<InstalledApp> EnumerateStorePackages()
    {
        try
        {
            return EnumerateStoreFromPackageManager();
        }
        catch (Exception ex)
        {
            // 极端情形（接口被策略挡了之类）。回落去读注册表 —— 噪声大，但总比整个分类空着强。
            AppLog.Exception("uninstall", ex, "读商店应用（PackageManager）");
            return EnumerateStoreFromRegistry();
        }
    }

    private static List<InstalledApp> EnumerateStoreFromPackageManager()
    {
        var found = new List<InstalledApp>();

        var pm = new Windows.Management.Deployment.PackageManager();

        // "" = 当前用户。传别人的 SID 要受限能力，这里不需要也不该要。
        foreach (var pkg in pm.FindPackagesForUser(""))
        {
            try
            {
                if (pkg.IsFramework || pkg.IsResourcePackage || pkg.IsBundle) continue;

                var id = pkg.Id;
                var fullName = id.FullName;

                var name = CleanDisplayName(pkg.DisplayName, fullName);
                if (name.Length == 0) continue;

                // 包目录 / 图标 / 发布者：任意一项读不动都不该拖垮这个包（系统包的 ACL 很严）
                var location = "";
                try { location = pkg.InstalledLocation?.Path ?? ""; } catch { /* 读不到就算了 */ }

                var icon = "";
                try { icon = pkg.Logo?.LocalPath ?? ""; } catch { /* 同上 */ }

                var publisher = "";
                try { publisher = pkg.PublisherDisplayName?.Trim() ?? ""; } catch { /* 同上 */ }

                // SignatureKind = System ⇒ Windows 自带的组件包。
                // ⚠️ 这里才是那个判据；注册表里根本没有对应的值（2026-10-13 查实）
                var isSystem = pkg.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System;

                // ⚠️⚠️ 版本号必须**手动拼**：PackageVersion 是 WinRT 结构体，投影到 C# 后没有重写
                //    ToString()，直接 .ToString() 会把类型名 "Windows.ApplicationModel.PackageVersion"
                //    当成版本显示出来（2026-10-13 截图实拍，整列都是这个）。
                var ver = id.Version;
                var version = $"{ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}";

                found.Add(new InstalledApp(
                    name,
                    version,
                    publisher,
                    location,
                    IsStore: true,
                    PackageFullName: fullName,
                    Category: isSystem ? AppCategory.System : AppCategory.Store,
                    IconPath: icon));
            }
            catch
            {
                // 单个包读不动（权限/损坏）不该拖垮整轮枚举
            }
        }

        // 同一个应用可能按架构拆成多个包（甚至两个视图各来一份）→ 按名称去重
        return found
            .GroupBy(a => DedupKey(a.Name))
            .Where(g => g.Key.Length > 0)
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// 去重用的键：比 <see cref="Normalize"/> **保守得多** —— 只裁空白、转小写。
    ///
    /// ⚠️ 2026-10-13 单独分出来。别图省事拿 <see cref="Normalize"/> 当这个键：
    ///    它会把括号里的内容**整段删掉**，于是
    ///    「Windows Package Manager Source (winget)」和「… (winget-font)」
    ///    这两个不同的包会被合并成一条（实拍比对时发现）。
    ///    去重真正要解决的只是「同一个应用按架构/语言拆成多个包」——
    ///    那种情况名字是**完全相同**的（如本机的「Windows 功能体验包」×9）。
    ///    Normalize 是给「和站点清单模糊比对」用的，那里才需要剥括号。
    /// </summary>
    private static string DedupKey(string? name) => (name ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// 包显示名的最后一道清洗：系统给的一般已经是人话了，但保底再走一遍间接字符串，
    /// 还是解不出来就退化成「包名的最后一段」（<c>28AE62C8.FurryXiyi.DismGUI</c> → <c>DismGUI</c>）。
    /// </summary>
    private static string CleanDisplayName(string? raw, string fullName)
    {
        var s = raw?.Trim() ?? "";

        if (s.Length > 0 && s[0] != '@' &&
            !s.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            return s;

        var resolved = s.Length > 0 ? IndirectString.Resolve(s) : "";
        return resolved.Length > 0 ? resolved : LastNameSegment(fullName);
    }

    /// <summary>
    /// 兜底路径：直接读注册表仓库键。
    /// ⚠️ 只在 <c>PackageManager</c> 不可用时才走 —— 它分不出"包属于谁"，噪声很大（见上方注释）。
    /// </summary>
    private static List<InstalledApp> EnumerateStoreFromRegistry()
    {
        var found = new List<InstalledApp>();

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                using var repo = baseKey.OpenSubKey(
                    @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
                if (repo is null) continue;

                foreach (var fullName in repo.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = repo.OpenSubKey(fullName);
                        var app = ReadPackage(sub, fullName);
                        if (app is not null) found.Add(app);
                    }
                    catch
                    {
                        // 单个包读不动不该拖垮整轮
                    }
                }
            }
            catch
            {
                // 整个视图读不到就算了
            }
        }

        // 同一个应用可能按架构/语言拆成多个包（甚至两个视图各来一份）→ 按名称去重
        return found
            .GroupBy(a => DedupKey(a.Name))
            .Where(g => g.Key.Length > 0)
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private static InstalledApp? ReadPackage(RegistryKey? k, string fullName)
    {
        if (k is null) return null;

        // Framework=1 = 运行时/框架包（VCLibs、WindowsAppRuntime…），不是用户装的"软件"
        // ⚠️ 2026-10-13 保持过滤：它们在系统「设置 → 应用」里同样不显示，卸掉还会连累一串商店应用。
        //    所以「运行库」分类里也不会出现它们 —— 那类东西的正确归属是桌面侧的 VC++ / .NET。
        if (k.GetValue("Framework") is int fw && fw == 1) return null;
        // 资源包 / 语言包同样不是"应用"
        if (k.GetValue("IsResourcePackage") is int rp && rp == 1) return null;

        // ⚠️ 注册表仓库键里**没有**能区分"商店应用 / Windows 自带组件"的值，也分不出"包属于哪个账户"
        //    （2026-10-13 逐键查实）—— 这正是这条回落路噪声大的原因，所以它只兜底。
        //    正式那条走 PackageManager（见 EnumerateStoreFromPackageManager）。
        var name = ResolvePackageName(k, fullName);
        if (name.Length == 0) return null;

        var location = (k.GetValue("PackageRootFolder") as string)?.Trim() ?? "";
        var publisher = (k.GetValue("PublisherDisplayName") as string)?.Trim() ?? "";

        // ⚠️ 商店包没有 UninstallString —— 卸载只能按 PackageFullName 走 PackageManager / Remove-AppxPackage。
        //    fullName 在这里必须原样留着，它就是那条路的钥匙。
        return new InstalledApp(name, VersionOf(fullName), publisher, location,
                                IsStore: true, PackageFullName: fullName,
                                Category: AppCategory.Store,
                                IconPath: ResolveStoreLogo(location));
    }

    /// <summary>
    /// 把注册表里的 DisplayName 变成能摆在界面上的名字。
    ///
    /// 两种难缠的写法：
    ///   · <c>ms-resource:AppDisplayName</c> —— 老式资源引用，没有包上下文解不开；
    ///   · <c>@{PackageFullName?ms-resource://…}</c> / <c>@!w~{…}</c> —— 间接字符串，要交给系统解。
    ///     ⚠️ 2026-10-13 补的：旧实现只认 <c>ms-resource:</c> 前缀，于是整屏都是
    ///     「@!w~{6E5C2524a-…}_10.0.26100.2_normal_neutral_cw5n1h2txyewy/ms-resource://FileExplorer/Res…」
    ///     这种鬼东西（Nick 截的那张图就是它）。现在交给 <see cref="IndirectString"/> 解。
    /// 都解不出来才退化成「包名的最后一段」（28AE62C8.FurryXiyi.DismGUI → DismGUI）。
    /// </summary>
    private static string ResolvePackageName(RegistryKey k, string fullName)
    {
        var raw = (k.GetValue("DisplayName") as string)?.Trim() ?? "";

        if (raw.Length > 0 && raw[0] != '@' &&
            !raw.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            return raw;

        if (raw.Length > 0 && raw[0] == '@')
        {
            var resolved = IndirectString.Resolve(raw);
            if (resolved.Length > 0 && resolved[0] != '@') return resolved;
        }

        return LastNameSegment(fullName);
    }

    /// <summary>
    /// 商店应用的图标：包目录里那张方形的 logo png。
    /// ⚠️ 包目录在 <c>C:\Program Files\WindowsApps\…</c> 下面，**根目录普通账户列不动** ——
    /// 但具体某个包的子目录是读得到的（应用自己要能加载它）。所以这里逐级 try/catch，
    /// 一处读不到就往下退，退到最后返回空串（界面显示分类占位图标，不影响别的）。
    /// </summary>
    private static string ResolveStoreLogo(string packageRoot)
    {
        if (packageRoot.Length == 0) return "";

        foreach (var dir in new[] { Path.Combine(packageRoot, "Assets"), packageRoot })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;

                // 优先 44×44（列表里就显示 32，正好）；没有就退到 150 与商店 logo
                foreach (var pattern in new[] { "Square44x44Logo*.png", "Square150x150Logo*.png", "StoreLogo*.png" })
                {
                    foreach (var hit in Directory.EnumerateFiles(dir, pattern)
                                                 .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    {
                        return hit;
                    }
                }
            }
            catch
            {
                // 这个目录读不动 → 试下一个
            }
        }

        return "";
    }

    /// <summary>PackageFullName = <c>Name_Version_Arch__PublisherHash</c> → 取 Name 的最后一段。</summary>
    private static string LastNameSegment(string fullName)
    {
        var name = fullName.Split('_')[0];
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot + 1 < name.Length ? name[(dot + 1)..] : name;
    }

    /// <summary>PackageFullName 的第二段就是版本号（如 <c>4.5.0.0</c>）。</summary>
    private static string VersionOf(string fullName)
    {
        var parts = fullName.Split('_');
        return parts.Length > 1 ? parts[1] : "";
    }

    private static void ReadHive(RegistryHive hive, RegistryView view, List<InstalledApp> sink)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) return;

            foreach (var subName in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var sub = uninstall.OpenSubKey(subName);
                    var app = ReadEntry(sub);
                    if (app is not null) sink.Add(app);
                }
                catch
                {
                    // 单个键读不动（权限/损坏）不该拖垮整轮枚举
                }
            }
        }
        catch
        {
            // 整个视图读不到就算了，继续下一个
        }
    }

    private static InstalledApp? ReadEntry(RegistryKey? k)
    {
        if (k is null) return null;

        var name = (k.GetValue("DisplayName") as string)?.Trim() ?? "";
        if (name.Length == 0) return null;

        // 运行库判定要**早于** SystemComponent 那道闸（见下），所以先算出来
        var isRuntime = LooksLikeRuntime(name);

        // ── 排除「不是软件」的条目 ──
        // SystemComponent=1：系统组件（驱动、补丁、**以及绝大多数运行库**）。
        //   ⚠️ 2026-10-13 改：**运行库放行**，单独归到 <see cref="AppCategory.Runtime"/>。
        //      Nick 要求分类里必须有「运行库」一项，而 VC++ Redist / .NET Runtime 恰恰全被这一条挡掉了
        //      —— 严格按旧规则走，那个分类会是个空壳。
        //   ⛔ 其余系统组件照旧跳过：它们不是"能卸的软件"，列出来只会淹掉真东西。
        if (k.GetValue("SystemComponent") is int sc && sc == 1 && !isRuntime) return null;
        // ParentKeyName：补丁/更新，挂在主程序下面
        if (!string.IsNullOrWhiteSpace(k.GetValue("ParentKeyName") as string)) return null;
        // ReleaseType：热修复 / 更新汇总 / Service Pack
        var releaseType = (k.GetValue("ReleaseType") as string)?.Trim() ?? "";
        if (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
            releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase) ||
            releaseType.Contains("ServicePack", StringComparison.OrdinalIgnoreCase))
            return null;
        // 明确声明"不显示在程序和功能里"的也跳过
        if (k.GetValue("NoDisplay") is int nd && nd == 1) return null;

        var version = (k.GetValue("DisplayVersion") as string)?.Trim() ?? "";
        var publisher = (k.GetValue("Publisher") as string)?.Trim() ?? "";
        var location = (k.GetValue("InstallLocation") as string)?.Trim() ?? "";

        // 卸载命令。QuietUninstallString 是厂商额外提供的无交互版本，能拿到就优先用它。
        // ⚠️ 有些条目两个都没有（只做登记的、或者卸载信息写在别处）—— 「应用卸载」页上要如实说
        //    "这条得去系统设置里卸"，⛔ 别让按钮点了没反应。
        var uninstall = (k.GetValue("UninstallString") as string)?.Trim() ?? "";
        var quiet = (k.GetValue("QuietUninstallString") as string)?.Trim() ?? "";

        return new InstalledApp(name, version, publisher, location, uninstall, quiet,
                                Category: isRuntime ? AppCategory.Runtime : AppCategory.Desktop,
                                IconPath: ResolveIconPath(k, location, uninstall));
    }

    // ── 「运行库」的识别 ────────────────────────────────────────────────────────

    /// <summary>
    /// 名字里带这些字样的一律当运行库 —— 它们是**给别的软件用的**，不是用户主动装的"应用"。
    ///
    /// ⚠️ 只按名字判，不看发布者：VC++ Redist 的 Publisher 是 Microsoft，
    ///    但 Microsoft 自己的 Office / Edge 也是 —— 按发布者判会把半个微软全家桶卷进来。
    /// ⛔ 别往这里加"runtime"这种过宽的词单独裸用（会命中 "Edge Runtime" 之类），
    ///    写成 ".net runtime" / "windows app runtime" 这种带限定的形式。
    /// </summary>
    private static readonly string[] RuntimeHints =
    {
        "redistributable", "vcredist", "visual c++", "运行库", "运行环境",
        ".net framework", ".net runtime", ".net desktop runtime", ".net host", "asp.net core",
        "windows app runtime", "windows desktop runtime",
        "directx", "webview2 runtime", "microsoft edge runtime",
        "java", "jre", "jdk", "openal", "physx", "vulkan",
        "msxml", "sql server native client", "odbc driver", "microsoft visual c++",
    };

    /// <summary>这条记录像不像「运行库 / 运行时」。</summary>
    internal static bool LooksLikeRuntime(string name)
    {
        if (name.Length == 0) return false;
        var n = name.ToLowerInvariant();
        foreach (var hint in RuntimeHints)
            if (n.Contains(hint, StringComparison.Ordinal)) return true;
        return false;
    }

    // ── 图标路径的解析 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 猜这条记录用哪个文件取图标（**只算路径，不掏图标** —— 那活儿在界面线程上按需做）。
    ///
    /// 四步走，命中就返回：
    ///   ① <c>DisplayIcon</c>：就是 Windows「程序和功能」自己取图标用的那个值，最准；
    ///   ② <c>InstallLocation</c> 里的主程序 exe（顶层，顶层没有就下探一层 bin\ 之类的子目录）；
    ///   ③ 卸载程序**所在目录**里的主程序 exe：不少国产软件不写 InstallLocation，但卸载器就摆在主程序旁边；
    ///   ④ 卸载程序自己：最后的兜底，图标不一定好看（常常是个通用卸载图标），但比一片空白强。
    /// 四步都不成 → 返回空串，界面上显示分类占位图标。
    /// </summary>
    private static string ResolveIconPath(RegistryKey? k, string location, string uninstall)
    {
        var fromDisplay = CleanIconValue((k?.GetValue("DisplayIcon") as string)?.Trim() ?? "");
        if (fromDisplay.Length > 0) return fromDisplay;

        var fromLocation = FindMainExecutable(location);
        if (fromLocation.Length > 0) return fromLocation;

        try
        {
            var (exe, _) = AppUninstaller.SplitCommand(uninstall);
            if (exe.Length > 0)
            {
                // ⚠️ 先看卸载器**旁边**（同目录）有没有主程序 —— 卸载器自己的图标往往是个
                //    通用"垃圾桶"，几百行全长得一样反而更糊。找不到才退回用它自己。
                var fromUninstallerDir = FindMainExecutable(Path.GetDirectoryName(exe) ?? "");
                if (fromUninstallerDir.Length > 0) return fromUninstallerDir;
                if (File.Exists(exe)) return exe;
            }
        }
        catch
        {
            // 卸载命令写得怪就算了
        }

        return "";
    }

    /// <summary>
    /// 把 <c>DisplayIcon</c> 的值洗成一个「确实存在、且能掏图标」的文件路径。
    /// 这个值在注册表里什么写法都有：<c>C:\a\app.exe,0</c>、<c>"C:\a\icon.ico"</c>、
    /// 裸路径、甚至 <c>-1</c>（明确表示"没图标"）。
    /// </summary>
    private static string CleanIconValue(string raw)
    {
        if (raw.Length == 0) return "";
        var s = raw.Trim().Trim('"');

        // 去掉末尾的 ",索引"（资源 ID），但别把路径里合法的逗号也带走
        var comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out _)) s = s[..comma].Trim().Trim('"');

        if (s.Length == 0 || !File.Exists(s)) return "";
        return Path.GetExtension(s).ToLowerInvariant() switch
        {
            ".exe" or ".ico" or ".dll" or ".png" => s,
            _ => "",
        };
    }

    /// <summary>
    /// 在安装目录里挑一个「像主程序」的 exe。⛔ 卸载器 / 安装器 / 更新器 / 助手都不是主程序 ——
    /// 挑错了界面上就会出现"所有软件长得一样"的卸载器图标，比没图标还难看。
    ///
    /// ⚠️ 2026-10-13 补了下探一层：相当多软件的主程序不在安装目录顶层，而在
    ///    <c>bin\</c> / <c>app\</c> 这类子目录里（火绒就是 <c>…\Huorong\Sysdiag\bin\</c>）。
    ///    只扫顶层的话，这些行全成了占位图标。⛔ 只下探**一层**，再深就该撞上运行库、插件目录了。
    /// </summary>
    private static string FindMainExecutable(string location)
    {
        if (location.Length == 0) return "";

        var hit = PickExecutable(location);
        if (hit.Length > 0) return hit;

        try
        {
            if (!Directory.Exists(location)) return "";

            foreach (var sub in Directory.EnumerateDirectories(location))
            {
                var stem = Path.GetFileName(sub).ToLowerInvariant();
                // 明显不是装主程序的地方，跳过
                if (stem.StartsWith("unins", StringComparison.Ordinal) || stem.Contains("uninst") ||
                    stem.Contains("cache") || stem.Contains("log") || stem.Contains("temp") ||
                    stem.Contains("update") || stem.Contains("plugin") || stem.Contains("resource"))
                    continue;

                hit = PickExecutable(sub);
                if (hit.Length > 0) return hit;
            }
        }
        catch
        {
            // 目录读不动（权限/已删）→ 当成没找到
        }

        return "";
    }

    /// <summary>在一个目录的**顶层**挑一个像主程序的 exe；挑不到返回空串。</summary>
    private static string PickExecutable(string dir)
    {
        if (dir.Length == 0) return "";

        try
        {
            if (!Directory.Exists(dir)) return "";

            foreach (var f in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                var stem = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                if (stem.StartsWith("unins", StringComparison.Ordinal) ||
                    stem.Contains("uninst") || stem.Contains("卸载") ||
                    stem.Contains("setup") || stem.Contains("install") ||
                    stem.Contains("update") || stem.Contains("upgrade") ||
                    stem.Contains("helper") || stem.Contains("service") ||
                    stem.Contains("crash") || stem.Contains("report") ||
                    stem.StartsWith("vc_redist", StringComparison.Ordinal) ||
                    stem.StartsWith("vcredist", StringComparison.Ordinal))
                    continue;

                return f;
            }
        }
        catch
        {
            // 目录读不动（权限/已删）→ 当成没找到
        }

        return "";
    }

    /// <summary>
    /// 名称归一化，用来做模糊比对：去掉空格、标点、位数后缀和常见版本尾巴。
    /// 例：「Google Chrome (x64)」→「googlechrome」；「360 系统急救箱 5.1」→「360系统急救箱」。
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        // 先剥掉括号里的补充说明。清单里的名字经常带注解
        //（「ToDesk 远程控制 5.x（以安装时官网版本为准）」「UU 远程（网易）」），
        // 那些字不是产品名的一部分，留着会把重合度拉低到认不出来。
        var cleaned = StripParenthetical(raw);

        var sb = new StringBuilder(cleaned.Length);
        foreach (var ch in cleaned)
        {
            // 只留中日韩文字、字母、数字
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }

        var s = sb.ToString();

        // 去掉常见的位数/架构/渠道/版本类型后缀（归一化后没有空格，所以直接去掉子串）。
        // ⚠️ 版本类型后缀很关键：清单里叫「微信」，注册表里写的是「微信（测试版）」——
        //    不去掉「测试版」的话两者差 3 个字，重合度只有 0.4，会被判成"没装"。
        foreach (var noise in new[]
                 {
                     // 位数 / 架构
                     "64bit", "32bit", "64位", "32位", "x64", "x86", "amd64", "arm64",
                     // 语言 / 平台
                     "forwindows", "windows版", "中文版",
                     // 版本类型（厂商自己加的，不是产品区分点）
                     "测试版", "正式版", "极速版", "免费版", "精简版", "便携版",
                     "绿色版", "安装版", "企业版", "专业版", "个人版", "完整版",
                     "增强版", "经典版", "网络版", "抢先版", "体验版", "最新版",
                 })
        {
            s = s.Replace(noise, "");
        }

        // 去掉结尾的版本串（如 chrome-setup → 我们更希望「chrome」能命中「chrome」）
        var cut = s.Length;
        while (cut > 0)
        {
            var c = s[cut - 1];
            if (char.IsDigit(c) || c == '.' || c == 'v') cut--;
            else break;
        }
        // 只有"版本尾巴"足够短（不超过整串 1/3）才砍，避免把「360安全卫士」这种纯数字品牌砍没了
        if (cut > 0 && cut < s.Length && (s.Length - cut) <= Math.Max(4, s.Length / 3))
            s = s[..cut];

        return s;
    }

    /// <summary>去掉括号（中英文都算）及其中的内容，按层嵌套匹配。</summary>
    private static string StripParenthetical(string s)
    {
        var sb = new StringBuilder(s.Length);
        var depth = 0;

        foreach (var ch in s)
        {
            if (ch is '（' or '(')
            {
                depth++;
                continue;
            }

            if (ch is '）' or ')')
            {
                if (depth > 0) depth--;
                continue;
            }

            if (depth == 0) sb.Append(ch);
        }

        return sb.ToString();
    }
}
