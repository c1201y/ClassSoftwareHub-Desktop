using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 提交软件（原生版，不再用 WebView2）。
/// 提交流程跟网页版一致：POST 到自建 Worker（带服务端令牌）→ 仓库 submissions/ 草稿 → 审核 Issue → 合并上架。
/// 入口按顺序回退、记住上次成功的入口；连不上时内容存本机，可重试 / 下载 JSON / 复制 / 去 GitHub 提 PR。
/// </summary>
public sealed partial class SubmitPage : Page
{
    /// <summary>提交入口：先试 CDN 新域，再试 CF 直连（与网页版一致）。</summary>
    private static readonly string[] SubmitEndpoints = { "https://cshapi.132614.xyz", "https://submit.132614.xyz" };
    private const int SubmitTimeoutMs = 10000;
    /// <summary>站点仓库的 submissions 目录（兜底走 GitHub 新建文件页）。</summary>
    private const string RepoNewFileUrl = "https://github.com/c1201y/ClassSoftwareHub/new/main/submissions";
    /// <summary>合法校验值位数：32=MD5 / 40=SHA-1 / 56=SHA-224 / 64=SHA-256 / 96=SHA-384 / 128=SHA-512</summary>
    private static readonly int[] HashLengths = { 32, 40, 56, 64, 96, 128 };

    private static readonly HttpClient Http = new();

    private static string DataDir => Core.AppPaths.DataDir;
    private static string EndpointFile => System.IO.Path.Combine(DataDir, "submit-endpoint.txt");
    private static string DraftFile => System.IO.Path.Combine(DataDir, "submit-draft.json");

    private sealed class DownloadDraft
    {
        public string Platform = "";
        public string Size = "";
        public string Note = "";
        public string Url = "";
        public string Hash = "";
    }

    private readonly List<DownloadDraft> _downloads = new();
    private readonly List<Border> _cards = new();
    private readonly List<TextBlock> _titles = new();
    private bool _busy;
    /// <summary>连不上时留存的那份 payload（兜底面板用）。</summary>
    private Dictionary<string, object?>? _pending;

    public SubmitPage()
    {
        InitializeComponent();

        foreach (var category in App.Content.Categories)
            CategoryCombo.Items.Add(new ComboBoxItem { Content = category.Name, Tag = category.Key });
        CategoryCombo.SelectedIndex = -1;

        AddDownload();
        RefreshDraftButton();
    }

    // ══════════ 下载项 ══════════
    private void AddDownload_Click(object sender, RoutedEventArgs e) => AddDownload();

    private void AddDownload()
    {
        var draft = new DownloadDraft();
        _downloads.Add(draft);

        var stack = new StackPanel { Spacing = 12 };

        var head = new Grid { ColumnSpacing = 10 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var remove = new Button { Content = "删除", Padding = new Thickness(10, 0, 10, 0), FontSize = 13 };
        remove.Click += (_, _) => RemoveDownload(draft);
        Grid.SetColumn(remove, 1);
        head.Children.Add(title);
        head.Children.Add(remove);
        stack.Children.Add(head);

        stack.Children.Add(Field("平台（如 Windows x64 安装版）", "Windows x64 安装版", null, value => draft.Platform = value));

        var row = new Grid { ColumnSpacing = 14 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(Field("体积", "如 1.6 MB", null, value => draft.Size = value));
        var note = Field("备注", "如 便携版 / 需要管理员权限", null, value => draft.Note = value);
        Grid.SetColumn(note, 1);
        row.Children.Add(note);
        stack.Children.Add(row);

        stack.Children.Add(Field("下载直链 *", "https://…/setup.exe", null, value => draft.Url = value));
        stack.Children.Add(Field("校验值（选填，防掉包）", "纯十六进制，算法按位数自动识别", null, value => draft.Hash = value));

        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            BorderThickness = new Thickness(1),
            BorderBrush = Res("CardStrokeColorDefaultBrush"),
            Background = Res("CardBackgroundFillColorDefaultBrush"),
            Child = stack,
        };

        _cards.Add(card);
        _titles.Add(title);
        DownloadsHost.Children.Add(card);
        RenumberDownloads();
    }

    private void RemoveDownload(DownloadDraft draft)
    {
        if (_downloads.Count <= 1) return;   // 至少留一项（跟网页版一致）
        var index = _downloads.IndexOf(draft);
        if (index < 0) return;
        _downloads.RemoveAt(index);
        DownloadsHost.Children.Remove(_cards[index]);
        _cards.RemoveAt(index);
        _titles.RemoveAt(index);
        RenumberDownloads();
    }

    private void RenumberDownloads()
    {
        for (var i = 0; i < _titles.Count; i++) _titles[i].Text = $"下载项 {i + 1}";
    }

    private TextBox Field(string header, string placeholder, string? description, Action<string>? onText)
    {
        var box = new TextBox
        {
            Header = header,
            PlaceholderText = placeholder,
            Description = description ?? "",
        };
        if (onText is not null) box.TextChanged += (_, _) => onText(box.Text);
        return box;
    }

    private Brush Res(string key) => Services.ThemeBrush.Get(this, key);

    // ══════════ 从 GitHub 一键读取 ══════════
    private readonly Dictionary<string, string> _lastFilled = new();
    private string _lastDownloadUrls = "";
    private bool _importing;

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_importing) return;

