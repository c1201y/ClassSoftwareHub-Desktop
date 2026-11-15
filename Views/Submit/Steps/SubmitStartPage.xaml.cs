using System;
using System.Collections.Generic;
using System.Linq;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Views.Submit.Steps;

/// <summary>
/// 第 1 步：选择起点。
///
/// <para>
/// 这一页不填任何必填项，只做一件事 —— 有仓库就先把能自动填的填掉。
/// 旧提交页把「从 GitHub 读取」做成表单顶上的一条工具，用户得先意识到它存在；
/// 放到第一步之后，后面的填写量是可预期的。
/// </para>
/// </summary>
public sealed partial class SubmitStartPage : Page
{
    private static readonly (string Key, string Name)[] DefaultPlatforms =
    {
        ("windows", "Windows"),
        ("macos", "macOS"),
        ("linux", "Linux"),
        ("android", "Android"),
        ("ios", "iOS"),
        ("web", "网页版"),
    };

    /// <summary>上一次读取写进去的值：用来判断「现在这个值是用户改的，还是读取填的」。</summary>
    private readonly Dictionary<string, string> _lastFilled = new();

    private string _lastDownloadUrls = "";
    private bool _importing;

    private SubmitDraft? Draft => SubmitSession.Current?.Draft;

    public SubmitStartPage()
    {
        InitializeComponent();

        if (Draft is null) return;
        RepoBox.Text = Draft.GitHubRepo;
        PrereleaseBox.IsChecked = Draft.PreferPrerelease;
        ImportButton.Content = "读取";
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_importing || Draft is null) return;

        ErrorBar.IsOpen = false;
        OkBar.IsOpen = false;
        Report.Visibility = Visibility.Collapsed;

        if (RepoBox.Text.Trim().Length == 0)
        {
            ErrorBar.Message = "请填写仓库地址，例如 github.com/owner/repo。";
            ErrorBar.IsOpen = true;
            return;
        }

        _importing = true;
        ImportButton.IsEnabled = false;
        ImportButton.Content = "正在读取";

