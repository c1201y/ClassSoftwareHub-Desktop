using System;
using System.IO;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace ClassSoftwareHub.Desktop.Views;












internal static class ClockBackdrop
{
    
    private static bool IsDark => Application.Current.RequestedTheme == ApplicationTheme.Dark;

    
    
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
