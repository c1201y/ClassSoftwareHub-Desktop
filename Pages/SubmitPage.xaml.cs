using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Services.License;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace ClassSoftwareHub.Desktop.Pages;






public sealed partial class SubmitPage : Page
{
    
    private static int SubmitTimeoutMs => Services.SubmitEndpoint.TimeoutMs;
    
    private const string RepoNewFileUrl = "https://github.com/c1201y/ClassSoftwareHub/new/main/submissions";
    
    private static readonly int[] HashLengths = { 32, 40, 56, 64, 96, 128 };

    
    private static readonly HttpClient Http = new(new AuthHandler(new HttpClientHandler()));

    private static string DataDir => Core.AppPaths.DataDir;
    private static string DraftFile => System.IO.Path.Combine(DataDir, "submit-draft.json");

    private sealed class DownloadDraft
    {
        public string Platform = "";
        public string Size = "";
        public string Note = "";
        public string Url = "";
        public string Hash = "";

        
        public string Kind = "";
    }

    
    
    
    
    
    
    private static readonly (string Label, string Kind)[] DownloadKinds =
    {
        ("自动识别（按链接判断）", ""),
        ("官网直链 / 直接下载", "file"),
        ("网页跳转", "netdisk"),
        ("应用商店", "store"),
    };

    
    
    
    
    private const int MaxDownloads = 5;

    
    private static int KindIndexOf(string kind)
    {
        if (kind.Length == 0) return -1;
        for (var i = 1; i < DownloadKinds.Length; i++)
            if (DownloadKinds[i].Kind == kind) return i;
        return -1;
    }

    
    
    
    
    
    private sealed class DownloadFields
    {
        public TextBox Platform = null!;
        public TextBox Size = null!;
        public TextBox Note = null!;
        public TextBox Url = null!;
        public TextBox Hash = null!;
        public ComboBox Kind = null!;
    }

    private readonly List<DownloadDraft> _downloads = new();
    
    private readonly List<DownloadFields> _fields = new();
    private readonly List<Border> _cards = new();
    private readonly List<TextBlock> _titles = new();
    private bool _busy;
    
    private Dictionary<string, object?>? _pending;

    public SubmitPage()
    {
        InitializeComponent();

        foreach (var category in App.Content.Categories)
            CategoryCombo.Items.Add(new ComboBoxItem { Content = category.Name, Tag = category.Key });
        CategoryCombo.SelectedIndex = -1;

        BuildPlatformChecks();

        AddDownload();
        RefreshDraftButton();
    }

    
    
    
    
    private static readonly (string Key, string Name)[] DefaultPlatforms =
    {
        ("windows", "Windows"),
        ("macos", "macOS"),
        ("linux", "Linux"),
        ("android", "Android"),
        ("ios", "iOS"),
        ("web", "网页版"),
    };

    
    
    
    
    private void BuildPlatformChecks()
    {
        var platforms = App.Content.Platforms.Count > 0
            ? App.Content.Platforms.Select(p => (p.Key, p.Name)).ToList()
            : DefaultPlatforms.Select(p => (p.Key, p.Name)).ToList();

        PlatformChecks.Children.Clear();
        foreach (var (key, name) in platforms)
            PlatformChecks.Children.Add(new CheckBox { Content = name, Tag = key, MinWidth = 0 });
    }

    
    private List<string> SelectedPlatforms()
        => PlatformChecks.Children
            .OfType<CheckBox>()
            .Where(c => c.IsChecked == true)
            .Select(c => c.Tag as string ?? "")
            .Where(k => k.Length > 0)
            .ToList();

    
    private void AddDownload_Click(object sender, RoutedEventArgs e) => AddDownload();

