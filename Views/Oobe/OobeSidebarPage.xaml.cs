using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Oobe;

/// <summary>
/// 第 5 步：侧边工具栏（开关类）。只改草稿，真正的落盘（含侧边栏重建）留到窗口收尾。
/// </summary>
public sealed partial class OobeSidebarPage : Page
{
    private bool _loading;

    public OobeSidebarPage()
    {
        InitializeComponent();

        _loading = true;
        var draft = OobeSession.Current!.Draft;
        SidebarSwitch.IsOn = draft.SidebarEnabled;
        PaletteTopSwitch.IsOn = draft.PaletteOnTop;
        LongLabelSwitch.IsOn = draft.SidebarLongLabels;
        PinnedSwitch.IsOn = draft.SidebarPinned;
        _loading = false;

        UpdateEnabledState();
    }

    private void Sidebar_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var session = OobeSession.Current;
        if (session is null) return;

        session.Draft.SidebarEnabled = SidebarSwitch.IsOn;
        UpdateEnabledState();
    }

    private void PaletteTop_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var session = OobeSession.Current;
        if (session is not null) session.Draft.PaletteOnTop = PaletteTopSwitch.IsOn;
    }

    private void LongLabel_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var session = OobeSession.Current;
        if (session is not null) session.Draft.SidebarLongLabels = LongLabelSwitch.IsOn;
    }

    private void Pinned_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var session = OobeSession.Current;
        if (session is not null) session.Draft.SidebarPinned = PinnedSwitch.IsOn;
    }

    /// <summary>
    /// 侧边栏关掉时，「按钮长名 / 常驻」没有意义 —— 用控件自带的禁用视觉置灰。
    /// （「常用工具窗口置顶」管的是另一个浮窗，不受这个开关影响。）
    /// </summary>
    private void UpdateEnabledState()
    {
        LongLabelCard.IsEnabled = SidebarSwitch.IsOn;
        PinnedCard.IsEnabled = SidebarSwitch.IsOn;
    }
}
