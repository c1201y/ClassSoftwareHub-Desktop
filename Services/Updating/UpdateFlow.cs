using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Services.Updating;

/// <summary>
/// 「下载 → 校验 → 静默安装 → 退出」这条流程的公共实现（设置页和启动时的强制更新都用它）。
/// 对话框没有"取消/稍后"：只有真失败了才会放掉，避免用户被永久卡住。
/// </summary>
public static class UpdateFlow
{
    /// <summary>跑一次更新。返回 false = 失败（对话框已经收起，调用方自己提示）。成功的话应用会直接退出。</summary>
    public static async Task<bool> RunAsync(
        XamlRoot xamlRoot, UpdateService service, UpdateRelease release, string title = "发现新版本（强制更新）")
    {
        if (release.Primary is not { } package) return false;

        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0 };
        var status = new TextBlock { Text = $"正在下载 {release.Tag}…", TextWrapping = TextWrapping.Wrap };
        var note = new TextBlock
        {
            Text = "下载完会先做 MD5 校验（防损坏 / 防替换），通过才会安装。安装期间请勿关闭应用。",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(bar);
        panel.Children.Add(status);
        panel.Children.Add(note);

        var allowClose = false;
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = "立即更新",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Closing += (_, args) => { if (!allowClose) args.Cancel = true; };
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = true;
        _ = dialog.ShowAsync();

        try
        {
            var progress = new Progress<double>(p =>
            {
                bar.Value = p * 100;
                status.Text = $"正在下载 {release.Tag}… {p:P0}";
            });

            var destination = Path.Combine(UpdateService.UpdatesDir, package.Name);
            var downloaded = await service.DownloadAndVerifyAsync(package, destination, progress);

            bar.Value = 100;
            status.Text = $"下载完成（{downloaded.VerifyNote}），正在启动安装程序…";

            UpdateService.RunInstaller(downloaded.FilePath);
            await Task.Delay(1200);      // 让安装程序先起来，别跟自己抢文件

            allowClose = true;
            Application.Current.Exit();
            return true;
        }
        catch (Exception ex)
        {
            allowClose = true;
            status.Text = "❌ 更新失败：" + ex.Message;
            await Task.Delay(2500);
            dialog.Hide();
            return false;
        }
    }
}
