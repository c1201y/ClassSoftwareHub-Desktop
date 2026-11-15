using System;
using System.IO;
using System.Linq;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClassSoftwareHub.Desktop.Views.Oobe;




public sealed partial class OobeAppearancePage : Page
{
    private bool _loading;

    public OobeAppearancePage()
    {
        InitializeComponent();

        LoadPreview(SystemPreview, "appearance-system.png");
        LoadPreview(LightPreview, "appearance-light.png");
        LoadPreview(DarkPreview, "appearance-dark.png");

        _loading = true;
        var draft = OobeSession.Current!.Draft;
        ThemeSystem.IsChecked = draft.Theme != "light" && draft.Theme != "dark";
        ThemeLight.IsChecked = draft.Theme == "light";
        ThemeDark.IsChecked = draft.Theme == "dark";
        SelectCombo(BackdropCombo, draft.Backdrop);
        SplitThemeSwitch.IsOn = draft.SplitTheme;
        SelectCombo(ExtThemeCombo, draft.ExternalTheme);
        _loading = false;

        UpdateRings(draft.Theme);
        UpdateExtThemeAvailability();
    }

    
    
    
    
    
    private static void LoadPreview(Image target, string assetName)
    {
        try
        {
            var path = EmbeddedAssets.ExtractToCache(assetName, "oobe-" + assetName);
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                target.Source = new BitmapImage(new Uri(path));
        }
        catch (Exception ex)
        {
            AppLog.Exception("oobe", ex, "外观预览图加载失败");
        }
    }

    private static void SelectCombo(ComboBox combo, string tag)
    {
        var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == tag);
        combo.SelectedItem = item ?? combo.Items[0];
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton radio) return;

        var theme = radio.Tag as string ?? "system";
        var session = OobeSession.Current;
        if (session is null) return;

        session.Draft.Theme = theme;
        session.PreviewTheme(theme);      
        UpdateRings(theme);
    }

    
    private void UpdateRings(string theme)
    {
        RingSystem.Opacity = theme == "system" ? 1 : 0;
        RingLight.Opacity = theme == "light" ? 1 : 0;
        RingDark.Opacity = theme == "dark" ? 1 : 0;
    }

    private void Backdrop_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (BackdropCombo.SelectedItem is not ComboBoxItem { Tag: string kind }) return;

        var session = OobeSession.Current;
        if (session is null) return;

        session.Draft.Backdrop = kind;
        session.PreviewBackdrop(kind);    
    }

    private void SplitTheme_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var session = OobeSession.Current;
        if (session is null) return;

        session.Draft.SplitTheme = SplitThemeSwitch.IsOn;
        UpdateExtThemeAvailability();
    }

    private void ExtTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ExtThemeCombo.SelectedItem is not ComboBoxItem { Tag: string theme }) return;

        var session = OobeSession.Current;
        if (session is not null) session.Draft.ExternalTheme = theme;
    }

    
    private void UpdateExtThemeAvailability()
    {
        ExtThemeCard.IsEnabled = SplitThemeSwitch.IsOn;
        ExtThemeHint.Text = SplitThemeSwitch.IsOn
            ? "只有侧边栏与「常用工具」浮窗跟着这个走；主界面与其它窗口仍跟「主题」"
            : "现在是关的：常用工具浮窗跟主界面的颜色模式保持一致";
    }
}
