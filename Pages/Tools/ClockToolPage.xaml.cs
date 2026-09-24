using System;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// 全屏时钟（对齐网页版 tools/ClockTool.vue）：
/// 蒙版/材质（无 · 白 · 黑 · 亚克力 · 云母）、背景底色、文字颜色、5 种时间字体、字号缩放、12 小时制、
/// 拖放设背景、全屏窗口、对时入口。
/// </summary>
public sealed partial class ClockToolPage : Page
{
    private const string TimeIsUrl = "https://time.is/";
    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

    private readonly ClockSettings _settings = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private bool _ready;

    public ClockToolPage()
    {
        InitializeComponent();

        foreach (var label in ClockRender.VeilLabels) VeilBox.Items.Add(label);
        foreach (var label in ClockRender.ToneLabels) ToneBox.Items.Add(label);
        foreach (var label in ClockRender.InkLabels) InkBox.Items.Add(label);
        foreach (var label in ClockRender.FontLabels) FontBox.Items.Add(label);
        VeilBox.SelectedIndex = (int)_settings.Veil;
        ToneBox.SelectedIndex = (int)_settings.Tone;
        InkBox.SelectedIndex = (int)_settings.Ink;
        FontBox.SelectedIndex = Array.IndexOf(ClockRender.FontFamilies, _settings.FontFamily);
        if (FontBox.SelectedIndex < 0) FontBox.SelectedIndex = 1;

        VeilSlider.Value = _settings.VeilStrength;
        ScaleSlider.Value = _settings.Scale;

        // 事件在构造之后再接（XAML 里挂事件 + 初值会触发解析期回调崩溃）
        VeilBox.SelectionChanged += (_, _) => { if (_ready) { _settings.Veil = (ClockVeil)VeilBox.SelectedIndex; Apply(); } };
        ToneBox.SelectionChanged += (_, _) => { if (_ready) { _settings.Tone = (ClockTone)ToneBox.SelectedIndex; Apply(); } };
        InkBox.SelectionChanged += (_, _) => { if (_ready) { _settings.Ink = (ClockInk)InkBox.SelectedIndex; Apply(); } };
        FontBox.SelectionChanged += (_, _) => { if (_ready) { _settings.FontFamily = ClockRender.FontFamilies[Math.Max(0, FontBox.SelectedIndex)]; Apply(); } };
        VeilSlider.ValueChanged += (_, _) => { if (_ready) { _settings.VeilStrength = VeilSlider.Value; Apply(); } };
        ScaleSlider.ValueChanged += (_, _) => { if (_ready) { _settings.Scale = ScaleSlider.Value; Apply(); } };
        ShowSecondsBox.Checked += (_, _) => { if (_ready) { _settings.ShowSeconds = true; Apply(); } };
        ShowSecondsBox.Unchecked += (_, _) => { if (_ready) { _settings.ShowSeconds = false; Apply(); } };
        ShowDateBox.Checked += (_, _) => { if (_ready) { _settings.ShowDate = true; Apply(); } };
        ShowDateBox.Unchecked += (_, _) => { if (_ready) { _settings.ShowDate = false; Apply(); } };
        Hour12Box.Checked += (_, _) => { if (_ready) { _settings.Hour12 = true; Apply(); } };
        Hour12Box.Unchecked += (_, _) => { if (_ready) { _settings.Hour12 = false; Apply(); } };

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Tick();

        // 离开页面就停表（定时器的委托会把整个页面钉在内存里）
        Unloaded += (_, _) => _timer.Stop();

        _ready = true;
        Apply();
        Tick();
        _timer.Start();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(ToolsPage));
    }

    /// <summary>把时钟丢到工具浮窗里跑。</summary>
    private void OpenPalette_Click(object sender, RoutedEventArgs e)
        => Views.ToolPaletteWindow.ShowTool("clock");

    private bool Dark => ActualTheme == ElementTheme.Dark;
    private bool HasPhoto => !string.IsNullOrWhiteSpace(_settings.BackgroundImagePath);

    // ══════════ 渲染 ══════════
    private void Apply()
    {
        var face = new SolidColorBrush(ClockRender.FaceColor(_settings, Dark, HasPhoto));
        TimeText.Foreground = face;
        SecText.Foreground = face;
        DateText.Foreground = face;

        Stage.Background = HasPhoto ? PhotoBrush() : ClockRender.BaseBrush(_settings, Dark);
        VeilHost.Background = ClockRender.VeilBrush(_settings);

        TimeText.FontFamily = new FontFamily(_settings.FontFamily);
        SecText.FontFamily = new FontFamily(_settings.FontFamily);
        DateText.FontFamily = new FontFamily(_settings.FontFamily);

        TimeText.FontSize = 64 * _settings.Scale;
        SecText.FontSize = 30 * _settings.Scale;
        DateText.FontSize = 14 * _settings.Scale;

        VeilStrengthPanel.Visibility = _settings.Veil == ClockVeil.None ? Visibility.Collapsed : Visibility.Visible;
        VeilStrengthLabel.Text = $"蒙版强度 {Math.Round(_settings.VeilStrength)}%";
        ScaleLabel.Text = $"字号大小 {Math.Round(_settings.Scale * 100)}%";
        ClearBgButton.IsEnabled = HasPhoto;
        BgName.Text = HasPhoto
            ? System.IO.Path.GetFileName(_settings.BackgroundImagePath)
            : "当前未设置背景图（上面拖一张进来即可）";

        Tick();
    }

    private Brush PhotoBrush()
    {
        try
        {
            return new ImageBrush
            {
                ImageSource = new BitmapImage(new Uri(_settings.BackgroundImagePath)),
                Stretch = Stretch.UniformToFill,
            };
        }
        catch
        {
            return ClockRender.BaseBrush(_settings, Dark);
        }
    }

    private void Tick()
    {
        var now = DateTime.Now;
        TimeText.Text = ClockRender.TimeText(now, _settings.Hour12);
        SecText.Text = ClockRender.SecText(now);
        DateText.Text = ClockRender.DateText(now);
        SecText.Visibility = _settings.ShowSeconds ? Visibility.Visible : Visibility.Collapsed;
        DateText.Visibility = _settings.ShowDate ? Visibility.Visible : Visibility.Collapsed;
    }

    // ══════════ 背景图 ══════════
    private async void PickBg_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.Thumbnail,
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            };
            foreach (var ext in ImageExts) picker.FileTypeFilter.Add(ext);

            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            SetBackground(file.Path);
        }
        catch (Exception ex)
        {
            Toast.Text = "图片打开失败：" + ex.Message;
        }
    }

    private void ClearBg_Click(object sender, RoutedEventArgs e)
    {
        _settings.BackgroundImagePath = "";
        Apply();
    }

    private void SetBackground(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (Array.IndexOf(ImageExts, ext) < 0)
        {
            Toast.Text = "请选择图片文件";
            return;
        }
        try
        {
            var size = new System.IO.FileInfo(path).Length;
            if (size > 12L * 1024 * 1024)
            {
                Toast.Text = "图片太大了（建议 12MB 以内）";
                return;
            }
        }
        catch { /* 取不到大小就直接用 */ }

        _settings.BackgroundImagePath = path;
        Apply();
        Toast.Text = "";
    }

    // 直接把图片拖进预览框也能设为背景
    private void Stage_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        DropHint.Visibility = Visibility.Visible;
    }

    private void Stage_DragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;

    private async void Stage_Drop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        try
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.Count > 0 && items[0] is StorageFile file) SetBackground(file.Path);
        }
        catch (Exception ex)
        {
            Toast.Text = "拖进来的文件读取失败：" + ex.Message;
        }
    }

    // ══════════ 全屏 / 对时 ══════════
    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        var copy = new ClockSettings
        {
            BackgroundImagePath = _settings.BackgroundImagePath,
            Veil = _settings.Veil,
            VeilStrength = _settings.VeilStrength,
            Tone = _settings.Tone,
            Ink = _settings.Ink,
            FontFamily = _settings.FontFamily,
            Scale = _settings.Scale,
            ShowSeconds = _settings.ShowSeconds,
            ShowDate = _settings.ShowDate,
            Hour12 = _settings.Hour12,
        };
        new Views.ClockFullscreenWindow(copy, Dark).Start();
    }

    private async void TimeSync_Click(object sender, RoutedEventArgs e)
    {
        try { await Launcher.LaunchUriAsync(new Uri(TimeIsUrl)); }
        catch (Exception ex) { Toast.Text = "打不开浏览器：" + ex.Message; }
    }
}
