using System;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>应用链接指向哪里。</summary>
public enum AppUriKind
{
    /// <summary>设置页的某个分组（<c>csh://settings/update</c>）。</summary>
    Settings,

    /// <summary>主界面里的某一页（<c>csh://page/downloads</c>，tag 跟导航项一致）。</summary>
    Page,

    /// <summary>某个内置工具（<c>csh://tool/function-plot</c>，id 跟 <c>ToolCatalog</c> 一致）。</summary>
    Tool,

    /// <summary>文档库里的一篇文章（<c>csh://article/xxx</c>，文档库上线后才有实际去处）。</summary>
    Article,

    /// <summary>交给系统的协议（<c>ms-settings:windowsupdate</c> 这类）。</summary>
    System,
}

/// <summary>解析结果。<see cref="Target"/> 是用途内的标识，<c>Raw</c> 是原始字符串。</summary>
public sealed record AppUriTarget(AppUriKind Kind, string Target, string Raw);

/// <summary>
/// 应用链接（<c>csh://</c>）的解析与构造（纯函数，不碰注册表、不碰界面）。
///
/// <para>
/// 需求 1:18（2026-10-30）：文档库的文章里会写链接，可能是「跳到另一篇文章」「跳到某个设置项」，
/// 也可能是「跳到 Windows 设置」。这些链接写在文章正文里（将来还有网页版），所以需要一个
/// 稳定的地址格式，而不是靠命令行参数。
/// </para>
///
/// <para>
/// 格式：
/// <code>
/// csh://settings/update      设置页 → 更新分组
/// csh://page/downloads       主界面 → 软件下载
/// csh://tool/function-plot   打开函数图像
/// csh://article/clear-c      文档库文章（文档库上线后生效）
/// ms-settings:windowsupdate  系统设置，原样交给 Windows
/// </code>
/// </para>
///
/// <para>
/// ⚠️ 只认这几种。<c>Target</c> 不做白名单校验（「update 是不是真的存在」由落地的那一层判断，
///    认不出就停在页面顶部）—— 白名单放这儿会让"加一个设置分组要改两处文件"。
/// </para>
/// </summary>
public static class AppUri
{
    /// <summary>自定义协议名。注册表里也用它（见 <c>Services/AppLinks</c>）。</summary>
    public const string Scheme = "csh";

    /// <summary>交给系统的协议前缀（Windows 设置页全部是 <c>ms-settings:</c>）。</summary>
    public const string SystemSchemePrefix = "ms-settings:";

    /// <summary>
    /// 解析一段链接。认不出返回 <c>null</c>（调用方决定忽略还是提示）。
    /// <para>
    /// 宽容两处：大小写不敏感（<c>CSH://Settings/Update</c> 也认）；也接受不带双斜杠的写法
    /// （<c>csh:settings/update</c>）—— 手写链接时很容易漏那两条斜杠，为此报错不值得。
    /// </para>
    /// </summary>
    public static AppUriTarget? Parse(string? raw)
    {
        var text = (raw ?? "").Trim().Trim('"');
        if (text.Length == 0) return null;

        if (text.StartsWith(SystemSchemePrefix, StringComparison.OrdinalIgnoreCase))
            return new AppUriTarget(AppUriKind.System, text[SystemSchemePrefix.Length..], text);

        if (!text.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase)) return null;

        // 统一成 csh://<用途>/<标识> 再交给 Uri 解析：
        // 自定义协议不带 "//" 时 Uri 会把整串当成 path（"settings/update"），带 "//" 时 host 才是用途。
        var rest = text[(Scheme.Length + 1)..];
        if (!rest.StartsWith("//", StringComparison.Ordinal)) rest = "//" + rest;

        if (!Uri.TryCreate(Scheme + ":" + rest, UriKind.Absolute, out var uri)) return null;

        var kind = uri.Host.ToLowerInvariant() switch
        {
            "settings" => AppUriKind.Settings,
            "page" => AppUriKind.Page,
            "tool" => AppUriKind.Tool,
            "article" => AppUriKind.Article,
            _ => (AppUriKind?)null,
        };
        if (kind is null) return null;

        // 路径去掉开头的 "/"；允许再带一层（csh://settings/appearance/theme 取 "appearance/theme"）
        var target = uri.AbsolutePath.Trim('/');
        if (target.Length == 0) return null;

        return new AppUriTarget(kind.Value, target, text);
    }

    /// <summary>构造设置跳转链接（写进文章里的那种形式）。</summary>
    public static string ForSettings(string section) => $"{Scheme}://settings/{section}";

    /// <summary>构造页面跳转链接。</summary>
    public static string ForPage(string tag) => $"{Scheme}://page/{tag}";

    /// <summary>构造工具跳转链接。</summary>
    public static string ForTool(string id) => $"{Scheme}://tool/{id}";

    /// <summary>构造文章跳转链接（文档库上线后使用）。</summary>
    public static string ForArticle(string id) => $"{Scheme}://article/{id}";
}
