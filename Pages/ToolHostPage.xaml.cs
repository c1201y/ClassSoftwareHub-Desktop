using System;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
///
/// <para>
/// 2026-11-13 补上"真的把实例收掉"（用户反馈进程里同时挂着三个 <c>数学实验室</c> 渲染进程）：
/// <list type="bullet">
///   <item>控件改由代码创建（XAML 里只留一个 <c>Grid</c> 容器）——
///         <c>WebView2.Close()</c> 之后的控件不能复用，写死在 XAML 里就再也建不回来。</item>
///   <item>离开本页：一次性工具**直接关**；<see cref="ToolCarrier.KeepsAlive"/> 认定的
///         「数学实验室」**先挂起**（页面状态留着），<see cref="WebViewMemory.IdleCloseMs"/>
///         之内没重入再关。判定与实现见 <see cref="WebViewMemory"/>。</item>
///   <item>页面因此要缓存（XAML 里 <c>NavigationCacheMode="Required"</c>），
///         否则页面对象一离开就被丢掉，挂起的控件没地方待。</item>
/// </list>
/// 之前没有 <c>Close</c>，全靠 GC —— 托管对象没被回收之前，渲染进程就一直挂着。
/// </para>
///
/// <para>
/// ⚠️ 本地 html 不能用 <c>file://</c> 加载（会被同源策略拦，fetch 本地资源也拿不到），
/// 统一走 <c>SetVirtualHostNameToFolderMapping</c> 映射成 https 虚拟主机。
/// </para>
/// </summary>
public sealed partial class ToolHostPage : Page
{
    private ToolHostRequest? _request;

    /// <summary>当前这个 WebView2（代码创建；关掉后就置空）。</summary>
    private WebView2? _web;

    private CoreWebView2? _core;

    /// <summary>当前跑的是哪个工具 —— 页面被缓存复用时要靠它判断"还是不是同一页"。</summary>
    private string? _loadedToolId;

    /// <summary>已经决定去向（成功或回退），避免重复导航。</summary>
    private bool _settled;

    /// <summary>初始化进行中（防重入）。</summary>
    private bool _starting;

    /// <summary>第几次进入本页 —— 挂起前用来判断"这期间又回来了没有"。</summary>
    private int _visit;

    /// <summary>挂起之后起的一次性关闭计时器。</summary>
    private DispatcherQueueTimer? _idleTimer;

    public ToolHostPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _visit++;
        _request = e.Parameter as ToolHostRequest;
        if (_request is null || string.IsNullOrEmpty(_request.EntryFile))
        {
            Fallback("没有工具包参数");
            return;
        }

        // 从缓存里回来：还是同一个工具、内核也还活着 → 解冻即可（这就是挂起换来的"秒回"）
        if (_core is not null && _web is not null && _loadedToolId == _request.WebToolId)
        {
            StopIdleTimer();
            _settled = false;
            AppLog.Step("tool-host", $"恢复工具 {_request.WebToolId}");
            WebViewMemory.Resume(_web, $"工具 {_request.WebToolId}");
            return;
        }

        // 换工具（或上一次已经关掉）：把旧的收干净再重来
        CloseWeb("换工具或重新进入");
        _settled = false;

