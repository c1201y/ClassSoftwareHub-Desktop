using System;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services.Updating;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Oobe;





public sealed partial class OobeUpdatePage : Page
{
    private readonly UpdateService _service = UpdateService.CreateDefault();

    private CancellationTokenSource? _cts;
    private UpdateRelease? _release;
    private bool _checked;

    public OobeUpdatePage()
    {
        InitializeComponent();

        
        Loaded += (_, _) =>
        {
            if (_checked) return;
            _checked = true;
            _ = CheckAsync();
        };
        Unloaded += (_, _) => CancelPending();
    }

    private void CancelPending()
    {
        try { _cts?.Cancel(); } catch {  }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await CheckAsync();

    private async Task CheckAsync()
    {
        CancelPending();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _release = null;
        CheckingPanel.Visibility = Visibility.Visible;
        ResultPanel.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;

        try
        {
            var channel = UpdateChannels.Parse(App.Settings.Current.UpdateChannel);
            var result = await _service.CheckAsync(channel, ShellConfig.ShellVersion, ct);
            if (ct.IsCancellationRequested) return;
            Show(result);
        }
        catch (OperationCanceledException)
        {
            
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            AppLog.Warn("oobe", "初次配置检查更新失败：" + ex.Message);
            ShowFailure();
        }
    }

    private void Show(UpdateCheckResult result)
    {
        CheckingPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Collapsed;

        if (result is { HasUpdate: true, Release: { } release })
        {
            _release = release;

            ResultIcon.Glyph = "\uE896";                 
            ResultTitle.Text = $"发现新版本 {release.Tag}";

            var detail = $"当前版本 {ShellConfig.ShellVersion}";
            if (release.Primary is { } package && package.SizeText.Length > 0)
                detail += $"　·　安装包 {package.SizeText}";
            if (release.PublishedAt is { } when) detail += $"　·　{when.LocalDateTime:yyyy-MM-dd}";
            ResultDetail.Text = detail;

            ActionRow.Visibility = Visibility.Visible;
            UpdateNowButton.IsEnabled = true;
            return;
        }

        ResultIcon.Glyph = "\uE73E";                     
        ResultTitle.Text = "已是最新版本";
        ResultDetail.Text = result.Message.Length > 0
            ? $"当前 {ShellConfig.ShellVersion}。{result.Message}"
            : $"当前 {ShellConfig.ShellVersion}，没有需要更新的内容。";
        ActionRow.Visibility = Visibility.Collapsed;
    }

    private void ShowFailure()
    {
        CheckingPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        ResultIcon.Glyph = "\uE7BA";                     
        ResultTitle.Text = "无法检查更新";
        ResultDetail.Text = "无法连接更新服务。";
        ActionRow.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Visible;
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_release is null) return;

        UpdateNowButton.IsEnabled = false;
        StatusText.Text = "正在下载并安装。安装期间应用将自动关闭，完成后自动重新打开。";
        StatusText.Visibility = Visibility.Visible;

        var ok = false;
        try
        {
            ok = await UpdateFlow.RunAsync(XamlRoot, _service, _release, "正在更新");
        }
        catch (Exception ex)
        {
            AppLog.Exception("oobe", ex, "初次配置里启动更新失败");
        }

        if (!ok)
        {
            UpdateNowButton.IsEnabled = true;
            StatusText.Text = "更新未完成，可在「设置」中重试。";
        }
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => OobeSession.Current?.Next();
}
