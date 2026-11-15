using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace ClassSoftwareHub.Desktop.Pages.Tools;




public sealed partial class QrCodeToolPage : Page
{
    private const string ModeText = "text";
    private const string ModeLink = "link";
    private const string ModeFile = "file";

    private string _mode = ModeText;
    private string? _filePath;
    private QrFileShareServer? _share;
    private DispatcherQueueTimer? _countdown;
    private byte[]? _png;
    private string _payload = "";

    public QrCodeToolPage()
    {
        InitializeComponent();
        ExpiryBox.SelectedIndex = 2;
        TextTab.IsChecked = true;
        ShowMode(ModeText);
        UpdateTextCount();
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (TextPanel is null) return;
        ShowMode(TextTab.IsChecked == true ? ModeText
            : LinkTab.IsChecked == true ? ModeLink
            : ModeFile);
    }

    private void ShowMode(string mode)
    {
        _mode = mode;
        StopShare();
        ResetPreview();

        TextPanel.Visibility = mode == ModeText ? Visibility.Visible : Visibility.Collapsed;
        LinkPanel.Visibility = mode == ModeLink ? Visibility.Visible : Visibility.Collapsed;
        FilePanel.Visibility = mode == ModeFile ? Visibility.Visible : Visibility.Collapsed;
        GenerateButton.IsEnabled = mode != ModeFile || _filePath is not null;
        StatusText.Text = "";
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TextCount is null) return;
        UpdateTextCount();
    }

    private void UpdateTextCount()
    {
        var length = TextInput.Text?.Length ?? 0;
        TextCount.Text = $"{length} 个字符；中文按 UTF-8 编码，容量约 800 字节。";
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        GenerateButton.IsEnabled = false;
        StatusText.Text = "正在生成…";

        try
        {
            string payload;
            StopShare();
            if (_mode == ModeText)
            {
                payload = TextInput.Text?.Trim() ?? "";
                if (payload.Length == 0)
                {
                    StatusText.Text = "请输入文本内容。";
                    return;
                }
            }
            else if (_mode == ModeLink)
            {
                payload = NormalizeLink(LinkInput.Text);
                if (payload.Length == 0)
                {
                    StatusText.Text = "请输入链接地址。";
                    return;
                }
            }
            else
            {
                if (_filePath is null || !File.Exists(_filePath))
                {
                    StatusText.Text = "请先选择文件。";
                    return;
                }

                payload = "";
            }

            var lifetime = SelectedLifetime();
            _share = _mode switch
            {
                ModeText => QrFileShareServer.StartText(payload, lifetime),
                ModeLink => QrFileShareServer.StartLink(payload, lifetime),
                _ => QrFileShareServer.Start(_filePath!, lifetime),
            };
            payload = _share.Url;

            await RenderAsync(payload);
            StartCountdown();
            StatusText.Text = _mode == ModeFile
                ? "二维码已生成；手机扫描后从本机下载文件。"
                : _mode == ModeLink
                    ? "二维码已生成；手机扫描后在本机地址有效期内跳转到链接。"
                    : "二维码已生成；手机扫描后在本机地址有效期内查看文本。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "生成失败：" + FriendlyError(ex);
        }
        finally
        {
            GenerateButton.IsEnabled = _mode != ModeFile || _filePath is not null;
        }
    }

    private async Task RenderAsync(string payload)
    {
        var result = QrCodeImage.Render(payload);
        _png = await QrCodeImage.EncodePngAsync(result.Pixels, result.Width, result.Height);
        _payload = payload;

        QrImage.Source = result.Bitmap;
        QrImage.Visibility = Visibility.Visible;
        EmptyBox.Visibility = Visibility.Collapsed;
        PayloadText.Text = payload;
        SaveButton.IsEnabled = true;
        CopyButton.IsEnabled = true;
    }

    private static string NormalizeLink(string? input)
    {
        var raw = input?.Trim() ?? "";
        if (raw.Length == 0) return "";

        var candidate = raw.Contains("://", StringComparison.Ordinal) ? raw : "https://" + raw;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return "";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return "";
        return candidate;
    }

    private TimeSpan? SelectedLifetime() => ExpiryBox.SelectedIndex switch
    {
        0 => TimeSpan.FromMinutes(5),
        1 => TimeSpan.FromMinutes(15),
        2 => TimeSpan.FromMinutes(30),
        3 => TimeSpan.FromMinutes(60),
        _ => null,
    };

    private void StartCountdown()
    {
        _countdown = DispatcherQueue.GetForCurrentThread()?.CreateTimer();
        if (_countdown is null) return;

        _countdown.Interval = TimeSpan.FromSeconds(1);
        _countdown.IsRepeating = true;
        _countdown.Tick += (_, _) => UpdateCountdown();
        _countdown.Start();
        UpdateCountdown();
    }

    private void UpdateCountdown()
    {
        if (_share is null) return;

        if (_share.ExpiresAt is null)
        {
            CountdownText.Text = "内容地址在本次运行期间有效。";
            return;
        }

        if (_share.IsExpired)
        {
            StopShare();
            CountdownText.Text = "内容地址已过期，请重新生成。";
            SaveButton.IsEnabled = false;
            CopyButton.IsEnabled = false;
            StatusText.Text = "链接已失效；二维码图片不再代表可用地址。";
            return;
        }

        var left = _share.ExpiresAt.Value - DateTimeOffset.UtcNow;
        var minutes = Math.Max(0, (int)left.TotalMinutes);
        var seconds = Math.Max(0, left.Seconds);
        CountdownText.Text = $"内容地址剩余 {minutes:00}:{seconds:00}。";
    }

    private void StopShare()
    {
        _countdown?.Stop();
        _countdown = null;
        if (_share is not null)
        {
            _share.Dispose();
            _share = null;
        }
    }

    private void ResetPreview()
    {
        _png = null;
        _payload = "";
        QrImage.Source = null;
        QrImage.Visibility = Visibility.Collapsed;
        EmptyBox.Visibility = Visibility.Visible;
        PayloadText.Text = "";
        CountdownText.Text = "";
        SaveButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
    }

    private async void PickFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.Downloads,
            };
            picker.FileTypeFilter.Add("*");
            Core.WindowHandle.BindPicker(picker, this);

            var file = await picker.PickSingleFileAsync();
            if (file is not null) SetFile(file.Path);
        }
        catch (Exception ex)
        {
            StatusText.Text = "选择文件失败：" + ex.Message;
        }
    }

    private void DropZone_Tapped(object sender, TappedRoutedEventArgs e) => PickFile_Click(sender, e);

    private void File_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private void File_DragLeave(object sender, DragEventArgs e)
    {
    }

    private async void File_Drop(object sender, DragEventArgs e)
    {
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var file = items.OfType<StorageFile>().FirstOrDefault();
            if (file is not null) SetFile(file.Path);
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取拖入文件失败：" + ex.Message;
        }
    }

    private void SetFile(string path)
    {
        _filePath = path;
        FileInfoText.Text = DescribeFile(path);
        ClearFileButton.IsEnabled = true;
        GenerateButton.IsEnabled = true;
        StopShare();
        ResetPreview();
        StatusText.Text = "已选择文件，选择有效期后生成二维码。";
    }

    private void ClearFile_Click(object sender, RoutedEventArgs e)
    {
        _filePath = null;
        FileInfoText.Text = "未选择文件";
        ClearFileButton.IsEnabled = false;
        GenerateButton.IsEnabled = false;
        StopShare();
        ResetPreview();
        StatusText.Text = "";
    }

    private static string DescribeFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{info.Name}  ·  {FormatSize(info.Length)}";
        }
        catch
        {
            return path;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
        return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.0") + " GB";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_png is null) return;

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                SuggestedFileName = "qrcode-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            };
            picker.FileTypeChoices.Add("PNG 图片", new List<string> { ".png" });
            Core.WindowHandle.BindPicker(picker, this);

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            await FileIO.WriteBytesAsync(file, _png);
            StatusText.Text = "已保存：" + file.Path;
        }
        catch (Exception ex)
        {
            StatusText.Text = "保存失败：" + ex.Message;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_payload.Length == 0) return;
        var data = new DataPackage();
        data.SetText(_payload);
        Clipboard.SetContent(data);
        Clipboard.Flush();
        StatusText.Text = "内容已复制到剪贴板。";
    }

    private static string FriendlyError(Exception ex)
    {
        var name = ex.GetType().Name;
        if (name.Contains("DataTooLong", StringComparison.OrdinalIgnoreCase))
            return "内容过长，超出二维码容量；请缩短文本或改用链接。";
        return ex.Message;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        StopShare();
        base.OnNavigatedFrom(e);
    }
}
