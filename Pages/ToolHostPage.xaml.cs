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



































public sealed partial class ToolHostPage : Page
{
    private ToolHostRequest? _request;

    
    private WebView2? _web;

    private CoreWebView2? _core;

    
    private string? _loadedToolId;

    
    private bool _settled;

    
    private bool _starting;

    
    private int _visit;

    
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

        
        if (_core is not null && _web is not null && _loadedToolId == _request.WebToolId)
        {
            StopIdleTimer();
            _settled = false;
            AppLog.Step("tool-host", $"恢复工具 {_request.WebToolId}");
            WebViewMemory.Resume(_web, $"工具 {_request.WebToolId}");
            return;
        }

        
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

        
        if (!ToolCarrier.KeepsAlive(toolId))
        {
            CloseWeb("离开工具页");
            return;
        }

        
        _ = SuspendThenScheduleCloseAsync(toolId, _visit);
    }

    
    private async Task SuspendThenScheduleCloseAsync(string toolId, int visit)
    {
        try { await Task.Delay(250); } catch { return; }

        
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
                
                DefaultBackgroundColor = Colors.Transparent,
            };
            Host.Children.Add(web);
            _web = web;

            var env = await App.WebRuntime.GetEnvironmentAsync();
            AppLog.Step("tool-host", "环境就绪");

            await web.EnsureCoreWebView2Async(env);
            AppLog.Step("tool-host", "内核就绪");

            
            if (_settled || !ReferenceEquals(_web, web))
            {
                CloseWeb("初始化期间离开");
                return;
            }

            var core = web.CoreWebView2;
            _core = core;

            
            core.SetVirtualHostNameToFolderMapping(
                ToolCarrier.HostName,
                request.Folder,
                CoreWebView2HostResourceAccessKind.Allow);

            
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ThemeScript());

            core.NavigationCompleted += OnNavigationCompleted;
            core.WebMessageReceived += OnWebMessageReceived;

            
            
            
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

    
    
    
    
    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            if (args.TryGetWebMessageAsString() == "ready")
                _ = sender.ExecuteScriptAsync(ThemeScript());
        }
        catch {  }
    }

    
    private void OnUi(Action action)
    {
        if (DispatcherQueue.HasThreadAccess) action();
        else DispatcherQueue.TryEnqueue(() => action());
    }

    
    
    
    
    private void CloseWeb(string reason)
    {
        StopIdleTimer();

        var web = _web;
        _web = null;
        _core = null;
        _loadedToolId = null;

        if (web is null) return;

        try { Host.Children.Remove(web); }
        catch {  }

        WebViewMemory.Close(web, $"工具页（{reason}）");
    }

    
    
    
    
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

    
    
    
    
    private void Fallback(string reason)
    {
        if (_settled) return;
        _settled = true;
        AppLog.Step("tool-host", $"回退原生页：{reason}");

        var page = _request?.NativePage ?? typeof(ToolsPage);
        var parameter = _request?.NativeParameter;

        if (Frame is not null) Frame.Navigate(page, parameter);
    }

    
    private string ThemeScript()
    {
        var dark = ActualTheme == ElementTheme.Dark
                   || (ActualTheme == ElementTheme.Default
                       && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        return $"document.documentElement.dataset.theme='{(dark ? "dark" : "light")}';";
    }
}
