using System;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 工具宿主页（2026-10-23）：把本地工具包（HTML）当页面跑。
///
/// <para>
/// 设计要点（都是 Nick 定的口径）：
/// <list type="bullet">
///   <item><b>Web 优先、原生兜底</b>：什么时候用哪边由 <see cref="ToolCarrier"/> 一次判定，本页只管跑。</item>
///   <item><b>不做超时</b>（2026-10-23 Nick 明确）：装了 WebView2 就加载网页，没装就直接走原生，
///         不搞"等几秒再切"那一套。</item>
///   <item><b>只保留异常回退</b>：初始化抛异常或导航明确失败才静默切原生页 ——
///         那是"加载不了"，不是"慢"。⛔ 不弹提示、不加角标：用户不该知道背后有两套实现。</item>
///   <item><b>只此一个 WebView2</b>：切工具时整页重建，不堆实例。</item>
/// </list>
/// </para>
/// <para>
/// ⚠️ 本地 html 不能用 <c>file://</c> 加载（会被同源策略拦，fetch 本地资源也拿不到），
/// 统一走 <c>SetVirtualHostNameToFolderMapping</c> 映射成 https 虚拟主机。
/// </para>
/// </summary>
public sealed partial class ToolHostPage : Page
{
    private ToolHostRequest? _request;

    /// <summary>已经决定去向了（成功或回退），避免重复导航。</summary>
    private bool _settled;

    public ToolHostPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _request = e.Parameter as ToolHostRequest;
        if (_request is null || string.IsNullOrEmpty(_request.EntryFile))
        {
            Fallback("没有工具包参数");
            return;
        }

        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            // 背景色透明：加载那一下露出来的是页面底色，而不是一块白
            Web.DefaultBackgroundColor = Colors.Transparent;

            var env = await App.WebRuntime.GetEnvironmentAsync();
            AppLog.Step("tool-host", "环境就绪");

            await Web.EnsureCoreWebView2Async(env);
            AppLog.Step("tool-host", "内核就绪");

            if (_settled) return;

            var core = Web.CoreWebView2;

            // 本地目录 → https://csh.tools/...（只允许读，不给写权限）
            core.SetVirtualHostNameToFolderMapping(
                ToolCarrier.HostName,
                _request!.Folder,
                CoreWebView2HostResourceAccessKind.Allow);

            // 页面脚本一执行就把主题写进 document，避免"先白后黑"闪一下
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ThemeScript());

            core.NavigationCompleted += OnNavigationCompleted;
            core.WebMessageReceived += OnWebMessageReceived;

            // 浏览器进程真的挂了（不是"慢"、也不是 GPU 子进程重启）才回退。
            // ⚠️ 这条不是超时：安全软件拦 WebView2 时就是这个症状 —— 子进程被反复杀掉，
            //    NavigationCompleted 永远不会来，只有 BrowserProcessExited 会报。
            core.ProcessFailed += (_, a) =>
            {
                if (a.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited) return;

                AppLog.Warn("tool-host", "浏览器进程退出，回退原生页");
                OnUi(() => Fallback("浏览器进程退出"));
            };
            core.ProcessFailed += (_, a) => AppLog.Warn("tool-host", $"浏览器进程异常：{a.ProcessFailedKind}");

            var fileName = System.IO.Path.GetFileName(_request.EntryFile);
            var url = $"https://{ToolCarrier.HostName}/{Uri.EscapeDataString(fileName)}";
            AppLog.Step("tool-host", $"导航到 {url}（根目录 {_request.Folder}）");

            core.Navigate(url);
        }
        catch (Exception ex)
        {
            AppLog.Exception("tool-host", ex, "初始化工具宿主");
            Fallback("初始化异常");
        }
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess)
        {
            AppLog.Step("tool-host", "工具页已就绪");
            return;
        }

        AppLog.Warn("tool-host", $"工具页加载失败：{args.WebErrorStatus}");
        Fallback("加载失败");
    }

    /// <summary>
    /// 页面主动发消息过来（目前只用来做"我起来了，把当前主题给我"）。
    /// ⚠️ 以后要扩展桥接就在这里加分支，别在页面里直接摸宿主。
    /// </summary>
    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            if (args.TryGetWebMessageAsString() == "ready")
                _ = sender.ExecuteScriptAsync(ThemeScript());
        }
        catch { /* 桥接失败不该影响工具本身 */ }
    }

    /// <summary>切回 UI 线程（WebView2 的事件可能在别的线程上回来）。</summary>
    private void OnUi(Action action)
    {
        if (DispatcherQueue.HasThreadAccess) action();
        else DispatcherQueue.TryEnqueue(() => action());
    }

    /// <summary>
    /// 静默退回原生页。
    /// ⛔ 别在这里加任何提示 —— 降级对用户是"无感"的（Nick 明确要求）。
    /// </summary>
    private void Fallback(string reason)
    {
        if (_settled) return;
        _settled = true;
        AppLog.Step("tool-host", $"回退原生页：{reason}");

        var page = _request?.NativePage ?? typeof(ToolsPage);
        var parameter = _request?.NativeParameter;

        if (Frame is not null) Frame.Navigate(page, parameter);
    }

    /// <summary>把深浅色注入网页（网页那边只认 <c>document.documentElement.dataset.theme</c>）。</summary>
    private string ThemeScript()
    {
        var dark = ActualTheme == ElementTheme.Dark
                   || (ActualTheme == ElementTheme.Default
                       && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        return $"document.documentElement.dataset.theme='{(dark ? "dark" : "light")}';";
    }
}
