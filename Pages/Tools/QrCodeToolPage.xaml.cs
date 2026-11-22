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
    private string? _filePath;
    private QrFileShareServer? _share;
    private DispatcherQueueTimer? _countdown;
    private byte[]? _png;
    private string _payload = "";

    /// <summary>右键菜单「生成二维码」带过来的文件，页面导航后自动填入。</summary>
    public static string? PendingFile { get; set; }

    public QrCodeToolPage()
    {
        InitializeComponent();
        ExpiryBox.SelectedIndex = 1;   // 默认 2 分钟
        UpdateDetect();
    }

    /// <summary>把输入归成「文件 / 链接 / 无法识别」三类，不区分模式。</summary>
    private (bool IsFile, string? Path, string? Link) Classify()
    {
        if (_filePath is { } chosen && File.Exists(chosen)) return (true, chosen, null);

        var raw = InputBox.Text?.Trim() ?? "";
        if (raw.Length == 0) return (false, null, null);

        try { if (File.Exists(raw)) return (true, raw, null); } catch { }

        var link = NormalizeLink(raw);
        return link.Length > 0 ? (false, null, link) : (false, null, null);
    }

    private void UpdateDetect()
    {
        var (isFile, path, link) = Classify();
        if (isFile)
        {
            DetectText.Text = "已识别为文件：" + DescribeFile(path!) + "。生成后手机从本机下载。";
            GenerateButton.IsEnabled = true;
        }
        else if (link is not null)
        {
            DetectText.Text = "已识别为链接：" + link;
            GenerateButton.IsEnabled = true;
        }
        else if ((InputBox.Text?.Trim().Length ?? 0) > 0)
        {
            DetectText.Text = "无法识别，请检查链接格式，或选择一个文件。";
            GenerateButton.IsEnabled = false;
        }
        else
        {
            DetectText.Text = "";
            GenerateButton.IsEnabled = false;
        }
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (InputBox is null) return;
        StopShare();
        ResetPreview();
        UpdateDetect();
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        var (isFile, path, link) = Classify();
        if (!isFile && link is null)
        {
            StatusText.Text = "请输入链接，或选择一个文件。";
            return;
        }

        GenerateButton.IsEnabled = false;
        StatusText.Text = "正在生成…";

        try
        {
            StopShare();
            var lifetime = SelectedLifetime();
            _share = isFile
                ? QrFileShareServer.Start(path!, lifetime)
                : QrFileShareServer.StartLink(link!, lifetime);

            await RenderAsync(_share.Url);
            StartCountdown();
            StatusText.Text = isFile
                ? "二维码已生成；手机扫描后从本机下载文件。"
                : "二维码已生成；手机扫描后在本机地址有效期内打开链接。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "生成失败：" + FriendlyError(ex);
        }
        finally
        {
            UpdateDetect();
        }
    }

    private async Task RenderAsync(string payload)
    {
        var result = QrCodeImage.Render(payload);
        _png = await QrCodeImage.EncodePngAsync(result.Pixels, result.Width, result.Height);
        _payload = payload;

        QrImage.Source = result.Bitmap;
        EmptyBox.Visibility = Visibility.Collapsed;
        QrPaper.Visibility = Visibility.Visible;
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

    /// <summary>有效期上限 10 分钟（2026-11-21 Nick 定），默认 2 分钟。</summary>
    private TimeSpan SelectedLifetime() => ExpiryBox.SelectedIndex switch
    {
        0 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(10),
        _ => TimeSpan.FromMinutes(2),
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
            StatusText.Text = "二维码已过期，请重新生成。";
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
        QrPaper.Visibility = Visibility.Collapsed;
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
        catch (Exception)
        {
            StatusText.Text = "无法读取该文件，请重新选择。";
        }
    }

    private void File_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void File_Drop(object sender, DragEventArgs e)
    {
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var file = items.OfType<StorageFile>().FirstOrDefault();
            if (file is not null) SetFile(file.Path);
        }
        catch (Exception)
        {
            StatusText.Text = "无法读取拖入的文件，请重新选择。";
        }
    }

    /// <summary>独立窗口或右键菜单调用：直接选好一个文件。</summary>
    public void LoadFile(string path) => SetFile(path);

    private void SetFile(string path)
    {
        _filePath = path;
        InputBox.Text = "";
        PickedName.Text = DescribeFile(path);
        DropEmpty.Visibility = Visibility.Collapsed;
        DropPicked.Visibility = Visibility.Visible;
        StopShare();
        ResetPreview();
        UpdateDetect();
        StatusText.Text = "已选择文件，选择有效期后生成二维码。";
    }

    private void ClearFile_Click(object sender, RoutedEventArgs e)
    {
        _filePath = null;
        PickedName.Text = "";
        DropPicked.Visibility = Visibility.Collapsed;
        DropEmpty.Visibility = Visibility.Visible;
        StopShare();
        ResetPreview();
        UpdateDetect();
        StatusText.Text = "";
    }

    /// <summary>整块文件区都可以点，触屏不必瞄准按钮。</summary>
    private void PickFile_Tapped(object sender, TappedRoutedEventArgs e) => PickFile_Click(sender, e);

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
        catch (Exception)
        {
            StatusText.Text = "保存失败，请换一个位置再试。";
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
            return "内容过长，超出二维码容量；请改用较短的链接。";
        return ex.Message;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (PendingFile is not { Length: > 0 } path) return;

        PendingFile = null;
        SetFile(path);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        StopShare();
        base.OnNavigatedFrom(e);
    }
}
