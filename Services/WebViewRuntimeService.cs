using Microsoft.Web.WebView2.Core;

namespace ClassSoftwareHub.Desktop.Services;


public sealed class WebViewRuntimeService
{
    public bool IsAvailable { get; private set; }
    public string? Version { get; private set; }
    public string? LastError { get; private set; }

    private CoreWebView2Environment? _environment;

    public static string UserDataDir { get; } = Path.Combine(SettingsStore.Dir, "WebView2");

    
    
    
    
    
    
    
    public static bool IsRuntimeInstalled()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch
        {
            return false;
        }
    }

    
    public bool Probe()
    {
        try
        {
            Version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            IsAvailable = !string.IsNullOrWhiteSpace(Version);
            LastError = null;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            Version = null;
            LastError = ex.Message;
        }
        return IsAvailable;
    }

    public async Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        var options = new CoreWebView2EnvironmentOptions
        {
            
            Language = "zh-CN",
            AreBrowserExtensionsEnabled = false
        };
#if DEBUG
        
        options.AdditionalBrowserArguments = "--remote-debugging-port=9333";
#endif
        _environment ??= await CoreWebView2Environment.CreateWithOptionsAsync(
            null,
            UserDataDir,
            options);
        return _environment;
    }
}
