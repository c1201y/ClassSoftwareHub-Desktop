using System;
using System.Collections.Generic;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClassSoftwareHub.Desktop.Views.Palette;

/// <summary>
/// 浮窗版时钟：大号时间 + 日期 + 一键进全屏时钟，**外加外观预览与预设切换**（2026-09-28 Nick 提）。
///
/// 外观遵循「上次的样子」（<see cref="ClockPresetStore"/>）：上次在用某套预设就按那套画，
/// 上次没用预设就保持那个自定义外观 —— 不会因为开一下浮窗就把工具页里调好的样子顶掉。
/// 这里只做切换与"恢复默认"，要细调（蒙版强度、字号…）还是去工具页。
/// </summary>
public sealed partial class MiniClock : UserControl
{
    private readonly DispatcherQueueTimer _timer;
    private readonly ClockSettings _settings = new();
    private readonly List<ClockPreset> _presets = new();
    private string _activePreset = "";

    public MiniClock()
    {
        InitializeComponent();

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Tick();

        // 跟主题走：字色/底色选的是"跟随应用主题"时，切主题得重画一次
        ActualThemeChanged += (_, _) => ApplyAppearance();

        Reload();
        _timer.Start();
    }

    /// <summary>浮窗收起时停表（省电省内存，收起状态下没人看）。</summary>
    public void Pause() => _timer.Stop();

    /// <summary>浮窗再打开时恢复。⚠️ 顺手重读一次外观 —— 用户可能刚在工具页里换过预设、调过底色。</summary>
    public void Resume()
    {
        Reload();
        _timer.Start();
    }

    /// <summary>
    /// 把外观与预设列表从存档读回来。
    /// ⚠️ 判据跟工具页完全一致（<c>LastUsedPreset</c>）：上次在用某套预设 → 按那套铺；
    /// 上次没用预设 → 按 <c>LastUsed</c> 那个外观铺，**不强行套任何预设**。两边算法要一起改。
    /// </summary>
    public void Reload()
    {
        var data = ClockPresetStore.Load();
        _presets.Clear();
        _presets.AddRange(data.Presets);

        var active = _presets.Find(p =>
            !string.IsNullOrEmpty(data.LastUsedPreset)
            && string.Equals(p.Name, data.LastUsedPreset, StringComparison.OrdinalIgnoreCase));

        if (active is not null)
        {
            _settings.CopyFrom(active.ToSettings());
            _activePreset = active.Name;
        }
        else
        {
            _settings.CopyFrom(data.LastUsed ?? new ClockSettings());
            _activePreset = "";
        }

        ApplyAppearance();
        RefreshPresets();
        Tick();
    }

    // ══════════ 外观预览 ══════════

    /// <summary>把外观画到浮窗那张"舞台"上（底色 / 背景图 / 蒙版 / 字色 / 字体 / 字号）。</summary>
    private void ApplyAppearance()
    {
        try
        {
            var dark = ActualTheme == ElementTheme.Dark;
            var hasPhoto = !string.IsNullOrWhiteSpace(_settings.BackgroundImagePath);

            var face = new SolidColorBrush(ClockRender.FaceColor(_settings, dark, hasPhoto));
            TimeText.Foreground = face;
            SecText.Foreground = face;
            DateText.Foreground = face;

            Stage.Background = hasPhoto
                ? PhotoBrushOf(_settings.BackgroundImagePath, ClockRender.BaseBrush(_settings, dark))
                : ClockRender.BaseBrush(_settings, dark);

            VeilHost.Background = ClockRender.VeilBrush(_settings);

            var font = new FontFamily(_settings.FontFamily);
            TimeText.FontFamily = font;
            SecText.FontFamily = font;
            DateText.FontFamily = font;

            // 浮窗就这么大：字号跟着设置缩放，但夹在一个放得下的范围内（别把按钮顶出去）
            TimeText.FontSize = Math.Clamp(46 * _settings.Scale, 24, 60);
            SecText.FontSize = Math.Clamp(18 * _settings.Scale, 10, 26);
            DateText.FontSize = Math.Clamp(11 * _settings.Scale, 8, 16);
        }
        catch (Exception ex)
        {
            Log("浮窗外观渲染失败: " + ex.Message);
        }
    }

    /// <summary>背景图刷子；解不开就退回底色刷。浮窗只有 ~370 宽，缩着解就够。</summary>
    private static Brush PhotoBrushOf(string path, Brush fallback)
    {
        try
        {
            var bmp = new BitmapImage { DecodePixelWidth = 480 };
            bmp.UriSource = new Uri(path);
            return new ImageBrush { ImageSource = bmp, Stretch = Stretch.UniformToFill };
        }
        catch
        {
            return fallback;
        }
    }

