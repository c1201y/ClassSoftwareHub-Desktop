using System;
using System.IO;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace ClassSoftwareHub.Desktop.Views.Submit.Steps;

/// <summary>
/// 第 2 步：软件信息。
///
/// <para>
/// 这里只有一处「会自己变」的字段 —— 软件 ID：名称每变一次，就按拼音 / 英文重新生成，
/// 直到用户亲手改过 ID（<see cref="SubmitDraft.IdEdited"/>）为止。
/// </para>
///
/// <para>
/// ⛔ ID 输入框刻意**不做双向绑定**：自动生成要写回文本框，绑定回写又会触发 TextChanged，
/// 一不留神就把「自动生成」记成「用户手改」，从此不再跟随名称。这里统一走 <c>_syncing</c> 守卫。
/// </para>
/// </summary>
public sealed partial class SubmitInfoPage : Page, ISubmitStep
{
    private readonly SubmitDraft? _draft;
    private bool _syncing;

    private SubmitDraft? Draft => _draft;

    public SubmitInfoPage()
    {
        // x:Bind 在 InitializeComponent 里就求值，草稿必须先就位
        _draft = SubmitSession.Current?.Draft ?? SubmitDraft.CreateEmpty();
        InitializeComponent();

        BuildCategories();
        SyncFromDraft();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // 页面被 Frame 缓存：从第 1 步读取回来、或「再提交一个」清空后，都要把界面拉回草稿
        SyncFromDraft();
    }

    private void BuildCategories()
    {
        CategoryCombo.Items.Clear();
        foreach (var category in App.Content.Categories)
            CategoryCombo.Items.Add(new ComboBoxItem { Content = category.Name, Tag = category.Key });
        CategoryCombo.SelectedIndex = -1;
    }

    private void SyncFromDraft()
    {
        if (_draft is null) return;

        _syncing = true;
        NameBox.Text = _draft.Name;
        IdBox.Text = _draft.Id;

        CategoryCombo.SelectedIndex = -1;
        for (var i = 0; i < CategoryCombo.Items.Count; i++)
        {
            if (CategoryCombo.Items[i] is ComboBoxItem item && (item.Tag as string) == _draft.Category)
            {
                CategoryCombo.SelectedIndex = i;
                break;
            }
        }
        _syncing = false;

        // 回到本页时把上一次的提示收掉，别让「请填写软件名称」挂在已填好的字段上方
        ErrorBar.IsOpen = false;
        UpdateIdHint();
        UpdateIconPreview();
    }

    /// <summary>任一字段改动就收起错误提示 —— 提示是「还缺什么」，补上了就该消失。</summary>
    private void Field_Changed(object sender, TextChangedEventArgs e) => ClearError();

    private void ClearError()
    {
        if (ErrorBar is not null) ErrorBar.IsOpen = false;
    }

