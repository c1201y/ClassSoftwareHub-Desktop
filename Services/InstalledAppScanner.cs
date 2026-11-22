using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>扫描到的一个可添加快捷项。</summary>
public sealed record ScannedApp(string Name, string Path);

/// <summary>
/// 扫描本机已安装的应用。数据来源是开始菜单与桌面上的快捷方式，
/// 也就是用户在系统里真正看得到、点得开的那些入口（2026-11-22）。
/// 不读注册表卸载项：那里面混着运行库、补丁和卸载程序，列出来反而更难找。
/// </summary>
public static class InstalledAppScanner
{
    /// <summary>明显不是应用的条目，扫到直接丢掉。</summary>
    private static readonly string[] NoiseWords =
    {
        "卸载", "uninstall", "readme", "说明", "帮助", "help", "文档",
        "documentation", "更新", "update", "官网", "website", "许可", "license",
        "release notes", "changelog", "命令行提示", "关于",
    };

    /// <summary>返回去重后的应用清单，按名称排序。</summary>
    public static List<ScannedApp> Scan()
    {
        var found = new List<ScannedApp>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in Roots())
        {
            if (!Directory.Exists(root)) continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.System,
                });
            }
            catch
            {
                continue;
            }

            foreach (var file in SafeEnumerate(files))
            {
                if (!seenPaths.Add(file)) continue;

                var name = System.IO.Path.GetFileNameWithoutExtension(file).Trim();
                if (name.Length == 0) continue;
                if (IsNoise(name)) continue;
                if (!seenNames.Add(name)) continue;      // 同一个应用在用户/公共开始菜单各有一份

                found.Add(new ScannedApp(name, file));
            }
        }

        return found
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>扫描范围：公共开始菜单、当前用户开始菜单、公共桌面、当前用户桌面。</summary>
    private static IEnumerable<string> Roots()
    {
        yield return SafeFolder(Environment.SpecialFolder.CommonStartMenu, "Programs");
        yield return SafeFolder(Environment.SpecialFolder.StartMenu, "Programs");
        yield return SafeFolder(Environment.SpecialFolder.CommonDesktopDirectory);
        yield return SafeFolder(Environment.SpecialFolder.DesktopDirectory);
    }

    private static string SafeFolder(Environment.SpecialFolder folder, string? sub = null)
    {
        try
        {
            var path = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(path)) return "";
            return sub is null ? path : System.IO.Path.Combine(path, sub);
        }
        catch
        {
            return "";
        }
    }

    private static IEnumerable<string> SafeEnumerate(IEnumerable<string> files)
    {
        using var e = files.GetEnumerator();
        while (true)
        {
            string current;
            try
            {
                if (!e.MoveNext()) yield break;
                current = e.Current;
            }
            catch
            {
                yield break;      // 某些目录枚举到一半会抛，前面拿到的照常保留
            }

            yield return current;
        }
    }

    private static bool IsNoise(string name)
    {
        foreach (var word in NoiseWords)
        {
            if (name.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
