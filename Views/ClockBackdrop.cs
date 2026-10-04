using System;
using System.IO;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 把「全屏时钟」的外观铺到全屏倒计时 / 全屏秒表上。
/// 由课堂计时器页顶那个「使用全屏时钟背景设置」开关控制（2026-10-04 Nick 要的）。
///
/// 只搬**背景那一套**：背景图、蒙版、底色、字色（外加进度条，否则白条压在浅底上等于没有）。
/// 字体、12/24 小时制、秒针那些是时钟自己的事，计时盘不该被改 —— 搬过来的话投影出来的
/// 数字会跟计时器页面上看到的对不上，反而怪。
///
/// ⚠️ 开关关着、或者时钟外观压根没存过，就**什么都不做**：全屏窗口保持原来那个深底。
///    所以调用方先把开关判掉，这里不再重复判一次。
/// </summary>
internal static class ClockBackdrop
{
    /// <summary>当前是不是深色主题（时钟的 Tone=跟随主题 时要靠它定底色）。</summary>
    private static bool IsDark => Application.Current.RequestedTheme == ApplicationTheme.Dark;

    /// <param name="bar">进度条；没有就传 null（全屏秒表没有进度条）。</param>
    /// <param name="ink">要吃字色的文本（大字、状态行、秒表那截小数……）。</param>
    public static void Apply(Grid root, Image bg, Border veil, ProgressBar? bar, params TextBlock[] ink)
    {
        try
        {
            var s = ClockPresetStore.Load().LastUsed ?? new ClockSettings();

            var hasPhoto = !string.IsNullOrWhiteSpace(s.BackgroundImagePath)
                           && File.Exists(s.BackgroundImagePath);

            root.Background = ClockRender.BaseBrush(s, IsDark);

            if (hasPhoto)
            {
                // ⚠️ 直接给 UriSource：本机文件走这条最省事，不用自己开流再喂 BitmapImage
                bg.Source = new BitmapImage(new Uri(s.BackgroundImagePath));
                bg.Visibility = Visibility.Visible;
            }
            else
            {
                bg.Source = null;
                bg.Visibility = Visibility.Collapsed;
            }

            veil.Background = ClockRender.VeilBrush(s);

            var face = ClockRender.FaceColor(s, IsDark, hasPhoto);
            var brush = new SolidColorBrush(face);
            foreach (var t in ink) t.Foreground = brush;

            if (bar is not null)
            {
                // 满的那截用字色（跟大字同色），底槽用同一色的极淡版 —— 浅底深字 / 深底浅字都不会丢
                bar.Foreground = new SolidColorBrush(Color.FromArgb(150, face.R, face.G, face.B));
                bar.Background = new SolidColorBrush(Color.FromArgb(38, face.R, face.G, face.B));
            }
        }
        catch (Exception ex)
        {
            AppLog.Info("clock-bg", "铺全屏时钟背景失败：" + ex.Message);
        }
    }
}
