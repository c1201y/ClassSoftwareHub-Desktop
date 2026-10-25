using System;
using System.IO;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>一个工具由谁来渲染。</summary>
public enum ToolCarrierKind
{
    /// <summary>网页内核（功能更全，需 WebView2 运行时 + 本地工具包）。</summary>
    Web,

    /// <summary>原生 WinUI 3 页面（兜底，**永远是可用**的那一个）。</summary>
    Native,
}

/// <summary>交给 <c>ToolHostPage</c> 的导航参数。</summary>
/// <param name="WebToolId">工具 id（与内容里的目录名一致）。</param>
/// <param name="EntryFile">入口 html 的完整路径。</param>
/// <param name="Folder">入口所在目录 —— 会映射成虚拟主机根。</param>
/// <param name="NativePage">退回去时导航到哪个原生页。</param>
/// <param name="NativeParameter">退回去时带给原生页的参数。</param>
public sealed record ToolHostRequest(
    string WebToolId, string EntryFile, string Folder, Type NativePage, object? NativeParameter);

/// <summary>
/// 工具载体选择（2026-10-23）。
///
/// <para>
/// 策略是 Nick 定的：**Web 优先，原生兜底，而且用户无感** ——
/// Web 版能力更全；检测不到 WebView2 运行时、或者本地还没有这个工具包时，
/// 静默走原生页，不弹任何提示（⛔ 别加 InfoBar / 角标：那是把内部实现暴露给用户）。
/// </para>
/// <para>
/// ⚠️ 两个条件必须**同时**满足才算 Web 可用：
/// <list type="number">
///   <item>系统装了 WebView2 运行时；</item>
///   <item>本地真有这个工具的 <c>index.html</c> —— 内容是从网上下载的，首启时可能还没有。</item>
/// </list>
/// 只满足一条就硬上 Web，结果是白屏（比走原生糟得多）。
/// </para>
/// </summary>
public static class ToolCarrier
{
    /// <summary>虚拟主机名。⚠️ 改这里要同步改 html 里的资源相对路径思路（用相对路径就不用改）。</summary>
    public const string HostName = "csh.tools";

    /// <summary>
    /// 工具包根目录。
    /// ⚠️ 故意**不放在 content 下面**：那个目录由 <c>GithubContentSync</c> 整体镜像，
    /// 仓库里没有的文件会被删掉 —— 本地工具包放进去会被清空。
    /// </summary>
    public static string Root => Path.Combine(AppPaths.DataDir, "tools");

    /// <summary>兜底位置：内容仓库里若也开始下发工具，就从这里取（先查本地包，再查内容包）。</summary>
    private static string ContentRoot => Path.Combine(AppPaths.DataDir, "content", "工具");

    /// <summary>
    /// 随应用安装包附带的网页工具包根目录（程序目录下 <c>WebTools/</c>）。
    /// <para>
    /// ⚠️ 与 <see cref="Root"/> 不同：这里的内容随安装包发出去、出厂即带，不会被内容同步清掉，
    /// 适合"联网前就有、不依赖下载"的工具（例如数学函数绘图）。<see cref="EntryOf"/> 把它放在最后查，
    /// 这样内容仓库若日后下发了同名工具，会以仓库版为准。
    /// </para>
    /// </summary>
    public static string BundledRoot => Path.Combine(AppContext.BaseDirectory, "WebTools");

    /// <summary>这个工具的入口文件在哪；没有就返回 null。</summary>
    public static string? EntryOf(string? webToolId)
    {
        if (string.IsNullOrWhiteSpace(webToolId)) return null;

        // 顺序：本地数据目录 → 内容仓库兜底 → 随包附带。后者只在前面都没有时才兜底。
        foreach (var root in new[] { Root, ContentRoot, BundledRoot })
        {
            try
            {
                var entry = Path.Combine(root, webToolId, "index.html");
                if (File.Exists(entry)) return entry;
            }
            catch { /* 路径非法就当没有 */ }
        }

        return null;
    }

    /// <summary>能不能用 Web 渲染。</summary>
    public static bool CanHost(string? webToolId) =>
        WebViewRuntimeService.IsRuntimeInstalled() && EntryOf(webToolId) is not null;

    /// <summary>
    /// 决定这个工具走哪条路。Web 可用就返回导航参数，否则返回 null（调用方走原生页）。
    /// </summary>
    public static ToolHostRequest? RequestFor(string? webToolId, Type nativePage, object? nativeParameter)
    {
        var entry = EntryOf(webToolId);
        if (entry is null) return null;
        if (!WebViewRuntimeService.IsRuntimeInstalled()) return null;

        return new ToolHostRequest(
            webToolId!,
            entry,
            Path.GetDirectoryName(entry) ?? Root,
            nativePage,
            nativeParameter);
    }
}
