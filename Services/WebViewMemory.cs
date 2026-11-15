using System;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace ClassSoftwareHub.Desktop.Services;



























public static class WebViewMemory
{
    
    public const int IdleCloseMs = 10 * 60 * 1000;

    
    
    
    
    
    
    
    
    
    
    
    public static async Task<bool> SuspendAsync(WebView2? view, string tag)
    {
        if (view is null) return false;

        try
        {
            var core = view.CoreWebView2;
            if (core is null) return false;

            
            view.Visibility = Visibility.Collapsed;

            try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; }
            catch {  }

            
            try { await Task.Delay(150); } catch { }

            try
            {
                await core.TrySuspendAsync();
                AppLog.Step("webview", $"{tag} 已挂起");
                return true;
            }
            catch (Exception ex)
            {
                
                
                AppLog.Debug("webview", $"{tag} 已降到低内存档位，冻结未生效：{ex.Message}");
                return false;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("webview", $"{tag} 挂起失败：{ex.Message}");
            return false;
        }
    }

    
    
    
    
    public static void Resume(WebView2? view, string tag)
    {
        if (view is null) return;

        try
        {
            var core = view.CoreWebView2;
            if (core is null) return;

            core.Resume();

            try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal; }
            catch { }

            view.Visibility = Visibility.Visible;

            AppLog.Step("webview", $"{tag} 已恢复");
        }
        catch (Exception ex)
        {
            AppLog.Warn("webview", $"{tag} 恢复失败：{ex.Message}");
        }
    }

    
    
    
    public static void Close(WebView2? view, string tag)
    {
        if (view is null) return;

        try
        {
            view.Close();
            AppLog.Step("webview", $"{tag} 已关闭");
        }
        catch (Exception ex)
        {
            AppLog.Warn("webview", $"{tag} 关闭失败：{ex.Message}");
        }
    }
}
