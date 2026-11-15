using System;
using System.Diagnostics;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// Microsoft Store **本体**的特例：这台电脑要是把商店卸载/精简掉了，站点给的商店链接
/// 点开是没有任何反应的（找谁安装？），所以这里提供「把商店装回来」的官方办法。
///
/// 手法就是系统自带的 `wsreset.exe -i`（Win10 1809+ / Win11 通用，会把 Microsoft Store
/// 重新下载安装一遍；不需要我们自己下什么安装包）。
/// </summary>
public static class StoreRepair
{
    /// <summary>微软商店自己的产品 ID（apps/9wzdncrfjbmp.json 就是它）。</summary>
    public const string StoreProductId = "9WZDNCRFJBMP";

    /// <summary>这个条目是不是「微软商店本体」。</summary>
    public static bool IsStoreItself(string? appId, string? url)
    {
        if (!string.IsNullOrWhiteSpace(appId) &&
            appId.Trim().Equals(StoreProductId, StringComparison.OrdinalIgnoreCase))
            return true;

        // 兜底：链接里带着商店自己的产品 ID
        return !string.IsNullOrWhiteSpace(url) &&
               url.Contains(StoreProductId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>用官方方式把 Microsoft Store 装回来 / 修复。返回是否成功启动（不代表装完了）。</summary>
    public static bool Reinstall()
    {
        // 首选：wsreset -i —— 系统自带，会重新注册并重新下载商店
        try
        {
            Process.Start(new ProcessStartInfo("wsreset.exe", "-i")
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch { /* 试下一种 */ }

        // 兜底：重新注册商店包（不需要管理员）
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
