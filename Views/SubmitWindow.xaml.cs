using System;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// ⚠️ 遗留：网页版「提交软件」小窗口。提交页早已原生化（Pages/SubmitPage），这里现在没有入口调用，只作兜底。
/// 用系统标题栏（不搞自绘），打开就是站点 #/submit，填完直接提交。
/// </summary>
public sealed partial class SubmitWindow : Window
{
    private readonly WebViewRuntimeService _runtime = new();

    public SubmitWindow()
    {
        InitializeComponent();
        Title = "提交软件 · " + ShellConfig.AppName;
        try
        {
            AppWindow.Resize(new SizeInt32(1020, 780));
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (!System.IO.File.Exists(icon)) icon = Environment.ProcessPath ?? "";
            if (icon.Length > 0 && System.IO.File.Exists(icon)) AppWindow.SetIcon(icon);
        }
        catch { /* 尺寸设不上不影响使用 */ }

        _ = InitAsync();
    }

    private async System.Threading.Tasks.Task InitAsync()
    {
        try
        {
            var env = await _runtime.GetEnvironmentAsync();
            await Web.EnsureCoreWebView2Async(env);

            var core = Web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsZoomControlEnabled = true;
            core.NewWindowRequested += OnNewWindowRequested;
            core.NavigationCompleted += OnNavigationCompleted;
            core.Navigate(ShellConfig.SubmitPageUrl);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // 外链一律交给系统浏览器，别在这个小窗口里乱开
        e.Handled = true;
        try { _ = Windows.System.Launcher.LaunchUriAsync(new Uri(e.Uri)); } catch { }
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            _everLoaded = true;
            ErrorPanel.Visibility = Visibility.Collapsed;
            LoadingPanel.Visibility = Visibility.Collapsed;
            Web.Opacity = 1;
        }
        else if (!_everLoaded)
        {
            // 首次加载失败才整屏报错
            ShowError(DescribeWebError(e.WebErrorStatus));
        }
        else
        {
            // 已经打开过页面：站内的失败（比如提交接口返回错误）由网页自己提示
            LoadingPanel.Visibility = Visibility.Collapsed;
            Web.Opacity = 1;
        }
    }

    private bool _everLoaded;

    private void ShowError(string? tip)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        Web.Opacity = 0;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorTip.Text = tip ?? "";
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        ErrorPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Visible;
        Ring.IsActive = true;
        try { Web.CoreWebView2?.Navigate(ShellConfig.SubmitPageUrl); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    /// <summary>兜底：这个窗口打不开（缺 WebView2 / 网络问题）时，去系统浏览器打开提交页。</summary>
    private void Browser_Click(object sender, RoutedEventArgs e)
    {
        try { _ = Windows.System.Launcher.LaunchUriAsync(new Uri(ShellConfig.SubmitPageUrl)); } catch { }
        Close();
    }

    private static string DescribeWebError(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.HostNameNotResolved => "域名解析失败（检查网络或 hosts）",
        CoreWebView2WebErrorStatus.ServerUnreachable => "服务器连不上",
        CoreWebView2WebErrorStatus.Timeout => "连接超时",
        CoreWebView2WebErrorStatus.ConnectionAborted => "连接被中断",
        CoreWebView2WebErrorStatus.Disconnected => "网络已断开",
        CoreWebView2WebErrorStatus.CannotConnect => "无法连接服务器",
        CoreWebView2WebErrorStatus.OperationCanceled => "加载被取消",
        _ => status.ToString()
    };
}
