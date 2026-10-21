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

        // 「本次更新内容」：Release 正文是**发版文档原文**，里面有配图语法（![…](…)）、
        // `# …版本发布` 大标题、首行那句下载提示、`## 其他内容` 之后的网站 / 爱发电 / 主页链接。
        // 原样摆上去等于让用户读一遍发布页 —— 重点全被这些噪音淹了（2026-10-12 Nick 报）。
        // 这里只抠「前言」小节的条目（发版文档开头那段要点摘要），逐条洗干净摆出来。
        // ⚠️ 2026-10-12 Nick 定：卡片上用**前言**，不用更新日志（后者是逐条流水账、还带作者署名）。
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

        // ⚠️ 用户点了哪颗按钮**必须留痕**（2026-10-12 Nick 报：点了更新什么都没发生，日志里查不出来）。
        //    有这一行，"他点了没反应"才分得清是"他没点"还是"点了但后面某一环断了"。
        AppLog.Info("update", $"用户选择「{ChoiceText(choice)}」· {release.Tag}");
        return choice;
    }

    /// <summary>把用户的选择翻成日志里的中文（见 <see cref="AskAsync"/>）。</summary>
    private static string ChoiceText(UpdateChoice choice) => choice switch
    {
        UpdateChoice.Now => "立即更新",
        UpdateChoice.Background => "后台下载",
        _ => "稍后",
    };

    /// <summary>「本次更新内容」最多摆几条 —— 再多卡片就撑得没边了。</summary>
    private const int NoteItemsMax = 4;

    /// <summary>发版文档里小节的取用顺序（见 <see cref="SummarizeNotes"/>）。</summary>
    private static readonly string[] NoteSections = { "前言", "更新日志" };

    /// <summary>
    /// 把 Release 正文（Markdown）压成「本次更新内容」该有的样子。
    ///
    /// 只收**小节里的条目**，优先级：`前言`（发版文档开头那段给用户看的要点摘要）
    /// → `更新日志`（更早的文档体例 / 只有日志时）→ 全文洗干净取前几行。
    ///
    /// ⚠️ 2026-10-12 Nick 定：卡片上要**前言**不要**更新日志** —— 前言三两句说完这一版干了什么，
    ///    更新日志是逐条流水账（每条还带 `（作者[@TinyNick](…)）`），摆在弹窗里太长、也没重点。
    /// ⚠️ 不要退回"把 Notes 原样摆出来"—— 正文里有配图语法、Markdown 标题、下载提示、
    ///    以及「## 其他内容」下面那一堆网站 / 爱发电 / 主页链接，用户看的是更新要点，不是发布页。
    /// </summary>
    private static List<string> SummarizeNotes(string? notes)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(notes)) return result;

        var lines = notes.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // ① 首选「前言」；没有这一节（更早的文档体例）再试「更新日志」
        foreach (var section in NoteSections)
        {
            CollectSection(lines, section, result);
            if (result.Count > 0) break;
        }

        // ② 退路：两个小节都没有 —— 全文洗干净，跳过配图与「## 其他内容」之后的链接堆
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

    /// <summary>
    /// 从 <paramref name="title"/> 这个小节里收条目，直到撞上下一个标题行（或「其他内容」那一段）。
    /// 收获直接追加进 <paramref name="result"/>；这一节不存在时什么也不做。
    ///
    /// ⚠️ 发版文档的小节标题有两种写法 —— `## 前言` 与 `前言` + 下一行 `--`（下划线式），
    ///    两种都要认，否则「更新日志」这四个字会被当成一条正文收进去。
    /// </summary>
    private static void CollectSection(string[] lines, string title, List<string> result)
    {
        var inSection = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var bare = line.TrimStart('#').Trim();   // 剥掉开头的 # 再比标题

            if (!inSection)
            {
                if (bare.StartsWith(title, StringComparison.Ordinal)) inSection = true;
                continue;
            }

            // 小节到头了：下一个标题行（`## …` 或下划线式）/「其他内容」那一段
            if (line.StartsWith("##") || bare.StartsWith("其他内容", StringComparison.Ordinal)) break;
            if (IsHeadingUnderline(lines, i + 1)) break;
            if (line.Length == 0 || line.StartsWith("--")) continue;

            var text = CleanNoteLine(line);
            if (text.Length > 0) result.Add(text);
        }
    }

    /// <summary>
    /// 第 <paramref name="index"/> 行是不是 Markdown 的"标题下划线"（`--…` / `==…`）。
    /// ⚠️ 必须**紧邻**上一行才算 —— 分隔线 `---` 前面通常空一行，不能当标题。
    /// </summary>
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

    /// <summary>
    /// 洗掉一行 Markdown：去图片、去作者署名、链接只留文字、去行首的项目符号 / 井号 / 引用号。
    /// 太长（发版文档里那些长句说明在卡片上摆不下）就截断。
    /// </summary>
    private static string CleanNoteLine(string line)
    {
        var s = line;

        s = Regex.Replace(s, @"!\[[^\]]*\]\([^)]*\)", "");                  // 图片 → 去掉
        s = Regex.Replace(s, @"（\s*作者\[[^\]]*\]\([^)]*\)\s*）", "");     // （作者[@xxx](url)）→ 去掉
        s = Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");               // [文字](url) → 文字
        s = s.TrimStart('-', '*', ' ', '>', '#').Trim();

        // 发版文档「前言」的长条目在 90 字上下，上限给到 96 就能整条摆下、不截半句；
        // 「更新日志」那种 150+ 字的长句才需要截，且下面会尽量断在句末。
        const int MaxChars = 96;
        if (s.Length > MaxChars)
        {
            var cut = s[..MaxChars];

            // 尽量断在句末标点，别把话截成半句（往回最多找 12 个字）
            var stop = cut.LastIndexOfAny(new[] { '。', '；', '！', '？' });
            s = stop >= MaxChars - 12 ? cut[..(stop + 1)] : cut.TrimEnd() + "…";
        }

        return s;
    }

    /// <summary>
    /// 第二步（用户已经同意）：下载 → 校验 → 静默安装 → 退出应用（装好会自动重新打开）。
    /// 返回 false = 失败（调用方自己提示）。成功的话应用会直接退出。
    /// </summary>
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

        var allowClose = false;          // 收尾阶段（正在安装）才允许关窗
        var userCancelled = false;       // 用户主动点了「取消下载」
        var closedByUser = false;        // 弹窗已经被用户关掉了，后面别再 Hide
        var detached = false;            // 用户点了「后台下载」：进度窗收掉，下载在后台继续
        DownloadTask? adopted = null;    // 转后台后承接这条下载的任务卡片（见下面 PrimaryButtonClick）

        // ⚠️ 这个 token 就是"卡死时的出口"。原来它根本没接线：
        // UpdateService 里的 HttpClient 是 Timeout.InfiniteTimeSpan（注释写着"靠 CancellationToken 控制"），
        // 但 DownloadAndVerifyAsync 调用时没传 ct —— 于是连接建立后传输停滞（半开连接、镜像挂起）时
        // ReadAsync 会永久阻塞，弹窗又关不掉，用户只能杀进程。
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

        // ⚠️ 这颗主按钮原来写的是「立即更新」—— 可用户**就是点了「立即更新」才走到这个窗**的，
        //    再摆一颗同名按钮毫无意义（点了也没第二个动作，纯粹是个摆设，2026-10-12 Nick 报）。
        //    改成「后台下载」：点一下把进度窗收掉，下载继续在后台跑，
        //    完成后发系统通知，装不装由他决定（跟外面那颗「后台下载」同一个收尾）。
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;              // 不靠按钮关窗 —— 我们自己 Hide，好把"转后台"这一步先做完
            if (detached) return;

            detached = true;
            allowClose = true;               // ⚠️ 置上它，Closing 里才不会再当成"用户取消"去掐断下载
            AppLog.Info("update", $"用户点了「后台下载」· {release.Tag}（进度窗收掉，下载继续在后台跑）");

            // 接手：把这次**已经在跑**的下载挂到任务页上，进度接着走、结束在那里说得清。
            // ⚠️ 令牌要交出去 —— 否则卡片上的「取消」点了掐不到真正在传的那一次（按钮会变成摆设）。
            adopted = DownloadManager.Current.TrackUpdate(
                $"ClassSoftwareHub {release.Tag}", release.Tag, UpdateTaskNote(), package.Url.ToString());
            adopted.AdoptCancellation(cts);

            TryHide(dialog, closedByUser);
            GoToDownloads();
        };

        dialog.Closing += (_, args) =>
        {
            if (allowClose) return;

            // 点「取消下载」/ Esc / 点窗外 —— 一律：允许马上关掉，并且真的把下载停掉。
            // 绝不 Cancel 关窗：宁可下载被取消，也不能把用户困在一个关不掉的弹窗里。
            closedByUser = true;
            userCancelled = true;
            // ⚠️ 用户主动取消**一定要留痕**（2026-10-12 Nick 报）。取消之后临时文件被清理、磁盘上什么都不剩，
            //    日志里再没有这一行的话，事后只能靠猜是"没下成"还是"用户自己掐了"。
            AppLog.Info("update", $"用户取消下载 · {release.Tag}（进度窗被关掉，正在中止传输）");
            try { cts.Cancel(); } catch { /* 已经取消过了 */ }
        };

        _ = dialog.ShowAsync();

        // 空闲超时（不是总时长）：连续这么久没有任何进度就认定卡死。
        // 慢速下载不会被误杀 —— 每来一次进度就续期一次。
        cts.CancelAfter(TimeSpan.FromSeconds(IdleSeconds));

        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                bar.Value = p.Percent;
                status.Text = $"正在下载 {release.Tag}　{p.Percent:0}%"
                    + (p.SizeText.Length > 0 ? "　·　" + p.SizeText : "")
                    + (p.SpeedText.Length > 0 ? "　·　" + p.SpeedText : "");
                try { cts.CancelAfter(TimeSpan.FromSeconds(IdleSeconds)); } catch { /* 已取消/已释放 */ }

                // 用户中途点了「后台下载」：从这一刻起进度同时写进「任务进行」页那张卡片
                if (adopted is not null) DownloadManager.Current.ReportProgress(adopted, p);
            });

            var destination = Path.Combine(UpdateService.UpdatesDir, package.Name);

            // 本地已有同名安装包（上次更新 / 回滚留下的）：先校验，过了就不重新下载 ——
            // 回滚旧版本时这一步能省一次上百 MB 的下载（这也是「本地保留最近 N 个安装包」的意义）。
            // 校验不过就删掉重下，绝不让坏包蒙混过关。
            var downloaded = await TryReuseLocalAsync(package, destination, t => status.Text = t, cts.Token)
                             ?? await service.DownloadAndVerifyAsync(package, destination, progress, cts.Token);

            cts.CancelAfter(Timeout.Infinite);   // 下载完了，别再触发超时打断安装

            // 用户中途选了「后台下载」：这次就不装了 —— 把包装进存档、发一条系统通知，
            // 什么时候装由他自己点（跟外面那颗「后台下载」的收尾完全一致）。
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

            // ⚠️⚠️ RunInstaller 只是"安排"（它内部会先等约 3 秒才拉起安装程序），
            //    随后必须**立刻真的退出应用**：Inno 一启动就查 AppMutex，
            //    这时应用还在的话安装会被静默取消（详见 UpdateService.RunInstaller 注释）。
            //    顺序反了（先装后退 / 不退）就是 2026-10-01「1.2 升不到 1.3」的根因。
            UpdateService.RunInstaller(downloaded.FilePath);
            allowClose = true;
            await Task.Delay(400);       // 让 cmd 来得及把延迟启动安排下去，再走
            ExitAppNow();
            return true;
        }
        catch (OperationCanceledException)
        {
            allowClose = true;

            // 用户自己取消的：他已经知道了，不用再解释（按钮那一行的日志已经留过痕）
            if (userCancelled)
            {
                AppLog.Info("update", $"更新流程因用户取消而结束 · {release.Tag}");
                return false;
            }

            // 已经转后台了：进度窗不在了，失败得走系统通知 —— 不然用户什么都看不到
            if (detached)
            {
                // 转后台之后有两种"取消"：用户自己在任务页点的、以及闲置超时自动中止的。
                // 这两件事在日志与界面上说法完全不同，必须分开（见 DownloadTask.CanceledByUser）。
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
        if (alreadyClosed) return;   // 用户已经关掉了，再 Hide 会抛
        try { dialog.Hide(); } catch { /* 已经关了就算了 */ }
    }

    // ── 后台下载 ─────────────────────────────────────────────────────

    /// <summary>
    /// 连续这么久没有任何进度就认定卡死（不是总时长）。慢速下载不会被误杀 —— 每来一次进度就续期一次。
    /// 弹窗式更新与后台下载用同一个规矩。
    /// </summary>
    private const int IdleSeconds = 45;

    private static int _bgBusy;      // 0 = 空闲，1 = 后台下载进行中（同一时间只允许一个）

    /// <summary>
    /// 后台下载：不弹进度窗，静默走「校验本地包 → 下载 → 校验」，
    /// 成功后记入存档待装标记并弹系统通知（带 现在安装 / 稍后安装 按钮）；
    /// 失败也报一声（托盘气泡兜底）。返回 false = 已经有一个在下了。
    ///
    /// ⚠️⚠️ 2026-10-15 Nick 报「点了后台下载没有任何反应，也没法手动安装、也不知道下没下好」——
    ///    原因就是这条路**从头到尾没有任何界面**：进度只用来续期超时，结果只发一条系统通知
    ///    （通知还可能被免打扰/专注助手吃掉）。现在改成开工前先在「任务进行」页挂一张卡片
    ///    （<see cref="DownloadManager.TrackUpdate"/>），进度、体积、速度、取消、装不装全在那儿看得见。
    /// </summary>
    public static bool StartBackgroundDownload(UpdateService service, UpdateRelease release)
    {
        if (release.Primary is not { } package) return false;
        if (Interlocked.Exchange(ref _bgBusy, 1) == 1)
        {
            AppLog.Info("update", $"已有一个后台下载在进行，这次的「后台下载」请求忽略 · {release.Tag}");
            GoToDownloads();    // 已经在下着了 —— 把人领到任务页去看那一条，别让他以为点了没用
            return false;
        }

        AppLog.Begin("update", $"后台下载开始（进度显示在「任务进行」页）· {release.Tag}");

        // ⚠️ 卡片与 Progress 都必须在 UI 线程建：ObservableCollection 不能跨线程改，
        //    而 Progress<T> 只有在 UI 线程建才捕获得到同步上下文（否则进度回调跑在
        //    ThreadPool 线程上，改任务属性会把绑定刷崩）。
        var task = DownloadManager.Current.TrackUpdate(
            $"ClassSoftwareHub {release.Tag}", release.Tag, UpdateTaskNote(), package.Url.ToString());

        // 把"用户在卡片上点取消"接进下载循环：链接到任务自己的令牌上。
        // 空闲超时打在这个 linked 上，用户取消打在那个上面 —— 两者事后能分开（见 CanceledByUser）。
        var cts = CancellationTokenSource.CreateLinkedTokenSource(task.Cancellation.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(IdleSeconds));

        var progress = new Progress<DownloadProgress>(p =>
        {
            DownloadManager.Current.ReportProgress(task, p);
            try { cts.CancelAfter(TimeSpan.FromSeconds(IdleSeconds)); } catch { /* 已取消 */ }
        });

        GoToDownloads();

        _ = Task.Run(async () =>
        {
            try
            {
                var destination = Path.Combine(UpdateService.UpdatesDir, package.Name);
                var downloaded = await TryReuseLocalAsync(package, destination, null, cts.Token)
                                 ?? await service.DownloadAndVerifyAsync(package, destination, progress, cts.Token);

                // 记入存档：首页横幅、任务页卡片、「稍后安装」都认它
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
                // 用户自己点的取消 vs 闲置超时：日志与界面上都得分开说
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
                try { cts.Dispose(); } catch { /* 已经释放过 */ }
                Interlocked.Exchange(ref _bgBusy, 0);
            }
        });
        return true;
    }

    /// <summary>
    /// 卡片上那句固定说明 —— 让人一眼知道这条是「应用更新」，不是普通下载。
    ///
    /// ⚠️ 只说"这是什么、能干什么"，**别复述进度状态** —— 状态行紧挨着它下面，
    ///    两边都报体积 / 都说"已通过校验"就成了同一句话说两遍。
    /// </summary>
    private static string UpdateTaskNote() => "应用更新包　·　可在此直接安装，无需手动查找安装包";

    /// <summary>文件大小（拿不到就 0，卡片上少显示一个数字而已）。</summary>
    private static long SizeOfFile(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    /// <summary>
    /// 把「任务进行」页推到前台。点完「后台下载」必须给一个即时反馈 ——
    /// 2026-10-15 Nick 报的"点完什么都没发生"，本质就是那一步之前没有任何界面变化。
    /// </summary>
    public static void GoToDownloads()
    {
        RunOnUi(() =>
        {
            try { App.MainWindow?.Shell.NavigateTo("downloads"); }
            catch { /* 界面还没就绪就算了，任务本身照跑 */ }
        });
    }

    /// <summary>
    /// 「任务进行」页每次露面时调一次：存档里还挂着待装更新（后台下载完但没装）而列表里没有对应卡片，
    /// 就补一条**已完成**的任务。
    ///
    /// 为什么需要：任务列表是**进程内**的，关掉应用再打开就空了 —— 而那条"下好了、等你装"的包
    /// 还躺在硬盘上。不补的话，用户下次进任务页只看到"当前无下载任务"，又回到"不知道下没下好"。
    /// </summary>
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

    /// <summary>立即安装待装的更新（首页横幅按钮 / 通知「现在安装」都走这里）。须在 UI 线程调用。</summary>
    public static async Task InstallPendingNowAsync()
    {
        var path = App.Settings.Current.UpdatePendingPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            // 记一笔：用户点了「现在安装」但包没了（被清理 / 被手动删），否则又是一个"点了没反应"
            AppLog.Info("update", $"点「现在安装」但待装安装包已不在（记录值：{(string.IsNullOrEmpty(path) ? "空" : path)}），忽略");
            return;
        }

        AppLog.Begin("update", $"安装待装更新 · {path}");

        UpdateService.RunInstaller(path);
        await Task.Delay(400);       // 让 cmd 来得及把延迟启动安排下去，再走
        ExitAppNow();
    }

    /// <summary>
    /// 用本地已有的安装包覆盖安装（设置页「本地安装包 → 覆盖安装」）。
    /// 「本地保留最近 N 个安装包」的意义就在这儿：想重装 / 装回旧版时不必再下一遍。
    ///
    /// ⚠️⚠️ 顺序与在线更新完全一致：<see cref="UpdateService.RunInstaller"/> 只是"安排"
    ///    （它自己会先等约 3 秒），随后必须**立刻真的退出应用** —— Inno 一启动就查 AppMutex，
    ///    应用还在跑的话安装会被静默取消、用户端只表现为"版本没变"（2026-10-01 的根因）。
    /// </summary>
    public static async Task InstallLocalAsync(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            AppLog.Info("update", $"覆盖安装被忽略：路径为空或文件不存在（{installerPath}）");
            return;
        }

        AppLog.Begin("update", $"用本地安装包覆盖安装 · {installerPath}");

        UpdateService.RunInstaller(installerPath);
        await Task.Delay(400);       // 让 cmd 来得及把延迟启动安排下去，再走
        ExitAppNow();
    }

    /// <summary>
    /// 真正把应用退掉，给随后的安装程序腾位置。
    ///
    /// ⚠️⚠️ 必须走 <see cref="MainWindow.ExitApp"/>（它会先置 <c>_exitRequested</c>）。
    ///    直接调 <c>Application.Current.Exit()</c> 会被 MainWindow 里 AppWindow.Closing 的托盘逻辑
    ///    拦成"用户点了 ×" → <c>SW_HIDE</c> 躲进托盘，**进程不退** → 安装程序查到 AppMutex 占用 →
    ///    静默模式自动取消 → 安装失败且无任何提示。这就是 2026-10-01「1.2 无法更新到 1.3」的根因。
    /// </summary>
    private static void ExitAppNow()
    {
        // 所有安装路径都汇到这儿。记一条：日志到这里就断了，这一行是"应用是自己退的、不是崩的"的证据
        // （装好之后日志由新实例接手，两段会话之间靠它接得上）。
        AppLog.Info("update", "应用主动退出，把位置让给安装程序（后续日志由新实例接手）");

        if (App.MainWindow is { } win)
        {
            win.ExitApp();
            return;
        }

        // 主窗口已经没了（理论上到不了这儿）：退而求其次
        try { Application.Current.Exit(); }
        catch { Environment.Exit(0); }
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
                // 记警告：这是"明明本地有包、为什么还下一遍"的唯一答案，静默删掉的话查不出来
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
                // 该发布什么校验值都没给：大小一致才敢直接用，否则重下
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
            // 本地包读不了 / 网络取校验值失败等：一律退回正常下载，不在复用这条路上添堵
            AppLog.Warning("update", $"复用本地安装包这一步出错，改为正常下载：{ex.Message}");
            return null;
        }
    }

    private static void DeleteQuiet(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 删不掉就让下载流程覆盖它 */ }
    }
}
