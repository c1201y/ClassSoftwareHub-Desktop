using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data.Tweaks;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;








public sealed record TweakApplyResult(
    int Requested,
    int Matched,
    string? Error,
    IReadOnlyList<string> Failed)
{
    public bool Ok => Error is null;
}



















public static class TweakStore
{
    
    
    
    
    public static int? ReadValue(TweakDef def)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(
                def.Scope == TweakScope.System ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
                RegistryView.Default);

            using var key = root.OpenSubKey(def.KeyPath, writable: false);
            var raw = key?.GetValue(def.ValueName);

            
            if (raw is null) return null;

            
            
            
            var value = TweakRules.ParseDword(raw);

            if (value is null)
                AppLog.Warn("tweaks", $"{def.FullPath} 的值类型不认识（{raw.GetType().Name}），按关处理");

            return value;
        }
        catch (Exception ex)
        {
            AppLog.Warn("tweaks", $"读取 {def.Id}（{def.FullPath}）失败：{ex.Message}");
            return null;
        }
    }

    
    
    
    
    public static int EffectiveValue(TweakDef def) => ReadValue(def) ?? def.OffValue;

    
    public static bool IsOn(TweakDef def) => EffectiveValue(def) == def.OnValue;

    
    
    
    
    public static async Task<TweakApplyResult> ApplyAsync(IReadOnlyList<(TweakDef Def, int Value)> changes)
    {
        if (changes.Count == 0) return new TweakApplyResult(0, 0, null, Array.Empty<string>());

        
        var userChanges = changes.Where(c => c.Def.Scope == TweakScope.User).ToList();
        var systemChanges = changes.Where(c => c.Def.Scope == TweakScope.System).ToList();

        string? error = null;

        
        foreach (var (def, value) in userChanges)
        {
            try
            {
                WriteUser(def, value);
            }
            catch (Exception ex)
            {
                AppLog.Warn("tweaks", $"写入 {def.Id}（{def.FullPath}）失败：{ex.Message}");
                error ??= ex.Message;
            }
        }

        AppLog.Info("tweaks",
            $"应用 {changes.Count} 项：当前用户 {userChanges.Count} 项、机器级 {systemChanges.Count}（{string.Join("、", changes.Select(c => c.Def.Id))}）");

        
        if (systemChanges.Count > 0)
        {
            var path = Path.Combine(AppPaths.DataDir, $"tweaks-{DateTime.Now:HHmmss}.reg");

            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(path, BuildRegFile(systemChanges), new UnicodeEncoding(false, true));

                var psi = new ProcessStartInfo("reg.exe", $"import \"{path}\"")
                {
                    Verb = "runas",              
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };

                using var proc = Process.Start(psi);
                if (proc is not null) await proc.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                
                AppLog.Warn("tweaks", "机器级改动未应用：" + ex.Message);
                error ??= ex.Message;
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }

        
        
        
        var refreshes = TweakRules.RefreshesFor(changes);

        if (refreshes.Count > 0)
        {
            await Task.Run(() =>
            {
                foreach (var kind in refreshes)
                {
                    if (kind == TweakRefresh.Tray) ShellNotify.TraySettings();
                    else if (kind == TweakRefresh.Desktop) ShellNotify.RefreshDesktop();
                }
            });
        }

        
        var failed = new List<string>();
        foreach (var (def, value) in changes)
            if (EffectiveValue(def) != value) failed.Add(def.Id);

        var matched = changes.Count - failed.Count;

        AppLog.Info("tweaks",
            matched == changes.Count
                ? $"全部生效（{matched}/{changes.Count}）"
                : $"有 {failed.Count} 项未生效（{matched}/{changes.Count}）：{string.Join("、", failed)}");

        return new TweakApplyResult(changes.Count, matched, error, failed);
    }

    
    private static void WriteUser(TweakDef def, int value)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);

        
        
        
        if (def.DeleteWhenOff && value == def.OffValue)
        {
            using var existing = root.OpenSubKey(def.KeyPath, writable: true);
            existing?.DeleteValue(def.ValueName, throwOnMissingValue: false);
            return;
        }

        using var key = root.CreateSubKey(def.KeyPath, writable: true)
                        ?? throw new InvalidOperationException($"打不开注册表键：{def.KeyPath}");

        key.SetValue(def.ValueName, value, RegistryValueKind.DWord);
    }

    
    
    
    
    
    public static string BuildRegFile(IReadOnlyList<(TweakDef Def, int Value)> changes) =>
        TweakRules.BuildRegText(changes);

    
    
    
    
    public static void RestartExplorer()
    {
        try
        {
            AppLog.Info("tweaks", "重启资源管理器");

            using (var kill = Process.Start(new ProcessStartInfo("taskkill.exe", "/f /im explorer.exe")
                   {
                       UseShellExecute = false,
                       CreateNoWindow = true,
                   }))
            {
                kill?.WaitForExit(5000);
            }

            
            for (var i = 0; i < 20 && Process.GetProcessesByName("explorer").Length > 0; i++)
                System.Threading.Thread.Sleep(100);

            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Exception("tweaks", ex, "重启资源管理器");
        }
    }
}
