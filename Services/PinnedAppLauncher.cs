using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Services;


public static class PinnedAppLauncher
{
    
    public static string DisplayName(PinnedApp app) =>
        string.IsNullOrWhiteSpace(app.Name)
            ? Path.GetFileNameWithoutExtension(app.Path)
            : app.Name;

    
    
    
    public static string? Launch(PinnedApp app)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(app.Path)) return "快捷方式路径为空";

            if (IsShellItem(app.Path))
            {
                var explorer = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                if (!File.Exists(explorer)) return "找不到文件资源管理器";

                Process.Start(new ProcessStartInfo(explorer, Quote(app.Path))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                return null;
            }

            if (!File.Exists(app.Path)) return "找不到文件：" + app.Path;

            Process.Start(new ProcessStartInfo(app.Path, app.Arguments ?? "")
            {
                UseShellExecute = true,
            });
            return null;
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("pinapp", $"启动 {app.Path} 失败：{ex.Message}");
            return "启动失败：" + ex.Message;
        }
    }

    public static void EnsureIds()
    {
        var apps = App.Settings.Current.PinnedApps;
        if (apps is null) return;
        var changed = false;
        foreach (var app in apps)
        {
            if (string.IsNullOrWhiteSpace(app.Id))
            {
                app.Id = Guid.NewGuid().ToString("N");
                changed = true;
            }
        }
        if (changed) App.Settings.Save();
    }

    public static PinnedApp? FindById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        EnsureIds();
        return App.Settings.Current.PinnedApps.FirstOrDefault(a => a.Id == id);
    }

    public static bool RemoveById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        EnsureIds();
        var apps = App.Settings.Current.PinnedApps;
        var index = apps.FindIndex(a => a.Id == id);
        if (index < 0) return false;
        apps.RemoveAt(index);
        App.Settings.Current.SidebarAppHotkeys?.Remove(id);
        App.Settings.Save();
        return true;
    }

    
    public static bool RemoveAt(int index)
    {
        var apps = App.Settings.Current.PinnedApps;
        if (index < 0 || index >= apps.Count) return false;

        apps.RemoveAt(index);
        App.Settings.Save();
        return true;
    }

    
    public static bool IsShellItem(string path) =>
        path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("::{", StringComparison.Ordinal);

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