    private void AddDownload()
    {
        
        if (_downloads.Count >= MaxDownloads) return;

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

        var platformBox = Field("平台（如 Windows x64 安装版）", "Windows x64 安装版", null, value => draft.Platform = value);
        stack.Children.Add(platformBox);

        var row = new Grid { ColumnSpacing = 14 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var sizeBox = Field("体积", "如 1.6 MB", null, value => draft.Size = value);
        var noteBox = Field("备注", "如 便携版 / 需要管理员权限", null, value => draft.Note = value);
        row.Children.Add(sizeBox);
        Grid.SetColumn(noteBox, 1);
        row.Children.Add(noteBox);
        stack.Children.Add(row);

        
        var urlBar = new ProgressBar
        {
            Height = 4,
            Minimum = 0,
            Maximum = 1,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        
        
        var urlHint = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Text = "没有自己的网盘就点右侧按钮，把文件传到本站存储；上传成功后这行会锁住。",
        };

        var urlGrid = new Grid { ColumnSpacing = 10 };
        urlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        urlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var urlBox = Field("下载直链 *", "https://…/setup.exe", null, value => draft.Url = value);

        
        
        
        var kindCombo = new ComboBox
        {
            Header = "下载方式",
            PlaceholderText = DownloadKinds[0].Label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            
            SelectedIndex = -1,
        };
        foreach (var (label, _) in DownloadKinds)
            kindCombo.Items.Add(new ComboBoxItem { Content = label });
        kindCombo.SelectedIndex = -1;
        kindCombo.SelectionChanged += (_, _) =>
            draft.Kind = kindCombo.SelectedIndex >= 1 ? DownloadKinds[kindCombo.SelectedIndex].Kind : "";

        var uploadButton = new Button { Content = "上传", MinWidth = 90, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(uploadButton, 1);
        uploadButton.Click += async (_, _) =>
        {
            var picked = await PickFileAsync("选择要上传到本站的文件", new[] { "*" });
            if (picked is null) return;

            await UploadToSiteAsync(picked.Path, Services.OssPurpose.File, uploadButton, urlBar, urlHint, url =>
            {
                draft.Url = url;
                urlBox.Text = url;
                
                urlBox.IsReadOnly = true;
                
                
                
                draft.Kind = "file";
                kindCombo.SelectedIndex = 1;
            });
        };

        urlGrid.Children.Add(urlBox);
        urlGrid.Children.Add(uploadButton);
        
        var urlBlock = new StackPanel { Spacing = 4 };
        urlBlock.Children.Add(urlGrid);
        urlBlock.Children.Add(urlBar);
        urlBlock.Children.Add(urlHint);
        stack.Children.Add(urlBlock);
        stack.Children.Add(kindCombo);

        var hashBox = Field("校验值（选填，用于防篡改）", "纯十六进制，算法按位数自动识别", null, value => draft.Hash = value);
        stack.Children.Add(hashBox);

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
        _fields.Add(new DownloadFields { Platform = platformBox, Size = sizeBox, Note = noteBox, Url = urlBox, Hash = hashBox, Kind = kindCombo });
        _titles.Add(title);
        DownloadsHost.Children.Add(card);
        RenumberDownloads();
        SyncAddButton();
    }

    private void RemoveDownload(DownloadDraft draft)
    {
        if (_downloads.Count <= 1) return;   
        var index = _downloads.IndexOf(draft);
        if (index < 0) return;
        _downloads.RemoveAt(index);
        _fields.RemoveAt(index);
        DownloadsHost.Children.Remove(_cards[index]);
        _cards.RemoveAt(index);
        _titles.RemoveAt(index);
        RenumberDownloads();
        SyncAddButton();
    }

    private void RenumberDownloads()
    {
        for (var i = 0; i < _titles.Count; i++) _titles[i].Text = $"下载项 {i + 1}";
    }

    
    private void SyncAddButton() => AddDownloadButton.IsEnabled = _downloads.Count < MaxDownloads;

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

    
    
    
    
    

    
    private async void IconUpload_Click(object sender, RoutedEventArgs e)
    {
        var picked = await PickFileAsync("选择图标图片",
            new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".ico" }, PickerLocationId.PicturesLibrary);
        if (picked is null) return;

        long size = 0;
        try { size = (long)(await picked.GetBasicPropertiesAsync()).Size; } catch {  }
        if (size > Services.OssUpload.IconInputMaxBytes)
        {
            IconUploadHint.Text = $"这张图有 {HumanSize(size)}，请换一张小一些的"
                                  + $"（上限 {HumanSize(Services.OssUpload.IconInputMaxBytes)}）。";
            IconUploadHint.Visibility = Visibility.Visible;
            return;
        }

        
        var shrunken = await Services.IconResize.ShrinkToTempAsync(picked.Path);
        try
        {
            await UploadToSiteAsync(shrunken ?? picked.Path, Services.OssPurpose.Icon,
                IconUploadButton, null, IconUploadHint, url => IconBox.Text = url);
        }
        finally
        {
            if (shrunken is not null)
            {
                try { File.Delete(shrunken); } catch {  }
            }
        }
    }

    
    private async Task UploadToSiteAsync(
        string filePath,
        Services.OssPurpose purpose,
        Button button,
        ProgressBar? bar,
        TextBlock hint,
        Action<string> onDone)
    {
        button.IsEnabled = false;
        if (bar is not null)
        {
            bar.Value = 0;
            bar.Visibility = Visibility.Visible;
        }
        hint.Visibility = Visibility.Visible;
        hint.Text = "正在上传…";

        try
        {
            var progress = new Progress<Services.UploadProgress>(p =>
            {
                if (bar is not null) bar.Value = p.Fraction;
                hint.Text = $"正在上传 {HumanSize(p.Sent)} / {HumanSize(p.Total)}（{p.Fraction * 100:0}%）";
            });

            var result = await Services.OssUpload.UploadAsync(filePath, purpose, progress);
            onDone(result.Url);
            hint.Text = result.OrphanMinutes > 0
                ? $"已上传，请在 {result.OrphanMinutes} 分钟内完成提交，否则会被自动清理。"
                : "已上传。";
        }
        catch (Services.OssUploadException ex)
        {
            hint.Text = DescribeUploadError(ex);
        }
        catch (Exception ex)
        {
            hint.Text = "上传失败：" + ex.Message;
        }
        finally
        {
            if (bar is not null) bar.Visibility = Visibility.Collapsed;
            button.IsEnabled = true;
        }
    }

    
    private static string DescribeUploadError(Services.OssUploadException ex) => ex.Code switch
    {
        Services.OssUploadErrorCode.TooLarge =>
            $"文件超过上限（{HumanSize(Services.OssUpload.MaxBytes)}），请改填官网或 GitHub Releases 直链。",
        Services.OssUploadErrorCode.SignFailed => "站点没有接受这次上传：" + ex.Message,
        Services.OssUploadErrorCode.Unreachable => "连不上提交服务，请检查网络后重试。",
        Services.OssUploadErrorCode.Network => "传输中断，请检查网络后重试。",
        Services.OssUploadErrorCode.Aborted => "已取消上传。",
        _ => "站点拒绝了这次上传：" + ex.Message,
    };

    
    private static async Task<StorageFile?> PickFileAsync(
        string commitText,
        IReadOnlyList<string> filters,
        PickerLocationId start = PickerLocationId.Downloads)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = start,
            CommitButtonText = commitText,
        };
        foreach (var filter in filters) picker.FileTypeFilter.Add(filter);

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));
        return await picker.PickSingleFileAsync();
    }

    private static string HumanSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / 1073741824.0:0.##} GB",
        >= 1L << 20 => $"{bytes / 1048576.0:0.#} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.#} KB",
        _ => bytes + " B",
    };

    
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
            ImportErrorBar.Message = "仓库地址为必填项，例如 github.com/owner/repo。";
            ImportErrorBar.IsOpen = true;
            return;
        }

        _importing = true;
        ImportButton.IsEnabled = false;
        ImportButton.Content = "正在读取";

        try
        {
            var result = await Services.GithubImport.FetchAsync(RepoBox.Text, PrereleaseBox.IsChecked == true, 12);
            ApplyImport(result);
        }
        catch (Services.GithubImportException exception)
        {
            ImportErrorBar.Message = exception.Kind switch
            {
                Services.GithubImportErrorKind.Invalid => "无法识别该仓库地址，需使用 github.com/owner/repo 形式。",
                Services.GithubImportErrorKind.NotFound => "未找到该仓库（可能为私有仓库或地址有误）。",
                Services.GithubImportErrorKind.RateLimit => "GitHub 接口调用次数已达上限（未登录时每小时 60 次，按网络出口共享），可稍后重试。",
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

        
        var takenIds = App.Content.Apps.Select(app => app.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suggestedId = Services.GithubImport.RepoToId(repo.Repo);
        if (suggestedId.Length > 0 && takenIds.Contains(suggestedId))
        {
            var suffix = 2;
            while (takenIds.Contains($"{suggestedId}-{suffix}") && suffix < 100) suffix++;
            var corrected = $"{suggestedId}-{suffix}";
            warnings.Add($"站内已存在 id 为 {suggestedId} 的软件，已自动改为 {corrected}。");
            suggestedId = corrected;
        }
        Put("软件 ID", IdBox.Text, suggestedId, text => IdBox.Text = text);
        if (IdBox.Text.Trim().Length > 0 && takenIds.Contains(IdBox.Text.Trim()))
            warnings.Add($"站内已存在 id 为 {IdBox.Text.Trim()} 的软件，直接提交会产生冲突，建议更换。");

        
        Put("软件名称", NameBox.Text, repo.Repo, text => NameBox.Text = text);
        Put("一句话简介", TaglineBox.Text, Services.GithubImport.ToTagline(repo.Description), text => TaglineBox.Text = text);
        Put("详细介绍", DescBox.Text, repo.Description, text => DescBox.Text = text);
        Put("版本号", VersionBox.Text, release?.TagName ?? "", text => VersionBox.Text = text);
        Put("系统限制", SystemBox.Text, result.System, text => SystemBox.Text = text);

        
        
        var detectedPlatforms = result.System
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (detectedPlatforms.Count > 0)
        {
            foreach (var box in PlatformChecks.Children.OfType<CheckBox>())
            {
                if (box.Content is string platformName && detectedPlatforms.Contains(platformName))
                    box.IsChecked = true;
            }
        }

        Put("官网地址", WebsiteBox.Text, repo.Homepage, text => WebsiteBox.Text = text);
        Put("GitHub 仓库", GithubBox.Text, repo.HtmlUrl, text => GithubBox.Text = text);

        
        
        Put("开源协议", LicenseBox.Text, repo.License, text => LicenseBox.Text = text);
        
        
        Put("开发者", AuthorBox.Text, repo.Owner, text => AuthorBox.Text = text);
        if (filled.Contains("开发者"))
            warnings.Add($"开发者已按仓库所有者填为 {repo.Owner}，若实际作者不同请手动修改。");

        if (repo.Homepage.Contains("apps.microsoft.com"))
            Put("应用商店地址", StoreBox.Text, repo.Homepage, text => StoreBox.Text = text);
        
        if (result.Facts.UsedPrerelease && release is not null)
            Put("提示条", NoticeBox.Text, $"当前提交的为预发布版本（{release.TagName}），稳定版需等待正式发布。", text => NoticeBox.Text = text);

        
        var icon = IconBox.Text.Trim();
        if (repo.OwnerAvatar.Length > 0 && icon != repo.OwnerAvatar
            && (icon.Length == 0 || _lastFilled.GetValueOrDefault("图标") == icon || overwrite))
        {
            IconBox.Text = repo.OwnerAvatar;
            _lastFilled["图标"] = repo.OwnerAvatar;
            filled.Add("图标");
            warnings.Add($"GitHub 接口无法获取软件图标，暂以仓库所有者（{repo.Owner}）的头像替代，需更换为官方图标。");
        }
        else if (icon.Length > 0)
        {
            kept.Add("图标");
        }

        
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
            if (downloadsEditedByUser && !overwrite)
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

        
        if (result.Facts.ReleaseFailed) warnings.Add("无法读取该仓库的版本信息（接口限流或网络异常），已填入仓库信息，版本号与安装包需手动补充。");
        if (result.Facts.NoRelease) warnings.Add("该仓库尚未发布任何 Release，版本号与安装包需手动填写。");
        if (result.Facts.NoAsset) warnings.Add("该 Release 中没有可下载的安装包（可能仅含源码包），需手动填写下载直链。");
        if (result.Facts.AssetSkipped > 0) warnings.Add($"已自动跳过 {result.Facts.AssetSkipped} 个非安装包文件（校验文件、调试符号、源码包等）。");
        if (result.Facts.Truncated) warnings.Add($"安装包数量较多，仅填入前 {result.Downloads.Count} 个。");
        if (result.Facts.UsedPrerelease && release is not null) warnings.Add($"当前使用预发布版本 {release.TagName}。");
        if (result.Facts.NewerPrereleaseTag.Length > 0) warnings.Add($"存在更新的预发布版 {result.Facts.NewerPrereleaseTag}。如需获取，需勾选「优先取最新预发布版」后重新读取。");
        if (repo.Archived) warnings.Add("该仓库已归档（不再维护），建议确认是否仍需收录。");

        
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

    
    private void SetDownloads(List<(string Platform, string Note, string Size, string Url, string Hash)> items)
    {
        _downloads.Clear();
        _fields.Clear();
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
            FillCard(_fields[^1], draft);
        }
        if (_downloads.Count == 0) AddDownload();
        _lastDownloadUrls = string.Join("\n", _downloads.Select(item => item.Url.Trim()));
    }

    
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
                
                
                if (item.Kind.Length > 0) entry["kind"] = item.Kind;
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
            
            
            ["platforms"] = SelectedPlatforms(),
            ["website"] = WebsiteBox.Text.Trim(),
            ["github"] = GithubBox.Text.Trim(),
            
            ["author"] = AuthorBox.Text.Trim(),
            ["license"] = LicenseBox.Text.Trim(),
            ["notice"] = NoticeBox.Text.Trim(),
            ["store"] = StoreBox.Text.Trim(),
            ["downloads"] = downloads,
            
            
            ["_联系方式"] = EncryptContact(),
        };

        var sortText = SortBox.Text.Trim();
        if (sortText.Length > 0 && long.TryParse(sortText, out var sort)) payload["sort"] = sort;

        return payload;
    }

    
    private string EncryptContact()
    {
        var contact = ContactBox.Text.Trim();
        if (contact.Length == 0) return "";
        try { return Services.AgeEncryption.EncryptToArmor(contact); }
        catch { return ""; } 
    }

    
    private string? Validate(Dictionary<string, object?> payload)
    {
        
        
        if (((List<string>)payload["platforms"]!).Count == 0)
            return "请在「适用系统」里至少勾选一项。";

        var missing = ((string?)payload["id"] ?? "").Length == 0
                      || ((string?)payload["name"] ?? "").Length == 0
                      || ((string?)payload["category"] ?? "").Length == 0
                      || ((string?)payload["tagline"] ?? "").Length == 0
                      || ((string?)payload["description"] ?? "").Length == 0
                      || ((string?)payload["_联系方式"] ?? "").Length == 0
                      || ((List<Dictionary<string, object?>>)payload["downloads"]!).Count == 0;
        if (missing) return "带 * 的必填项尚未填写完整（至少需一条下载直链）。";

        var downloads = (List<Dictionary<string, object?>>)payload["downloads"]!;
        for (var i = 0; i < downloads.Count; i++)
        {
            if (downloads[i].TryGetValue("hash", out var hash) && hash is string text && !IsHashLike(text))
                return $"第 {i + 1} 个下载项的校验值格式不正确：需为纯十六进制，位数需匹配一种算法（32/40/56/64/96/128）。";
        }
        return null;
    }

    
    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var payload = BuildPayload();
        var error = Validate(payload);
        if (error is not null)
        {
            ShowResult(false, "无法提交", error);
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
                
                SaveDraft(payload);
                _pending = payload;
                RefreshDraftButton();
                ShowResult(false, "提交失败", "无法连接提交服务（网络或地区限制）。内容已保存于本机，可重试或使用下方的备用方式。");
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
                    ? "提交成功，审核通过后上架。"
                    : reply.Value.Message!);
            }
            else
            {
                ShowResult(false, "提交未通过校验", reply.Value.Error ?? "服务端未接受该内容，检查后可重试。");
            }
        }
        catch (Exception ex)
        {
            ShowResult(false, "提交失败", "发生意外错误：" + ex.Message);
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
        SubmitButton.Content = busy ? "正在提交" : "提交";
        FallbackRetryButton.IsEnabled = !busy;
        FallbackRetryButton.Content = busy ? "正在提交" : "重试";
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
                if (!hasSuccess && !hasError) continue;   

                var message = root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String ? msg.GetString() : null;
                return (new Reply(hasSuccess, message, hasError ? err.GetString() : null), baseUrl);
            }
            catch
            {
                
            }
        }
        return (null, null);
    }

    private static List<string> OrderedEndpoints() => Services.SubmitEndpoint.Ordered();

    private static void RememberEndpoint(string baseUrl) => Services.SubmitEndpoint.Remember(baseUrl);

    
    private static void SaveDraft(Dictionary<string, object?> payload)
    {
        try
        {
            System.IO.Directory.CreateDirectory(DataDir);
            System.IO.File.WriteAllText(DraftFile, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch {  }
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
        catch {  }
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
        
        var contact = Text("_联系方式");
        ContactBox.Text = contact.StartsWith("-----BEGIN AGE", StringComparison.Ordinal) ? "" : contact;
        VersionBox.Text = Text("version");
        SizeBox.Text = Text("size");
        SystemBox.Text = Text("system");

        
        
        if (draft.TryGetValue("platforms", out var platsValue) && platsValue is JsonElement plats
            && plats.ValueKind == JsonValueKind.Array)
        {
            var keys = plats.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? "")
                .ToHashSet();

            foreach (var box in PlatformChecks.Children.OfType<CheckBox>())
                box.IsChecked = keys.Contains(box.Tag as string ?? "");
        }
        WebsiteBox.Text = Text("website");
        GithubBox.Text = Text("github");
        
        AuthorBox.Text = Text("author");
        LicenseBox.Text = Text("license");
        NoticeBox.Text = Text("notice");
        StoreBox.Text = Text("store");
        SortBox.Text = Text("sort");

        var categoryKey = Text("category");
        for (var i = 0; i < CategoryCombo.Items.Count; i++)
            if (CategoryCombo.Items[i] is ComboBoxItem item && (item.Tag as string) == categoryKey)
                CategoryCombo.SelectedIndex = i;

        
        _downloads.Clear();
        _fields.Clear();
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
                
                item.Kind = JsonText(element, "kind");
                FillCard(_fields[^1], item);
            }
        }
        if (_downloads.Count == 0) AddDownload();

        FallbackPanel.Visibility = Visibility.Collapsed;
        _pending = null;
        ShowResult(true, "已恢复", "上次未提交成功的内容已填回表单，检查后可再次提交。");
    }

    private static string JsonText(JsonElement element, string key)
        => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    
    private static void FillCard(DownloadFields fields, DownloadDraft draft)
    {
        fields.Platform.Text = draft.Platform;
        fields.Size.Text = draft.Size;
        fields.Note.Text = draft.Note;
        fields.Url.Text = draft.Url;
        fields.Hash.Text = draft.Hash;
        
        fields.Kind.SelectedIndex = KindIndexOf(draft.Kind);
        
        fields.Url.IsReadOnly = draft.Url.Trim().StartsWith("oss://", StringComparison.OrdinalIgnoreCase);
    }

    
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
        if (file is null) { Toast.Text = "无可导出的内容"; return; }
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
        if (file is null) { Toast.Text = "无可复制的内容"; return; }
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
        catch (Exception ex) { Toast.Text = "无法打开浏览器：" + ex.Message; }
    }
}
