using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace ClassSoftwareHub.Desktop.Pages.Tools;






public sealed partial class ClockToolPage : Page
{
    private const string TimeIsUrl = "https://time.is/";
    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

    
    
    
    
    private const int MaxPresets = 5;

    
    private const double PresetCardWidth = 150;
    private const double PresetPreviewHeight = 84;

    private readonly ClockSettings _settings = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    private readonly List<ClockPreset> _presets = new();
    private bool _ready;

    
    private string _activePreset = "";

    
    
    
    
    private bool _keepPreset = true;

    public ClockToolPage()
    {
        InitializeComponent();

        
        Core.WheelRelay.Attach(this, RootScroll);

        
        
        
        var state = ClockPresetStore.Load();
        _presets.AddRange(state.Presets);

        var active = _presets.Find(p =>
            !string.IsNullOrEmpty(state.LastUsedPreset)
            && string.Equals(p.Name, state.LastUsedPreset, StringComparison.OrdinalIgnoreCase));

        if (active is not null)
        {
            _settings.CopyFrom(active.ToSettings());     
            _activePreset = active.Name;
        }
        else if (state.LastUsed is not null)
        {
            _settings.CopyFrom(state.LastUsed);
        }

        foreach (var label in ClockRender.VeilLabels) VeilBox.Items.Add(label);
        foreach (var label in ClockRender.ToneLabels) ToneBox.Items.Add(label);
        foreach (var label in ClockRender.InkLabels) InkBox.Items.Add(label);
        foreach (var label in ClockRender.FontLabels) FontBox.Items.Add(label);
        SyncControls();     

        
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

        
        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => Persist();

        
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            _saveTimer.Stop();
            Persist();
        };