        ImportErrorBar.IsOpen = false;
        ImportOkBar.IsOpen = false;
        ImportReport.Visibility = Visibility.Collapsed;

        if (RepoBox.Text.Trim().Length == 0)
        {
            ImportErrorBar.Message = "请先填写仓库地址，例如 github.com/owner/repo。";
            ImportErrorBar.IsOpen = true;
            return;
        }

        _importing = true;
        ImportButton.IsEnabled = false;
        ImportButton.Content = "读取中…";

        try
        {
            var result = await Services.GithubImport.FetchAsync(RepoBox.Text, PrereleaseBox.IsChecked == true, 12);
            ApplyImport(result);
        }
        catch (Services.GithubImportException exception)
        {
            ImportErrorBar.Message = exception.Kind switch
            {
                Services.GithubImportErrorKind.Invalid => "无法识别该仓库地址，请使用 github.com/owner/repo 形式。",
                Services.GithubImportErrorKind.NotFound => "未找到该仓库（可能为私有仓库或地址有误）。",
                Services.GithubImportErrorKind.RateLimit => "GitHub 接口调用次数已达上限（未登录时每小时 60 次，按网络出口共享），请稍后重试。",
                _ => "读取失败：" + exception.Message,
            };
            ImportErrorBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            ImportErrorBar.Message = "读取失败：" + exception.Message;
            ImportErrorBar.IsOpen = true;
        }
        finally
        {
            _importing = false;
            ImportButton.IsEnabled = true;
            ImportButton.Content = "读取";
        }
    }

    private void ApplyImport(Services.GithubImportResult result)
    {
        var repo = result.Repo;
        var release = result.Release;
        var overwrite = OverwriteBox.IsChecked == true;

        var filled = new List<string>();
        var kept = new List<string>();
        var warnings = new List<string>();

        void Put(string label, string current, string value, Action<string> assign)
        {
            var next = (value ?? "").Trim();
            if (next.Length == 0) return;
            var shown = (current ?? "").Trim();
            if (shown == next)
            {
                _lastFilled[label] = next;
                kept.Add(label);
                return;
            }
            var editedByUser = shown.Length > 0
                               && (!_lastFilled.TryGetValue(label, out var last) || last != shown);
            if (editedByUser && !overwrite)
            {
                kept.Add(label);
                return;
            }
            assign(next);
            _lastFilled[label] = next;
            filled.Add(label);
        }

        // ── 软件 ID：由仓库名生成，跟站内已有软件撞车就自动加序号 ──
        var takenIds = App.Content.Apps.Select(app => app.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suggestedId = Services.GithubImport.RepoToId(repo.Repo);
        if (suggestedId.Length > 0 && takenIds.Contains(suggestedId))
        {
            var suffix = 2;
            while (takenIds.Contains($"{suggestedId}-{suffix}") && suffix < 100) suffix++;
            var corrected = $"{suggestedId}-{suffix}";
            warnings.Add($"站内已经有 id 为 {suggestedId} 的软件了，自动改成 {corrected}。");
            suggestedId = corrected;
        }
        Put("软件 ID", IdBox.Text, suggestedId, text => IdBox.Text = text);
        if (IdBox.Text.Trim().Length > 0 && takenIds.Contains(IdBox.Text.Trim()))
            warnings.Add($"站内已经有 id 为 {IdBox.Text.Trim()} 的软件，直接提交会撞车，建议改一个。");

        // ── 文本字段 ──
        Put("软件名称", NameBox.Text, repo.Repo, text => NameBox.Text = text);
        Put("一句话简介", TaglineBox.Text, Services.GithubImport.ToTagline(repo.Description), text => TaglineBox.Text = text);
        Put("详细介绍", DescBox.Text, repo.Description, text => DescBox.Text = text);
        Put("版本号", VersionBox.Text, release?.TagName ?? "", text => VersionBox.Text = text);
        Put("系统限制", SystemBox.Text, result.System, text => SystemBox.Text = text);
        Put("官网地址", WebsiteBox.Text, repo.Homepage, text => WebsiteBox.Text = text);
        Put("GitHub 仓库", GithubBox.Text, repo.HtmlUrl, text => GithubBox.Text = text);
        if (repo.Homepage.Contains("apps.microsoft.com"))
            Put("应用商店地址", StoreBox.Text, repo.Homepage, text => StoreBox.Text = text);
        // 用的是预发布版：写一条提示条，详情页会在下载区上方提示
        if (result.Facts.UsedPrerelease && release is not null)
            Put("提示条", NoticeBox.Text, $"当前提交的是预发布版本（{release.TagName}），稳定版请等正式发布。", text => NoticeBox.Text = text);

        // ── 图标：接口拿不到软件图标，先用仓库所有者的头像顶上 ──
        var icon = IconBox.Text.Trim();
        if (repo.OwnerAvatar.Length > 0 && icon != repo.OwnerAvatar
            && (icon.Length == 0 || _lastFilled.GetValueOrDefault("图标") == icon || overwrite))
        {
            IconBox.Text = repo.OwnerAvatar;
            _lastFilled["图标"] = repo.OwnerAvatar;
            filled.Add("图标");
            warnings.Add($"GitHub 接口拿不到软件图标，先用仓库所有者（{repo.Owner}）的头像顶上，记得换成官方图标。");
        }
        else if (icon.Length > 0)
        {
            kept.Add("图标");
        }

        // ── 下载项 ──
        var currentUrls = string.Join("\n", _downloads.Select(item => item.Url.Trim()).Where(url => url.Length > 0));
        var downloadsEditedByUser = currentUrls.Length > 0 && currentUrls != _lastDownloadUrls;
        var hashByUrl = new Dictionary<string, string>();
        foreach (var item in _downloads)
        {
            var url = item.Url.Trim();
            if (url.Length > 0 && item.Hash.Trim().Length > 0 && !hashByUrl.ContainsKey(url))
                hashByUrl[url] = item.Hash.Trim();
        }

        if (result.Downloads.Count > 0)
        {
            if (downloadsEditedByUser && !overwrite)
            {
                kept.Add("下载项");
                warnings.Add("下载项检测为手动填写，已保留原内容；如需使用读取到的链接，请勾选「覆盖我手写的内容」后重新读取。");
            }
            else
            {
                SetDownloads(result.Downloads.Select(item => (
                    Platform: item.Platform,
                    Note: item.Note,
                    Size: item.Size,
                    Url: item.Url,
                    Hash: hashByUrl.GetValueOrDefault(item.Url.Trim(), "")
                )).ToList());
                filled.Add($"下载项（{result.Downloads.Count}）");
            }
        }
        else if (!result.Facts.ReleaseFailed)
        {
            var url = release?.HtmlUrl ?? (repo.HtmlUrl + "/releases/latest");
            if (downloadsEditedByUser && !overwrite)
            {
                kept.Add("下载项");
            }
            else
            {
                SetDownloads(new List<(string, string, string, string, string)>
                {
                    ("最新版（网页）", "仓库没有可直接下载的安装包，点开是 Release 页面", "网页", url, ""),
                });
                filled.Add("下载项");
            }
        }

        // ── 需注意的地方 ──
        if (result.Facts.ReleaseFailed) warnings.Add("无法读取该仓库的版本信息（接口限流或网络异常），已填入仓库信息，版本号与安装包请手动补充。");
        if (result.Facts.NoRelease) warnings.Add("该仓库尚未发布任何 Release，版本号与安装包请手动填写。");
        if (result.Facts.NoAsset) warnings.Add("该 Release 中没有可下载的安装包（可能仅含源码包），请手动填写下载直链。");
        if (result.Facts.AssetSkipped > 0) warnings.Add($"已自动跳过 {result.Facts.AssetSkipped} 个非安装包文件（校验文件、调试符号、源码包等）。");
        if (result.Facts.Truncated) warnings.Add($"安装包数量较多，仅填入前 {result.Downloads.Count} 个。");
        if (result.Facts.UsedPrerelease && release is not null) warnings.Add($"当前使用预发布版本 {release.TagName}。");
        if (result.Facts.NewerPrereleaseTag.Length > 0) warnings.Add($"存在更新的预发布版 {result.Facts.NewerPrereleaseTag}。如需获取，请勾选「优先取最新预发布版」后重新读取。");
        if (repo.Archived) warnings.Add("该仓库已归档（不再维护），建议确认是否仍要收录。");

        // 地址栏统一成规范写法，方便核对
        RepoBox.Text = repo.HtmlUrl;

        var summary = $"已读取 {repo.FullName}：最新版本 {(release?.TagName.Length > 0 ? release.TagName : "—")}，"
                      + $"共 {result.Downloads.Count} 个安装包（来源：{result.Via}）"
                      + (repo.License.Length > 0 ? $"，许可证 {repo.License}" : "");
        ImportOkBar.Message = summary;
        ImportOkBar.IsOpen = true;

        BuildImportReport(filled, kept, warnings);
    }

    private void BuildImportReport(List<string> filled, List<string> kept, List<string> warnings)
    {
        ImportReportHost.Children.Clear();
        if (filled.Count == 0 && kept.Count == 0 && warnings.Count == 0)
        {
            ImportReport.Visibility = Visibility.Collapsed;
            return;
        }

        void Line(string label, string value, Brush? color = null)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = label, FontSize = 13, Opacity = 0.65 });
            var text = new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap };
            if (color is not null) text.Foreground = color;
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            ImportReportHost.Children.Add(row);
        }

        if (filled.Count > 0) Line("已填", string.Join("、", filled));
        if (kept.Count > 0) Line("已保留", string.Join("、", kept));
        if (warnings.Count > 0)
        {
            Line("需注意", string.Join("\n", warnings.Select(text => "· " + text)),
                Res("SystemFillColorCautionBrush"));
        }
        ImportReport.Visibility = Visibility.Visible;
    }

    /// <summary>整段重建下载项（一键读取 / 恢复草稿都用它）。</summary>
    private void SetDownloads(List<(string Platform, string Note, string Size, string Url, string Hash)> items)
    {
        _downloads.Clear();
        _cards.Clear();
        _titles.Clear();
        DownloadsHost.Children.Clear();

        foreach (var item in items)
        {
            AddDownload();
            var draft = _downloads[^1];
            draft.Platform = item.Platform;
            draft.Note = item.Note;
            draft.Size = item.Size;
            draft.Url = item.Url;
            draft.Hash = item.Hash;
            FillCard(_cards[^1], draft);
        }
        if (_downloads.Count == 0) AddDownload();
        _lastDownloadUrls = string.Join("\n", _downloads.Select(item => item.Url.Trim()));
    }

    // ══════════ 组装 payload ══════════
    private static string NormalizeHash(string raw)
    {
        var text = (raw ?? "").Trim();
        text = Regex.Replace(text, @"^(md5|sha-?1|sha-?224|sha-?256|sha-?384|sha-?512)\s*[:=]?\s*", "",
            RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "^0x", "", RegexOptions.IgnoreCase);
        return Regex.Replace(text, @"[\s:]", "");
    }

    private static bool IsHashLike(string value)
        => Regex.IsMatch(value, "^[0-9a-fA-F]+$") && HashLengths.Contains(value.Length);

    private Dictionary<string, object?> BuildPayload()
    {
        var downloads = _downloads
            .Where(item => item.Url.Trim().Length > 0)
            .Select(item =>
            {
                var entry = new Dictionary<string, object?>
                {
                    ["platform"] = item.Platform.Trim(),
                    ["note"] = item.Note.Trim(),
                    ["size"] = item.Size.Trim(),
                    ["url"] = item.Url.Trim(),
                };
                var hash = NormalizeHash(item.Hash);
                if (hash.Length > 0) entry["hash"] = hash;
                return entry;
            })
            .ToList();

        var payload = new Dictionary<string, object?>
        {
            ["id"] = IdBox.Text.Trim(),
            ["name"] = NameBox.Text.Trim(),
            ["icon"] = IconBox.Text.Trim(),
            ["category"] = (CategoryCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "",
            ["tagline"] = TaglineBox.Text.Trim(),
            ["description"] = DescBox.Text.Trim(),
            ["version"] = VersionBox.Text.Trim(),
            ["size"] = SizeBox.Text.Trim(),
            ["system"] = SystemBox.Text.Trim(),
            ["website"] = WebsiteBox.Text.Trim(),
            ["github"] = GithubBox.Text.Trim(),
            ["notice"] = NoticeBox.Text.Trim(),
            ["store"] = StoreBox.Text.Trim(),
            ["downloads"] = downloads,
            // 下划线开头 = 只给审核工单看的元数据，合并时会被剥掉，不会发布到站点
            ["_联系方式"] = ContactBox.Text.Trim(),
        };

        var sortText = SortBox.Text.Trim();
        if (sortText.Length > 0 && long.TryParse(sortText, out var sort)) payload["sort"] = sort;

        return payload;
    }

    /// <summary>必填校验 + 校验值格式检查；返回要显示的错误，null 表示没问题。</summary>
    private string? Validate(Dictionary<string, object?> payload)
    {
        var missing = ((string?)payload["id"] ?? "").Length == 0
                      || ((string?)payload["name"] ?? "").Length == 0
                      || ((string?)payload["category"] ?? "").Length == 0
                      || ((string?)payload["tagline"] ?? "").Length == 0
                      || ((string?)payload["description"] ?? "").Length == 0
                      || ((string?)payload["system"] ?? "").Length == 0
                      || ((string?)payload["_联系方式"] ?? "").Length == 0
                      || ((List<Dictionary<string, object?>>)payload["downloads"]!).Count == 0;
        if (missing) return "请把带 * 的必填项填完（至少一条下载直链）。";

        var downloads = (List<Dictionary<string, object?>>)payload["downloads"]!;
        for (var i = 0; i < downloads.Count; i++)
        {
            if (downloads[i].TryGetValue("hash", out var hash) && hash is string text && !IsHashLike(text))
                return $"第 {i + 1} 个下载项的校验值格式不对：必须是纯十六进制，位数要能对上一种算法（32/40/56/64/96/128）。";
        }
        return null;
    }

    // ══════════ 提交 ══════════
    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var payload = BuildPayload();
        var error = Validate(payload);
        if (error is not null)
        {
            ShowResult(false, "还不能提交", error);
            return;
        }

        _busy = true;
        SetBusy(true);
        ShowResult(false, "", "");
        FallbackPanel.Visibility = Visibility.Collapsed;
        Toast.Text = "";

        try
        {
            var (reply, endpoint) = await PostAsync(payload);
            if (reply is null)
            {
                // 所有入口都不通：存草稿 + 给兜底面板
                SaveDraft(payload);
                _pending = payload;
                RefreshDraftButton();
                ShowResult(false, "提交失败", "提交服务没连上（网络或地区限制）。内容已存在本机，可以重试或走下面的兜底方式。");
                FallbackPanel.Visibility = Visibility.Visible;
                return;
            }

            if (endpoint is not null) RememberEndpoint(endpoint);

            if (reply.Value.Success)
            {
                ClearDraft();
                _pending = null;
                RefreshDraftButton();
                ShowResult(true, "已提交", string.IsNullOrWhiteSpace(reply.Value.Message)
                    ? "提交成功，管理员审核通过后就会上架。"
                    : reply.Value.Message!);
            }
            else
            {
                ShowResult(false, "提交未通过校验", reply.Value.Error ?? "服务端拒绝了这份内容，请检查后重试。");
            }
        }
        catch (Exception ex)
        {
            ShowResult(false, "提交失败", "出现意外错误：" + ex.Message);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        SubmitButton.IsEnabled = !busy;
        SubmitButton.Content = busy ? "提交中…" : "提交";
        FallbackRetryButton.IsEnabled = !busy;
        FallbackRetryButton.Content = busy ? "提交中…" : "重试";
    }

    private void ShowResult(bool ok, string title, string message)
    {
        if (title.Length == 0)
        {
            ResultBar.IsOpen = false;
            return;
        }
        ResultBar.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        ResultBar.Title = title;
        ResultBar.Message = message;
        ResultBar.IsOpen = true;
    }

    private readonly record struct Reply(bool Success, string? Message, string? Error);

    /// <summary>按顺序试每个入口，返回第一个"确实是提交接口"的响应；全不通返回 (null, null)。</summary>
    private async Task<(Reply? reply, string? endpoint)> PostAsync(Dictionary<string, object?> payload)
    {
        foreach (var baseUrl in OrderedEndpoints())
        {
            try
            {
                using var cts = new CancellationTokenSource(SubmitTimeoutMs);
                using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var response = await Http.PostAsync(baseUrl + "/api/submit", content, cts.Token);
                var text = await response.Content.ReadAsStringAsync(cts.Token);

                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                var hasSuccess = root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
                var hasError = root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String;
                if (!hasSuccess && !hasError) continue;   // 不是提交接口的响应（回源还没生效时会返回 nginx 错误页）

                var message = root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String ? msg.GetString() : null;
                return (new Reply(hasSuccess, message, hasError ? err.GetString() : null), baseUrl);
            }
            catch
            {
                // 连不上 / 超时 / 响应不是 JSON → 换下一个入口
            }
        }
        return (null, null);
    }

    private static List<string> OrderedEndpoints()
    {
        try
        {
            if (System.IO.File.Exists(EndpointFile))
            {
                var remembered = System.IO.File.ReadAllText(EndpointFile).Trim();
                if (SubmitEndpoints.Contains(remembered))
                    return new List<string> { remembered }.Concat(SubmitEndpoints.Where(item => item != remembered)).ToList();
            }
        }
        catch { /* 读不到就用默认顺序 */ }
        return SubmitEndpoints.ToList();
    }

    private static void RememberEndpoint(string baseUrl)
    {
        try
        {
            System.IO.Directory.CreateDirectory(DataDir);
            System.IO.File.WriteAllText(EndpointFile, baseUrl);
        }
        catch { /* 记不上不影响提交 */ }
    }

    // ══════════ 本机草稿（提交失败后不丢内容） ══════════
    private static void SaveDraft(Dictionary<string, object?> payload)
    {
        try
        {
            System.IO.Directory.CreateDirectory(DataDir);
            System.IO.File.WriteAllText(DraftFile, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 写不进去就算了 */ }
    }

    private static Dictionary<string, object?>? LoadDraft()
    {
        try
        {
            if (!System.IO.File.Exists(DraftFile)) return null;
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(System.IO.File.ReadAllText(DraftFile));
        }
        catch { return null; }
    }

    private static void ClearDraft()
    {
        try { if (System.IO.File.Exists(DraftFile)) System.IO.File.Delete(DraftFile); }
        catch { /* 忽略 */ }
    }

    private void RefreshDraftButton()
        => RestoreDraftButton.Visibility = LoadDraft() is null ? Visibility.Collapsed : Visibility.Visible;

    private void RestoreDraft_Click(object sender, RoutedEventArgs e)
    {
        var draft = LoadDraft();
        if (draft is null) { RefreshDraftButton(); return; }

        string Text(string key) => draft.TryGetValue(key, out var value) && value is not null ? value.ToString() ?? "" : "";

        IdBox.Text = Text("id");
        NameBox.Text = Text("name");
        IconBox.Text = Text("icon");
        TaglineBox.Text = Text("tagline");
        DescBox.Text = Text("description");
        ContactBox.Text = Text("_联系方式");
        VersionBox.Text = Text("version");
        SizeBox.Text = Text("size");
        SystemBox.Text = Text("system");
        WebsiteBox.Text = Text("website");
        GithubBox.Text = Text("github");
        NoticeBox.Text = Text("notice");
        StoreBox.Text = Text("store");
        SortBox.Text = Text("sort");

        var categoryKey = Text("category");
        for (var i = 0; i < CategoryCombo.Items.Count; i++)
            if (CategoryCombo.Items[i] is ComboBoxItem item && (item.Tag as string) == categoryKey)
                CategoryCombo.SelectedIndex = i;

        // 下载项整段重建
        _downloads.Clear();
        _cards.Clear();
        _titles.Clear();
        DownloadsHost.Children.Clear();

        if (draft.TryGetValue("downloads", out var value) && value is JsonElement downloads && downloads.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in downloads.EnumerateArray())
            {
                AddDownload();
                var item = _downloads[^1];
                item.Platform = JsonText(element, "platform");
                item.Size = JsonText(element, "size");
                item.Note = JsonText(element, "note");
                item.Url = JsonText(element, "url");
                item.Hash = JsonText(element, "hash");
                FillCard(_cards[^1], item);
            }
        }
        if (_downloads.Count == 0) AddDownload();

        FallbackPanel.Visibility = Visibility.Collapsed;
        _pending = null;
        ShowResult(true, "已恢复", "上次没提交成功的内容已经填回表单，检查一下再提交。");
    }

    private static string JsonText(JsonElement element, string key)
        => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    /// <summary>把下载项的值写回界面（恢复草稿用）。</summary>
    private static void FillCard(Border card, DownloadDraft draft)
    {
        if (card.Child is not StackPanel stack) return;
        var values = new[] { draft.Platform, draft.Size, draft.Note, draft.Url, draft.Hash };
        var boxes = new List<TextBox>();
        foreach (var child in stack.Children)
        {
            if (child is TextBox box) boxes.Add(box);
            else if (child is Grid row)
                foreach (var cell in row.Children)
                    if (cell is TextBox cellBox) boxes.Add(cellBox);
        }
        for (var i = 0; i < boxes.Count && i < values.Length; i++) boxes[i].Text = values[i];
    }

    // ══════════ 兜底：下载 / 复制 / 去 GitHub ══════════
    private static (string Name, string Text)? BuildSubmissionFile(Dictionary<string, object?>? data)
    {
        if (data is null) return null;
        var id = data.TryGetValue("id", out var value) ? value?.ToString() ?? "submission" : "submission";
        if (id.Trim().Length == 0) id = "submission";
        id = id.Trim();

        var now = DateTime.Now;
        var time = now.ToString("yyyy-MM-dd HH:mm:ss");
        var body = new Dictionary<string, object?>(data)
        {
            ["_提交时间"] = time,
            ["_原始ID冲突"] = App.Content.Apps.Any(app => app.Id == id) ? true : null,
        };

        var fileName = $"{id}-{now:yyyyMMdd-HHmmss}.json";
        return (fileName, JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async void DownloadSubmission_Click(object sender, RoutedEventArgs e)
    {
        var file = BuildSubmissionFile(_pending ?? LoadDraft());
        if (file is null) { Toast.Text = "没有可导出的内容"; return; }
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = file.Value.Name };
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));
            var target = await picker.PickSaveFileAsync();
            if (target is null) return;
            await Windows.Storage.FileIO.WriteTextAsync(target, file.Value.Text);
            Toast.Text = "提交文件已保存：" + target.Path;
        }
        catch (Exception ex)
        {
            Toast.Text = "保存失败：" + ex.Message;
        }
    }

    private void CopySubmission_Click(object sender, RoutedEventArgs e)
    {
        var file = BuildSubmissionFile(_pending ?? LoadDraft());
        if (file is null) { Toast.Text = "没有可复制的内容"; return; }
        try
        {
            var package = new DataPackage();
            package.SetText(file.Value.Text);
            Clipboard.SetContent(package);
            Toast.Text = "已复制提交 JSON";
        }
        catch (Exception ex)
        {
            Toast.Text = "复制失败：" + ex.Message;
        }
    }

    private async void OpenGithubSubmit_Click(object sender, RoutedEventArgs e)
    {
        var file = BuildSubmissionFile(_pending ?? LoadDraft());
        var url = file is null
            ? RepoNewFileUrl
            : $"{RepoNewFileUrl}?filename={Uri.EscapeDataString(file.Value.Name)}";
        try { await Launcher.LaunchUriAsync(new Uri(url)); }
        catch (Exception ex) { Toast.Text = "打不开浏览器：" + ex.Message; }
    }
}
