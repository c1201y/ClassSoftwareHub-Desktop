using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.Updating;






public static class UpdateFlow
{
    
    public enum UpdateChoice
    {
        
        Later,
        
        Now,
        
        Background,
    }

    
    
    
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

        
        
        
        
        
        var items = SummarizeNotes(release.Notes);
        if (items.Count > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "本次更新内容：",
                FontSize = 12,
                Opacity = 0.7,
                Margin = new Thickness(0, 4, 0, 0),
            });

            var itemsPanel = new StackPanel { Spacing = 3 };
            foreach (var item in items)
            {
                itemsPanel.Children.Add(new TextBlock
                {
                    Text = "· " + item,
                    FontSize = 12,
                    Opacity = 0.8,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            body.Children.Add(itemsPanel);
        }

        body.Children.Add(new TextBlock
        {
            Text = "更新为自愿操作，不会自动安装。可「立即更新」现在就装，「后台下载」转入「任务进行」页下载、"
                 + "完成后随时安装，或「稍后」暂不处理。",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

        var dialog = new ContentDialog
        {
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = xamlRoot,
            Title = "发现新版本",
            Content = body,
            PrimaryButtonText = "立即更新",
            SecondaryButtonText = "后台下载",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        var choice = result switch
        {
            ContentDialogResult.Primary => UpdateChoice.Now,
            ContentDialogResult.Secondary => UpdateChoice.Background,
            _ => UpdateChoice.Later,
        };

        
        
        AppLog.Info("update", $"用户选择「{ChoiceText(choice)}」· {release.Tag}");
        return choice;
    }

    
    private static string ChoiceText(UpdateChoice choice) => choice switch
    {
        UpdateChoice.Now => "立即更新",
        UpdateChoice.Background => "后台下载",
        _ => "稍后",
    };

    
    private const int NoteItemsMax = 4;

    
    private static readonly string[] NoteSections = { "前言", "更新日志" };

    
    
    
    
    
    
    
    
    
    
    
    private static List<string> SummarizeNotes(string? notes)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(notes)) return result;

        var lines = notes.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        
        foreach (var section in NoteSections)
        {
            CollectSection(lines, section, result);
            if (result.Count > 0) break;
        }

        
        if (result.Count == 0)
        {
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.StartsWith("##") && line.Contains("其他内容")) break;
                if (line.Length == 0 || line.StartsWith("![")) continue;

                var text = CleanNoteLine(line);
                if (text.Length == 0) continue;

                result.Add(text);
                if (result.Count >= NoteItemsMax) break;
            }
        }

        if (result.Count > NoteItemsMax)
        {
            var total = result.Count;
            result = result.GetRange(0, NoteItemsMax);
            result.Add($"……本次共 {total} 条，其余可在更新日志页查看。");
        }

        return result;
    }

    
    
    
    
    
    
    
    private static void CollectSection(string[] lines, string title, List<string> result)
    {
        var inSection = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var bare = line.TrimStart('#').Trim();   

            if (!inSection)
            {
                if (bare.StartsWith(title, StringComparison.Ordinal)) inSection = true;
                continue;
            }

            
            if (line.StartsWith("##") || bare.StartsWith("其他内容", StringComparison.Ordinal)) break;
            if (IsHeadingUnderline(lines, i + 1)) break;
            if (line.Length == 0 || line.StartsWith("--")) continue;

            var text = CleanNoteLine(line);
            if (text.Length > 0) result.Add(text);
        }
    }

    
    
    
    
    private static bool IsHeadingUnderline(string[] lines, int index)
    {
        if (index < 0 || index >= lines.Length) return false;

        var next = lines[index].Trim();
        if (next.Length < 2) return false;

        foreach (var c in next)
        {
            if (c != '-' && c != '=') return false;
        }
        return true;
    }

    
    
    
    
    private static string CleanNoteLine(string line)
    {
        var s = line;

        s = Regex.Replace(s, @"!\[[^\]]*\]\([^)]*\)", "");                  
        s = Regex.Replace(s, @"（\s*作者\[[^\]]*\]\([^)]*\)\s*）", "");     
        s = Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");               
        s = s.TrimStart('-', '*', ' ', '>', '#').Trim();

        
        
        const int MaxChars = 96;
        if (s.Length > MaxChars)
        {
            var cut = s[..MaxChars];

            
            var stop = cut.LastIndexOfAny(new[] { '。', '；', '！', '？' });
            s = stop >= MaxChars - 12 ? cut[..(stop + 1)] : cut.TrimEnd() + "…";
        }

        return s;
    }

    
    
    
    
    public static async Task<bool> RunAsync(
        XamlRoot xamlRoot, UpdateService service, UpdateRelease release, string title = "正在更新")
    {
        if (release.Primary is not { } package) return false;

        AppLog.Begin("update", $"进入「正在更新」流程 · {release.Tag}（下载 → 校验 → 安装）");

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

        var allowClose = false;          
        var userCancelled = false;       
        var closedByUser = false;        
        var detached = false;            
        DownloadTask? adopted = null;    

        
        
        
        
        using var cts = new CancellationTokenSource();

        var dialog = new ContentDialog
        {
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = xamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = "后台下载",
            CloseButtonText = "取消下载",
            DefaultButton = ContentDialogButton.Primary,
        };

        
        
        
        
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;              
            if (detached) return;

            detached = true;
            allowClose = true;               
            AppLog.Info("update", $"用户点了「后台下载」· {release.Tag}（进度窗收掉，下载继续在后台跑）");

            
            
            adopted = DownloadManager.Current.TrackUpdate(
                $"ClassSoftwareHub {release.Tag}", release.Tag, UpdateTaskNote(), package.Url.ToString());
            adopted.AdoptCancellation(cts);

            TryHide(dialog, closedByUser);
            GoToDownloads();
        };

        dialog.Closing += (_, args) =>
        {
            if (allowClose) return;

            
            
            closedByUser = true;
            userCancelled = true;
            
            
            AppLog.Info("update", $"用户取消下载 · {release.Tag}（进度窗被关掉，正在中止传输）");
            try { cts.Cancel(); } catch {  }
        };

        _ = dialog.ShowAsync();

        
        
        cts.CancelAfter(TimeSpan.FromSeconds(IdleSeconds));

        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                bar.Value = p.Percent;
                status.Text = $"正在下载 {release.Tag}　{p.Percent:0}%"
                    + (p.SizeText.Length > 0 ? "　·　" + p.SizeText : "")
                    + (p.SpeedText.Length > 0 ? "　·　" + p.SpeedText : "");
                try { cts.CancelAfter(TimeSpan.FromSeconds(IdleSeconds)); } catch {  }

                
                if (adopted is not null) DownloadManager.Current.ReportProgress(adopted, p);
            });

            var destination = Path.Combine(UpdateService.UpdatesDir, package.Name);

            
            
            
            var downloaded = await TryReuseLocalAsync(package, destination, t => status.Text = t, cts.Token)
                             ?? await service.DownloadAndVerifyAsync(package, destination, progress, cts.Token);

            cts.CancelAfter(Timeout.Infinite);   

            
            
            if (detached)
            {
                var settings = App.Settings;
                settings.Current.UpdatePendingPath = downloaded.FilePath;
                settings.Current.UpdatePendingTag = release.Tag;
                settings.Save();

                AppLog.Done("update", $"下载完成并已标记待装（用户已转后台，等他点安装）· {release.Tag} → {downloaded.FilePath}");
                RunOnUi(() =>
                {
                    if (adopted is not null)
                        DownloadManager.Current.CompleteExternal(adopted, downloaded.FilePath, SizeOfFile(downloaded.FilePath));
                    App.MainWindow?.NotifyUpdateReady(release.Tag);
                });
                return true;
            }

            bar.Value = 100;
            status.Text = $"下载完成（{downloaded.VerifyNote}），正在安装：安装完成后应用将自动重新打开。";
            AppLog.Step("update", $"下载完成且校验通过（{downloaded.VerifyNote}），开始安装 · {release.Tag}");

            
            
            
            
            UpdateService.RunInstaller(downloaded.FilePath);
            allowClose = true;
            await Task.Delay(400);       
            ExitAppNow();
            return true;
        }
        catch (OperationCanceledException)
        {
            allowClose = true;

            
            if (userCancelled)
            {
                AppLog.Info("update", $"更新流程因用户取消而结束 · {release.Tag}");
                return false;
            }

            
            if (detached)
            {
                
                
                var byUser = adopted?.CanceledByUser == true;
                AppLog.Warning("update", byUser
                    ? $"用户在「任务进行」页取消了更新下载 · {release.Tag}"
                    : $"后台下载停滞（连续 {IdleSeconds} 秒无进度），已中止并通知用户 · {release.Tag}");

                RunOnUi(() =>
                {
                    if (adopted is null) return;
                    if (byUser)
                    {
                        DownloadManager.Current.CancelExternal(adopted);
                        return;
                    }
                    DownloadManager.Current.FailExternal(adopted, $"下载停滞（连续 {IdleSeconds} 秒无进度）");
                    App.MainWindow?.NotifyBackgroundDownloadFailed(release.Tag, $"下载停滞（连续 {IdleSeconds} 秒无进度）");
                });
                return false;
            }

            AppLog.Warning("update", $"下载停滞（连续 {IdleSeconds} 秒无进度），已取消 · {release.Tag}");
            status.Text = $"下载停滞（连续 {IdleSeconds} 秒无进度），已取消。\n" +
                          "可能为网络问题或下载源无响应，可稍后重试。";
            await Task.Delay(2500);
            TryHide(dialog, closedByUser);
            return false;
        }
        catch (Exception ex)
        {
            allowClose = true;

            if (detached)
            {
                AppLog.Fail("update", ex, $"后台下载失败（已发系统通知）· {release.Tag}");
                RunOnUi(() =>
                {
                    if (adopted is not null) DownloadManager.Current.FailExternal(adopted, ex.Message);
                    App.MainWindow?.NotifyBackgroundDownloadFailed(release.Tag, ex.Message);
                });
                return false;
            }

            AppLog.Fail("update", ex, $"更新失败（进度窗显示后自动收起）· {release.Tag}");
            status.Text = "更新失败：" + ex.Message;
            await Task.Delay(2500);
            TryHide(dialog, closedByUser);
            return false;
        }
    }

    private static void TryHide(ContentDialog dialog, bool alreadyClosed)
    {
        if (alreadyClosed) return;   
        try { dialog.Hide(); } catch {  }
    }

    

    
    
    
    
    private const int IdleSeconds = 45;

    private static int _bgBusy;      

    
    
    
    
    
    
    
    
    
    
    public static bool StartBackgroundDownload(UpdateService service, UpdateRelease release)
    {
        if (release.Primary is not { } package) return false;
        if (Interlocked.Exchange(ref _bgBusy, 1) == 1)
        {
            AppLog.Info("update", $"已有一个后台下载在进行，这次的「后台下载」请求忽略 · {release.Tag}");
            GoToDownloads();    
            return false;
        }

        AppLog.Begin("update", $"后台下载开始（进度显示在「任务进行」页）· {release.Tag}");

        
        
        
        var task = DownloadManager.Current.TrackUpdate(
            $"ClassSoftwareHub {release.Tag}", release.Tag, UpdateTaskNote(), package.Url.ToString());

        
        
        var cts = CancellationTokenSource.CreateLinkedTokenSource(task.Cancellation.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(IdleSeconds));

        var progress = new Progress<DownloadProgress>(p =>
        {
            DownloadManager.Current.ReportProgress(task, p);
            try { cts.CancelAfter(TimeSpan.FromSeconds(IdleSeconds)); } catch {  }
        });

        GoToDownloads();

        _ = Task.Run(async () =>
        {
            try
            {
                var destination = Path.Combine(UpdateService.UpdatesDir, package.Name);
                var downloaded = await TryReuseLocalAsync(package, destination, null, cts.Token)
                                 ?? await service.DownloadAndVerifyAsync(package, destination, progress, cts.Token);

                
                var settings = App.Settings;
                settings.Current.UpdatePendingPath = downloaded.FilePath;
                settings.Current.UpdatePendingTag = release.Tag;
                settings.Save();

                AppLog.Done("update", $"后台下载完成，已标记待装并发系统通知 · {release.Tag} → {downloaded.FilePath}");
                RunOnUi(() =>
                {
                    DownloadManager.Current.CompleteExternal(task, downloaded.FilePath, SizeOfFile(downloaded.FilePath));
                    App.MainWindow?.NotifyUpdateReady(release.Tag);
                });
            }
            catch (OperationCanceledException)
            {
                
                var byUser = task.CanceledByUser;
                AppLog.Warning("update", byUser
                    ? $"用户在「任务进行」页取消了更新下载 · {release.Tag}"
                    : $"后台下载停滞超时（连续 {IdleSeconds} 秒无进度），已中止并通知用户 · {release.Tag}");

                RunOnUi(() =>
                {
                    if (byUser)
                    {
                        DownloadManager.Current.CancelExternal(task);
                        return;
                    }
                    DownloadManager.Current.FailExternal(task, $"下载停滞（连续 {IdleSeconds} 秒无进度）");
                    App.MainWindow?.NotifyBackgroundDownloadFailed(release.Tag, $"下载停滞（连续 {IdleSeconds} 秒无进度）");
                });
            }
            catch (Exception ex)
            {
                AppLog.Fail("update", ex, $"后台下载失败（已发系统通知）· {release.Tag}");
                RunOnUi(() =>
                {
                    DownloadManager.Current.FailExternal(task, ex.Message);
                    App.MainWindow?.NotifyBackgroundDownloadFailed(release.Tag, ex.Message);
                });
            }
            finally
            {
                try { cts.Dispose(); } catch {  }
                Interlocked.Exchange(ref _bgBusy, 0);
            }
        });
        return true;
    }

    
    
    
    
    
    
    private static string UpdateTaskNote() => "应用更新包　·　可在此直接安装，无需手动查找安装包";

    
    private static long SizeOfFile(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    
    
    
    
    public static void GoToDownloads()
    {
        RunOnUi(() =>
        {
            try { App.MainWindow?.Shell.NavigateTo("downloads"); }
            catch {  }
        });
    }

    
    
    
    
    
    
    
    public static void EnsurePendingTaskVisible()
    {
        var s = App.Settings.Current;
        var path = s.UpdatePendingPath ?? "";
        if (path.Length == 0 || !File.Exists(path)) return;

        var tag = s.UpdatePendingTag ?? "";

        foreach (var t in DownloadManager.Current.Tasks)
            if (t.IsUpdate && (tag.Length == 0 ? t.Path == path : t.UpdateTag == tag)) return;

        var size = SizeOfFile(path);
        var task = DownloadManager.Current.TrackUpdate(
            $"ClassSoftwareHub {(tag.Length > 0 ? tag : "新版本")}", tag, UpdateTaskNote(), "");
        DownloadManager.Current.CompleteExternal(task, path, size);
    }

    
    public static async Task InstallPendingNowAsync()
    {
        var path = App.Settings.Current.UpdatePendingPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            
            AppLog.Info("update", $"点「现在安装」但待装安装包已不在（记录值：{(string.IsNullOrEmpty(path) ? "空" : path)}），忽略");
            return;
        }

        AppLog.Begin("update", $"安装待装更新 · {path}");

        UpdateService.RunInstaller(path);
        await Task.Delay(400);       
        ExitAppNow();
    }

    
    
    
    
    
    
    
    
    public static async Task InstallLocalAsync(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            AppLog.Info("update", $"覆盖安装被忽略：路径为空或文件不存在（{installerPath}）");
            return;
        }

        AppLog.Begin("update", $"用本地安装包覆盖安装 · {installerPath}");

        UpdateService.RunInstaller(installerPath);
        await Task.Delay(400);       
        ExitAppNow();
    }

    
    
    
    
    
    
    
    
    private static void ExitAppNow()
    {
        
        
        AppLog.Info("update", "应用主动退出，把位置让给安装程序（后续日志由新实例接手）");

        if (App.MainWindow is { } win)
        {
            win.ExitApp();
            return;
        }

        
        try { Application.Current.Exit(); }
        catch { Environment.Exit(0); }
    }

    private static void RunOnUi(Action action)
    {
        var q = App.MainWindow?.DispatcherQueue;
        if (q is not null) { q.TryEnqueue(() => action()); }
        else action();
    }

    
    
    
    
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
                
                AppLog.Warning("update", $"本地安装包 MD5 不符（本地 {md5} ≠ 期望 {expectedMd5}），已删除并改为重新下载 · {path}");
                DeleteQuiet(path);
                return null;
            }
            if (wantSha && !string.Equals(package.Sha256, sha, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warning("update", $"本地安装包 SHA256 不符（本地 {sha} ≠ 期望 {package.Sha256}），已删除并改为重新下载 · {path}");
                DeleteQuiet(path);
                return null;
            }
            if (expectedMd5.Length == 0 && !wantSha)
            {
                
                if (package.Size <= 0 || new FileInfo(path).Length != package.Size)
                {
                    AppLog.Warning("update", $"本地安装包大小不符（本地 {new FileInfo(path).Length} ≠ 期望 {package.Size}），已删除并改为重新下载 · {path}");
                    DeleteQuiet(path);
                    return null;
                }
                AppLog.Info("update", $"复用本地安装包：该发布未提供校验值，大小一致，直接采用 · {path}");
                return new DownloadedPackage(path, md5, sha, false,
                    "本地安装包大小一致（该发布未提供校验值）");
            }
            AppLog.Info("update", $"复用本地安装包：校验通过，省去一次下载 · {path}");
            return new DownloadedPackage(path, md5, sha, true, "本地安装包校验通过，未重新下载");
        }
        catch (Exception ex)
        {
            
            AppLog.Warning("update", $"复用本地安装包这一步出错，改为正常下载：{ex.Message}");
            return null;
        }
    }

    private static void DeleteQuiet(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch {  }
    }
}
