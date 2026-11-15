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

        
        ActualThemeChanged += (_, _) => ApplyAppearance();

        Reload();
        _timer.Start();
    }

    
    public void Pause() => _timer.Stop();

    
    public void Resume()
    {
        Reload();
        _timer.Start();
    }

    
    
    
    
    
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

            var font = new FontFamily(ClockRender.NormalizeFont(_settings.FontFamily));
            TimeText.FontFamily = font;
            SecText.FontFamily = font;
            DateText.FontFamily = font;

            
            TimeText.FontSize = Math.Clamp(46 * _settings.Scale, 24, 60);
            SecText.FontSize = Math.Clamp(18 * _settings.Scale, 10, 26);
            DateText.FontSize = Math.Clamp(11 * _settings.Scale, 8, 16);
        }
        catch (Exception ex)
        {
            Log("浮窗外观渲染失败: " + ex.Message);
        }
    }

    
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

            
            AutomationProperties.SetName(chip, active
                ? $"预设「{preset.Name}」（正在使用）"
                : $"应用预设「{preset.Name}」");
            ToolTipService.SetToolTip(chip, preset.Summary());

            if (active) chip.Style = AccentChipStyle();

            chip.Click += Preset_Click;
            PresetHost.Children.Add(chip);
        }
    }

    
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

    
    
    
    
    private void ResetAppearance_Click(object sender, RoutedEventArgs e)
    {
        _settings.CopyFrom(new ClockSettings());
        _activePreset = "";
        Persist();
        ApplyAppearance();
        RefreshPresets();
        Tick();
    }

    
    
    
    
    private void Persist()
    {
        var data = ClockPresetStore.Load();
        data.LastUsed = _settings.Clone();
        data.LastUsedPreset = _activePreset;
        ClockPresetStore.Save(data);
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

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        var dark = ActualTheme == ElementTheme.Dark;

        
        
        
        Views.ClockFullscreenWindow.Show(_settings.Clone(), dark);

        
        
        Views.ToolPaletteWindow.HidePalette();
    }

    
    
    
    
    private void Details_Click(object sender, RoutedEventArgs e)
    {
        Views.ToolPaletteWindow.HidePalette();
        App.MainWindow?.OpenToolSettings(typeof(Pages.Tools.ClockToolPage));
    }

    private static void Log(string message)
    {
        try
        {
            Core.AppLog.Info("palette", message);
        }
        catch {  }
    }
}
