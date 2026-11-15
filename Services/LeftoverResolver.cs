using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 一条残留的「归属证据强度」。
///
/// 为什么要有这个：卸载前后求差集，只能说明"这条是这次卸载过程中新出现的"，
/// **不等于**"它就是被卸载的那个软件留下的"（卸载器可能顺手写了别的东西，
/// 甚至别的程序恰好在同一时间启动了）。所以还要过一遍名字/路径比对，
/// 按证据强弱分三档 —— 强/中默认勾上，弱默认**不勾**（宁可少清，不可错清）。
/// </summary>
public enum LeftoverEvidence
{
    /// <summary>弱：只是"卸载前后新出现"，名字/位置都对不上。默认不勾，摆出来让人自己判断。</summary>
    Weak = 0,

    /// <summary>中：名字与软件名/发布者/安装目录名其中一项吻合。</summary>
    Medium = 1,

    /// <summary>强：名字就是软件名、或者位置就在这个软件的安装目录里、或者注册表值的内容指向这个软件。</summary>
    Strong = 2,
}

/// <summary>一条待确认的残留。</summary>
public sealed class LeftoverItem
{
    public LeftoverItem(FootprintNode node)
    {
        Kind = node.Kind;
        Probe = node.Probe;
        Path = node.Path;
        Name = node.Name;
        ValueName = node.ValueName;
    }

    public FootprintKind Kind { get; }
    public string Probe { get; }
    public string Path { get; }
    public string Name { get; }
    public string ValueName { get; }

    /// <summary>归属证据强度。</summary>
    public LeftoverEvidence Evidence { get; set; } = LeftoverEvidence.Weak;

    /// <summary>为什么判成这一档（摆给人看的一句话，如「名字与软件名一致」）。</summary>
    public string Reason { get; set; } = "";

    /// <summary>默认勾不勾。⚠️ 弱证据一律不勾。</summary>
    public bool Selected { get; set; }

    /// <summary>占用字节数（注册表项为 0）。测不动就是 -1。</summary>
    public long SizeBytes { get; set; } = -1;

    /// <summary>目录里的文件数（注册表项为 0）。</summary>
    public int FileCount { get; set; }

    /// <summary>清理之后这一条还在不在（复扫结果，写进汇报）。</summary>
    public bool StillThere { get; set; } = true;

    public string DisplayPath => Compress(Path, ValueName);

    /// <summary>
    /// 只把**父级**位置摆出来（<c>%LOCALAPPDATA%\Foo</c>），末段单独由界面显示成主标题。
    /// 注册表值就直接给它的键。
    /// </summary>
    public string DisplayParent
    {
        get
        {
            try
            {
                return Kind switch
                {
                    FootprintKind.Path => Compress(System.IO.Path.GetDirectoryName(Path) ?? "", ""),
                    FootprintKind.RegistryKey => Compress(ParentKeyPath(Path), ""),
                    _ => Compress(Path, ""),
                };
            }
            catch
            {
                return Path;
            }
        }
    }

    /// <summary>末段名字（界面主标题用）。</summary>
    public string DisplayLeaf
    {
        get
        {
            if (Kind == FootprintKind.RegistryValue) return ValueName;

            var i = Path.LastIndexOf('\\');
            var leaf = i >= 0 ? Path[(i + 1)..] : Path;
            return leaf.Length > 0 ? leaf : Path;
        }
    }

    private static string ParentKeyPath(string keyPath)
    {
        var i = keyPath.LastIndexOf('\\');
        return i > 0 ? keyPath[..i] : keyPath;
    }

    public string EvidenceText => Evidence switch
    {
        LeftoverEvidence.Strong => "确定",
        LeftoverEvidence.Medium => "很可能",
        _ => "不确定",
    };

    public string SizeText => Kind != FootprintKind.Path || SizeBytes < 0
        ? ""
        : SizeBytes == 0
            ? "空"
            : HumanSize(SizeBytes);

    private static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };

    /// <summary>种类 + 位置，摆成一行副标题（如「注册表项 · 开机自启(当前用户)」）。</summary>
    public string KindText => Kind switch
    {
        FootprintKind.Path => Directory.Exists(Path) ? "文件夹" : "文件",
        FootprintKind.RegistryKey => "注册表项",
        _ => "注册表值",
    };

    /// <summary>按类型排序用的权重：文件夹 → 文件 → 注册表值 → 注册表项。</summary>
    public int SortRank => Kind switch
    {
        FootprintKind.Path => Directory.Exists(Path) ? 0 : 1,
        FootprintKind.RegistryValue => 2,
        _ => 3,
    };

    /// <summary>
    /// 把长路径缩成能看的样子：换掉几个已知的环境变量前缀（<c>%LOCALAPPDATA%\Foo</c>）。
    /// ⚠️ 只换前缀，不改后面 —— 后半段才是"是哪个软件"的关键信息。
    /// </summary>
    private static string Compress(string path, string valueName)
    {
        var s = path;

        foreach (var (full, token) in ShortPrefixes)
        {
            if (s.StartsWith(full, StringComparison.OrdinalIgnoreCase))
            {
                s = token + s[full.Length..];
                break;
            }
        }

        return valueName.Length > 0 ? s + "  ▸  " + valueName : s;
    }

    private static readonly (string Full, string Token)[] ShortPrefixes = BuildShortPrefixes();

    private static (string, string)[] BuildShortPrefixes()
    {
        var list = new List<(string, string)>
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "%PROGRAMDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "%ProgramFiles%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "%ProgramFiles(x86)%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"),
        };

        // 长的排前面：%LOCALAPPDATA% 在 %USERPROFILE% 底下，先匹配短的就会把
        // `%USERPROFILE%\AppData\Local\Foo` 缩成 `%USERPROFILE%\AppData\...`，长长一串没意义
        return list.Where(x => x.Item1.Length > 0)
                   .OrderByDescending(x => x.Item1.Length)
                   .ToArray();
    }
}

