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
    /// <summary>用户对「发现新版本」弹窗的选择。</summary>
    public enum UpdateChoice
    {
        /// <summary>稍后（关闭 / Esc）。</summary>
        Later,
        /// <summary>立即更新：下载 → 校验 → 静默安装 → 自动重启。</summary>
        Now,
        /// <summary>后台下载：不弹进度窗，下载完发系统通知，装不装等用户点。</summary>
        Background,
    }

    /// <summary>
    /// 第一步：把「发现新版本」摆给用户看，让他自己决定。
    /// </summary>
    public static async Task<UpdateChoice> AskAsync(XamlRoot xamlRoot, UpdateRelease release)
    {
        if (release.Primary is not { } package) return UpdateChoice.Later;

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
                Text = "本次更新内容：",
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
            Text = "更新为自愿操作，不会自动安装。可「立即更新」现在就装，「后台下载」先下好稍后安装，或「稍后」暂不处理。",
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
            SecondaryButtonText = "后台下载",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => UpdateChoice.Now,
            ContentDialogResult.Secondary => UpdateChoice.Background,
            _ => UpdateChoice.Later,
        };
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
        var status = new TextBlock { Text = $"正在下载 {release.Tag}", TextWrapping = TextWrapping.Wrap };
        var note = new TextBlock
        {
            Text = "下载完成后先进行 MD5 校验（防损坏 / 防替换），校验通过后方可安装。" +
               "安装过程中应用将自动关闭，安装完成后自动重新打开（约十几秒），此过程并非程序异常。",
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
                status.Text = $"正在下载 {release.Tag} {p:P0}";
                try { cts.CancelAfter(TimeSpan.FromSeconds(IdleSeconds)); } catch { /* 已取消/已释放 */ }
            });

            var destination = Path.Combine(UpdateService.UpdatesDir, package.Name);

            // 本地已有同名安装包（上次更新 / 回滚留下的）：先校验，过了就不重新下载 ——
            // 回滚旧版本时这一步能省一次上百 MB 的下载（这也是「本地保留最近 N 个安装包」的意义）。
            // 校验不过就删掉重下，绝不让坏包蒙混过关。
            var downloaded = await TryReuseLocalAsync(package, destination, t => status.Text = t, cts.Token)
                             ?? await service.DownloadAndVerifyAsync(package, destination, progress, cts.Token);

            cts.CancelAfter(Timeout.Infinite);   // 下载完了，别再触发超时打断安装
            bar.Value = 100;
            status.Text = $"下载完成（{downloaded.VerifyNote}），正在安装：安装完成后应用将自动重新打开。";

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

            status.Text = $"下载停滞（连续 {IdleSeconds} 秒无进度），已取消。\n" +
                          "可能为网络问题或下载源无响应，可稍后重试。";
            await Task.Delay(2500);
            TryHide(dialog, closedByUser);
            return false;
        }
        catch (Exception ex)
        {
            allowClose = true;
            status.Text = "更新失败：" + ex.Message;
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

    // ── 后台下载 ─────────────────────────────────────────────────────

    private static int _bgBusy;      // 0 = 空闲，1 = 后台下载进行中（同一时间只允许一个）

    /// <summary>
    /// 后台下载：不弹进度窗，静默走「校验本地包 → 下载 → 校验」，
    /// 成功后记入存档待装标记并弹系统通知（带 现在安装 / 稍后安装 按钮）；
    /// 失败也报一声（托盘气泡兜底）。返回 false = 已经有一个在下了。
    /// </summary>
    public static bool StartBackgroundDownload(UpdateService service, UpdateRelease release)
    {
        if (release.Primary is not { } package) return false;
        if (Interlocked.Exchange(ref _bgBusy, 1) == 1) return false;

        _ = Task.Run(async () =>
        {
            try
            {
                var destination = Path.Combine(UpdateService.UpdatesDir, package.Name);
                using var cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromSeconds(45));   // 空闲超时，同 RunAsync 的规矩
                var progress = new Progress<double>(p =>
                {
                    try { cts.CancelAfter(TimeSpan.FromSeconds(45)); } catch { /* 已取消 */ }
                });

                var downloaded = await TryReuseLocalAsync(package, destination, null, cts.Token)
                                 ?? await service.DownloadAndVerifyAsync(package, destination, progress, cts.Token);

                // 记入存档：首页横幅与「稍后安装」都认它
                var settings = App.Settings;
                settings.Current.UpdatePendingPath = downloaded.FilePath;
                settings.Current.UpdatePendingTag = release.Tag;
                settings.Save();

                RunOnUi(() => App.MainWindow?.NotifyUpdateReady(release.Tag));
            }
            catch (OperationCanceledException)
            {
                RunOnUi(() => App.MainWindow?.NotifyBackgroundDownloadFailed(release.Tag, "下载停滞（连续 45 秒无进度）"));
            }
            catch (Exception ex)
            {
                RunOnUi(() => App.MainWindow?.NotifyBackgroundDownloadFailed(release.Tag, ex.Message));
            }
            finally
            {
                Interlocked.Exchange(ref _bgBusy, 0);
            }
        });
        return true;
    }

    /// <summary>立即安装待装的更新（首页横幅按钮 / 通知「现在安装」都走这里）。须在 UI 线程调用。</summary>
    public static async Task InstallPendingNowAsync()
    {
        var path = App.Settings.Current.UpdatePendingPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        UpdateService.RunInstaller(path);
        await Task.Delay(1200);      // 让安装程序先起来，别跟自己抢文件
        Application.Current.Exit();
    }

    private static void RunOnUi(Action action)
    {
        var q = App.MainWindow?.DispatcherQueue;
        if (q is not null) { q.TryEnqueue(() => action()); }
        else action();
    }

    /// <summary>
    /// 本地同名安装包复用：MD5（发布方提供了就必过）→ SHA256（同）→ 两样都没有时退而比文件大小。
    /// 任何一项对不上：删掉本地文件、返回 null 走正常下载。返回 null 也涵盖「本地没有包」。
    /// </summary>
    private static async Task<DownloadedPackage?> TryReuseLocalAsync(
        UpdatePackage package, string path,
        Action<string>? status, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(package.Name) || !File.Exists(path)) return null;
            status?.Invoke("发现本地已有该版本的安装包，正在校验…");

            var expectedMd5 = await UpdateService.ResolveMd5Async(package, ct);
            var wantSha = (package.Sha256 ?? "").Length > 0;
            var (md5, sha) = await UpdateService.HashFileAsync(path, wantSha, ct);

            if (expectedMd5.Length > 0 && !string.Equals(expectedMd5, md5, StringComparison.OrdinalIgnoreCase))
            {
                DeleteQuiet(path);
                return null;
            }
            if (wantSha && !string.Equals(package.Sha256, sha, StringComparison.OrdinalIgnoreCase))
            {
                DeleteQuiet(path);
                return null;
            }
            if (expectedMd5.Length == 0 && !wantSha)
            {
                // 该发布什么校验值都没给：大小一致才敢直接用，否则重下
                if (package.Size <= 0 || new FileInfo(path).Length != package.Size)
                {
                    DeleteQuiet(path);
                    return null;
                }
                return new DownloadedPackage(path, md5, sha, false,
                    "本地安装包大小一致（该发布未提供校验值）");
            }
            return new DownloadedPackage(path, md5, sha, true, "本地安装包校验通过，未重新下载");
        }
        catch
        {
            // 本地包读不了 / 网络取校验值失败等：一律退回正常下载，不在复用这条路上添堵
            return null;
        }
    }

    private static void DeleteQuiet(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 删不掉就让下载流程覆盖它 */ }
    }
}
