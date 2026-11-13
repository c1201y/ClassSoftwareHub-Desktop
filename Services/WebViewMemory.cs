using System;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// WebView2 的内存回收（2026-11-13）。
///
/// <para>
/// 起因：用户反馈进程里挂着一堆 <c>WebView2: 数学实验室</c> 的渲染进程，每条约 27～44 MB。
/// <see cref="MemoryTrimmer"/> 管不到它们 —— 那是**另一个进程**，对本进程做
/// <c>GC.Collect</c> / <c>EmptyWorkingSet</c> 一点用都没有。
/// </para>
///
/// <para>
/// 能动的只有两件事，且都落在 <see cref="CoreWebView2"/> 上：
/// <list type="number">
///   <item><b>挂起</b>（<see cref="CoreWebView2.TrySuspendAsync"/>）：把页面冻结、堆与缓存压到最低，
///         渲染进程还在但基本不占内存；<see cref="CoreWebView2.Resume"/> 后页面状态原样还在。
///         ⚠️ **只在控件不可见时可用**，所以必须先 <c>Visibility = Collapsed</c>，顺序反了会抛。</item>
///   <item><b>关闭</b>（<see cref="WebView2.Close"/>）：控件连同渲染进程一起释放。
///         ⚠️ 关掉的控件**不能再用**，要重新 <c>new WebView2()</c> 加回视觉树 —— 所以调用方得自己持有容器，
///         不能是 XAML 里写死的那个实例。</item>
/// </list>
/// </para>
///
/// <para>
/// 策略（Nick 定）：一次性查阅类工具离开即关；「数学实验室」离开先挂起，
/// <see cref="IdleCloseMs"/> 内没回来才关。网页浮层（<c>MainWindow.WebSheet</c>）常驻一个，只挂起不关。
/// </para>
/// </summary>
public static class WebViewMemory
{
    /// <summary>挂起之后多久没再打开就关掉（10 分钟）。</summary>
    public const int IdleCloseMs = 10 * 60 * 1000;

    /// <summary>
    /// 挂起：先藏起来，再把内存目标压到 Low，最后冻结。
    /// 返回 true = 真的冻结了；false = 只降到了低内存档位（冻结没被接受）。
    ///
    /// <para>
    /// ⚠️ 2026-11-13 实测：<see cref="CoreWebView2.TrySuspendAsync"/> 在"控件刚离开视觉树"那一下会抛
    /// 「组或资源的状态不是执行请求操作的正确状态」—— 因为 <c>Visibility</c> 的改动要等一次布局才传到
    /// 底层控制器。所以这里藏完之后先等一小会儿，再冻结；仍然失败也不算错，Low 档位本身已经把
    /// 渲染进程从 ~104 MB 压到 ~25 MB（实测）。
    /// </para>
    /// </summary>
    public static async Task<bool> SuspendAsync(WebView2? view, string tag)
    {
        if (view is null) return false;

        try
        {
            var core = view.CoreWebView2;
            if (core is null) return false;

            // ⚠️ 顺序不能反：TrySuspend 要求控件此刻不可见
            view.Visibility = Visibility.Collapsed;

            try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; }
            catch { /* 旧运行时没有这个枚举值，退化成只挂起 */ }

            // 等一次布局，把"不可见"真正传给底层控制器（少了这一下 TrySuspend 会直接抛）
            try { await Task.Delay(150); } catch { }

            try
            {
                await core.TrySuspendAsync();
                AppLog.Step("webview", $"{tag} 已挂起");
                return true;
            }
            catch (Exception ex)
            {
                // 不是故障：冻结只在"控件确实不可见"时才被接受，页面刚离开视觉树时经常不满足。
                // 内存目标已经是 Low，能省下的大头已经省了；退出时还会把整个控件关掉。
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

    /// <summary>
    /// 恢复：解冻 + 内存目标恢复 Normal + 重新显示。
    /// 没挂起过也安全（<see cref="CoreWebView2.Resume"/> 对活动页面是空操作）。
    /// </summary>
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

    /// <summary>
    /// 关掉控件（渲染进程随之退出）。关掉的控件不能复用 —— 调用方要把自己的引用一并清掉。
    /// </summary>
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
