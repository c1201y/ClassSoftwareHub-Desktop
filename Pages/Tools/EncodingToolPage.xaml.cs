using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// 编码 / 哈希工具：Base64、URL 编解码 + MD5 / SHA-1 / SHA-256 / SHA-512。
/// 桌面端增强：可直接拖入文件算哈希（大文件流式算，不吃内存），并把官方哈希值粘进来一键核对。
/// </summary>
public sealed partial class EncodingToolPage : Page
{
    private static readonly string[] Algorithms = { "MD5", "SHA-1", "SHA-256", "SHA-512" };
    private readonly Dictionary<string, TextBlock> _hashTexts = new();

    private string _filePath = "";
    private CancellationTokenSource? _fileCts;

    public EncodingToolPage()
    {
        InitializeComponent();
        BuildHashRows();

        // 事件在构造之后再接（XAML 里挂事件 + 初值会触发解析期回调崩溃）
        TextModeRadio.Checked += (_, _) => SwitchMode(fileMode: false);
        FileModeRadio.Checked += (_, _) => SwitchMode(fileMode: true);
        TextModeRadio.IsChecked = true;
        SwitchMode(fileMode: false);

        RefreshTextHashes();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _fileCts?.Cancel();
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(ToolsPage));
    }

    private void BuildHashRows()
    {
        foreach (var name in Algorithms)
        {
            var value = new TextBlock
            {
                Text = "—",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Padding = new Thickness(8, 6, 8, 6),
            };
            _hashTexts[name] = value;

            var copy = new Button { Content = "复制", Padding = new Thickness(10, 0, 10, 0), FontSize = 13 };
            var captured = name;
            copy.Click += (_, _) => Copy(_hashTexts[captured].Text, captured);

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock
            {
                Text = name,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(copy, 1);
            head.Children.Add(label);
            head.Children.Add(copy);

            var box = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = Res("ControlFillColorSecondaryBrush"),
                Child = value,
            };

            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(head);
            row.Children.Add(box);
            HashPanel.Children.Add(row);
        }
    }

    private Microsoft.UI.Xaml.Media.Brush Res(string key) => Services.ThemeBrush.Get(this, key);

    // ══════════ 模式 ══════════
    private bool _fileMode;

    private void SwitchMode(bool fileMode)
    {
        _fileMode = fileMode;
        FileArea.Visibility = fileMode ? Visibility.Visible : Visibility.Collapsed;
        if (fileMode)
        {
            if (_filePath.Length == 0)
            {
                SetRowsEmpty("—");
                SourceText.Text = "来源：还没选文件";
            }
            else
            {
                ShowFileHashes(_filePath, _fileHashes);
            }
        }
        else
        {
            RefreshTextHashes();
        }
        RefreshMatch();
    }

    // ══════════ 文本 ══════════
    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_fileMode) RefreshTextHashes();
    }

    private void Output_TextChanged(object sender, TextChangedEventArgs e)
    {
        var has = !string.IsNullOrEmpty(OutputBox.Text);
        CopyOutButton.IsEnabled = has;
        UseOutputButton.IsEnabled = has;
    }

    private void RefreshTextHashes()
    {
        var text = InputBox.Text ?? "";
        if (text.Length == 0) SetRowsEmpty("—");
        else ApplyHashes(ComputeTextHashes(text));
        SourceText.Text = "来源：输入框里的文本（实时计算）";
        RefreshMatch();
    }

    private static Dictionary<string, string> ComputeTextHashes(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new Dictionary<string, string>
        {
            ["MD5"] = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant(),
            ["SHA-1"] = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(),
            ["SHA-256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            ["SHA-512"] = Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant(),
        };
    }

    // ══════════ 文件 ══════════
    private Dictionary<string, string> _fileHashes = new();

    private async void PickFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            await LoadFileAsync(file.Path);
        }
        catch (Exception ex)
        {
            Toast.Text = "选文件失败：" + ex.Message;
        }
    }

    private void ClearFile_Click(object sender, RoutedEventArgs e)
    {
        _fileCts?.Cancel();
        _filePath = "";
        _fileHashes = new();
        ClearFileButton.IsEnabled = false;
        HashProgress.Visibility = Visibility.Collapsed;
        DropHintText.Text = "把文件拖到这里";
        SourceText.Text = "来源：还没选文件";
        SetRowsEmpty("—");
        RefreshMatch();
    }

    private void File_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        DropHintText.Text = "松开鼠标，开始算哈希";
    }

    private void File_DragLeave(object sender, DragEventArgs e)
    {
        DropHintText.Text = _filePath.Length == 0 ? "把文件拖到这里" : "换个文件？";
    }

    private async void File_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.Count > 0 && items[0] is StorageFile file) await LoadFileAsync(file.Path);
        }
        catch (Exception ex)
        {
            Toast.Text = "拖进来的文件读取失败：" + ex.Message;
            DropHintText.Text = "把文件拖到这里";
        }
    }

    private async Task LoadFileAsync(string path)
    {
        _filePath = path;
        _fileHashes = new();
        ClearFileButton.IsEnabled = true;
        PickFileButton.IsEnabled = false;
        HashProgress.Visibility = Visibility.Visible;
        SetRowsEmpty("计算中…");
        SourceText.Text = $"来源：{System.IO.Path.GetFileName(path)} · 计算中…";
        DropHintText.Text = "已收到文件，换个文件可以再拖一个进来";

        _fileCts?.Cancel();
        var cts = new CancellationTokenSource();
        _fileCts = cts;

        try
        {
            var info = new FileInfo(path);
            var hashes = await Task.Run(() => HashFile(path, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;

            _fileHashes = hashes;
            ApplyHashes(hashes);
            SourceText.Text = $"来源：{info.Name}（{SizeText(info.Length)}）· {info.FullName}";
        }
        catch (OperationCanceledException) { /* 换文件了，旧计算作废 */ }
        catch (Exception ex)
        {
            SourceText.Text = "算哈希失败：" + ex.Message;
            SetRowsEmpty("—");
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                HashProgress.Visibility = Visibility.Collapsed;
                PickFileButton.IsEnabled = true;
            }
        }
        RefreshMatch();
    }

    private void ShowFileHashes(string path, Dictionary<string, string> hashes)
    {
        if (hashes.Count == 0) { SetRowsEmpty("—"); SourceText.Text = "来源：还没算完（换个模式再回来）"; return; }
        ApplyHashes(hashes);
        try
        {
            var info = new FileInfo(path);
            SourceText.Text = $"来源：{info.Name}（{SizeText(info.Length)}）· {info.FullName}";
        }
        catch { SourceText.Text = "来源：" + path; }
    }

    /// <summary>流式算四种哈希：1MB 一块，多大的文件都不吃内存。</summary>
    private static Dictionary<string, string> HashFile(string path, CancellationToken token)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);

        var buffer = new byte[1 << 20];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, buffer.Length, FileOptions.SequentialScan))
        {
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                var span = buffer.AsSpan(0, read);
                md5.AppendData(span);
                sha1.AppendData(span);
                sha256.AppendData(span);
                sha512.AppendData(span);
            }
        }

        return new Dictionary<string, string>
        {
            ["MD5"] = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant(),
            ["SHA-1"] = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(),
            ["SHA-256"] = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant(),
            ["SHA-512"] = Convert.ToHexString(sha512.GetHashAndReset()).ToLowerInvariant(),
        };
    }

    private static string SizeText(long bytes)
        => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.##} GB"
         : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.##} MB"
         : bytes >= 1L << 10 ? $"{bytes / (double)(1L << 10):0.#} KB"
         : $"{bytes} B";

    // ══════════ 哈希显示 + 核对 ══════════
    private void ApplyHashes(Dictionary<string, string> hashes)
    {
        foreach (var name in Algorithms)
            _hashTexts[name].Text = hashes.TryGetValue(name, out var v) && v.Length > 0 ? v : "—";
    }

    private void SetRowsEmpty(string placeholder)
    {
        foreach (var name in Algorithms) _hashTexts[name].Text = placeholder;
    }

    private void Expected_TextChanged(object sender, TextChangedEventArgs e) => RefreshMatch();

    private static string Norm(string s)
        => new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private void RefreshMatch()
    {
        var expected = Norm(ExpectedBox.Text ?? "");
        if (expected.Length == 0)
        {
            MatchText.Text = "";
            return;
        }

        var hit = Algorithms.FirstOrDefault(a => _hashTexts[a].Text is var value && value != "—" && Norm(value) == expected);
        if (hit is not null)
        {
            MatchText.Text = $"✓ 与 {hit} 一致";
            MatchText.Foreground = Res("SystemFillColorSuccessBrush");
        }
        else
        {
            MatchText.Text = _fileMode && _fileHashes.Count == 0
                ? "先选一个文件（或换回文本模式）"
                : "✗ 与当前显示的 4 种哈希都不一致（注意：算出来的哈希要空格、大小写都算一致）";
            MatchText.Foreground = Res("SystemFillColorCriticalBrush");
        }
    }

    // ══════════ 编解码 ══════════
    private void B64Enc_Click(object sender, RoutedEventArgs e)
    {
        try { OutputBox.Text = Convert.ToBase64String(Encoding.UTF8.GetBytes(InputBox.Text ?? "")); StatusText.Text = ""; }
        catch (Exception ex) { StatusText.Text = "编码失败：" + ex.Message; }
    }

    private void B64Dec_Click(object sender, RoutedEventArgs e)
    {
        try { OutputBox.Text = Encoding.UTF8.GetString(Convert.FromBase64String((InputBox.Text ?? "").Trim())); StatusText.Text = ""; }
        catch { StatusText.Text = "解码失败，请检查 Base64 内容"; }
    }

    private void UrlEnc_Click(object sender, RoutedEventArgs e)
        => OutputBox.Text = Uri.EscapeDataString(InputBox.Text ?? "");

    private void UrlDec_Click(object sender, RoutedEventArgs e)
    {
        try { OutputBox.Text = Uri.UnescapeDataString(InputBox.Text ?? ""); StatusText.Text = ""; }
        catch { StatusText.Text = "解码失败，请检查内容"; }
    }

    private void CopyOut_Click(object sender, RoutedEventArgs e) => Copy(OutputBox.Text, "结果");

    private void UseOutput_Click(object sender, RoutedEventArgs e) => InputBox.Text = OutputBox.Text;

    private void Copy(string? text, string what)
    {
        if (string.IsNullOrEmpty(text) || text == "—") return;
        try
        {
            var dp = new DataPackage();
            dp.SetText(text);
            Clipboard.SetContent(dp);
            StatusText.Text = $"已复制{what}";
            Toast.Text = $"已复制{what}";
        }
        catch { StatusText.Text = "复制失败"; }
    }
}
