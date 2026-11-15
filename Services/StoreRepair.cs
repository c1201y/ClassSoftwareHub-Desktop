using System;
using System.Diagnostics;

namespace ClassSoftwareHub.Desktop.Services;








public static class StoreRepair
{
    
    public const string StoreProductId = "9WZDNCRFJBMP";

    
    public static bool IsStoreItself(string? appId, string? url)
    {
        if (!string.IsNullOrWhiteSpace(appId) &&
            appId.Trim().Equals(StoreProductId, StringComparison.OrdinalIgnoreCase))
            return true;

        
        return !string.IsNullOrWhiteSpace(url) &&
               url.Contains(StoreProductId, StringComparison.OrdinalIgnoreCase);
    }

    
    public static bool Reinstall()
    {
        
        try
        {
            Process.Start(new ProcessStartInfo("wsreset.exe", "-i")
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch {  }

        
        try
        {
            const string script =
                "Get-AppxPackage *WindowsStore* | ForEach-Object { " +
                "Add-AppxPackage -DisableDevelopmentMode -Register ($_.InstallLocation + '\\AppxManifest.xml') " +
                "-ErrorAction SilentlyContinue }";

            Process.Start(new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"" + script + "\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