/// <summary>
/// 从"卸载后的现场"里拎出待确认的残留（2026-10-14 加）。
///
/// ⛔⛔ <b>先说清一个反直觉的地方（这是设计上最容易做错的一步）</b>：
///    残留的正确定义是「**卸载前就在、卸载后还在、本该被删掉**」。这种东西在前后两份快照里
///    <b>都存在</b> —— 所以"求差集"是抓不到它的！一开始我把它写成"只取差集"，
///    那样能抓到的东西全是卸载器**新写**的（日志、<c>unins000.dat</c>、重启后继续删的 RunOnce 项），
///    而真正的重头戏「软件自己那个 AppData 文件夹没被删掉」会一条都抓不到。
///
///    ⇒ 所以判据是**两路并行**，各管一段：
///      ① **名字/位置吻合**（主路）：扫完卸载后的现场，凡是名字跟这个软件对得上、
///         或者位置就在它安装目录里的，就是候选 —— 不管它在卸载前存不存在。
///         这也正是 Geek / HiBit 那类工具干的事（它们没有安装前的基线，只有这条路）。
///      ② **卸载过程中新冒出来的**（辅路）：差集里那些名字对不上的，归到"不确定"档。
///         它们确实跟这次卸载有关（以前没有），但没法确定归属，所以默认**不勾**。
///
///   ⛔ 名字对不上、而且卸载前就在的，一律**扔掉** —— 那是别的软件的东西，
///      （本机 <c>%LOCALAPPDATA%</c> 底下几百个目录，全摆出来只会淹掉真东西）。
///
/// 判完再过一遍**禁碰名单**：系统目录、系统注册表键、以及本程序自己的东西，
/// 不管证据多强都直接扔掉，⛔ 连摆都不摆出来（摆出来就会有人去勾）。
///
/// ⛔⛔ 这里最要紧的不是"能清多少"，而是"**绝不误伤**"：
///   · 判不准的一律降到弱证据 + 默认不勾；
///   · 禁碰名单是**硬编码的常量**，不读配置、不联网、不可绕过（<see cref="LeftoverCleaner"/> 里还会再判一次）；
///   · 名字比对上还要过长度与占比门槛（见 <see cref="Matches"/>），挡掉"短名字满世界命中"。
/// </summary>
public static class LeftoverResolver
{
    /// <summary>
    /// 从"卸载前后两份快照"里拎出待确认的残留清单。
    /// </summary>
    /// <param name="before">UAC 弹窗**之前**抓的那份（用来区分"新冒出来的"和"老早就在的"）。</param>
    /// <param name="after">卸载程序退出、稍等两拍之后复扫的那份（**候选就是从这一份里出的**）。</param>
    /// <param name="app">这次卸的是哪个软件（拿来比对名字/位置）。</param>
    public static List<LeftoverItem> Resolve(FootprintSnapshot before, FootprintSnapshot after, InstalledApp app)
    {
        var sw = Stopwatch.StartNew();

        var profile = new AppProfile(app);
        var items = new List<LeftoverItem>();

        var truncated = before.Truncated || after.Truncated;

        foreach (var node in after.Nodes.Values)
        {
            if (IsForbidden(node)) continue;

            var isNew = !before.Has(node);

            var item = new LeftoverItem(node);
            Grade(item, profile, isNew, truncated);

            // 名字对不上、而且卸载前就在那儿的 ⇒ 别人的东西，扔掉
            if (item.Evidence == LeftoverEvidence.Weak && !isNew) continue;

            items.Add(item);
        }

        Measure(items);

        var sorted = items
            .OrderByDescending(i => (int)i.Evidence)
            .ThenBy(i => i.SortRank)
            .ThenBy(i => i.DisplayPath, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        sw.Stop();
        AppLog.Info("leftover",
            $"残留判定（{app.Name}）：差集 {sorted.Count} 条" +
            $"（确定 {sorted.Count(i => i.Evidence == LeftoverEvidence.Strong)}、" +
            $"很可能 {sorted.Count(i => i.Evidence == LeftoverEvidence.Medium)}、" +
            $"不确定 {sorted.Count(i => i.Evidence == LeftoverEvidence.Weak)}），" +
            $"用时 {sw.ElapsedMilliseconds} ms");

        return sorted;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ① 归属比对
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 给一条残留打证据等级。
    /// ⚠️ 名字比对一律走 <see cref="InstalledApps.Normalize"/>（剥括号、去位数后缀、只留字母数字）
    ///    再比 —— 光看字面的话「ClassSoftwareHub Desktop」和目录名 <c>ClassSoftwareHub</c> 就对不上。
    ///    但⛔ 别拿 Normalize 的结果当**去重键**（它会把「(winget)」和「(winget-font)」合并，见那边的注释），
    ///    这里只是长度阈值以上的包含判断，不会误合并。
    /// </summary>
    private static void Grade(LeftoverItem item, AppProfile profile, bool isNew, bool truncated)
    {
        // ── 注册表值：看**内容**指向哪，比看名字准得多 ──
        if (item.Kind == FootprintKind.RegistryValue)
        {
            GradeRegistryValue(item, profile, isNew, truncated);
            return;
        }

        var leaf = LeafName(item);

        // ── 位置优先：就在这个软件的安装目录里/下，不用猜名字 ──
        if (profile.Location.Length > 0 && IsInsideOrEqual(item.Path, profile.Location))
        {
            Set(item, LeftoverEvidence.Strong, "位置位于该软件的安装目录之内");
            return;
        }

        var n = InstalledApps.Normalize(leaf);

        // 去掉扩展名再来一次：「LeftoverLab.exe」应当和软件名 `LeftoverLab` 算**一致**，
        // 而不是"包含"。少了这一步，路径登记 / 程序关联那一类残留永远只能拿到"很可能"
        // —— 那太松了，它们的名字其实完全吻合。（2026-10-15 实测踩过）
        var nStem = InstalledApps.Normalize(Path.GetFileNameWithoutExtension(leaf));

        // ── 强证据（默认勾选）：名字**完全一致** ──
        //  ⛔⛔ 只认"完全一致"，不认"包含"。曾经的写法是"一方包含另一方、
        //     被包含的那截占容器 60% 以上"，结果把一个 90 MB 的用户目录
        //     （`%USERPROFILE%\ClassSoftwareHub-Mobile`）判成了某款软件的残留并**默认勾上**
        //     —— 名字看着挺像，可那是人家自己的工程目录。2026-10-15 实拍。
        if (Exact(n, profile.NameKey, 4) || Exact(nStem, profile.NameKey, 4))
        {
            Set(item, LeftoverEvidence.Strong, "名称与软件名一致");
            return;
        }

        if (Exact(n, profile.FolderKey, 3) || Exact(nStem, profile.FolderKey, 3) ||
            Exact(n, profile.ExeKey, 3) || Exact(nStem, profile.ExeKey, 3))
        {
            Set(item, LeftoverEvidence.Strong, "名称与程序目录名一致");
            return;
        }

        // ── 关联键：名字看不出归属，但它的**默认值**会写明指向谁 ──
        //    文件关联（`.扩展名`）和 ProgID 这两类键，键名本身一点归属信息都没有：
        //    `.labtest` 这个词里根本没有 `LeftoverLab` 的影子，光比名字永远是"名字对不上"
        //    ⇒ 这类残留会**整类漏掉**。（2026-10-15 实测：测试软件卸载后 `.labtest` 一直留着。）
        //    但它们有个共同点 —— 默认值写着自己指向谁（`.labtest` ▸ 默认值 = `LeftoverLab.Document`）。
        //    ⛔ 只认"归一化后**完全等于**软件名"，所以 `.txt` ▸ `txtfile` 这种系统关联
        //       绝不会被误判成任何软件的残留。
        if (item.Kind == FootprintKind.RegistryKey && IsAssociationProbe(item.Probe))
        {
            var target = TryReadDefaultValue(item.Path);
            if (target.Length > 0)
            {
                if (profile.Location.Length > 0 &&
                    target.Contains(profile.Location, StringComparison.OrdinalIgnoreCase))
                {
                    Set(item, LeftoverEvidence.Strong, "关联目标为该软件的安装目录");
                    return;
                }

                foreach (var part in TargetNameParts(target))
                {
                    if (Exact(part, profile.NameKey, 4) || Exact(part, profile.ExeKey, 3))
                    {
                        Set(item, LeftoverEvidence.Strong, "关联目标为该软件");
                        return;
                    }
                }
            }
        }

        // ── 中证据（摆出来，但**不勾**）：只是"像"，要人自己看一眼 ──
        if (Contains(n, profile.NameKey, 0.4, 4) || Contains(nStem, profile.NameKey, 0.4, 4) ||
            Contains(n, profile.FolderKey, 0.5, 4) || Contains(nStem, profile.FolderKey, 0.5, 4))
        {
            Set(item, LeftoverEvidence.Medium, "名称与软件名相似");
            return;
        }

        foreach (var token in profile.Tokens)
        {
            if (Contains(n, token, 0.5, 5) || Contains(nStem, token, 0.5, 5))
            {
                Set(item, LeftoverEvidence.Medium, $"名称含「{token}」");
                return;
            }
        }

        // 走到这儿就是"名字对不上"。分两种：
        //   · 卸载**过程中新出现**的 → 确实跟这次卸载有关，摆出来但默认不勾；
        //   · 卸载前就有的 → 楼上已经扔掉了（见 Resolve），走不到这儿。
        Set(item, LeftoverEvidence.Weak,
            isNew ? "卸载过程中新增，名称不符" : "名称不符");

        // 快照被截断过 ⇒ 差集那一半不可靠，这时候新出现的东西**一律降级不勾**
        if (truncated) item.Selected = false;
    }

    /// <summary>
    /// 注册表值的归属。值名一般是「软件名」或「一堆路径」，值**内容**才是决定性的：
    ///   · <c>Run</c> 里那条 = 一条命令行，里面会出现软件自己的 exe 路径；
    ///   · <c>SharedDLLs</c> 里那条：**值名**就是 DLL 路径（这类键的特性）；
    ///   · 其它（如 <c>FileExts</c> 下的 ProgID）值名往往就是关联名。
    /// </summary>
    private static void GradeRegistryValue(LeftoverItem item, AppProfile profile, bool isNew, bool truncated)
    {
        // ① 「共享组件登记」：值名是完整文件路径 —— 能直接判位置
        if (item.Probe.StartsWith("共享组件", StringComparison.Ordinal))
        {
            if (profile.Location.Length > 0 && IsInsideOrEqual(item.ValueName, profile.Location))
            {
                Set(item, LeftoverEvidence.Strong, "登记的组件位于该软件的安装目录之内");
                return;
            }

            var dllName = Path.GetFileName(item.ValueName);
            var dllKey = InstalledApps.Normalize(dllName);
            var dllStem = InstalledApps.Normalize(Path.GetFileNameWithoutExtension(dllName));

            if (Exact(dllKey, profile.NameKey, 4) || Exact(dllStem, profile.NameKey, 4) ||
                Exact(dllKey, profile.ExeKey, 3) || Exact(dllStem, profile.ExeKey, 3))
            {
                Set(item, LeftoverEvidence.Strong, "登记的组件名与该程序一致");
                return;
            }
        }

        // ② 读值的内容，看里面有没有指向这个软件
        var data = TryReadValueData(item);
        if (data.Length > 0)
        {
            if (profile.Location.Length > 0 &&
                data.Contains(profile.Location, StringComparison.OrdinalIgnoreCase))
            {
                Set(item, LeftoverEvidence.Strong, "该值内容指向此软件的安装目录");
                return;
            }

            // 归一化之后**完全相等**才算 —— 这里同样是"宁严勿宽"：
            // 从一条命令行里抠出来的文件名，只有跟软件名一模一样才敢自动勾。
            var dataStem = InstalledApps.Normalize(
                Path.GetFileNameWithoutExtension(data.Trim().Trim('"').Trim()));
            if (Exact(dataStem, profile.NameKey, 4) || Exact(dataStem, profile.ExeKey, 3))
            {
                Set(item, LeftoverEvidence.Strong, "该值内容指向此软件");
                return;
            }
        }

        // ③ 退回名字比对
        var n = InstalledApps.Normalize(item.ValueName);
        var nStem = InstalledApps.Normalize(Path.GetFileNameWithoutExtension(item.ValueName));

        if (Exact(n, profile.NameKey, 4) || Exact(nStem, profile.NameKey, 4) ||
            Exact(n, profile.ExeKey, 3) || Exact(nStem, profile.ExeKey, 3))
        {
            Set(item, LeftoverEvidence.Strong, "名称与软件名一致");
            return;
        }

        if (Contains(n, profile.NameKey, 0.4, 4) || Contains(nStem, profile.NameKey, 0.4, 4))
        {
            Set(item, LeftoverEvidence.Medium, "值名称与软件名相似");
            return;
        }

        foreach (var token in profile.Tokens)
        {
            if (Contains(n, token, 0.5, 5) || Contains(nStem, token, 0.5, 5))
            {
                Set(item, LeftoverEvidence.Medium, $"名称含「{token}」");
                return;
            }
        }

        Set(item, LeftoverEvidence.Weak, isNew ? "卸载过程中新增，名称不符" : "名称不符");
        if (truncated) item.Selected = false;
    }

    private static string TryReadValueData(LeftoverItem item)
    {
        try
        {
            if (!RegistryPath.TryParse(item.Path, out var hive, out var subPath)) return "";
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = root.OpenSubKey(subPath, writable: false);
            return key?.GetValue(item.ValueName) as string ?? "";
        }
        catch (Exception ex)
        {
            AppLog.Info("leftover", $"读注册表值内容失败（{item.Path} ▸ {item.ValueName}）：{ex.GetType().Name}");
            return "";
        }
    }

    /// <summary>
    /// 读一个注册表**键**的默认值（<c>GetValue("")</c>）。
    /// ⚠️ 为什么非读不可：<c>Depth:1</c> 的关联类体检点只把**一级子键**收进快照，
    ///    不列值 —— 所以 `.labtest` 这条记录的默认值在任何一份快照里都不存在，
    ///    只能现场去读一次。
    /// </summary>
    private static string TryReadDefaultValue(string path)
    {
        try
        {
            if (!RegistryPath.TryParse(path, out var hive, out var subPath)) return "";
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = root.OpenSubKey(subPath, writable: false);
            return key?.GetValue("") as string ?? "";
        }
        catch (Exception ex)
        {
            AppLog.Info("leftover", $"读注册表默认值失败（{path}）：{ex.GetType().Name}");
            return "";
        }
    }

    /// <summary>探针是不是"关联类"体检点（键名是 ProgID / 扩展名，归属只能看默认值）。</summary>
    private static bool IsAssociationProbe(string probe)
        => probe.StartsWith("文件夹联", StringComparison.Ordinal)
        || probe.StartsWith("程序关联", StringComparison.Ordinal)
        || probe.StartsWith("路径登记", StringComparison.Ordinal);

    /// <summary>
    /// 从一个"关联目标"里抠出候选名字。
    ///   · ProgID 形态（<c>LeftoverLab.Document</c>）：整串 + 点前那一段（<c>LeftoverLab</c>）；
    ///   · 命令行形态（含路径/空格）：整串抠文件名之后的名字。
    /// ⚠️ 两种形态必须分开 —— 对命令行做"按点拆"会把它拆得面目全非。
    /// </summary>
    private static IEnumerable<string> TargetNameParts(string target)
    {
        var t = target.Trim().Trim('"').Trim();
        if (t.Length == 0) yield break;

        if (t.Contains('\\') || t.Contains('/') || t.Contains(' '))
        {
            yield return InstalledApps.Normalize(Path.GetFileNameWithoutExtension(t));
            yield break;
        }

        yield return InstalledApps.Normalize(t);

        var dot = t.IndexOf('.');
        if (dot > 0) yield return InstalledApps.Normalize(t[..dot]);
    }

    /// <summary>
    /// 名字**完全一致**才算吻合（归一化之后相等）。
    /// ⚠️ <paramref name="require"/> 是长度下限：三个字符以下的名字（「QQ」「vs」）判准了也说明不了什么。
    /// </summary>
    private static bool Exact(string candidate, string key, int require)
        => candidate.Length >= require && key.Length >= require &&
           candidate.Equals(key, StringComparison.Ordinal);

    /// <summary>
    /// 名字"**像**"（一方包含另一方，且被包含的那截占容器 <paramref name="ratio"/> 以上）。
    /// ⛔ 这一档只用于 <see cref="LeftoverEvidence.Medium"/>（摆出来给人看、**默认不勾**），
    ///    绝不可以拿去自动勾选 —— 名字像不等于就是它。2026-10-15 那次误删就是这么来的。
    /// </summary>
    private static bool Contains(string candidate, string key, double ratio, int require)
    {
        if (candidate.Length < require || key.Length < require) return false;
        if (candidate.Equals(key, StringComparison.Ordinal)) return true;

        if (candidate.Contains(key, StringComparison.Ordinal))
            return key.Length >= candidate.Length * ratio;

        if (key.Contains(candidate, StringComparison.Ordinal))
            return candidate.Length >= key.Length * ratio;

        return false;
    }

    private static string LeafName(LeftoverItem item)
    {
        if (item.Kind == FootprintKind.RegistryKey)
        {
            var i = item.Path.LastIndexOf('\\');
            return i >= 0 ? item.Path[(i + 1)..] : item.Path;
        }

        try
        {
            var leaf = Path.GetFileName(item.Path);
            return leaf.Length > 0 ? leaf : item.Path;
        }
        catch
        {
            return item.Name;
        }
    }

    /// <summary>
    /// 打档并且**决定默认勾不勾**。
    /// ⛔⛔ 只有"确定"（<see cref="LeftoverEvidence.Strong"/>）才默认勾上。
    ///    「很可能」和「不确定」都摆出来给人看，但**不勾** ——
    ///    它们是"名字像"和"新出现"这两种推测，勾上就等于替用户做了他没做的判断。
    ///    2026-10-15 那次误删（把一个 90 MB 的用户目录判成残留并自动勾选后清掉）就是这条规则改之前发生的。
    /// </summary>
    private static void Set(LeftoverItem item, LeftoverEvidence level, string reason)
    {
        item.Evidence = level;
        item.Reason = reason;
        item.Selected = level == LeftoverEvidence.Strong;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ② 禁碰名单
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 这些位置**永不**进列表。
    /// ⛔⛔ 这是硬编码常量，不是配置项 —— 不读文件、不联网、界面上也没有开关。
    ///    清理器（<see cref="LeftoverCleaner"/>）动手前还会再判一次，两道闸。
    /// </summary>
    private static bool IsForbidden(FootprintNode node)
    {
        if (node.Kind == FootprintKind.Path) return IsForbiddenPath(node.Path);
        if (node.Kind == FootprintKind.RegistryKey) return IsForbiddenRegistryKey(node.Probe, node.Path);
        return IsForbiddenRegistryValue(node);
    }

    /// <summary>
    /// ⛔⛔ <b>第二道闸</b>：给 <see cref="LeftoverCleaner"/> 动手前再判一次用的。
    /// 界面上勾了什么不算数 —— 真正要删之前必须再过一遍这份禁碰名单。
    /// </summary>
    public static bool IsForbiddenPathPublic(string path) => IsForbiddenPath(path);

    /// <summary>
    /// ⛔⛔ 第二道闸（注册表版）。
    /// ⚠️ <b>必须区分"删整条键"和"删某个值"</b>：<c>HKCU\...\CurrentVersion\Run</c> 这条**键**
    ///    在禁碰名单里（它是系统固定结构，谁都不能删），但删它底下的**某一个值**
    ///    恰恰是最典型的残留清理。一开始这里不分类别一律按键判，于是自启项那一条
    ///    被判成"受保护的注册表位置"直接跳过（2026-10-15 体检报告里那条"被拦 1"）。
    /// </summary>
    public static bool IsForbiddenRegistryPublic(string probe, string path, string valueName)
        => IsForbidden(new FootprintNode(FootprintKind.RegistryValue, probe, path, path, valueName));

    /// <summary>同上，只是给"删整条键"用的（值名传空）。</summary>
    public static bool IsForbiddenKeyPublic(string probe, string path)
        => IsForbidden(new FootprintNode(FootprintKind.RegistryKey, probe, path, path, ""));

    private static bool IsForbiddenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;

        string full;
        try { full = Path.GetFullPath(path); }
        catch { return true; }

        // ① 整个 Windows 目录之下（含 System32 / WinSxS / Fonts / 驱动库…）
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0 && IsInsideOrEqual(full, windows)) return true;

        // ② 逐个禁碰前缀
        foreach (var prefix in ForbiddenPathPrefixes)
            if (prefix.Length > 0 && IsInsideOrEqual(full, prefix)) return true;

        // ③ 叶子名命中禁碰名单（不管它在哪）
        var leaf = Path.GetFileName(full.TrimEnd('\\'));
        if (leaf.Length == 0) return true;
        if (ForbiddenLeafNames.Contains(leaf)) return true;

        // ④ 一切以点开头、且不是已知软件痕迹的（.ssh / .gnupg / .gitconfig 这类）
        //    ⚠️ 这条是有意保守：真正的软件残留目录极少以点开头，而点开头的目录里
        //       藏着密钥和凭据的概率不低 —— 宁可漏。
        if (leaf.StartsWith('.')) return true;

        return false;
    }

    /// <summary>
    /// 注册表项的禁碰判定。⚠️ 分两类体检点，规则不一样：
    ///
    ///   · **泛扫容器**（<c>HK* \SOFTWARE</c> 三个、<c>HK*\SOFTWARE\Classes</c> 两个）：
    ///     它们的子键里混着一整片系统区域，所以要连"整片禁碰前缀"一起判。
    ///   · **专门体检点**（卸载登记 / 路径登记 / 程序关联 / 文件关联 / 开自启 / 共享组件）：
    ///     父键是系统固定的，**父键本身绝不动**，我们只看它底下那一级 ——
    ///     所以这里⛔ 不能套前缀规则，否则「路径登记」那一整类会被
    ///     <c>HKCU\SOFTWARE\Microsoft</c> 这个前缀全部挡掉（这正是它的父键）。
    /// </summary>
    private static bool IsForbiddenRegistryKey(string probe, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;

        var i = path.LastIndexOf('\\');
        var leaf = i >= 0 ? path[(i + 1)..] : path;
        if (ForbiddenRegistryLeafNames.Contains(leaf)) return true;

        if (!GenericSoftwareKeyProbes.Contains(probe)) return false;

        foreach (var prefix in ForbiddenRegistryPrefixes)
            if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>「泛扫容器」类体检点的标签（这几个的判定要走整片前缀规则）。</summary>
    private static readonly HashSet<string> GenericSoftwareKeyProbes = new(StringComparer.Ordinal)
    {
        "用户软件键", "机器软件键", "机器软件键(32位)",
        "文件夹联(机器)", "文件夹联(当前用户)",
    };

    /// <summary>
    /// 注册表里的"整片禁碰区"。⛔ 硬编码常量，不可配置。
    /// ⚠️ 只对<see cref="GenericSoftwareKeyProbes"/>生效 —— 原因见上面那段。
    /// </summary>
    private static readonly string[] ForbiddenRegistryPrefixes =
    {
        @"HKCU\SOFTWARE\Microsoft",
        @"HKLM\SOFTWARE\Microsoft",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft",
        @"HKCU\SOFTWARE\Policies",
        @"HKLM\SOFTWARE\Policies",
        @"HKLM\SOFTWARE\WOW6432Node\Policies",
        @"HKCU\SOFTWARE\Classes\CLSID",
        @"HKLM\SOFTWARE\Classes\CLSID",
        @"HKLM\SOFTWARE\WOW6432Node\Classes\CLSID",
    };

    private static bool IsForbiddenRegistryValue(FootprintNode node)
    {
        // 自启项里有一批"系统自己在用"的值名，⛔ 一个都不许动
        if (node.Probe.StartsWith("开机自启", StringComparison.Ordinal) ||
            node.Probe.StartsWith("一次性自启", StringComparison.Ordinal))
        {
            return ForbiddenRunValueNames.Contains(node.ValueName)
                || node.ValueName.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);
        }

        // 共享组件登记：值名就是 DLL 的完整路径。指向系统目录的一律不动
        //（删掉会造成"系统 DLL 少了一个引用"的假象，InstallShield 那套会据此误删真文件）。
        if (node.Probe.StartsWith("共享组件", StringComparison.Ordinal))
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (windows.Length > 0 && node.ValueName.Length > 0
                && IsInsideOrEqual(node.ValueName, windows))
                return true;
        }

        return false;
    }

