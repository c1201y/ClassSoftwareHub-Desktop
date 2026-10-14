using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>数据根：漫游（Roaming）还是本地（Local）。</summary>
public enum AppDataZone { Local, Roaming }

/// <summary>可清理项的分类（界面下拉就是按它分的）。</summary>
public enum AppDataKind { Temp, Browser, Store, Dev, Graphics }

/// <summary>一条白名单规则：相对路径模式（<c>*</c> 恰好匹配一段目录名）+ 归属 + 说明。</summary>
public sealed record AppDataRule(string Pattern, AppDataZone Zone, AppDataKind Kind, string Note);

/// <summary>命中白名单的一个可清理目录（体积在后台另行统计）。</summary>
public sealed record AppDataEntry(
    string RelPath, string FullPath, AppDataZone Zone, AppDataKind Kind, string Note);

/// <summary>体检视图里的一个一级目录（**只读**，永远不可清理）。</summary>
public sealed record AppDataFolder(string Name, string FullPath, AppDataZone Zone);

/// <summary>一次清理的结果。</summary>
public sealed record CleanOutcome(bool Ok, int Cleaned, int Skipped, string Message);

/// <summary>
/// AppData 扫描 + 白名单清理（2026-10-13 二版功能，Nick：「实验性功能就是 AppData 文件夹管理」）。
///
/// ⛔⛔ 三层边界（这是本文件存在的全部意义，改动前先读懂）：
///
///  **第 1 层 · 只读体检** —— <see cref="TopFolders"/> 扫 <c>%LOCALAPPDATA%</c> 与
///     <c>%APPDATA%</c> 的一级目录，只出体积排行，**不提供任何删除入口**。
///     实测本机：Local 70 个 / 11.51 GB，Roaming 44 个 / 7.47 GB。
///
///  **第 2 层 · 白名单清理** —— 只有 <see cref="Rules"/> 里列出的**缓存目录**可清。
///     ⚠️ Nick 2026-10-14 定案：**不进回收站，直接永久删除**（见 <see cref="Clean"/>）。
///     之所以敢这么删，是因为这一层清的全是**缓存**（临时文件、浏览器 Cache、着色器缓存、包管理器缓存），
///     删掉程序会自己重建；而"删了会丢东西"的那些（<c>Local State</c>、聊天记录、存档、账号数据）
///     **压根不在白名单里**。回收站那版实现（Services/RecycleBin.cs）已按此定案移除。
///
///  **第 3 层 · 禁碰区** —— 一级目录**整目录删除这条路根本不开放**（见 <see cref="IsCleanable"/>
///     里的"段数必须 ≥ 2"）。原因有实据：<c>%LOCALAPPDATA%\Programs</c>（1.3 GB）是**程序安装本体**
///     （VS Code、Chrome 都装在里面），<c>%APPDATA%\Tencent</c>（2.85 GB，却只有 2956 个文件 —— 典型大文件）
///     大概率是**聊天记录**。按一级目录下手＝迟早把人家机器删废。
///     同样禁碰：<c>*\Settings</c>、<c>*.db</c>、<c>Local State</c> 这类配置/账号数据。
///
/// ⛔ 一条极易踩的坑，已经在代码里挡掉了：
///  - <c>%LOCALAPPDATA%\Application Data</c> 这类**联接点（junction）指回自己**（老 Windows 升级上来的
///    用户配置里常见）。递归统计时不跳过 reparse point 就是**无限递归**。
///    所以 <see cref="WalkOptions"/> 必须带 <c>AttributesToSkip = FileAttributes.ReparsePoint</c>。
/// </summary>
public static class AppDataScan
{
    /// <summary>递归遍历选项：跳过读不到的、**并且跳过 reparse point**（否则联接点会绕成死循环）。</summary>
    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        MatchType = MatchType.Simple,
    };

    /// <summary><c>%LOCALAPPDATA%</c>（本机实测 11.51 GB 的那半边）。</summary>
    public static string LocalRoot =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary><c>%APPDATA%</c>（漫游，本机实测 7.47 GB 的那半边）。</summary>
    public static string RoamingRoot =>
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>
    /// ⛔⛔ 白名单。**只往长里加之前先想清楚**：这里每一条都等于"我担保它删掉不会丢用户数据"。
    /// 判据是「删了之后应用自己会重建，最坏情况只是下次访问重新下载 / 重新生成」。
    /// 拿不准的一律不加 —— 界面上少一项没人会痛，删错了没法交代。
    ///
    /// <c>*</c> 恰好匹配**一段**目录名（用来跨 Chrome/Edge 的多个 profile）。
    /// </summary>
    private static readonly AppDataRule[] Rules =
    {
        // ── 系统临时 ──
        new(@"Temp", AppDataZone.Local, AppDataKind.Temp,
            "系统临时文件。正在被程序占用的会跳过，属于正常现象。"),
        new(@"Microsoft\Windows\INetCache", AppDataZone.Local, AppDataKind.Temp,
            "旧版 WinINet 网页缓存。删了下次访问重新下载。"),

        // ── 浏览器缓存（逐 profile 匹配，不动书签/密码/历史）──
        new(@"Google\Chrome\User Data\*\Cache", AppDataZone.Local, AppDataKind.Browser,
            "Chrome 网页缓存。不含书签、密码、历史记录。"),
        new(@"Google\Chrome\User Data\*\Code Cache", AppDataZone.Local, AppDataKind.Browser,
            "Chrome 脚本字节码缓存。"),
        new(@"Google\Chrome\User Data\*\GPUCache", AppDataZone.Local, AppDataKind.Browser,
            "Chrome 图形缓存。"),
        new(@"Google\Chrome\User Data\*\Service Worker\CacheStorage", AppDataZone.Local, AppDataKind.Browser,
            "Chrome 离线网页缓存。"),
        new(@"Microsoft\Edge\User Data\*\Cache", AppDataZone.Local, AppDataKind.Browser,
            "Edge 网页缓存。不含收藏夹、密码、历史记录。"),
        new(@"Microsoft\Edge\User Data\*\Code Cache", AppDataZone.Local, AppDataKind.Browser,
            "Edge 脚本字节码缓存。"),
        new(@"Microsoft\Edge\User Data\*\GPUCache", AppDataZone.Local, AppDataKind.Browser,
            "Edge 图形缓存。"),
        new(@"Microsoft\Edge\User Data\*\Service Worker\CacheStorage", AppDataZone.Local, AppDataKind.Browser,
            "Edge 离线网页缓存。"),
        new(@"BraveSoftware\Brave-Browser\User Data\*\Cache", AppDataZone.Local, AppDataKind.Browser,
            "Brave 网页缓存。"),

        // ── 商店应用（每个包的 LocalCache，界面文字叫「应用商店」类）──
        new(@"Packages\*\LocalCache", AppDataZone.Local, AppDataKind.Store,
            "商店应用的本地缓存。目录本身保留，只清里面的内容。"),
        new(@"Packages\*\TempState", AppDataZone.Local, AppDataKind.Store,
            "商店应用的临时状态。"),
        new(@"Packages\*\AC\Temp", AppDataZone.Local, AppDataKind.Store,
            "商店应用的账户组件临时文件。"),

        // ── 开发工具缓存 ──
        new(@"npm-cache", AppDataZone.Local, AppDataKind.Dev,
            "npm 下载缓存。删了不影响已装好的包，只影响下次安装速度。"),
        new(@"NuGet\v3-cache", AppDataZone.Local, AppDataKind.Dev,
            "NuGet 包缓存（v3）。"),
        new(@"pip\Cache", AppDataZone.Local, AppDataKind.Dev,
            "pip 下载缓存。"),
        new(@"Yarn\Cache", AppDataZone.Local, AppDataKind.Dev,
            "Yarn 下载缓存。"),
        new(@"Code\Cache", AppDataZone.Roaming, AppDataKind.Dev,
            "VS Code 缓存。不含配置与插件本体。"),
        new(@"Code\CachedData", AppDataZone.Roaming, AppDataKind.Dev,
            "VS Code 的 JS 字节码缓存。"),
        new(@"Code\CachedExtensionVSIXs", AppDataZone.Roaming, AppDataKind.Dev,
            "VS Code 已装插件的安装包残留。"),
        new(@"Code\Code Cache", AppDataZone.Roaming, AppDataKind.Dev,
            "VS Code 脚本字节码缓存。"),
        new(@"Code\GPUCache", AppDataZone.Roaming, AppDataKind.Dev,
            "VS Code 图形缓存。"),

        // ── 图形 / 着色器缓存 ──
        new(@"D3DSCache", AppDataZone.Local, AppDataKind.Graphics,
            "Direct3D 着色器缓存。删了游戏会重新编译一次。"),
        new(@"NVIDIA\DXCache", AppDataZone.Local, AppDataKind.Graphics,
            "NVIDIA 着色器缓存。"),
        new(@"NVIDIA\GLCache", AppDataZone.Local, AppDataKind.Graphics,
            "NVIDIA OpenGL 着色器缓存。"),
        new(@"AMD\DxCache", AppDataZone.Local, AppDataKind.Graphics,
            "AMD 着色器缓存。"),
        new(@"Intel\ShaderCache", AppDataZone.Local, AppDataKind.Graphics,
            "Intel 着色器缓存。"),
    };

    /// <summary>接口用到的两套「根 + 归属」，省得每处都写两遍。</summary>
    private static IEnumerable<(string Root, AppDataZone Zone)> RootPairs()
    {
        var local = LocalRoot;
        if (local.Length > 0) yield return (local, AppDataZone.Local);

        var roaming = RoamingRoot;
        if (roaming.Length > 0) yield return (roaming, AppDataZone.Roaming);
    }

    private static IEnumerable<AppDataRule> RulesFor(AppDataZone zone) =>
        Rules.Where(r => r.Zone == zone);

    // ── 第 1 层：只读体检 ──────────────────────────────────────────────

    /// <summary>
    /// 列出两个根下的**一级目录**（体检视图用）。⛔ 只读，返回的东西永远不进清理候选。
    /// 联接点会被跳过：它们指回别处（甚至指回根自己），既统计不准又容易绕成死循环。
    /// </summary>
    public static IReadOnlyList<AppDataFolder> TopFolders()
    {
        var list = new List<AppDataFolder>();

        foreach (var (root, zone) in RootPairs())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in SafeChildren(root, dirs: true))
            {
                var name = Path.GetFileName(dir);
                if (name.Length == 0) continue;
                list.Add(new AppDataFolder(name, dir, zone));
            }
        }

        return list;
    }

    // ── 第 2 层：白名单清理 ────────────────────────────────────────────

    /// <summary>走一遍白名单，列出所有**实际存在**的可清理目录（体积另行统计）。</summary>
    public static IReadOnlyList<AppDataEntry> FindCleanable()
    {
        var found = new List<AppDataEntry>();

        foreach (var (root, zone) in RootPairs())
        {
            if (!Directory.Exists(root)) continue;

            foreach (var rule in RulesFor(zone))
                Walk(root, rule.Pattern.Split('\\'), 0, rule, zone, found);
        }

        // 同一条路径可能被多条规则命中（比如将来加了通配更宽的规则）—— 去重，免得列表里出现两条。
        var dedup = found
            .GroupBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // ⛔⛔ **列出来的必须真能清**。这里再问一遍 <see cref="Clean"/> 用的那道闸（IsCleanable）。
        //    它俩原本各判各的，于是白名单里凡是"一级目录"的规则（Temp / npm-cache / D3DSCache）
        //    都变成"界面上有、一点就弹『这个路径不在白名单内』" —— 2026-10-15 实拍。
        //    多问一遍不影响判断，但以后再出现同类不一致，界面就不会撒谎了。
        var cleanable = dedup.Where(e => IsCleanable(e.FullPath)).ToList();
        if (cleanable.Count != dedup.Count)
        {
            AppLog.Warning("appdata",
                $"白名单里有 {dedup.Count - cleanable.Count} 条规则列得出、清不掉（判据不一致），已在列表里隐去："
                + string.Join("、", dedup.Except(cleanable).Select(e => e.RelPath)));
        }

        return cleanable
            .OrderBy(e => e.RelPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>按模式逐段下沉；<c>*</c> 那一段枚举下属目录。</summary>
    private static void Walk(string dir, string[] segs, int idx,
        AppDataRule rule, AppDataZone zone, List<AppDataEntry> into)
    {
        if (idx >= segs.Length)
        {
            if (!Directory.Exists(dir)) return;
            if (IsReparsePoint(dir)) return;

            var rel = RelativeToRoot(dir, zone);
            if (rel.Length == 0) return;

            into.Add(new AppDataEntry(rel, dir, zone, rule.Kind, rule.Note));
            return;
        }

        var seg = segs[idx];
        if (seg == "*")
        {
            // ⚠️ 白名单里大半条目是「装了才存在」的（Brave、酷狗这种没装就是没有）。
            //    先判存在再去枚举，否则 DirectoryNotFoundException 会被记成"枚举目录失败"，看着像出错。
            if (!Directory.Exists(dir)) return;

            foreach (var sub in SafeChildren(dir, dirs: true))
                Walk(sub, segs, idx + 1, rule, zone, into);
            return;
        }

        Walk(Path.Combine(dir, seg), segs, idx + 1, rule, zone, into);
    }

    // ── 体积统计 ───────────────────────────────────────────────────────

    /// <summary>
    /// 递归统计一个目录的占用。**慢**（本机全量统计 ~30 秒），调用方必须放后台并允许取消。
    /// 读不到的文件跳过不报错 —— 统计值宁可略小，也不要因为一个文件读不到就整项变成"未知"。
    /// </summary>
    public static (long Bytes, int Files) Measure(string path, CancellationToken ct)
    {
        long bytes = 0;
        var files = 0;

        try
        {
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", WalkOptions))
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    bytes += f.Length;
                    files++;
                }
                catch (Exception ex)
                {
                    // 枚举过程中文件被删/被独占属正常，调试级记一条就够（高频读数走「调试」，见日志契约）
                    AppLog.Debug("appdata", $"统计单个文件失败（{f.FullName}）：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("appdata", $"统计目录失败（{path}）：{ex.Message}");
        }

        return (bytes, files);
    }

    // ── 第 3 层：删除前的最后一道闸 ────────────────────────────────────

    /// <summary>
    /// ⛔⛔ **真正决定"能不能删"的就是这个方法**，界面上的可点状态不算数 —— 执行前必须再问它一次。
    /// 与 <see cref="CacheHome"/> 的 <c>IsInsideRoot</c> 是同一个角色：删除动作前的最后一道校验。
    ///
    /// 四道判据，缺一不可：
    ///  ① 路径确实在 <c>%LOCALAPPDATA%</c> 或 <c>%APPDATA%</c> 之下（不是别处凑巧同名的目录）；
    ///  ② **相对段数 ≥ 2** —— 一级目录永不整目录清理；
    ///  ③ 不是 reparse point（不能顺着联接点删到别的地方去）；
    ///  ④ 命中白名单模式（逐段比对）。
    ///
    /// ⛔⛔ ② 有一条**白名单显式点名**的例外，见下方注释 —— 2026-10-15 就是因为这条闸把
    ///     <c>Temp</c> 拦死，界面上列得出来、一点就弹「不在白名单内」。
    /// </summary>
    public static bool IsCleanable(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return false;

        string full;
        try { full = Path.GetFullPath(fullPath); }
        catch { return false; }

        foreach (var (root, zone) in RootPairs())
        {
            string rootFull;
            try { rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar; }
            catch { continue; }

            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;

            var rel = full[rootFull.Length..].TrimEnd(Path.DirectorySeparatorChar);
            if (rel.Length == 0) return false;                              // ② 根目录本身

            var segs = rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (segs.Any(s => s is "." or "..")) return false;              // ③ 轨迹里不许有跳转
            if (IsReparsePoint(full)) return false;                         // ③ 联接点

            var rule = RulesFor(zone).FirstOrDefault(r => Matches(r.Pattern, segs));   // ④ 白名单
            if (rule is null) return false;

            // ② 一级目录（<c>%LOCALAPPDATA%\X</c> 这种）。
            //    ⛔⛔ 例外只给**白名单里点名写死、且不含通配符**的一级目录（`Temp`、`npm-cache`、
            //        `D3DSCache`）。它们本身就是"缓存根"，删的是**内容**不是目录本身，
            //        而且是我们逐条担保过的 —— 拿 `Programs`/`Tencent` 挡人的活由 ④ 号闸干，
            //        不归这条管。带 `*` 的例外一律不给：那等于"清空任意一级目录"。
            if (segs.Length < 2 && rule.Pattern.Contains('*')) return false;

            return true;
        }

        return false;
    }

    /// <summary>逐段比对，<c>*</c> 匹配任意一段（但段数必须一致 —— 不允许跨层乱匹配）。</summary>
    private static bool Matches(string pattern, string[] segs)
    {
        var p = pattern.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != segs.Length) return false;

        for (var i = 0; i < p.Length; i++)
        {
            if (p[i] == "*") continue;
            if (!string.Equals(p[i], segs[i], StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    // ── 执行清理 ───────────────────────────────────────────────────────

    /// <summary>
    /// 清掉一个白名单目录的**内容**（永久删除，目录本身保留）。
    ///
    /// ⚠️ **不进回收站**（Nick 2026-10-14 定案）。白名单里全是缓存，删了程序自己重建；
    ///    会丢东西的一律没进白名单，所以这里敢直接删。
    ///
    /// 被程序占用的文件删不掉 —— **跳过并计数**，把数量如实回报给界面，而不是假报成功。
    /// </summary>
    public static CleanOutcome Clean(AppDataEntry entry, CancellationToken ct)
    {
        // ① 最后一道闸。**别把这段当冗余**：界面状态可能过期（目录被换成联接点、规则后来改了）。
        if (!IsCleanable(entry.FullPath))
        {
            AppLog.Warning("appdata", "拒绝清理不在白名单内的路径：" + entry.FullPath);
            return new CleanOutcome(false, 0, 0, "这个路径不在白名单内，已拒绝执行。");
        }

        if (!Directory.Exists(entry.FullPath))
            return new CleanOutcome(false, 0, 0, "目录已经不存在了。");

        // ② 深度优先删掉里面的东西（根目录自己不动）
        var deleted = 0;
        var skipped = 0;
        PurgeInto(entry.FullPath, ref deleted, ref skipped, ct);

        if (deleted == 0 && skipped == 0)
            return new CleanOutcome(false, 0, 0, "这个目录本来就是空的。");

        // ③ 目录本身保留：有些程序找不到自己的缓存目录会报错，原地补一个空的
        try
        {
            Directory.CreateDirectory(entry.FullPath);
        }
        catch (Exception ex)
        {
            AppLog.Info("appdata", $"清理后重建目录失败（{entry.RelPath}）：{ex.Message}");
        }

        AppLog.Info("appdata",
            $"已清理（永久删除 · {entry.RelPath}）：删掉 {deleted} 项，跳过 {skipped} 项");

        return new CleanOutcome(
            skipped == 0, deleted, skipped,
            skipped == 0 ? "" : $"有 {skipped} 项正被程序占用，已跳过。");
    }

    /// <summary>
    /// 深度优先永久删除 <paramref name="dir"/> 里的**所有内容**（目录自己保留）。
    ///
    /// 顺序很要紧：先递归清空子目录、再删子目录，最后才删文件 —— 反过来会遇到
    /// "目录非空"而删不掉。删不掉的（占用、无权限）**跳过并计数**，绝不抛出去。
    /// ⛔ 联接点一律不碰：删它等于动了目录外面的东西。
    /// </summary>
    private static void PurgeInto(string dir, ref int deleted, ref int skipped, CancellationToken ct)
    {
        foreach (var sub in SafeChildren(dir, dirs: true))
        {
            if (ct.IsCancellationRequested) return;

            PurgeInto(sub, ref deleted, ref skipped, ct);

            try
            {
                Directory.Delete(sub, recursive: false);
                deleted++;
            }
            catch (Exception ex)
            {
                skipped++;
                AppLog.Debug("appdata", $"跳过目录（{sub}）：{ex.Message}");
            }
        }

        foreach (var file in SafeFiles(dir))
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex)
            {
                skipped++;
                AppLog.Debug("appdata", $"跳过文件（{file}）：{ex.Message}");
            }
        }
    }

    // ── 打开所在目录 ────────────────────────────────────────────────────

    /// <summary>
    /// 在资源管理器里打开一个目录（**只读动作**，不删任何东西）。
    ///
    /// ⛔ 只放行两个根下面的路径 —— 界面上传进来的路径同样不该无条件相信，
    /// 跟 <see cref="Clean"/> 一个道理：最后一道闸要在服务层。
    /// </summary>
    public static bool OpenInExplorer(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;

            var full = Path.GetFullPath(path);
            var inside = RootPairs().Any(p =>
                full.StartsWith(
                    Path.GetFullPath(p.Root) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));

            if (!inside)
            {
                AppLog.Warning("appdata", "拒绝打开不在 AppData 下的路径：" + full);
                return false;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{full}\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Fail("appdata", ex, "打开目录：" + path);
            return false;
        }
    }

    // ── 小工具 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 这一类缓存正被哪些程序用着（确认弹窗里提前提醒"先关掉它再清"）。
    /// 只比进程名，不做路径匹配 —— 目的是提醒，不是精确判断，所以宁可少报也不要误报。
    /// </summary>
    private static readonly Dictionary<AppDataKind, string[]> KindProcesses = new()
    {
        [AppDataKind.Browser] = new[] { "chrome", "msedge", "brave", "firefox" },
        [AppDataKind.Dev] = new[] { "Code", "devenv" },
    };

    /// <summary>返回当前正在运行、且与这类缓存相关的进程名（取不到/没配就不报）。</summary>
    public static IReadOnlyList<string> RunningApps(AppDataKind kind)
    {
        if (!KindProcesses.TryGetValue(kind, out var names)) return Array.Empty<string>();

        var running = new List<string>();
        foreach (var name in names)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                var alive = procs.Length > 0;
                foreach (var p in procs) p.Dispose();

                if (alive) running.Add(name);
            }
            catch (Exception ex)
            {
                AppLog.Debug("appdata", $"查询进程失败（{name}）：{ex.Message}");
            }
        }

        return running;
    }

    /// <summary>
    /// 列一层子项（**不递归**）。<paramref name="dirs"/> 决定只要目录还是目录+文件都要。
    /// ⛔ 一律跳过 reparse point —— 见类注释里那条死循环的坑。
    /// </summary>
    private static IEnumerable<string> SafeChildren(string dir, bool dirs)
    {
        IEnumerable<string> items;
        try
        {
            items = dirs ? Directory.EnumerateDirectories(dir) : Directory.EnumerateFileSystemEntries(dir);
        }
        catch (Exception ex)
        {
            AppLog.Debug("appdata", $"枚举目录失败（{dir}）：{ex.Message}");
            yield break;
        }

        foreach (var item in items)
        {
            bool skip;
            try { skip = IsReparsePoint(item); }
            catch { skip = true; }

            if (!skip) yield return item;
        }
    }

    /// <summary>列一层的**文件**（不递归，同样跳过 reparse point）。</summary>
    private static IEnumerable<string> SafeFiles(string dir)
    {
        IEnumerable<string> items;
        try
        {
            items = Directory.EnumerateFiles(dir);
        }
        catch (Exception ex)
        {
            AppLog.Debug("appdata", $"枚举文件失败（{dir}）：{ex.Message}");
            yield break;
        }

        foreach (var item in items)
        {
            bool skip;
            try { skip = IsReparsePoint(item); }
            catch { skip = true; }

            if (!skip) yield return item;
        }
    }

    /// <summary>是不是联接点 / 符号链接。</summary>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            // 读不到属性就当它是联接点：**宁可漏扫，不可误删**
            return true;
        }
    }

    /// <summary>把绝对路径折回相对某个根的相对路径；不属于任何根则返回空串。</summary>
    private static string RelativeToRoot(string fullPath, AppDataZone zone)
    {
        string root;
        try
        {
            root = Path.GetFullPath(zone == AppDataZone.Local ? LocalRoot : RoamingRoot)
                   + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(fullPath);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return "";
            return full[root.Length..].TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return "";
        }
    }
}