        if (IsLoaded) _ = StartAsync();
        else Loaded += OnFirstLoaded;
    }

    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        _ = StartAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        if (_web is null || _core is null) return;

        var toolId = _loadedToolId ?? "";

        // 一次性工具：离开就关 —— 它们的渲染进程比重新加载贵
        if (!ToolCarrier.KeepsAlive(toolId))
        {
            CloseWeb("离开工具页");
            return;
        }

        // 数学实验室：等切页动画走完再挂起，免得在转场里闪一下空白
        _ = SuspendThenScheduleCloseAsync(toolId, _visit);
    }

    /// <summary>错开转场 → 挂起 → 起关闭计时。</summary>
    private async Task SuspendThenScheduleCloseAsync(string toolId, int visit)
    {
        try { await Task.Delay(250); } catch { return; }

        // 这 250 毫秒里用户又回来了（或已经关了）→ 什么都不做
        if (visit != _visit || _web is null || _core is null) return;

        var frozen = await WebViewMemory.SuspendAsync(_web, $"工具 {toolId}");

        if (visit != _visit || _web is null) return;
        StartIdleTimer(toolId, visit, frozen);
    }

    private async Task StartAsync()
    {
        var request = _request;
        if (request is null || _starting) return;

        _starting = true;
        try
        {
            var web = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                // 背景色透明：加载那一下露出来的是页面底色，而不是一块白
                DefaultBackgroundColor = Colors.Transparent,
            };
            Host.Children.Add(web);
            _web = web;

            var env = await App.WebRuntime.GetEnvironmentAsync();
            AppLog.Step("tool-host", "环境就绪");

            await web.EnsureCoreWebView2Async(env);
            AppLog.Step("tool-host", "内核就绪");

            // 初始化期间用户已经走了 / 已经回退 → 把刚建的这个收掉，别留孤儿进程
            if (_settled || !ReferenceEquals(_web, web))
            {
                CloseWeb("初始化期间离开");
                return;
            }

            var core = web.CoreWebView2;
            _core = core;

            // 本地目录 → https://csh.tools/...（只允许读，不给写权限）
            core.SetVirtualHostNameToFolderMapping(
                ToolCarrier.HostName,
                request.Folder,
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

            var fileName = System.IO.Path.GetFileName(request.EntryFile);
            var url = $"https://{ToolCarrier.HostName}/{Uri.EscapeDataString(fileName)}";

            _loadedToolId = request.WebToolId;
            AppLog.Step("tool-host", $"导航到 {url}（根目录 {request.Folder}）");

            core.Navigate(url);
        }
        catch (Exception ex)
        {
            AppLog.Exception("tool-host", ex, "初始化工具宿主");
            CloseWeb("初始化异常");
            Fallback("初始化异常");
        }
        finally
        {
            _starting = false;
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
    /// 关掉当前 WebView2（渲染进程随之退出）并清空引用。
    /// ⚠️ 关掉的控件不能复用 —— 下次进本页会重新 <c>new WebView2()</c>。
    /// </summary>
    private void CloseWeb(string reason)
    {
        StopIdleTimer();

        var web = _web;
        _web = null;
        _core = null;
        _loadedToolId = null;

        if (web is null) return;

        try { Host.Children.Remove(web); }
        catch { /* 已经不在树上就算了 */ }

        WebViewMemory.Close(web, $"工具页（{reason}）");
    }

    /// <summary>
    /// 挂起之后起的一次性计时：到点还没重入就把控件关掉。
    /// <paramref name="visit"/> 是起表时的进入序号 —— 对不上说明这期间回来过，不能关。
    /// </summary>
    private void StartIdleTimer(string toolId, int visit, bool frozen = false)
    {
        StopIdleTimer();

        try
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(WebViewMemory.IdleCloseMs);
            timer.IsRepeating = false;
            timer.Tick += (s, _) =>
            {
                if (s is DispatcherQueueTimer t) t.Stop();
                _idleTimer = null;

                if (visit != _visit || _web is null) return;

                CloseWeb($"工具 {toolId} 闲置 {WebViewMemory.IdleCloseMs / 60000} 分钟");
            };

            _idleTimer = timer;
            timer.Start();

            AppLog.Step("webview", $"工具 {toolId} {(frozen ? "已冻结" : "已降到低内存档位")}，" +
                                   $"{WebViewMemory.IdleCloseMs / 60000} 分钟内不回来就关掉");
        }
        catch (Exception ex)
        {
            AppLog.Warn("webview", "起关闭计时失败：" + ex.Message);
        }
    }

    private void StopIdleTimer()
    {
        try
        {
            if (_idleTimer is null) return;
            _idleTimer.Stop();
            _idleTimer = null;
        }
        catch { }
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
