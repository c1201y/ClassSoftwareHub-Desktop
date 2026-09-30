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

/// <summary>
/// 全屏时钟（对齐网页版 tools/ClockTool.vue）：
/// 蒙版/材质（无 · 白 · 黑 · 亚克力 · 云母）、背景底色、文字颜色、5 种时间字体、字号缩放、12 小时制、
/// 拖放设背景、全屏窗口、对时入口。
/// </summary>
public sealed partial class ClockToolPage : Page
{
    private const string TimeIsUrl = "https://time.is/";
    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

    /// <summary>
    /// 预设上限。⚠️ 2026-09-28（Nick）：20 → <b>5</b> —— 卡片列是"一眼挑一套"的陈列位，
    /// 不是仓库；超过 5 张就得换行、越堆越高，反而挑不出来。
    /// </summary>
    private const int MaxPresets = 5;

    /// <summary>卡片尺寸（不用大：预览 + 名字 + 两颗按钮，能一眼认出来就行）。</summary>
    private const double PresetCardWidth = 150;
    private const double PresetPreviewHeight = 84;

    private readonly ClockSettings _settings = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    private readonly List<ClockPreset> _presets = new();
    private bool _ready;

    /// <summary>当前"正在使用的那套预设"的名字；空 = 没在用预设（第一次启动 / 手调过 / 恢复过默认）。</summary>
    private string _activePreset = "";

    /// <summary>
    /// 正在程序化地改外观（套预设 / 恢复默认 / 构造铺初值）—— 这期间 <see cref="Apply"/>
    /// **不许**把 _activePreset 清掉（那会把刚套上的预设判成"手调"）。
    /// </summary>
    private bool _keepPreset = true;

    public ClockToolPage()
    {
        InitializeComponent();

        // 先把上次的样子读回来（记忆），再铺控件初值。
        // ⚠️ 2026-09-28（Nick）：「每次启动遵循上次」——上次在用某套预设，就按**那套预设**铺；
        //    上次没在用预设（纯手调 / 默认），就按上次那个外观铺，**不强行套任何预设**。
        var state = ClockPresetStore.Load();
        _presets.AddRange(state.Presets);

        var active = _presets.Find(p =>
            !string.IsNullOrEmpty(state.LastUsedPreset)
            && string.Equals(p.Name, state.LastUsedPreset, StringComparison.OrdinalIgnoreCase));

        if (active is not null)
        {
            _settings.CopyFrom(active.ToSettings());     // 预设为准（那套后来改过也立刻生效）
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
        SyncControls();     // 此刻还没挂事件，改控件不会回调

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

        // 记忆用的落盘定时器：拖滑块时 ValueChanged 是连续的，攒一会儿再写一次文件
        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => Persist();

        // 离开页面就停表（定时器的委托会把整个页面钉在内存里），顺手把攒着的改动落盘
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            _saveTimer.Stop();
            Persist();
        };

        _ready = true;
        Apply();
        Tick();
        _keepPreset = false;      // 初值铺完了 —— 之后任何一次外观变化都算"用户手调"
        RefreshPresets();
        _timer.Start();
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
        // 文件名只占一行的一个格（右边还有「删除图片」），长了走省略号，全名挂在悬停提示上
        BgName.Text = HasPhoto
            ? System.IO.Path.GetFileName(_settings.BackgroundImagePath)
            : "未设置背景图（可拖入图片）";
        ToolTipService.SetToolTip(BgName, HasPhoto ? _settings.BackgroundImagePath : BgName.Text);

        // 记忆：外观一改就记下来（防抖）
        if (_ready)
        {
            // 手调过就不再算"正在用某套预设"（下次启动遵循的是"上次没用预设" → 保持这个外观）
            if (!_keepPreset) MarkCustomAppearance();
            ScheduleSave();
        }

        Tick();
    }

    /// <summary>
    /// 用户手动改了外观 → 退出"正在使用预设"状态。只影响高亮和下次启动的判据，
    /// **不会**动外观本身（外观就是用户刚调的那个）。
    /// </summary>
    private void MarkCustomAppearance()
    {
        if (_activePreset.Length == 0) return;
        _activePreset = "";
        RefreshPresets();
    }

    private Brush PhotoBrush()
        => PhotoBrushOf(_settings.BackgroundImagePath, ClockRender.BaseBrush(_settings, Dark));

