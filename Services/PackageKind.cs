using System;
using System.Collections.Generic;
using System.IO;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>下载下来的这个文件，用户接下来还能对它做什么。</summary>
public enum PackageAction
{
    /// <summary>普通文件（图片 / 文档 / 脚本……）—— 打开就是终点，不多给按钮。</summary>
    None,

    /// <summary>安装包 —— 给「安装」。</summary>
    Install,

    /// <summary>压缩包 —— 给「解压」。</summary>
    Extract,
}

/// <summary>
/// 按扩展名判「下载下来的东西是安装包还是压缩包」。
///
/// 为什么要有它：下载完成后卡片上原本只有「打开」，而「打开」对这两类文件都不是最好的动作 ——
/// 安装包该说「安装」，压缩包该说「解压」。用户不必自己分辨下到的是哪一种。
///
/// ⚠️ 与 <see cref="Data.DownloadLinkKind"/> 不是一回事，别合并：
///    那个判的是**链接**指向哪儿（文件直链 / 网盘 / 官网 / 商店），判完决定"按钮写下载还是前往官网"；
///    这个判的是**已经落到硬盘上的文件**是什么，判完决定"给不给安装/解压"。一个在下载前，一个在下载后。
///
/// ⚠️ 只认扩展名，不看文件头。代价是不认识伪装后缀（.exe 其实是 zip 之类），
///    但那种情况本来也不该猜 —— 判错的代价是用户点了「安装」什么都没发生。
/// </summary>
public static class PackageKind
{
    /// <summary>
    /// 双击就该往"安装"走的后缀。
    /// ⚠️ .exe 不一定真是安装程序（可能是绿色版），但那个位置给「安装」仍然是最合理的猜测 ——
    ///    用户看到「安装」点开发现是绿色版直接跑起来，不会觉得被坑。
    /// </summary>
    private static readonly HashSet<string> Installers = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".appinstaller", ".msu",
    };

    /// <summary>
    /// 压缩包后缀。
    ///
    /// ⚠️ 复合后缀（.tar.gz / .tar.xz / .tar.bz2 / .tar.zst）在这里**不用单列** ——
    ///    <see cref="Path.GetExtension"/> 只给最后一段，而 .gz / .xz / .bz2 / .zst 本来就在集合里，
    ///    所以 foo.tar.gz 会被认成压缩包，正确。
    /// ⚠️ 不把 .iso / .cab 算进来：前者是镜像（该挂载），后者双击走的是系统查看器，
    ///    两者「解压」都不是用户预期的下一步。
    /// </summary>
    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar",
        ".gz", ".tgz", ".xz", ".txz", ".bz2", ".tbz2", ".tbz",
        ".zst", ".tzst", ".lzma", ".lz",
    };

    /// <summary>这条路是干什么的（认不出来就给 <see cref="PackageAction.None"/>）。</summary>
    public static PackageAction Of(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return PackageAction.None;

        var ext = Path.GetExtension(path);
        if (ext.Length < 2) return PackageAction.None;

        if (Installers.Contains(ext)) return PackageAction.Install;
        if (Archives.Contains(ext)) return PackageAction.Extract;
        return PackageAction.None;
    }

    public static bool IsInstaller(string? path) => Of(path) == PackageAction.Install;

    public static bool IsArchive(string? path) => Of(path) == PackageAction.Extract;

    /// <summary>界面上按钮该写什么（认不出来给空串，调用方据此决定不给按钮）。</summary>
    public static string ActionText(PackageAction action) => action switch
    {
        PackageAction.Install => "安装",
        PackageAction.Extract => "解压",
        _ => "",
    };
}
