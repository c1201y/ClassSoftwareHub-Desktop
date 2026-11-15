using System;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace ClassSoftwareHub.Desktop.Views;





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
        catch {  }

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
            
            ShowError(DescribeWebError(e.WebErrorStatus));
        }
        else
        {
            
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

    
    private void Browser_Click(object sender, RoutedEventArgs e)
    {
        try { _ = Windows.System.Launcher.LaunchUriAsync(new Uri(ShellConfig.SubmitPageUrl)); } catch { }
        Close();
    }

    private static string DescribeWebError(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.HostNameNotResolved => "域名解析失败（网络或 hosts 配置有误）",
        CoreWebView2WebErrorStatus.ServerUnreachable => "服务器不可达",
        CoreWebView2WebErrorStatus.Timeout => "连接超时",
        CoreWebView2WebErrorStatus.ConnectionAborted => "连接被中断",
        CoreWebView2WebErrorStatus.Disconnected => "网络已断开",
        CoreWebView2WebErrorStatus.CannotConnect => "无法连接服务器",
        CoreWebView2WebErrorStatus.OperationCanceled => "加载被取消",
        _ => status.ToString()
    };
}
