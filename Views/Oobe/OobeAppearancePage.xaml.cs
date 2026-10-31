using System;
using System.IO;
using System.Linq;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClassSoftwareHub.Desktop.Views.Oobe;

/// <summary>
/// 第 4 步：外观。主题、背景材质只在本窗预览，真正的落盘在窗口收尾。
/// </summary>
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

    /// <summary>
    /// 预览图跟着 exe 走（内嵌资源），释放到缓存目录后再交给 Image —— 单文件发布也没有散落文件。
    /// ⚠️ 用 Image 而不是 Border.Background 的 ImageBrush：主题一换（本页选浅色/深色就是换主题），
    ///    挂在 Background 上的 ImageBrush 不保证重新绘制（实测切浅色后三张图整块空白）。
    /// </summary>
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
        session.PreviewTheme(theme);      // 本窗立刻换色，真正的写入留到收尾
        UpdateRings(theme);
    }

    /// <summary>选中态画在卡片外圈：默认的单选圆点被模板去掉了，靠这圈强调色表达。</summary>
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
        session.PreviewBackdrop(kind);    // 让用户当场看到材质变化
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

    /// <summary>关掉分体外观时，「外部组件外观」没有意义 —— 置灰并说明当前行为。</summary>
    private void UpdateExtThemeAvailability()
    {
        ExtThemeCard.IsEnabled = SplitThemeSwitch.IsOn;
        ExtThemeHint.Text = SplitThemeSwitch.IsOn
            ? "只有侧边栏与「常用工具」浮窗跟着这个走；主界面与其它窗口仍跟「主题」"
            : "现在是关的：常用工具浮窗跟主界面的颜色模式保持一致";
    }
}