        try
        {
            Draft.GitHubRepo = RepoBox.Text.Trim();
            Draft.PreferPrerelease = PrereleaseBox.IsChecked == true;

            var result = await GithubImport.FetchAsync(RepoBox.Text, PrereleaseBox.IsChecked == true, 12);
            ApplyImport(result);
        }
        catch (GithubImportException exception)
        {
            ErrorBar.Message = exception.Kind switch
            {
                GithubImportErrorKind.Invalid => "无法识别该仓库地址，需使用 github.com/owner/repo 形式。",
                GithubImportErrorKind.NotFound => "未找到该仓库（可能为私有仓库或地址有误）。",
                GithubImportErrorKind.RateLimit => "GitHub 接口调用次数已达上限（未登录时每小时 60 次，按网络出口共享），可稍后重试。",
                _ => "读取失败：" + exception.Message,
            };
            ErrorBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            ErrorBar.Message = "读取失败：" + exception.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            _importing = false;
            ImportButton.IsEnabled = true;
            ImportButton.Content = "读取";
        }
    }

    private void ApplyImport(GithubImportResult result)
    {
        if (Draft is null) return;

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

        // ── 软件 ID：仓库名生成，撞车自动加序号；之后名称再变也不覆盖它 ──
        var suggestedId = GithubImport.RepoToId(repo.Repo);
        if (suggestedId.Length > 0)
        {
            var unique = SoftwareSlug.EnsureUnique(suggestedId, SoftwareSlug.TakenIds());
            if (!string.Equals(unique, suggestedId, StringComparison.OrdinalIgnoreCase))
                warnings.Add($"站内已存在 id 为 {suggestedId} 的软件，已自动改为 {unique}。");

            Put("软件 ID", Draft.Id, unique, text =>
            {
                Draft.Id = text;
                // 读取得到的 id 视同「已确认」，之后改名称不再覆盖
                Draft.IdEdited = true;
            });
        }

        Put("软件名称", Draft.Name, repo.Repo, text => Draft.Name = text);
        Put("一句话简介", Draft.Tagline, GithubImport.ToTagline(repo.Description), text => Draft.Tagline = text);
        Put("详细介绍", Draft.Description, repo.Description, text => Draft.Description = text);
        Put("版本号", Draft.Version, release?.TagName ?? "", text => Draft.Version = text);
        Put("系统说明", Draft.System, result.System, text => Draft.System = text);

        // 适用系统：接口给的是 "Windows / macOS / Linux"，按名字对到平台 key
        var detected = result.System
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (detected.Count > 0)
        {
            var platforms = App.Content.Platforms.Count > 0
                ? App.Content.Platforms.Select(p => (p.Key, p.Name)).ToList()
                : DefaultPlatforms.Select(p => (p.Key, p.Name)).ToList();

            Draft.Platforms.Clear();
            foreach (var (key, name) in platforms)
                if (detected.Contains(name)) Draft.Platforms.Add(key);
        }

        Put("官网地址", Draft.Website, repo.Homepage, text => Draft.Website = text);
        Put("GitHub 仓库", Draft.Github, repo.HtmlUrl, text => Draft.Github = text);
        Put("开源协议", Draft.License, repo.License, text => Draft.License = text);
        Put("开发者", Draft.Author, repo.Owner, text => Draft.Author = text);

        if (filled.Contains("开发者"))
            warnings.Add($"开发者已按仓库所有者填为 {repo.Owner}，若实际作者不同请手动修改。");

        if (repo.Homepage.Contains("apps.microsoft.com"))
            Put("应用商店地址", Draft.Store, repo.Homepage, text => Draft.Store = text);

        if (result.Facts.UsedPrerelease && release is not null)
            Put("提示条", Draft.Notice, $"当前提交的为预发布版本（{release.TagName}），稳定版需等待正式发布。",
                text => Draft.Notice = text);

        // ── 图标：接口拿不到软件图标，先用仓库所有者头像顶上 ──
        var icon = Draft.Icon.Trim();
        if (repo.OwnerAvatar.Length > 0 && icon != repo.OwnerAvatar
            && (icon.Length == 0 || _lastFilled.GetValueOrDefault("图标") == icon || overwrite))
        {
            Draft.Icon = repo.OwnerAvatar;
            _lastFilled["图标"] = repo.OwnerAvatar;
            filled.Add("图标");
            warnings.Add($"GitHub 接口无法获取软件图标，暂以仓库所有者（{repo.Owner}）的头像替代，需更换为官方图标。");
        }
        else if (icon.Length > 0)
        {
            kept.Add("图标");
        }

        // ── 下载项 ──
        var currentUrls = string.Join("\n", Draft.Downloads.Select(item => item.Url.Trim()).Where(url => url.Length > 0));
        var downloadsEdited = currentUrls.Length > 0 && currentUrls != _lastDownloadUrls;

        var hashByUrl = new Dictionary<string, string>();
        foreach (var item in Draft.Downloads)
        {
            var url = item.Url.Trim();
            if (url.Length > 0 && item.Hash.Trim().Length > 0 && !hashByUrl.ContainsKey(url))
                hashByUrl[url] = item.Hash.Trim();
        }

        if (result.Downloads.Count > 0)
        {
            if (downloadsEdited && !overwrite)
            {
                kept.Add("下载项");
                warnings.Add("下载项检测为手动填写，已保留原内容；如需使用读取到的链接，需勾选「覆盖已填写的内容」后重新读取。");
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
            if (downloadsEdited && !overwrite)
            {
                kept.Add("下载项");
            }
            else
            {
                SetDownloads(new List<(string, string, string, string, string)>
                {
                    ("最新版（网页）", "仓库无可直接下载的安装包，打开后为 Release 页面", "网页", url, ""),
                });
                filled.Add("下载项");
            }
        }

        // ── 需注意的地方 ──
        if (result.Facts.ReleaseFailed) warnings.Add("无法读取该仓库的版本信息（接口限流或网络异常），已填入仓库信息，版本号与安装包需手动补充。");
        if (result.Facts.NoRelease) warnings.Add("该仓库尚未发布任何 Release，版本号与安装包需手动填写。");
        if (result.Facts.NoAsset) warnings.Add("该 Release 中没有可下载的安装包（可能仅含源码包），需手动填写下载直链。");
        if (result.Facts.AssetSkipped > 0) warnings.Add($"已自动跳过 {result.Facts.AssetSkipped} 个非安装包文件（校验文件、调试符号、源码包等）。");
        if (result.Facts.Truncated) warnings.Add($"安装包数量较多，仅填入前 {result.Downloads.Count} 个。");
        if (result.Facts.UsedPrerelease && release is not null) warnings.Add($"当前使用预发布版本 {release.TagName}。");
        if (result.Facts.NewerPrereleaseTag.Length > 0) warnings.Add($"存在更新的预发布版 {result.Facts.NewerPrereleaseTag}。如需获取，需勾选「优先取最新预发布版」后重新读取。");
        if (repo.Archived) warnings.Add("该仓库已归档（不再维护），建议确认是否仍需收录。");

        RepoBox.Text = repo.HtmlUrl;
        Draft.GitHubRepo = repo.HtmlUrl;

        OkBar.Message = $"已读取 {repo.FullName}：最新版本 {(release?.TagName.Length > 0 ? release.TagName : "—")}，"
                        + $"共 {result.Downloads.Count} 个安装包（来源：{result.Via}）"
                        + (repo.License.Length > 0 ? $"，许可证 {repo.License}" : "");
        OkBar.IsOpen = true;

        BuildReport(filled, kept, warnings);
        Draft.Save();
    }

    private void BuildReport(List<string> filled, List<string> kept, List<string> warnings)
    {
        ReportHost.Children.Clear();

        if (filled.Count == 0 && kept.Count == 0 && warnings.Count == 0)
        {
            Report.Visibility = Visibility.Collapsed;
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
            ReportHost.Children.Add(row);
        }

        if (filled.Count > 0) Line("已填", string.Join("、", filled));
        if (kept.Count > 0) Line("已保留", string.Join("、", kept));
        if (warnings.Count > 0)
            Line("需注意", string.Join("\n", warnings.Select(text => "· " + text)),
                ThemeBrush.Get(this, "SystemFillColorCautionBrush"));

        Report.Visibility = Visibility.Visible;
    }

    /// <summary>整段重建下载项（一键读取用）。</summary>
    private void SetDownloads(List<(string Platform, string Note, string Size, string Url, string Hash)> items)
    {
        if (Draft is null) return;

        Draft.Downloads.Clear();
        foreach (var item in items)
        {
            Draft.Downloads.Add(new DownloadDraft
            {
                Platform = item.Platform,
                Note = item.Note,
                Size = item.Size,
                Url = item.Url,
                Hash = item.Hash,
            });
        }

        if (Draft.Downloads.Count == 0) Draft.Downloads.Add(new DownloadDraft());
        Draft.Renumber();

        _lastDownloadUrls = string.Join("\n", Draft.Downloads.Select(item => item.Url.Trim()));
    }
}