    // ── 名称 → ID ────────────────────────────────────────────────────────

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || _draft is null) return;

        // 初始化阶段控件还没全建好就触发过一次 TextChanged，⛔ 不能假定 IdBox / IdHint 已存在
        if (IdBox is null || IdHint is null) return;

        ClearError();
        if (_draft.IdEdited) return;

        var generated = SoftwareSlug.EnsureUnique(
            SoftwareSlug.FromName(NameBox.Text), SoftwareSlug.TakenIds());

        _syncing = true;
        IdBox.Text = generated;
        _draft.Id = generated;
        _syncing = false;

        ClearError();
        UpdateIdHint();
    }

    private void IdBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || _draft is null) return;
        if (IdHint is null) return;

        ClearError();

        _draft.Id = IdBox.Text.Trim();
        _draft.IdEdited = true;
        UpdateIdHint();
    }

    private void UpdateIdHint()
    {
        var id = IdBox.Text.Trim();

        if (id.Length == 0)
        {
            IdHint.Text = "填写名称后自动生成，也可手动填写。";
            IdHint.ClearValue(TextBlock.ForegroundProperty);
            return;
        }

        if (!SoftwareSlug.IsValid(id))
        {
            IdHint.Text = "只能使用小写字母、数字与短横线，且不能以横线开头或结尾。";
            IdHint.Foreground = ThemeBrush.Get(this, "SystemFillColorCautionBrush");
            return;
        }

        if (SoftwareSlug.IsTaken(id))
        {
            IdHint.Text = "站内已有同名 ID，提交后会冲突，建议换一个。";
            IdHint.Foreground = ThemeBrush.Get(this, "SystemFillColorCautionBrush");
            return;
        }

        IdHint.Text = $"详情页地址：#/download/{id}";
        IdHint.ClearValue(TextBlock.ForegroundProperty);
    }

    private void Category_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _draft is null) return;
        _draft.Category = (CategoryCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        ClearError();
    }

    // ── 图标预览与上传 ───────────────────────────────────────────────────

    private void IconBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IconPreview is null) return;
        ClearError();
        UpdateIconPreview();
    }

    private void UpdateIconPreview()
    {
        var url = (_draft?.Icon ?? "").Trim();

        if (url.Length == 0)
        {
            IconPreview.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            IconPreview.Source = new BitmapImage(new Uri(url));
            IconPreview.Visibility = Visibility.Visible;
        }
        catch
        {
            // 链接不是合法地址：保留占位图标，不在这一处拦着用户
            IconPreview.Visibility = Visibility.Collapsed;
        }
    }

    private async void IconUpload_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null) return;

        var picked = await PickFileAsync("选择图标图片",
            new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".ico" }, PickerLocationId.PicturesLibrary);
        if (picked is null) return;

        long size = 0;
        try { size = (long)(await picked.GetBasicPropertiesAsync()).Size; } catch { /* 拿不到大小就照常往下走 */ }

        if (size > OssUpload.IconInputMaxBytes)
        {
            IconHint.Text = $"这张图有 {HumanSize(size)}，请换一张小一些的"
                            + $"（上限 {HumanSize(OssUpload.IconInputMaxBytes)}）。";
            IconHint.Visibility = Visibility.Visible;
            return;
        }

        // 缩不了（不是图片 / 编解码器不认 / 压完更大）就用原文件 —— 压缩只是优化，不是门槛
        var shrunken = await IconResize.ShrinkToTempAsync(picked.Path);
        try
        {
            await UploadIconAsync(shrunken ?? picked.Path);
        }
        finally
        {
            if (shrunken is not null)
            {
                try { File.Delete(shrunken); } catch { /* 删不掉就留给系统清临时目录 */ }
            }
        }
    }

    private async Task UploadIconAsync(string path)
    {
        if (_draft is null) return;

        IconUploadButton.IsEnabled = false;
        IconBar.Value = 0;
        IconBar.Visibility = Visibility.Visible;
        IconHint.Visibility = Visibility.Visible;
        IconHint.Text = "正在上传…";

        try
        {
            var progress = new Progress<UploadProgress>(p =>
            {
                IconBar.Value = p.Fraction;
                IconHint.Text = $"正在上传 {HumanSize(p.Sent)} / {HumanSize(p.Total)}（{p.Fraction * 100:0}%）";
            });

            var result = await OssUpload.UploadAsync(path, OssPurpose.Icon, progress);

            _draft.Icon = result.Url;
            IconBox.Text = result.Url;
            UpdateIconPreview();

            IconHint.Text = result.OrphanMinutes > 0
                ? $"已上传，请在 {result.OrphanMinutes} 分钟内完成提交，否则会被自动清理。"
                : "已上传。";
        }
        catch (OssUploadException ex)
        {
            IconHint.Text = ex.Code switch
            {
                OssUploadErrorCode.TooLarge => "图片超过上限，请换一张更小的。",
                OssUploadErrorCode.SignFailed => "站点没有接受这次上传：" + ex.Message,
                OssUploadErrorCode.Unreachable => "连不上提交服务，请检查网络后重试。",
                OssUploadErrorCode.Network => "传输中断，请检查网络后重试。",
                OssUploadErrorCode.Aborted => "已取消上传。",
                _ => "站点拒绝了这次上传：" + ex.Message,
            };
        }
        catch (Exception ex)
        {
            IconHint.Text = "上传失败：" + ex.Message;
        }
        finally
        {
            IconBar.Visibility = Visibility.Collapsed;
            IconUploadButton.IsEnabled = true;
        }
    }

    /// <summary>弹系统选文件框；owner 必须是向导窗口，否则会挂到主窗口上、被挡在后面。</summary>
    private static async Task<StorageFile?> PickFileAsync(
        string commitText,
        System.Collections.Generic.IReadOnlyList<string> filters,
        PickerLocationId start = PickerLocationId.Downloads)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = start,
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

    public string? Validate() => _draft?.ValidateInfo();

    public void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
