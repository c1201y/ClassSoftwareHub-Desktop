using System;
using System.IO;
using System.Threading;
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

        var allowClose = false;          // 收尾阶段（正在安装）才允许关窗
        var userCancelled = false;       // 用户主动点了「取消下载」
        var closedByUser = false;        // 弹窗已经被用户关掉了，后面别再 Hide

        // ⚠️ 这个 token 就是"卡死时的出口"。原来它根本没接线：
        // UpdateService 里的 HttpClient 是 Timeout.InfiniteTimeSpan（注释写着"靠 CancellationToken 控制"），
        // 但 DownloadAndVerifyAsync 调用时没传 ct —— 于是连接建立后传输停滞（半开连接、镜像挂起）时
        // ReadAsync 会永久阻塞，弹窗又关不掉，用户只能杀进程。
        using var cts = new CancellationTokenSource();

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = "立即更新",
            CloseButtonText = "取消下载",
            DefaultButton = ContentDialogButton.Primary,
        };

        // 主按钮只是"更新进行中"的指示，点了不该关窗
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = true;

        dialog.Closing += (_, args) =>
        {
            if (allowClose) return;

            // 点「取消下载」/ Esc / 点窗外 —— 一律：允许马上关掉，并且真的把下载停掉。
            // 绝不 Cancel 关窗：宁可下载被取消，也不能把用户困在一个关不掉的弹窗里。
            closedByUser = true;
            userCancelled = true;
            try { cts.Cancel(); } catch { /* 已经取消过了 */ }
        };

        _ = dialog.ShowAsync();

        // 空闲超时（不是总时长）：连续这么久没有任何进度就认定卡死。
        // 慢速下载不会被误杀 —— 每来一次进度就续期一次。
        const int IdleSeconds = 45;
        cts.CancelAfter(TimeSpan.FromSeconds(IdleSeconds));

        try
        {
            var progress = new Progress<double>(p =>
            {
                bar.Value = p * 100;
                status.Text = $"正在下载 {release.Tag}… {p:P0}";
                try { cts.CancelAfter(TimeSpan.FromSeconds(IdleSeconds)); } catch { /* 已取消/已释放 */ }
            });

            var destination = Path.Combine(UpdateService.UpdatesDir, package.Name);
            var downloaded = await service.DownloadAndVerifyAsync(package, destination, progress, cts.Token);

            cts.CancelAfter(Timeout.Infinite);   // 下载完了，别再触发超时打断安装
            bar.Value = 100;
            status.Text = $"下载完成（{downloaded.VerifyNote}），正在安装…装好后应用会自动重新打开。";

            UpdateService.RunInstaller(downloaded.FilePath);
            await Task.Delay(1200);      // 让安装程序先起来，别跟自己抢文件

            allowClose = true;
            Application.Current.Exit();
            return true;
        }
        catch (OperationCanceledException)
        {
            allowClose = true;

            // 用户自己取消的：他已经知道了，不用再解释
            if (userCancelled) return false;

            status.Text = $"下载卡住了（连续 {IdleSeconds} 秒没有任何进度），已经取消。\n" +
                          "多半是网络问题或者下载源没响应 —— 稍后再试一次。";
            await Task.Delay(2500);
            TryHide(dialog, closedByUser);
            return false;
        }
        catch (Exception ex)
        {
            allowClose = true;
            status.Text = "❌ 更新失败：" + ex.Message;
            await Task.Delay(2500);
            TryHide(dialog, closedByUser);
            return false;
        }
    }

    private static void TryHide(ContentDialog dialog, bool alreadyClosed)
    {
        if (alreadyClosed) return;   // 用户已经关掉了，再 Hide 会抛
        try { dialog.Hide(); } catch { /* 已经关了就算了 */ }
    }
}
