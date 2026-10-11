using System;
using System.IO;
using System.Linq;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 一次性清掉「旧结构」遗留在数据根上的死文件（Nick 2026-10-10 定案）。
///
/// 背景：以前所有东西都堆在数据根上 —— 解包的图片/图标、十几个模块各自的 <c>.log</c>。
/// 后来虽然分批改到 <c>cache\</c> / <c>logs\</c>，但两处搬迁函数都写成
/// 「目标已存在就跳过」（<c>if (File.Exists(target)) continue;</c>），而旧副本与新文件恰好同名，
/// 于是一个都没搬走，全部卡在根目录里当僵尸文件。
///
/// 这里直接**清掉**（而不是再搬）：这些文件全是可再生的 ——
/// 素材下一次用到会重新解包进 <c>cache\{版本}\</c>，日志在 <c>logs\</c> 里已有更新的那一份。
/// 同时把 <c>cache\</c> 根下"旧结构散装素材"也一并收掉，让 <c>cache\</c> 下**只剩版本目录**。
///
/// ⛔ 安全边界（白名单，别扩大）：
///  - 只删**文件**，绝不删目录；
///  - 数据根下只认 <c>*.png</c> / <c>*.ico</c> / <c>*.log</c> 与两个调试 dump；
///    其中日志还要求 <c>logs\</c> 里已有同名文件（证明它是"搬完剩下的旧那份"），否则留着；
///  - <c>cache\</c> 根下只认 <c>*.png</c> / <c>*.ico</c> / <c>*.wav</c>（旧结构遗留的散装素材）；
///  - <c>*.json</c>、其它 <c>*.txt</c>（settings.json、*-base.txt、*-endpoint.txt…）、子目录一律不碰；
///  - 每一步异常都落日志，禁止空 catch。
/// </summary>
public static class LegacyCleanup
{
    private static bool _done;

    /// <summary>数据根下属于「旧版堆上来的素材副本」的扩展名。</summary>
    private static readonly string[] RootAssetPatterns = { "*.png", "*.ico" };

    /// <summary>调试期留下的窗口/对话框 dump。</summary>
    private static readonly string[] RootDumpFiles = { "pane-dump.txt", "dlg-dump.txt" };

    /// <summary><c>cache\</c> 根下「旧结构散装素材」的扩展名。</summary>
    private static readonly string[] CacheRootPatterns = { "*.png", "*.ico", "*.wav" };

    /// <summary>应用启动早期调一次（内部保证同一次运行只跑一遍）。</summary>
    public static void Run()
    {
        if (_done) return;
        _done = true;

        try
        {
            var removed = CleanDataRoot() + CleanCacheRoot();
            if (removed > 0)
                AppLog.Info("legacy", $"已清理 {removed} 个旧结构遗留文件");
        }
        catch (Exception ex)
        {
            AppLog.Info("legacy", "清理旧残留失败：" + ex.Message);
        }
    }

    /// <summary>数据根：旧素材副本、旧日志、调试 dump。</summary>
    private static int CleanDataRoot()
    {
        var root = SettingsStore.Dir;
        if (!Directory.Exists(root)) return 0;

        var removed = 0;

        // ① 旧素材副本 —— 现在解包进 cache\{版本}\ 了
        foreach (var pattern in RootAssetPatterns)
            foreach (var file in Directory.GetFiles(root, pattern))
                if (TryDelete(file)) removed++;

        // ② 旧日志 —— 只在 logs\ 里已有同名时才删（否则它是唯一的一份，留着）
        //    ⚠️ 2026-10-11 起 logs\ 下面不再平铺模块日志，而是「启动一次一个会话目录」，
        //       所以这里要**递归**找同名，不能再只看 logs\ 根（否则这条规则永远判 false，旧副本收不掉）。
        var logRoot = AppLog.Root;
        foreach (var file in Directory.GetFiles(root, "*.log"))
        {
            var name = Path.GetFileName(file);
            if (!File.Exists(Path.Combine(logRoot, name)) && !ExistsUnder(logRoot, name)) continue;
            if (TryDelete(file)) removed++;
        }

        // ③ 调试 dump
        foreach (var name in RootDumpFiles)
        {
            var file = Path.Combine(root, name);
            if (File.Exists(file) && TryDelete(file)) removed++;
        }

        return removed;
    }

    /// <summary><c>cache\</c> 根下的散装素材（只扫顶层，版本目录里的东西不动）。</summary>
    private static int CleanCacheRoot()
    {
        var cache = CacheHome.Root;
        if (!Directory.Exists(cache)) return 0;

        var removed = 0;
        foreach (var pattern in CacheRootPatterns)
            foreach (var file in Directory.GetFiles(cache, pattern))
                if (TryDelete(file)) removed++;

        return removed;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Info("legacy", $"删除失败（{Path.GetFileName(path)}）：{ex.Message}");
            return false;
        }
    }

    /// <summary>日志根（含各会话子目录）里有没有这个名字的文件 —— 只在需要时才递归扫。</summary>
    private static bool ExistsUnder(string logRoot, string fileName)
    {
        try
        {
            if (!Directory.Exists(logRoot)) return false;
            return Directory.EnumerateFiles(logRoot, fileName, SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }
}
