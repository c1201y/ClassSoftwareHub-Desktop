using System;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Views.Oobe;

/// <summary>
/// 第 1 步：欢迎。开场演完（跑马灯 → 标语 → 品牌名 → 图标 / 欢迎语 / 版本号）才亮出页底提示。
/// 页面只负责把窗口"已经露脸"这件事转告给开场控制 —— 动画本身都在
/// <see cref="Controls.OobeWelcomeIntro"/> 里。
/// </summary>
public sealed partial class OobeWelcomePage : Page
{
    private bool _revealed;

    public OobeWelcomePage()
    {
        InitializeComponent();
        LoadCaveat();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (OobeSession.Current is { } session) session.Started += OnSessionStarted;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (OobeSession.Current is { } session) session.Started -= OnSessionStarted;
    }

    /// <summary>窗口真的显示出来了才开始唱开场，否则动画全在不可见的窗口里跑完。</summary>
    private void OnSessionStarted(object? sender, EventArgs e) => Intro.Play();

    /// <summary>Win10 上确实有功能会灰掉，与其以后在页面上撞见，不如首屏底下先说一句。</summary>
    private void LoadCaveat()
    {
        if (SystemInfo.IsWin11)
        {
            Caveat.Visibility = Visibility.Collapsed;
            return;
        }

        Caveat.Text = "部分功能仅支持 Windows 11，在 Windows 10 上会显示为不可用";
    }

    private void Intro_Completed(object sender, EventArgs e)
    {
        if (_revealed) return;
        _revealed = true;

        if (Caveat.Visibility == Visibility.Visible) OobeMotion.Fade(Caveat, 0);
    }
}