    // ══════════ 预设切换 ══════════

    private void RefreshPresets()
    {
        PresetHost.Children.Clear();

        if (_presets.Count == 0)
        {
            PresetHost.Children.Add(new TextBlock
            {
                Text = "尚未保存预设 · 可在「详细设置」中保存",
                FontSize = 12,
                Opacity = 0.55,
                VerticalAlignment = VerticalAlignment.Center,
            });
            return;
        }

        foreach (var preset in _presets)
        {
            var active = string.Equals(preset.Name, _activePreset, StringComparison.OrdinalIgnoreCase);
            var chip = new Button
            {
                Content = preset.Name,
                Tag = preset,
                MinWidth = 0,
                Padding = new Thickness(10, 5, 10, 5),
                FontSize = 12.5,
            };

            // 无障碍 + 自动化测试用：光看 UIA 树认不出哪颗是哪套，也看不出在用哪套
            AutomationProperties.SetName(chip, active
                ? $"预设「{preset.Name}」（正在使用）"
                : $"应用预设「{preset.Name}」");
            ToolTipService.SetToolTip(chip, preset.Summary());

            if (active) chip.Style = AccentChipStyle();

            chip.Click += Preset_Click;
            PresetHost.Children.Add(chip);
        }
    }

    /// <summary>主题色按钮样式（正在用的那套高亮）。取不到就返回 null = 用默认样式，不影响功能。</summary>
    private static Style? AccentChipStyle()
    {
        try
        {
            return Application.Current.Resources.TryGetValue("AccentButtonStyle", out var v) ? v as Style : null;
        }
        catch { return null; }
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ClockPreset preset) return;

        _settings.CopyFrom(preset.ToSettings());
        _activePreset = preset.Name;
        Persist();
        ApplyAppearance();
        RefreshPresets();
        Tick();
    }

    /// <summary>
    /// 恢复默认：退回出厂外观，并且**不再套用任何预设** ——
    /// 这条要一起写进存档，否则下次打开又被上次那套预设顶回去（Nick 要的"遵循上次"就变成了"赖着不走"）。
    /// </summary>
    private void ResetAppearance_Click(object sender, RoutedEventArgs e)
    {
        _settings.CopyFrom(new ClockSettings());
        _activePreset = "";
        Persist();
        ApplyAppearance();
        RefreshPresets();
        Tick();
    }

    /// <summary>
    /// 落盘。⚠️ 先读回最新的一份再改：工具页那边也在写这个文件（它管 Presets 列表），
    /// 拿手上这份可能过期的快照整份覆盖，会把工具页刚存的预设抹掉。
    /// </summary>
    private void Persist()
    {
        var data = ClockPresetStore.Load();
        data.LastUsed = _settings.Clone();
        data.LastUsedPreset = _activePreset;
        ClockPresetStore.Save(data);
    }

    // ══════════ 时间 / 按钮 ══════════

    private void Tick()
    {
        var now = DateTime.Now;
        TimeText.Text = ClockRender.TimeText(now, _settings.Hour12);
        SecText.Text = ClockRender.SecText(now);
        DateText.Text = ClockRender.DateText(now);
        SecText.Visibility = _settings.ShowSeconds ? Visibility.Visible : Visibility.Collapsed;
        DateText.Visibility = _settings.ShowDate ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        var dark = ActualTheme == ElementTheme.Dark;

        // 2026-09-28：浮窗现在自己就知道当前是哪套外观（上面那块预览就是照它画的），
        // 所以直接把这份传过去 —— 在浮窗里切了预设再点全屏，全屏就该是刚切的那套。
        // （以前传 null 是因为手上只有一套默认值，传过去反而会顶掉用户调好的设置。）
        Views.ClockFullscreenWindow.Show(_settings.Clone(), dark);

        // 进全屏后浮窗还杵在那儿就得手动再关一次，多此一举 —— 顺手收掉。
        // 它本来就浮在最上层，不收的话正好挡住全屏时钟中间那块字。
        Views.ToolPaletteWindow.HidePalette();
    }

    /// <summary>
    /// 「详细设置」：浮窗就这么大，放不下字体/底色/遮罩那一套 —— 直接把人送到应用里的时钟工具页。
    /// 顺手把浮窗收起来，免得挡着主界面。
    /// </summary>
    private void Details_Click(object sender, RoutedEventArgs e)
    {
        Views.ToolPaletteWindow.HidePalette();
        App.MainWindow?.OpenToolSettings(typeof(Pages.Tools.ClockToolPage));
    }

    private static void Log(string message)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassSoftwareHub");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "palette.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { /* 日志写不进去就算了，别反过来影响时钟 */ }
    }
}
