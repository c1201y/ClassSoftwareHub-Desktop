using Microsoft.Web.WebView2.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>检测系统 WebView2 运行时（Evergreen），并提供共享环境。</summary>
public sealed class WebViewRuntimeService
{
    public bool IsAvailable { get; private set; }
    public string? Version { get; private set; }
    public string? LastError { get; private set; }

    private CoreWebView2Environment? _environment;

    public static string UserDataDir { get; } = Path.Combine(SettingsStore.Dir, "WebView2");

    /// <summary>同步探测（启动时调用一次）。</summary>
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
            // 只加载远程站点；不使用任何本地回退页面
            Language = "zh-CN",
            AreBrowserExtensionsEnabled = false
        };
#if DEBUG
        // 开发期：暴露一个 CDP 调试口，方便外壳侧排查注入/样式问题
        options.AdditionalBrowserArguments = "--remote-debugging-port=9333";
#endif
        _environment ??= await CoreWebView2Environment.CreateWithOptionsAsync(
            null,
            UserDataDir,
            options);
        return _environment;
    }
}
