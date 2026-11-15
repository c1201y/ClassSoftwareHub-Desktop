using System;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.Docs;

/// <summary>
/// 文章里点链接时走这里（需求 15 的写作口径：正文可以带超链接）。
///
/// <para>
/// 三种去向：
/// <list type="bullet">
///   <item><c>csh://…</c> —— 应用自己的页面 / 工具 / 设置 / 另一篇文章，交给主窗口统一处理；</item>
///   <item><c>ms-settings:…</c> —— 系统设置页，同样交给主窗口（它认得这个前缀）；</item>
///   <item><c>http(s) / mailto</c> —— 系统浏览器 / 邮件应用。</item>
/// </list>
/// </para>
/// <para>
/// 认不出的地址只记日志、不弹窗 —— 文章可能比应用更新，一条坏链接不该打断阅读。
/// </para>
/// </summary>
public static class DocLinks
{
    /// <summary>⛔ 只在 UI 线程调用（链接点击事件本身就在 UI 线程）。</summary>
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