    /// <summary>
    /// 背景图刷子。解不开就用 <paramref name="fallback"/>（底色刷），不会崩。
    /// <paramref name="decodeWidth"/> &gt; 0 时缩着解 —— 预设卡只有 138 宽，没必要按原图尺寸解到内存里。
    /// ⚠️ DecodePixelWidth 必须在 UriSource 之前设（设完 UriSource 就开始解码了）。
    /// </summary>
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
                Toast.Text = "图片过大（建议 12MB 以内）";
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
            Toast.Text = "拖入的文件读取失败：" + ex.Message;
        }
    }

    // ══════════ 全屏 / 对时 ══════════
    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        // 走统一入口：已经开着就就地换成这套设置（不会再叠出第二个全屏窗口）
        Views.ClockFullscreenWindow.Show(CopyOf(_settings), Dark);
    }

    private async void TimeSync_Click(object sender, RoutedEventArgs e)
    {
        try { await Launcher.LaunchUriAsync(new Uri(TimeIsUrl)); }
        catch (Exception ex) { Toast.Text = "无法启动浏览器：" + ex.Message; }
    }

    // ══════════ 外观预设 / 记忆（2026-09-27 Nick 提） ══════════

    /// <summary>把 _settings 的当前值回灌到控件上（套用预设、恢复默认之后要用）。</summary>
    private void SyncControls()
    {
        var wasReady = _ready;
        _ready = false;   // 这段里改控件会触发 SelectionChanged / ValueChanged，别让它反过来再 Apply 一次
        try
        {
            VeilBox.SelectedIndex = (int)_settings.Veil;
            ToneBox.SelectedIndex = (int)_settings.Tone;
            InkBox.SelectedIndex = (int)_settings.Ink;
            var fontIndex = Array.IndexOf(ClockRender.FontFamilies, _settings.FontFamily);
            FontBox.SelectedIndex = fontIndex < 0 ? 1 : fontIndex;
            VeilSlider.Value = _settings.VeilStrength;
            ScaleSlider.Value = _settings.Scale;
            ShowSecondsBox.IsChecked = _settings.ShowSeconds;
            ShowDateBox.IsChecked = _settings.ShowDate;
            Hour12Box.IsChecked = _settings.Hour12;
        }
        finally { _ready = wasReady; }
    }

    /// <summary>防抖落盘：拖滑块时 ValueChanged 是连续的，攒 600ms 再写一次文件。</summary>
    private void ScheduleSave()
    {
        // ⚠️ DispatcherQueueTimer 没有 Restart()（那是 WPF DispatcherTimer 的 API）——
        // 先停再开就是"重新计时"，别再写 Restart 了。
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Persist()
    {
        // ⚠️ 先读回最新的一份再改：浮窗时钟也在写这个文件（它只动 LastUsed / LastUsedPreset）。
        //    拿手上这份可能已经过期的快照去整份覆盖，会把浮窗刚写进去的改回去 ——
        //    「共用存档整份覆盖抹掉另一边」这个坑这台机器上踩过，别再各写各的。
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

    /// <summary>
    /// 一张预设卡：上半是**这套外观实际画出来的样子**（底色 · 背景图 · 蒙版 · 字色 · 字体 · 字号），
    /// 下半是名字 + 「套用」「删除」。卡不大（150 宽），一眼认出是哪套。
    /// </summary>
    private FrameworkElement BuildPresetCard(ClockPreset preset)
    {
        var ps = preset.ToSettings();
        var active = string.Equals(preset.Name, _activePreset, StringComparison.OrdinalIgnoreCase);
        var hasPhoto = !string.IsNullOrWhiteSpace(ps.BackgroundImagePath);

        // ── 预览：跟右边大预览同一套渲染（ClockRender），所以它长什么样、全屏就是什么样 ──
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
        var font = new FontFamily(ps.FontFamily);

        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        line.Children.Add(new TextBlock
        {
            Text = "10:24",
            FontSize = Math.Clamp(26 * ps.Scale, 13, 38),
            FontWeight = FontWeights.Bold,
            FontFamily = font,
            Foreground = face,
        });
        if (ps.ShowSeconds)
            line.Children.Add(new TextBlock
            {
                Text = ":08",
                FontSize = Math.Clamp(12 * ps.Scale, 8, 17),
                FontWeight = FontWeights.SemiBold,
                Opacity = 0.85,
                FontFamily = font,
                Foreground = face,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 3),
            });

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
            FontSize = 12.5,
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

        // 名字给无障碍和自动化测试看（卡片本身不是按钮，光看 UIA 树认不出哪张是哪套）
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
            FontSize = 12.5,
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

        // 同名直接覆盖：老师多半是"改一改再存回去"，而不是想要两套一模一样名字的
        var preset = ClockPreset.From(name, _settings);
        var existing = _presets.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) _presets[existing] = preset;
        else _presets.Add(preset);

        // 存完就算"正在使用这套" —— 这样关掉再进来，起来的还是它（Nick 要的「遵循上次」）
        _activePreset = preset.Name;

        Persist();
        RefreshPresets();
        Toast.Text = $"已保存预设「{name}」，正在使用。";
    }

    private void ApplyPreset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ClockPreset preset) return;

        _keepPreset = true;                       // 这一段是程序化换装，别被 Apply 判成"手调"
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
        if (wasActive) _activePreset = "";        // 删掉正在用的那套 → 外观留着，但不再是"在用预设"

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
        _activePreset = "";                       // 恢复默认 = 明确"不用任何预设"
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
