using System;
using System.IO;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClassSoftwareHub.Desktop.Views.Oobe;

/// <summary>
/// 第 12 步：完成。内部三张卡横滑；到最后一页「下一步」换成「完成」。
/// </summary>
public sealed partial class OobeFinishPage : Page
{
    private const int LastSlide = 2;

    private int _slide;

    public OobeFinishPage()
    {
        InitializeComponent();

        // 配图跟着深浅色走：页是缓存的，主题可能在前面「外观」那步被改过，所以每次显示都重取一次。
        Loaded += (_, _) => LoadSlideImages();
        ActualThemeChanged += (_, _) => LoadSlideImages();

        UpdateChrome();
    }

    /// <summary>截图有深浅两套（白 = 浅色主题版），按本页实际的深浅色取。</summary>
    private void LoadSlideImages()
    {
        var tone = ActualTheme == ElementTheme.Light ? "light" : "dark";
        SetImage(DownloadsBack, "oobe-slide-downloads-back-" + tone + ".png");
        SetImage(DownloadsFront, "oobe-slide-downloads-front-" + tone + ".png");
        SetImage(ToolsBack, "oobe-slide-tools-back-" + tone + ".png");
        SetImage(ToolsFront, "oobe-slide-tools-front-" + tone + ".png");
        SetImage(FinishBanner, "oobe-finish-banner.jpg");
    }

    /// <summary>
    /// 内嵌资源释放到缓存目录后交给 Image —— 单文件发布也没有散落文件。
    /// ⚠️ 用 Image 而不是 Border.Background 的 ImageBrush：后两屏初始是折叠的，
    ///    在折叠状态挂上的 ImageBrush 到显示时不会重新绘制（深色下实测整块空白），
    ///    Image 元素没有这个问题。
    /// </summary>
    private static void SetImage(Image target, string assetName)
    {
        try
        {
            var path = EmbeddedAssets.ExtractToCache(assetName, "oobe-" + assetName);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            var bitmap = new BitmapImage(new Uri(path));
            bitmap.ImageFailed += (_, e) =>
                AppLog.Warn("oobe", "完成页配图解码失败：" + assetName + "：" + e.ErrorMessage);
            target.Source = bitmap;
        }
        catch (Exception ex)
        {
            AppLog.Exception("oobe", ex, "完成页配图加载失败：" + assetName);
        }
    }

    /// <summary>底部导航条主按钮的文案：最后一屏变「完成」。</summary>
    public string PrimaryText => _slide >= LastSlide ? "完成" : "下一步";

    /// <summary>底部导航条「下一步」：先翻轮播，最后一屏才收尾。</summary>
    internal void StepForward()
    {
        if (_slide >= LastSlide) OobeSession.Current?.Finish();
        else ShowSlide(_slide + 1, forward: true, animate: true);
    }

    /// <summary>底部导航条「上一步」：第一屏再往回就是退回第 11 步。</summary>
    internal void StepBack()
    {
        if (_slide <= 0) OobeSession.Current?.Back();
        else ShowSlide(_slide - 1, forward: false, animate: true);
    }

    private void ShowSlide(int index, bool forward, bool animate)
    {
        var slides = new[] { Slide1, Slide2, Slide3 };
        var target = Math.Clamp(index, 0, LastSlide);
        var from = slides[Math.Clamp(_slide, 0, LastSlide)];
        var to = slides[target];
        _slide = target;

        UpdateChrome();

        if (!animate || ReferenceEquals(from, to))
        {
            foreach (var slide in slides)
                slide.Visibility = ReferenceEquals(slide, to) ? Visibility.Visible : Visibility.Collapsed;

            to.Opacity = 1;
            to.RenderTransform = null;
            to.IsHitTestVisible = true;
            return;
        }

        OobeMotion.Swap(from, to, forward, distance: 60, durationMs: 300);
    }

    private void UpdateChrome()
    {
        Dot1.Opacity = _slide == 0 ? 1 : 0.25;
        Dot2.Opacity = _slide == 1 ? 1 : 0.25;
        Dot3.Opacity = _slide == 2 ? 1 : 0.25;

        OobeSession.Current?.RefreshChrome();
    }

    // ⚠️ 「软件下载」的导航 tag 是 apps，不是 downloads —— downloads 是下半区的「任务进行」
    //    （DownloadsNav，Tag="downloads"）。写错会跳到任务页，而且不报错（2026-10-31 实拍发现）。
    private void GoDownloads_Click(object sender, RoutedEventArgs e) => Go("apps");

    private void GoTweaks_Click(object sender, RoutedEventArgs e) => Go("systemtweaks");

    private void GoAi_Click(object sender, RoutedEventArgs e) => Go("hubai-settings");

    /// <summary>
    /// 完成页的入口：先收尾（写设置 + 亮主窗口），再跳页。
    /// ⚠️ 顺序不能反 —— 主窗口还没显示时 Shell 的 Frame 还没导航过任何页面，先跳会被吞掉。
    /// </summary>
    private void Go(string tag) => OobeSession.Current?.FinishAndNavigate(tag);
}
