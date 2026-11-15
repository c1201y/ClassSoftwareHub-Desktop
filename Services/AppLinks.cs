using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;























public static class AppLinks
{
    
    private const string ProtocolKeyPath = @"Software\Classes\" + AppUri.Scheme;

    
    private static string QueueFile => Path.Combine(AppPaths.DataDir, "pending-link.txt");

    
    private static readonly TimeSpan QueueMaxAge = TimeSpan.FromSeconds(30);

    

    
    
    
    
    
    
    
    
    
    
    
    
    
    public static bool RegisterProtocol()
    {
        try
        {
            var exe = ExecutablePath();
            if (string.IsNullOrEmpty(exe)) return false;

            var command = $"\"{exe}\" \"%1\"";

            using var key = Registry.CurrentUser.CreateSubKey(ProtocolKeyPath, writable: true);
            if (key is null) return false;

            
            if (key.GetValue(null) is string existing && existing.Equals(command, StringComparison.OrdinalIgnoreCase))
            {
                using var icon = key.OpenSubKey("DefaultIcon", writable: false);
                if (icon?.GetValue(null) is string iconValue && iconValue.Equals($"\"{exe}\",0", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            key.SetValue(null, $"URL:{ShellConfig.AppName} Link");
            key.SetValue("URL Protocol", "");

            
            using (var icon = key.CreateSubKey("DefaultIcon", writable: true))
                icon?.SetValue(null, $"\"{exe}\",0");

            using (var open = key.CreateSubKey(@"shell\open\command", writable: true))
                open?.SetValue(null, command);

            AppLog.Step("links", $"已注册 {AppUri.Scheme}:// 协议 → {exe}");
            return true;
        }
        catch (Exception ex)
        {
            
            AppLog.Warn("links", "注册协议失败：" + ex.Message);
            return false;
        }
    }

    
    public static bool UnregisterProtocol()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(ProtocolKeyPath, throwOnMissingSubKey: false);
            AppLog.Step("links", $"已撤销 {AppUri.Scheme}:// 协议注册");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("links", "撤销协议注册失败：" + ex.Message);
            return false;
        }
    }

    

    
    
    
    
    public static string? ExtractFromCommandLine()
    {
        try
        {
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                var text = (arg ?? "").Trim().Trim('"');
                if (text.Length == 0) continue;

                
                if (text.StartsWith(AppUri.Scheme + ":", StringComparison.OrdinalIgnoreCase)) return text;
            }
        }
        catch
        {
            
        }

        return null;
    }

    

    
    
    
    
    public static bool Queue(string uri)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);

            
            var text = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
                       + Environment.NewLine + uri;

            
            var tmp = QueueFile + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            File.Move(tmp, QueueFile, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("links", "传递链接失败：" + ex.Message);
            return false;
        }
    }

    
    
    
    public static string? TryTakeQueued()
    {
        try
        {
            if (!File.Exists(QueueFile)) return null;

            string text;
            try
            {
                text = File.ReadAllText(QueueFile, Encoding.UTF8);
            }
            catch (IOException)
            {
                
                return null;
            }

            File.Delete(QueueFile);

            var lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length < 2) return null;

            if (!long.TryParse(lines[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stamp))
                return null;

            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(stamp);
            if (age < TimeSpan.Zero || age > QueueMaxAge)
            {
                AppLog.Info("links", $"丢弃过期待处理链接（{age.TotalSeconds:0} 秒前留下）");
                return null;
            }

            var uri = string.Join("\n", lines, 1, lines.Length - 1).Trim();
            return uri.Length > 0 ? uri : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("links", "读取待处理链接失败：" + ex.Message);
            return null;
        }
    }

    

    
    private static string? ExecutablePath()
    {
        try
        {
            
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path)) return path;

            using var proc = Process.GetCurrentProcess();
            return proc.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}
