using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services.Updating;

/// <summary>updates 目录里的一个安装包（给设置页列表用的只读视图）。</summary>
public sealed record InstallerFileInfo(
    string Name,
    string FullPath,
    long Length,
    DateTimeOffset ModifiedUtc,
    bool HasMd5File);

/// <summary>
/// 更新安装包自动清理：更新流程把安装包统一下到 <c>%LOCALAPPDATA%\ClassSoftwareHub\updates</c>，
/// 一版一个、越攒越大。这里在启动时把「保留数量」（<see cref="AppSettings.InstallerKeepCount"/>，默认 3）
/// 之外的旧包删掉 —— 留下的正好给「版本记录 → 装回旧版本」用：本地已有同名包且校验通过就不再下载（见 UpdateFlow）。
///
/// ⛔ 安全边界（这条绝不能松）：
///  - **只认** <c>ClassSoftwareHub-Setup-*.exe</c> 及其同名 <c>.md5</c>，其余文件一律不碰；
///  - 只在自家 <c>updates</c> 目录里动手，绝不波及系统「下载」文件夹；
///  - 文件被占用（安装程序还在跑等）删不掉就跳过，下次启动再试；
///  - 每一步的异常都落日志（<see cref="ScreenCapture.Log"/>），禁止空 catch。
/// </summary>
public static class InstallerCleanup
{
    private static readonly Regex InstallerPattern =
        new(@"^ClassSoftwareHub-Setup-.+\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Md5Pattern =
        new(@"^ClassSoftwareHub-Setup-.+\.md5$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>安装包存放目录（与更新流程同源：UpdateService.UpdatesDir）。</summary>
    public static string InstallerDirectory => UpdateService.UpdatesDir;

    /// <summary>列出本地安装包，新的在前。目录不存在时返回空表。</summary>
    public static IReadOnlyList<InstallerFileInfo> Scan()
    {
        var dir = InstallerDirectory;
        if (!Directory.Exists(dir)) return Array.Empty<InstallerFileInfo>();

        var list = new List<InstallerFileInfo>();
        foreach (var path in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(path);
            if (!InstallerPattern.IsMatch(name)) continue;
            try
            {
                var fi = new FileInfo(path);
                var md5 = Path.ChangeExtension(path, ".md5");
                list.Add(new InstallerFileInfo(
                    name, path, fi.Length,
                    new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero),
                    File.Exists(md5)));
            }
            catch (Exception ex)
            {
                ScreenCapture.Log($"[cleanup] 读取安装包信息失败（{name}）: {ex.Message}");
            }
        }

        return list.OrderByDescending(f => f.ModifiedUtc).ToList();
    }

    /// <summary>
    /// 按保留数量清理：按修改时间从新到旧留 <paramref name="keep"/> 个，其余删除（连带同名 .md5）。
    /// 顺手清掉「安装包已不在、只剩 .md5」的孤儿校验文件。返回删除的安装包个数。
    /// </summary>
    public static int Clean(int keep)
    {
        keep = Math.Clamp(keep, 1, 10);
        var dir = InstallerDirectory;
        if (!Directory.Exists(dir)) return 0;

        var files = Directory.EnumerateFiles(dir)
            .Where(p => InstallerPattern.IsMatch(Path.GetFileName(p)))
            .Select(p => new FileInfo(p))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .ToList();

        var removed = 0;
        for (var i = keep; i < files.Count; i++)
            if (TryDeleteWithMd5(files[i].FullName)) removed++;

        // 孤儿 .md5：对应的 .exe 已经不在了，校验文件留着只会让人困惑
        foreach (var path in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(path);
            if (!Md5Pattern.IsMatch(name)) continue;
            var exe = Path.ChangeExtension(path, ".exe");
            if (!File.Exists(exe)) TryDelete(path);
        }

        return removed;
    }

    /// <summary>删除安装包及其同名 .md5。单个文件失败只记日志、不打断整轮清理。</summary>
    public static bool TryDeleteWithMd5(string exePath)
    {
        var ok = TryDelete(exePath);
        var md5 = Path.ChangeExtension(exePath, ".md5");
        if (File.Exists(md5)) TryDelete(md5);
        return ok;
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
            // 被占用（安装程序正在运行）属正常情况：跳过，下次启动再试
            ScreenCapture.Log($"[cleanup] 删除失败（{Path.GetFileName(path)}）: {ex.Message}");
            return false;
        }
    }

    /// <summary>应用启动时挂后台清一轮（不阻塞首帧；新版本装完后的第一次启动会把旧包收掉）。</summary>
    public static void Start() => Task.Run(() =>
    {
        try
        {
            var keep = App.Settings.Current.InstallerKeepCount;
            var removed = Clean(keep);
            if (removed > 0)
                ScreenCapture.Log($"[cleanup] 已清理 {removed} 个旧安装包（保留最近 {Math.Clamp(keep, 1, 10)} 个）");
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("[cleanup] 安装包清理失败: " + ex.Message);
        }
    });
}
