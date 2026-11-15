using System;
using System.IO;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClassSoftwareHub.Desktop.Views.Oobe;




public sealed partial class OobeFinishPage : Page
{
    private const int LastSlide = 2;

    private int _slide;

    public OobeFinishPage()
    {
        InitializeComponent();

        
        Loaded += (_, _) => LoadSlideImages();
        ActualThemeChanged += (_, _) => LoadSlideImages();

        UpdateChrome();
    }

    
    private void LoadSlideImages()
    {
        var tone = ActualTheme == ElementTheme.Light ? "light" : "dark";
        SetImage(DownloadsBack, "oobe-slide-downloads-back-" + tone + ".png");
        SetImage(DownloadsFront, "oobe-slide-downloads-front-" + tone + ".png");
        SetImage(ToolsBack, "oobe-slide-tools-back-" + tone + ".png");
        SetImage(ToolsFront, "oobe-slide-tools-front-" + tone + ".png");
        SetImage(FinishBanner, "oobe-finish-banner.jpg");
    }

    
    
    
    
    
    
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

    
    public string PrimaryText => _slide >= LastSlide ? "完成" : "下一步";

    
    internal void StepForward()
    {
        if (_slide >= LastSlide) OobeSession.Current?.Finish();
        else ShowSlide(_slide + 1, forward: true, animate: true);
    }

    
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

    
    
    private void GoDownloads_Click(object sender, RoutedEventArgs e) => Go("apps");

    private void GoTweaks_Click(object sender, RoutedEventArgs e) => Go("systemtweaks");

    private void GoAi_Click(object sender, RoutedEventArgs e) => Go("hubai-settings");

    
    
    
    
    private void Go(string tag) => OobeSession.Current?.FinishAndNavigate(tag);
}
