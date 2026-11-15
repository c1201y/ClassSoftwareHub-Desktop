using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services.Updating;


public sealed record InstallerFileInfo(
    string Name,
    string FullPath,
    long Length,
    DateTimeOffset ModifiedUtc,
    bool HasMd5File);












public static class InstallerCleanup
{
    private static readonly Regex InstallerPattern =
        new(@"^ClassSoftwareHub-Setup-.+\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Md5Pattern =
        new(@"^ClassSoftwareHub-Setup-.+\.md5$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    
    public static string InstallerDirectory => UpdateService.UpdatesDir;

    
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

        
        foreach (var path in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(path);
            if (!Md5Pattern.IsMatch(name)) continue;
            var exe = Path.ChangeExtension(path, ".exe");
            if (!File.Exists(exe)) TryDelete(path);
        }

        return removed;
    }

    
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
            
            ScreenCapture.Log($"[cleanup] 删除失败（{Path.GetFileName(path)}）: {ex.Message}");
            return false;
        }
    }

    
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