    private static string[] ForbiddenPathPrefixes => _forbiddenPathPrefixes ??= BuildForbiddenPathPrefixes();

    private static string[]? _forbiddenPathPrefixes;

    private static string[] BuildForbiddenPathPrefixes()
    {
        var la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var ra = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var list = new List<string>
        {
            // 程序目录里的"公共设施"
            Path.Combine(pf, "Common Files"),
            Path.Combine(pf, "WindowsApps"),
            Path.Combine(pf, "Windows Defender"),
            Path.Combine(pf, "Windows Defender Advanced Threat Protection"),
            Path.Combine(pf, "Windows NT"),
            Path.Combine(pf, "WindowsPowerShell"),
            Path.Combine(pf, "Windows Mail"),
            Path.Combine(pf, "Windows Media Player"),
            Path.Combine(pf, "Windows Multimedia Platform"),
            Path.Combine(pf, "Windows Photo Viewer"),
            Path.Combine(pf, "Windows Portable Devices"),
            Path.Combine(pf, "Windows Sidebar"),
            Path.Combine(pf, "Windows System Assessment Tool"),
            Path.Combine(pf, "Internet Explorer"),
            Path.Combine(pf, "ModifiableWindowsApps"),
            Path.Combine(pf, "Reference Assemblies"),
            Path.Combine(pf, "MSBuild"),
            Path.Combine(pf, "dotnet"),
            Path.Combine(pf, "Microsoft"),

            // 公共数据
            Path.Combine(pd, "Microsoft"),
            Path.Combine(pd, "Package Cache"),
            Path.Combine(pd, "Packages"),
            Path.Combine(pd, "Windows"),
            Path.Combine(pd, "Windows Defender"),
            Path.Combine(pd, "Desktop") ,

            // 用户数据（Local）
            Path.Combine(la, "Microsoft"),
            Path.Combine(la, "Packages"),
            Path.Combine(la, "Temp"),
            Path.Combine(la, "ClassSoftwareHub"),   // ⛔⛔ 本程序自己 —— 清到这儿就是把缓存/日志/隔离区全删了
            Path.Combine(la, "ConnectedDevicesPlatform"),
            Path.Combine(la, "CrashDumps"),
            Path.Combine(la, "D3DSCache"),
            Path.Combine(la, "GroupPolicy"),
            Path.Combine(la, "History"),
            Path.Combine(la, "INetCache"),
            Path.Combine(la, "INetCookies"),
            Path.Combine(la, "PlaceholderTileLogoFolder"),
            Path.Combine(la, "Publishers"),
            Path.Combine(la, "VirtualStore"),
            Path.Combine(la, "ElevatedDiagnostics"),
            Path.Combine(la, "Programs", "Common"),

            // 用户数据（Roaming）
            // ⛔⛔ 这里**故意不列** `%APPDATA%\Microsoft`：开始菜单的快捷方式目录就在它下面
            //     （`%APPDATA%\Microsoft\Windows\Start Menu\Programs`），列进去会把整个开始菜单
            //     体检点一并挡死 —— 而"开始菜单里留下的空文件夹"恰恰是最典型的残留之一。
            //     它真正的保护由**叶子名规则**提供（`%APPDATA%` 探测出的直接子项叫 `Microsoft`，
            //     命中禁碰叶子名，一样进不来）。2026-10-14 实测踩过这个坑。
            Path.Combine(ra, "ClassSoftwareHub"),

            // 用户目录下的"当然是用户的东西"
            // ⛔⛔ 这里**故意不列** `%USERPROFILE%\AppData`：那是个"整片挡死"的前缀，
            //     而 `%LOCALAPPDATA%` / `%APPDATA%` / `%LOCALAPPDATA%\Programs` 全都长在它下面
            //     —— 列进去的话整个残留扫描会一条都扫不出来。2026-10-14 实测踩过这个坑。
            //     AppData 自己那一层的保护同样由**叶子名规则**给（探测出的子项就叫 `AppData`，
            //     在禁碰叶子名里）。底下具体哪些系统目录要挡，由上面那批精确前缀负责。
            Path.Combine(up, "Desktop"),
            Path.Combine(up, "Documents"),
            Path.Combine(up, "Downloads"),
            Path.Combine(up, "Music"),
            Path.Combine(up, "Pictures"),
            Path.Combine(up, "Videos"),
            Path.Combine(up, "Favorites"),
            Path.Combine(up, "Links"),
            Path.Combine(up, "Contacts"),
            Path.Combine(up, "Saved Games"),
            Path.Combine(up, "Searches"),
            Path.Combine(up, "3D Objects"),
            Path.Combine(up, "OneDrive"),
            Path.Combine(up, "OneDrive - Personal"),
        };

        return list.Where(p => p.Length > 3).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static HashSet<string>? _forbiddenLeafNames;

    private static HashSet<string> ForbiddenLeafNames => _forbiddenLeafNames ??= new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Microsoft", "Common Files", "WindowsApps", "WinSxS",
        "System32", "SysWOW64", "System", "Assembly", "Fonts", "Inf",
        "AppData", "ProgramData", "Users", "Program Files", "Program Files (x86)",
        "ClassSoftwareHub", "Package Cache", "Packages", "Temp", "Tmp",
        "desktop.ini", "ntuser.dat", "ntuser.ini", "NTUSER.DAT.LOG1", "NTUSER.DAT.LOG2",
        "bootmgr", "BOOTNXT", "pagefile.sys", "swapfile.sys", "hiberfil.sys", "DumpStack.log.tmp",
        "$Recycle.Bin", "System Volume Information", "Recovery", "PerfLogs",
        "config.msi", "$PatchCache$", "$WinREAgent", "$SysReset", "Intel", "AMD", "NVIDIA",
        "Realtek", "Common", "InstallShield", "Windows Kits", "Application Verifier",
    };

