using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace ClassSoftwareHub.Desktop.Pages.Tools;




public sealed record HashCheckRequest(string Hash, string AppName);






public sealed class HashRow : INotifyPropertyChanged
{
    private string _value = "—";

    public HashRow(string name) => Name = name;

    public string Name { get; }

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}










public sealed partial class EncodingToolPage : Page
{
    
    
    private static readonly string[] Algorithms = Core.FileHash.Algorithms;
    private const string EmptyMark = "—";

    
    private readonly Dictionary<string, HashRow> _fileRows = new();

    
    private readonly Dictionary<string, HashRow> _textRows = new();

    private string _filePath = "";
    private Dictionary<string, string> _fileHashes = new();
    private CancellationTokenSource? _fileCts;

    public EncodingToolPage()
    {
        InitializeComponent();

        
        
        Core.WheelRelay.Attach(this, FileView, TextView);

        BuildHashRows(HashPanel, _fileRows);
        BuildHashRows(TextHashPanel, _textRows);

        
        
        
        if (ModeBar.Items.Count > 0) ModeBar.SelectedItem = ModeBar.Items[0];
        ShowTab(file: true);

        SetFileRowsEmpty(EmptyMark);
        RefreshTextHashes();

        
        
        
        Unloaded += (_, _) => _fileCts?.Cancel();
    }

    
    
    
    
    
    
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not HashCheckRequest req || string.IsNullOrWhiteSpace(req.Hash)) return;

        if (ModeBar.Items.Count > 0) ModeBar.SelectedItem = ModeBar.Items[0];
        ShowTab(file: true);
        ExpectedBox.Text = req.Hash;
        
        
        RefreshMatch();

        var who = string.IsNullOrWhiteSpace(req.AppName) ? "该软件" : "「" + req.AppName + "」";
        FromAppHint.Text = $"已带入{who}的官方校验值，将下载的安装包拖入下方区域后自动核对。";
        FromAppHint.Visibility = Visibility.Visible;
    }

    
    
    private void ModeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        => ShowTab((sender.SelectedItem?.Tag as string) == "file");

    private void ShowTab(bool file)
    {
        FileView.Visibility = file ? Visibility.Visible : Visibility.Collapsed;
        TextView.Visibility = file ? Visibility.Collapsed : Visibility.Visible;

        
        
        
        
        var view = file ? FileView : TextView;
        if (view.IsLoaded) view.ChangeView(0, 0, null);
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private static void BuildHashRows(ListView host, Dictionary<string, HashRow> table)
    {
        var items = new ObservableCollection<HashRow>();
        foreach (var name in Algorithms)
        {
            var row = new HashRow(name);
            table[name] = row;
            items.Add(row);
        }
        host.ItemsSource = items;
    }

    
    private void CopyHashRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HashRow row) Copy(row.Value, row.Name);
    }

    private Brush Res(string key) => Services.ThemeBrush.Get(this, key);

    

    private void PickFile_Click(object sender, RoutedEventArgs e) => ChooseFile();

    
    private void DropZone_Tapped(object sender, TappedRoutedEventArgs e) => ChooseFile();

    private async void ChooseFile()
    {
        try
        {
            var picker = new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add("*");
            
            
            Core.WindowHandle.BindPicker(picker, this);
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            await LoadFileAsync(file.Path);
        }
        catch (Exception ex)
        {
            Toast.Text = "选择文件失败：" + ex.Message;
        }
    }

    private void ClearFile_Click(object sender, RoutedEventArgs e)
    {
        _fileCts?.Cancel();
        _filePath = "";
        _fileHashes = new();
        ClearFileButton.IsEnabled = false;
        HashProgress.Visibility = Visibility.Collapsed;
        PickFileButton.IsEnabled = true;
        DropHintText.Text = IdleHint;
        SourceText.Text = IdleSubHint;
        SetFileRowsEmpty(EmptyMark);
        RefreshMatch();
    }

    
    private const string IdleHint = "把文件拖到这里，或单击选择";
    private const string IdleSubHint = "支持任意文件类型，大文件流式读取";

    private void File_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        DropHintText.Text = "松开鼠标即开始计算哈希";
    }

    private void File_DragLeave(object sender, DragEventArgs e)
    {
        DropHintText.Text = _filePath.Length == 0 ? IdleHint : "可拖入新文件重新计算";
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
            Toast.Text = "拖入的文件读取失败：" + ex.Message;
            DropHintText.Text = IdleHint;
        }
    }

    private async Task LoadFileAsync(string path)
    {
        _filePath = path;
        _fileHashes = new();
        ClearFileButton.IsEnabled = true;
        PickFileButton.IsEnabled = false;
        HashProgress.Visibility = Visibility.Visible;
        SetFileRowsEmpty("计算中…");
        SourceText.Text = $"{System.IO.Path.GetFileName(path)} · 计算中";
        DropHintText.Text = "已选文件，可再拖入新文件重新计算";

        _fileCts?.Cancel();
        var cts = new CancellationTokenSource();
        _fileCts = cts;

        try
        {
            var info = new FileInfo(path);
            var hashes = await Task.Run(() => Core.FileHash.Compute(path, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;

            _fileHashes = hashes;
            ApplyFileHashes(hashes);
            SourceText.Text = $"{info.Name}（{SizeText(info.Length)}）· {info.FullName}";
        }
        catch (OperationCanceledException) {  }
        catch (Exception ex)
        {
            SourceText.Text = "计算哈希失败：" + ex.Message;
            SetFileRowsEmpty(EmptyMark);
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

    private static string SizeText(long bytes)
        => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.##} GB"
         : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.##} MB"
         : bytes >= 1L << 10 ? $"{bytes / (double)(1L << 10):0.#} KB"
         : $"{bytes} B";

    
    private void ApplyFileHashes(Dictionary<string, string> hashes)
    {
        foreach (var name in Algorithms)
            _fileRows[name].Value = hashes.TryGetValue(name, out var v) && v.Length > 0 ? v : EmptyMark;
    }

    private void SetFileRowsEmpty(string placeholder)
    {
        foreach (var name in Algorithms) _fileRows[name].Value = placeholder;
    }

    
    private void Input_TextChanged(object sender, TextChangedEventArgs e) => RefreshTextHashes();

    private void Output_TextChanged(object sender, TextChangedEventArgs e)
    {
        var has = !string.IsNullOrEmpty(OutputBox.Text);
        CopyOutButton.IsEnabled = has;
        UseOutputButton.IsEnabled = has;
    }

    private void RefreshTextHashes()
    {
        var text = InputBox.Text ?? "";
        if (text.Length == 0)
        {
            foreach (var name in Algorithms) _textRows[name].Value = EmptyMark;
            return;
        }

        var hashes = ComputeTextHashes(text);
        foreach (var name in Algorithms) _textRows[name].Value = hashes[name];
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

        if (_fileHashes.Count == 0)
        {
            
            MatchText.Text = "选择文件后自动比对";
            MatchText.Foreground = Res("TextFillColorSecondaryBrush");
            return;
        }

        var hit = Algorithms.FirstOrDefault(a => Norm(_fileRows[a].Value) == expected);
        if (hit is not null)
        {
            MatchText.Text = $"✓ 与 {hit} 一致";
            MatchText.Foreground = Res("SystemFillColorSuccessBrush");
        }
        else
        {
            MatchText.Text = "✗ 与上面四种哈希值均不一致";
            MatchText.Foreground = Res("SystemFillColorCriticalBrush");
        }
    }

    
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

    
    private void ClearInput_Click(object sender, RoutedEventArgs e) => InputBox.Text = "";

    private void Copy(string? text, string what)
    {
        if (string.IsNullOrEmpty(text) || text == EmptyMark || text.StartsWith("计算中")) return;
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
