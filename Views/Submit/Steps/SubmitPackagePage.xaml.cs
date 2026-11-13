using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace ClassSoftwareHub.Desktop.Views.Submit.Steps;

/// <summary>
/// 第 3 步：安装包与系统。
///
/// <para>
/// 每条下载项默认只露「平台」与「直链 / 上传」，其余四项收进「更多选项」。
/// 上传走站点 OSS 直传，成功后回填 <c>oss://</c> 键并把输入框锁住。
/// </para>
/// </summary>
public sealed partial class SubmitPackagePage : Page, ISubmitStep
{
    /// <summary>内容还没同步下来时的平台兜底（与 软件数据/platforms.json 的 key 一致）。</summary>
    private static readonly (string Key, string Name)[] DefaultPlatforms =
    {
        ("windows", "Windows"),
        ("macos", "macOS"),
        ("linux", "Linux"),
        ("android", "Android"),
        ("ios", "iOS"),
        ("web", "网页版"),
    };

    private readonly SubmitDraft? _draft;
    private bool _syncing;

    private SubmitDraft? Draft => _draft;

    public SubmitPackagePage()
    {
        _draft = SubmitSession.Current?.Draft ?? SubmitDraft.CreateEmpty();
        InitializeComponent();

        BuildPlatformChecks();
        SyncFromDraft();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        SyncFromDraft();
    }

    /// <summary>平台复选框按 <c>ContentStore.Platforms</c> 生成，清单新增平台会自动多出一个框。</summary>
    private void BuildPlatformChecks()
    {
        var platforms = App.Content.Platforms.Count > 0
            ? App.Content.Platforms.Select(p => (p.Key, p.Name)).ToList()
            : DefaultPlatforms.Select(p => (p.Key, p.Name)).ToList();

        PlatformChecks.Children.Clear();
        foreach (var (key, name) in platforms)
        {
            var box = new CheckBox { Content = name, Tag = key, MinWidth = 0 };
            box.Checked += Platform_Changed;
            box.Unchecked += Platform_Changed;
            PlatformChecks.Children.Add(box);
        }
    }

    private void SyncFromDraft()
    {
        if (_draft is null) return;

        _syncing = true;
        foreach (var box in PlatformChecks.Children.OfType<CheckBox>())
            box.IsChecked = _draft.Platforms.Contains(box.Tag as string ?? "");
        _syncing = false;

        // 回到本页时收掉上一次的提示
        ErrorBar.IsOpen = false;
        _draft.Renumber();
        SyncAddButton();
    }

    /// <summary>任一字段改动就收起错误提示。</summary>
    private void Field_Changed(object sender, TextChangedEventArgs e)
    {
        if (ErrorBar is not null) ErrorBar.IsOpen = false;
    }

    private void Platform_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _draft is null) return;

        var key = (sender as CheckBox)?.Tag as string ?? "";
        if (key.Length == 0) return;

        ErrorBar.IsOpen = false;

        if ((sender as CheckBox)?.IsChecked == true)
        {
            if (!_draft.Platforms.Contains(key)) _draft.Platforms.Add(key);
        }
        else
        {
            _draft.Platforms.Remove(key);
        }
    }

    // ── 下载项增删 ───────────────────────────────────────────────────────

    private void AddDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null || _draft.Downloads.Count >= SubmitDraft.MaxDownloads) return;

        _draft.Downloads.Add(new DownloadDraft());
        _draft.Renumber();
        SyncAddButton();
    }

    private void RemoveDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null || _draft.Downloads.Count <= 1) return;
        if ((sender as FrameworkElement)?.Tag is not DownloadDraft item) return;

        _draft.Downloads.Remove(item);
        _draft.Renumber();
        SyncAddButton();
    }

    /// <summary>到上限就把「添加」灰掉（与网页端同一表现，别让人点了没反应）。</summary>
    private void SyncAddButton()
        => AddDownloadButton.IsEnabled = _draft is not null && _draft.Downloads.Count < SubmitDraft.MaxDownloads;

    // ── 上传到本站 ───────────────────────────────────────────────────────

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (Draft is null) return;
        if ((sender as FrameworkElement)?.Tag is not DownloadDraft item) return;
        if (item.Uploading) return;

        var picked = await PickFileAsync("选择要上传到本站的文件", new[] { "*" });
        if (picked is null) return;

        await UploadToSiteAsync(picked.Path, item);
    }

    private static async Task UploadToSiteAsync(string filePath, DownloadDraft item)
    {
        item.Uploading = true;
        item.UploadProgress = 0;
        item.UploadHint = "正在上传…";

        try
        {
            var progress = new Progress<UploadProgress>(p =>
            {
                item.UploadProgress = p.Fraction;
                item.UploadHint = $"正在上传 {HumanSize(p.Sent)} / {HumanSize(p.Total)}（{p.Fraction * 100:0}%）";
            });

            var result = await OssUpload.UploadAsync(filePath, OssPurpose.File, progress);

            item.Url = result.Url;
            // ⛔ 必须显式标「文件直链」：oss:// 键推断不出类型，停在自动识别会被当成网页跳转
            item.Kind = "file";
            item.UploadHint = result.OrphanMinutes > 0
                ? $"已上传，请在 {result.OrphanMinutes} 分钟内完成提交，否则会被自动清理。"
                : "已上传。";
        }
        catch (OssUploadException ex)
        {
            item.UploadHint = ex.Code switch
            {
                OssUploadErrorCode.TooLarge =>
                    $"文件超过上限（{HumanSize(OssUpload.MaxBytes)}），请改填官网或 GitHub Releases 直链。",
                OssUploadErrorCode.SignFailed => "站点没有接受这次上传：" + ex.Message,
                OssUploadErrorCode.Unreachable => "连不上提交服务，请检查网络后重试。",
                OssUploadErrorCode.Network => "传输中断，请检查网络后重试。",
                OssUploadErrorCode.Aborted => "已取消上传。",
                _ => "站点拒绝了这次上传：" + ex.Message,
            };
        }
        catch (Exception ex)
        {
            item.UploadHint = "上传失败：" + ex.Message;
        }
        finally
        {
            item.Uploading = false;
        }
    }

    private static async Task<StorageFile?> PickFileAsync(string commitText, IReadOnlyList<string> filters)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.Downloads,
            CommitButtonText = commitText,
        };
        foreach (var filter in filters) picker.FileTypeFilter.Add(filter);

        var owner = SubmitSession.Current?.WindowHandle ?? IntPtr.Zero;
        if (owner != IntPtr.Zero) WinRT.Interop.InitializeWithWindow.Initialize(picker, owner);

        return await picker.PickSingleFileAsync();
    }

    private static string HumanSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / 1073741824.0:0.##} GB",
        >= 1L << 20 => $"{bytes / 1048576.0:0.#} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.#} KB",
        _ => bytes + " B",
    };

    // ── 导航壳接口 ───────────────────────────────────────────────────────

    public string? Validate() => _draft?.ValidatePackage();

    public void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