    private static HashSet<string>? _forbiddenRegistryLeafNames;

    /// <summary>
    /// 注册表里这些**直接子键**永不进列表。
    /// ⚠️ 它们多半挂在 <c>HKLM\SOFTWARE\Classes</c> 底下，而那个位置本身是允许扫的
    ///    （软件自己的 ProgID 就在那儿），所以要单独列一份子键黑名单。
    /// </summary>
    private static HashSet<string> ForbiddenRegistryLeafNames => _forbiddenRegistryLeafNames ??= new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "CLSID", "AppID", "Interface", "TypeLib", "Wow6432Node", "Local Settings",
        "Installer", "Component Categories", "Extensions", "MIME", "PackagedCom",
        "ActivatableClasses", "Media Type", "VirtualStore", "Licenses", "CLSID\\{00000000-0000-0000-0000-000000000000}",
        "Microsoft", "Policies", "Windows", "Windows NT", "Classes", "RegisteredApplications",
        "OEM", "Setup", "SystemCertificates", "Cryptography", "ProtectedStorage",
        "CurrentVersion", "Explorer", "Run", "RunOnce", "App Paths", "Uninstall",
        "SharedDLLs", "FileExts", "Applications", "StartupApproved", "Shell Extensions",
        "*", ".exe", ".dll", ".com", ".bat", ".cmd", ".ps1",
    };

    /// <summary>自启项里"系统自己在用"的值名 —— 一个都不许删。</summary>
    private static HashSet<string>? _forbiddenRunValueNames;

    private static HashSet<string> ForbiddenRunValueNames => _forbiddenRunValueNames ??= new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "SecurityHealth", "SecurityHealthSystray", "ctfmon", "OneDrive",
        "OneDriveSetup", "MicrosoftEdgeAutoLaunch", "WindowsDefender",
        "RTHDVCPL", "RtHDVBg", "IAStorIcon", "igfxTray", "Persistence",
        "Steam Client Bootstrapper", "RtkAudUService64_BG",
    };

    // ══════════════════════════════════════════════════════════════════════════
    //  ③ 体积测算
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>单条测算的时间上限 —— 万一指到一个几万文件的目录，不能让界面干等。</summary>
    private const int MeasureBudgetMs = 1500;

    private static void Measure(List<LeftoverItem> items)
    {
        foreach (var item in items)
        {
            if (item.Kind != FootprintKind.Path) { item.SizeBytes = 0; continue; }

            var sw = Stopwatch.StartNew();
            long bytes = 0;
            var files = 0;

            try
            {
                if (File.Exists(item.Path))
                {
                    bytes = new FileInfo(item.Path).Length;
                    files = 1;
                }
                else if (Directory.Exists(item.Path))
                {
                    foreach (var f in Directory.EnumerateFiles(item.Path, "*", SearchOption.AllDirectories))
                    {
                        if (sw.ElapsedMilliseconds > MeasureBudgetMs) break;
                        try { bytes += new FileInfo(f).Length; files++; }
                        catch { /* 单个文件读不到就跳过 */ }
                    }
                }
                else
                {
                    item.SizeBytes = -1;   // 已经不在了
                    continue;
                }
            }
            catch (Exception ex)
            {
                AppLog.Info("leftover", $"测算体积失败（{item.Path}）：{ex.GetType().Name}");
                item.SizeBytes = -1;
                continue;
            }

            item.SizeBytes = bytes;
            item.FileCount = files;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  路径工具
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <paramref name="child"/> 是不是 <paramref name="parent"/> 本身或它的后代。
    /// ⚠️ 必须补分隔符再比前缀 —— 否则 <c>C:\Foo</c> 会"包含" <c>C:\Foobar</c>（经典坑）。
    /// </summary>
    internal static bool IsInsideOrEqual(string child, string parent)
    {
        if (child.Length == 0 || parent.Length == 0) return false;

        string c, p;
        try
        {
            c = Path.GetFullPath(child).TrimEnd('\\');
            p = Path.GetFullPath(parent).TrimEnd('\\');
        }
        catch
        {
            return false;
        }

        if (c.Equals(p, StringComparison.OrdinalIgnoreCase)) return true;
        return c.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>这次卸载涉及的那个软件，预先算好要比对的各种名字。</summary>
    private sealed class AppProfile
    {
        public AppProfile(InstalledApp app)
        {
            NameKey = InstalledApps.Normalize(app.Name);
            FolderKey = app.Location.Length > 0
                ? InstalledApps.Normalize(Path.GetFileName(app.Location.TrimEnd('\\')))
                : "";
            Location = app.Location;

            // ⛔⛔ 这里**没有**"发布者"这一项，而且是**故意**不要的。
            //    曾经有过一版拿 Publisher 去比名字，结果：测试软件的发布者是 ClassSoftwareHub，
            //    于是 `%USERPROFILE%\ClassSoftwareHub-Mobile`（Nick 自己的手机版工程目录，90 MB）
            //    被当成它的残留、默认勾选、清理掉了。2026-10-15 实拍。
            //    根本原因：发布者是**品牌/公司名**，一个词能对上几十个不相干的目录
            //    （「Microsoft」「Tencent」底下什么都有）。这条判据的收益远小于它的风险。

            // 主程序名：从卸载命令里掏出来的 exe 文件名
            // ⛔⛔ 必须排掉"卸载器自己" —— `UninstallString` 指的**就是**卸载器
            //    （绝大多数是 `unins000.exe`），拿它归一化出来是「unins」，
            //    那会命中一大片带 unins 字样的东西，等于把闸门拆了。
            var exeKey = "";
            try
            {
                var raw = app.QuietUninstallString.Length > 0 ? app.QuietUninstallString : app.UninstallString;
                var (exe, _) = AppUninstaller.SplitCommand(raw);
                var stem = exe.Length > 0 ? Path.GetFileNameWithoutExtension(exe) : "";

                if (!LooksLikeUninstaller(stem)) exeKey = InstalledApps.Normalize(stem);
            }
            catch
            {
                // 卸载命令写得怪就不比这一项
            }

            ExeKey = exeKey;

            // 名字拆词：「ClassSoftwareHub Desktop」→ ["classsoftwarehub", "desktop"]
            var tokens = new List<string>();
            foreach (var part in app.Name.Split(
                         new[] { ' ', '-', '_', '·', '（', '）', '(', ')', '/', '\\', '.', ',' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var t = InstalledApps.Normalize(part);
                if (t.Length >= 5 && !tokens.Contains(t)) tokens.Add(t);
            }

            Tokens = tokens;
        }

        public string NameKey { get; }
        public string FolderKey { get; }
        public string ExeKey { get; }
        public string Location { get; }
        public List<string> Tokens { get; }

        /// <summary>这个 exe 名字看着像不像"卸载程序"（`unins000` / `uninstall` / `卸载`…）。</summary>
        private static bool LooksLikeUninstaller(string stem)
        {
            if (stem.Length == 0) return true;
            var s = stem.ToLowerInvariant();

            return s.StartsWith("unins", StringComparison.Ordinal)
                || s.Contains("uninst", StringComparison.Ordinal)
                || s.Contains("卸载", StringComparison.Ordinal)
                || s.StartsWith("setup", StringComparison.Ordinal)
                || s.StartsWith("install", StringComparison.Ordinal);
        }

        /// <summary>
        /// 差的清单要不要收。目前一律收（弱证据摆出来但默认不勾），
        /// 留这个开关是为了以后要"只看确定的"时不用改一堆调用点。
        /// </summary>
        public bool AllowWeak => true;
    }
}
