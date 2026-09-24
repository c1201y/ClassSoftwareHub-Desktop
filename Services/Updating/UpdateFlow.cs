using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Services.Updating;

/// <summary>
/// 更新流程：**先问用户要不要更新**（绝不强制），用户点了「立即更新」才进入
/// 「下载 → 校验 → 静默安装 → 自动重启」。
/// 设置页的「检查更新」和启动时的自动检查都走这里。
/// </summary>
public static class UpdateFlow
{
    /// <summary>
    /// 第一步：把「发现新版本」摆给用户看，让他自己决定。
    /// 返回 true = 用户要更新；false = 用户选了「稍后」（或没得下载）。
    /// </summary>
    public static async Task<bool> AskAsync(XamlRoot xamlRoot, UpdateRelease release)
    {
        if (release.Primary is not { } package) return false;

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = $"新版本：{release.Tag}",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        var detail = $"安装包：{package.SizeText}　·　通道：{UpdateChannels.ToDisplay(release.Channel)}";
        if (release.PublishedAt is { } when) detail += $"　·　{when.LocalDateTime:yyyy-MM-dd}";
        body.Children.Add(new TextBlock { Text = detail, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

        if (!string.IsNullOrWhiteSpace(release.Notes))
        {
            body.Children.Add(new TextBlock
            {
                Text = "这次更新：",
                FontSize = 12,
                Opacity = 0.7,
                Margin = new Thickness(0, 4, 0, 0),
            });
            body.Children.Add(new TextBlock
            {
                Text = release.Notes.Trim(),
                FontSize = 12,
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 8,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        body.Children.Add(new TextBlock
        {
            Text = "更新是自愿的，不会自动装。想更新就点「立即更新」，想晚点再说就点「稍后」。",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "发现新版本",
            Content = body,
            PrimaryButtonText = "立即更新",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    /// <summary>
    /// 第二步（用户已经同意）：下载 → 校验 → 静默安装 → 退出应用（装好会自动重新打开）。
    /// 返回 false = 失败（调用方自己提示）。成功的话应用会直接退出。
    /// </summary>
    public static async Task<bool> RunAsync(
        XamlRoot xamlRoot, UpdateService service, UpdateRelease release, string title = "正在更新")
    {
        if (release.Primary is not { } package) return false;

        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0 };
        var status = new TextBlock { Text = $"正在下载 {release.Tag}…", TextWrapping = TextWrapping.Wrap };
        var note = new TextBlock
        {
            Text = "下载完会先做 MD5 校验（防损坏 / 防替换），通过才会安装。" +
               "安装时应用会自动关闭，装好之后会自动重新打开（大约十几秒），不是崩了。",
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
            status.Text = $"下载完成（{downloaded.VerifyNote}），正在安装…装好后应用会自动重新打开。";

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