        _ready = true;
        Apply();
        Tick();
        _keepPreset = false;      
        RefreshPresets();
        _timer.Start();
    }

    
    private void OpenPalette_Click(object sender, RoutedEventArgs e)
        => Views.ToolPaletteWindow.ShowTool("clock");

    private bool Dark => ActualTheme == ElementTheme.Dark;
    private bool HasPhoto => !string.IsNullOrWhiteSpace(_settings.BackgroundImagePath);

    
    private void Apply()
    {
        var face = new SolidColorBrush(ClockRender.FaceColor(_settings, Dark, HasPhoto));
        TimeText.Foreground = face;
        SecText.Foreground = face;
        DateText.Foreground = face;

        Stage.Background = HasPhoto ? PhotoBrush() : ClockRender.BaseBrush(_settings, Dark);
        VeilHost.Background = ClockRender.VeilBrush(_settings);

        var faceFont = new FontFamily(ClockRender.NormalizeFont(_settings.FontFamily));
        TimeText.FontFamily = faceFont;
        SecText.FontFamily = faceFont;
        DateText.FontFamily = faceFont;

        TimeText.FontSize = 64 * _settings.Scale;
        SecText.FontSize = 30 * _settings.Scale;
        DateText.FontSize = 14 * _settings.Scale;

        VeilStrengthPanel.Visibility = _settings.Veil == ClockVeil.None ? Visibility.Collapsed : Visibility.Visible;
        VeilStrengthLabel.Text = $"蒙版强度 {Math.Round(_settings.VeilStrength)}%";
        ScaleLabel.Text = $"字号大小 {Math.Round(_settings.Scale * 100)}%";
        ClearBgButton.IsEnabled = HasPhoto;
        
        BgName.Text = HasPhoto
            ? System.IO.Path.GetFileName(_settings.BackgroundImagePath)
            : "未设置背景图（可拖入图片）";
        ToolTipService.SetToolTip(BgName, HasPhoto ? _settings.BackgroundImagePath : BgName.Text);

        
        if (_ready)
        {
            
            if (!_keepPreset) MarkCustomAppearance();
            ScheduleSave();
        }

        Tick();
    }

    
    
    
    
    private void MarkCustomAppearance()
    {
        if (_activePreset.Length == 0) return;
        _activePreset = "";
        RefreshPresets();
    }

    private Brush PhotoBrush()
        => PhotoBrushOf(_settings.BackgroundImagePath, ClockRender.BaseBrush(_settings, Dark));

    
    
    
    
    
    private static Brush PhotoBrushOf(string path, Brush fallback, int decodeWidth = 0)
    {
        try
        {
            var bmp = new BitmapImage();
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.UriSource = new Uri(path);
            return new ImageBrush { ImageSource = bmp, Stretch = Stretch.UniformToFill };
        }
        catch
        {
            return fallback;
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
                Toast.Text = "图片过大（建议 12MB 以内）";
                return;
            }
        }
        catch {  }

        _settings.BackgroundImagePath = path;
        Apply();
        Toast.Text = "";
    }

    
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
            Toast.Text = "拖入的文件读取失败：" + ex.Message;
        }
    }

    
    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        
        Views.ClockFullscreenWindow.Show(CopyOf(_settings), Dark);
    }

    private async void TimeSync_Click(object sender, RoutedEventArgs e)
    {
        try { await Launcher.LaunchUriAsync(new Uri(TimeIsUrl)); }
        catch (Exception ex) { Toast.Text = "无法启动浏览器：" + ex.Message; }
    }

    

    
    private void SyncControls()
    {
        var wasReady = _ready;
        _ready = false;   
        try
        {
            VeilBox.SelectedIndex = (int)_settings.Veil;
            ToneBox.SelectedIndex = (int)_settings.Tone;
            InkBox.SelectedIndex = (int)_settings.Ink;
            
            var savedFont = ClockRender.NormalizeFont(_settings.FontFamily);
            var fontIndex = Array.IndexOf(ClockRender.FontFamilies, savedFont);
            FontBox.SelectedIndex = fontIndex < 0 ? 1 : fontIndex;
            VeilSlider.Value = _settings.VeilStrength;
            ScaleSlider.Value = _settings.Scale;
            ShowSecondsBox.IsChecked = _settings.ShowSeconds;
            ShowDateBox.IsChecked = _settings.ShowDate;
            Hour12Box.IsChecked = _settings.Hour12;
        }
        finally { _ready = wasReady; }
    }

    
    private void ScheduleSave()
    {
        
        
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Persist()
    {
        
        
        
        var data = ClockPresetStore.Load();
        data.LastUsed = _settings.Clone();
        data.LastUsedPreset = _activePreset;
        data.Presets = _presets;
        ClockPresetStore.Save(data);
    }

    private void RefreshPresets()
    {
        PresetHost.Items.Clear();

        if (_presets.Count == 0)
        {
            PresetHint.Text = $"尚未保存预设（上限 {MaxPresets} 套）。调整外观后单击「保存预设」，下次可直接套用。";
            return;
        }

        PresetHint.Text = _activePreset.Length > 0
            ? $"正在使用「{_activePreset}」· 已存 {_presets.Count} / {MaxPresets} 套。修改任一项外观后将转为自定义外观。"
            : $"已存 {_presets.Count} / {MaxPresets} 套。单击卡片上的「套用」立即应用，应用后仍可继续调整。";

        foreach (var preset in _presets) PresetHost.Items.Add(BuildPresetCard(preset));
    }

    
    
    
    
    private FrameworkElement BuildPresetCard(ClockPreset preset)
    {
        var ps = preset.ToSettings();
        var active = string.Equals(preset.Name, _activePreset, StringComparison.OrdinalIgnoreCase);
        var hasPhoto = !string.IsNullOrWhiteSpace(ps.BackgroundImagePath);

        
        var preview = new Grid { Height = PresetPreviewHeight };
        preview.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = hasPhoto
                ? PhotoBrushOf(ps.BackgroundImagePath, ClockRender.BaseBrush(ps, Dark), decodeWidth: 320)
                : ClockRender.BaseBrush(ps, Dark),
        });
        preview.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = ClockRender.VeilBrush(ps),
        });

        var face = new SolidColorBrush(ClockRender.FaceColor(ps, Dark, hasPhoto));
        var font = new FontFamily(ClockRender.NormalizeFont(ps.FontFamily));

        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        var hourBlock = new TextBlock
        {
            Text = "10:24",
            FontSize = Math.Clamp(26 * ps.Scale, 13, 38),
            FontWeight = FontWeights.Bold,
            FontFamily = font,
            Foreground = face,
        };
        
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(hourBlock, FontNumeralAlignment.Tabular);
        line.Children.Add(hourBlock);
        if (ps.ShowSeconds)
        {
            var secBlock = new TextBlock
            {
                Text = ":08",
                FontSize = Math.Clamp(12 * ps.Scale, 8, 17),
                FontWeight = FontWeights.SemiBold,
                Opacity = 0.85,
                FontFamily = font,
                Foreground = face,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 3),
            };
            Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(secBlock, FontNumeralAlignment.Tabular);
            line.Children.Add(secBlock);
        }

        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        center.Children.Add(line);
        if (ps.ShowDate)
            center.Children.Add(new TextBlock
            {
                Text = DateTime.Now.ToString("M/d"),
                FontSize = 9.5,
                Opacity = 0.85,
                FontFamily = font,
                Foreground = face,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        preview.Children.Add(center);

        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = Res("CardStrokeColorDefaultBrush"),
            Child = preview,
        });

        content.Children.Add(new TextBlock
        {
            Text = preset.Name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(CardButton("套用", $"套用预设「{preset.Name}」", preset, ApplyPreset_Click));
        actions.Children.Add(CardButton("删除", $"删除预设「{preset.Name}」", preset, DeletePreset_Click));
        content.Children.Add(actions);

        var card = new Border
        {
            Width = PresetCardWidth,
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = active
                ? new SolidColorBrush(Accent())
                : Res("CardStrokeColorDefaultBrush"),
            Background = Res("CardBackgroundFillColorDefaultBrush"),
            Child = content,
        };

        
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card,
            active ? $"预设卡片「{preset.Name}」（使用中）" : $"预设卡片「{preset.Name}」");
        ToolTipService.SetToolTip(card, preset.Summary());
        return card;
    }

    private Button CardButton(string label, string automationName, ClockPreset preset, RoutedEventHandler handler)
    {
        var b = new Button
        {
            Content = label,
            Tag = preset,
            MinWidth = 0,
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 12,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, automationName);
        b.Click += handler;
        return b;
    }

    private static Windows.UI.Color Accent() =>
        new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);

    private async void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_presets.Count >= MaxPresets)
        {
            Toast.Text = $"预设数量已达上限 {MaxPresets} 套，请先删除一套。";
            return;
        }

        var input = new TextBox
        {
            Text = $"预设 {_presets.Count + 1}",
            PlaceholderText = "输入预设名称（如「考试模式」）",
        };

        var dialog = new ContentDialog
        {
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = XamlRoot,
            Title = "保存为预设",
            Content = input,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var name = input.Text.Trim();
        if (name.Length == 0) name = $"预设 {_presets.Count + 1}";

        
        var preset = ClockPreset.From(name, _settings);
        var existing = _presets.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) _presets[existing] = preset;
        else _presets.Add(preset);

        
        _activePreset = preset.Name;

        Persist();
        RefreshPresets();
        Toast.Text = $"已保存预设「{name}」，正在使用。";
    }

    private void ApplyPreset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ClockPreset preset) return;

        _keepPreset = true;                       
        _settings.CopyFrom(preset.ToSettings());
        SyncControls();
        Apply();
        _activePreset = preset.Name;
        _keepPreset = false;

        Persist();
        RefreshPresets();
        Toast.Text = $"已套用预设「{preset.Name}」。";
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ClockPreset preset) return;

        _presets.Remove(preset);
        var wasActive = string.Equals(preset.Name, _activePreset, StringComparison.OrdinalIgnoreCase);
        if (wasActive) _activePreset = "";        

        Persist();
        RefreshPresets();
        Toast.Text = wasActive
            ? $"已删除预设「{preset.Name}」，当前外观保持不变（现为自定义外观）。"
            : $"已删除预设「{preset.Name}」。";
    }

    private void ResetPreset_Click(object sender, RoutedEventArgs e)
    {
        _keepPreset = true;
        _settings.CopyFrom(new ClockSettings());
        SyncControls();
        Apply();
        _activePreset = "";                       
        _keepPreset = false;

        Persist();
        RefreshPresets();
        Toast.Text = "已恢复默认外观。";
    }

    private static void CopyInto(ClockSettings target, ClockSettings source)
    {
        target.BackgroundImagePath = source.BackgroundImagePath;
        target.Veil = source.Veil;
        target.VeilStrength = source.VeilStrength;
        target.Tone = source.Tone;
        target.Ink = source.Ink;
        target.FontFamily = source.FontFamily;
        target.Scale = source.Scale;
        target.ShowSeconds = source.ShowSeconds;
        target.ShowDate = source.ShowDate;
        target.Hour12 = source.Hour12;
    }

    private static ClockSettings CopyOf(ClockSettings source)
    {
        var copy = new ClockSettings();
        CopyInto(copy, source);
        return copy;
    }

    private Brush Res(string key) => Services.ThemeBrush.Get(this, key);
}
