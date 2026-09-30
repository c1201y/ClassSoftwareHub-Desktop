using System;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Services.VirtualKeyboard;
using ClassSoftwareHub.Desktop.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 「实验性功能 → 虚拟键盘」。
///
/// ⚠️ 它归在实验性功能分组里，**不占设置页**（Nick 2026-09-30 定）。
/// ⛔ 所有项一律用系统 <c>SettingsCard</c>，不自行绘制卡片。
/// </summary>
public sealed partial class VirtualKeyboardPage : Page
{
    /// <summary>装载默认值期间把回调憋住（不然初始化会把设置一份份写回去）。</summary>
    private bool _loading = true;

    /// <summary>滑块写盘防抖：拖一次会触发几十次 ValueChanged，逐次写文件没必要。</summary>
    private DispatcherQueueTimer? _saveTimer;

    public VirtualKeyboardPage()
    {
        InitializeComponent();
        LoadFromSettings();
        _loading = false;

        Unloaded += (_, _) => FlushSave();
    }

    private void LoadFromSettings()
    {
        var settings = App.Settings.Current;

        EnableSwitch.IsOn = settings.VirtualKeyboardEnabled;
        CaptureSwitch.IsOn = settings.VirtualKeyboardCaptureSystem;

        ModeCombo.SelectedIndex = KeyboardLayouts.NormalizeMode(settings.VirtualKeyboardMode)
            == KeyboardLayouts.ModeCompact ? 1 : 0;

        ScaleSlider.Value = Math.Clamp(settings.VirtualKeyboardScale, ScaleSlider.Minimum, ScaleSlider.Maximum);
        WidthSlider.Value = Math.Clamp(settings.VirtualKeyboardWidthRatio, WidthSlider.Minimum, WidthSlider.Maximum);
        FontSlider.Value = Math.Clamp(settings.VirtualKeyboardFontScale, FontSlider.Minimum, FontSlider.Maximum);

        PlacementCombo.SelectedIndex = settings.VirtualKeyboardFloating ? 1 : 0;

        ThemeCombo.SelectedIndex = settings.VirtualKeyboardTheme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };

        RefreshLabels();
    }

    private void RefreshLabels()
    {
        ScaleText.Text = $"{Math.Round(ScaleSlider.Value * 100)}%";
        WidthText.Text = $"{Math.Round(WidthSlider.Value * 100)}%";
        FontText.Text = $"{Math.Round(FontSlider.Value * 100)}%";
    }

    // ── 读写设置 ────────────────────────────────────────────

    private void DebouncedSave()
    {
        if (_saveTimer is null)
        {
            // ⚠️ 定时器必须用字段持有 —— 局部变量会被回收，回调就再也不来了。
            _saveTimer = DispatcherQueue.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (timer, _) =>
            {
                timer.Stop();
                App.Settings.Save();
            };
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushSave()
    {
        if (_saveTimer is { IsRunning: true })
        {
            _saveTimer.Stop();
            App.Settings.Save();
        }
    }

    // ── 事件 ────────────────────────────────────────────────

    private void Enable_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var on = EnableSwitch.IsOn;
        App.Settings.Current.VirtualKeyboardEnabled = on;
        App.Settings.Save();

        VirtualKeyboardService.Apply();

        // 打开的那一刻直接摆出来 —— 不用他自己再找按钮。
        if (on) VirtualKeyboardWindow.ShowKeyboard(userInitiated: true);
        else VirtualKeyboardWindow.HideKeyboard();
    }

    private void Capture_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        App.Settings.Current.VirtualKeyboardCaptureSystem = CaptureSwitch.IsOn;
        App.Settings.Save();
        VirtualKeyboardService.Apply();
    }

    private void Show_Click(object sender, RoutedEventArgs e) =>
        VirtualKeyboardWindow.Toggle();

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        var compact = ModeCombo.SelectedIndex == 1;
        App.Settings.Current.VirtualKeyboardMode = compact
            ? KeyboardLayouts.ModeCompact
            : KeyboardLayouts.ModeFull;
        App.Settings.Save();

        // 布局变了要整盘重建，不能只刷外观。
        VirtualKeyboardWindow.ApplySettings();
    }

    private void Scale_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;

        App.Settings.Current.VirtualKeyboardScale = e.NewValue;
        DebouncedSave();
        RefreshLabels();
        VirtualKeyboardWindow.RefreshLook();
    }

    private void Width_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;

        App.Settings.Current.VirtualKeyboardWidthRatio = e.NewValue;
        DebouncedSave();
        RefreshLabels();
        VirtualKeyboardWindow.RefreshLook();
    }

    private void Font_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;

        App.Settings.Current.VirtualKeyboardFontScale = e.NewValue;
        DebouncedSave();
        RefreshLabels();
        VirtualKeyboardWindow.RefreshLook();
    }

    private void Placement_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        App.Settings.Current.VirtualKeyboardFloating = PlacementCombo.SelectedIndex == 1;
        App.Settings.Save();
        VirtualKeyboardWindow.RefreshLook();
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        App.Settings.Current.VirtualKeyboardTheme = ThemeCombo.SelectedIndex switch
        {
            1 => "light",
            2 => "dark",
            _ => "system",
        };
        App.Settings.Save();
        VirtualKeyboardWindow.RefreshLook();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        FlushSave();
    }
}
