using System;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.Docs;
















public static class DocLinks
{
    
    public static async Task OpenAsync(string? url)
    {
        var text = (url ?? "").Trim();
        if (text.Length == 0) return;

        if (text.StartsWith("csh://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith(AppUri.SystemSchemePrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (App.MainWindow is { } window) await window.HandleAppUriAsync(text);
            return;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https" or "mailto")) return;

        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(uri))
                AppLog.Warn("docs", "系统没有打开链接：" + text);
        }
        catch (Exception ex)
        {
            AppLog.Warn("docs", $"打开链接失败（{text}）：{ex.Message}");
        }
    }
}
