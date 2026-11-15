using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace ClassSoftwareHub.Desktop.Views.Oobe;





public static class OobeMotion
{
    private static bool? _animationsEnabled;

    
    public static bool AnimationsEnabled
    {
        get
        {
            if (_animationsEnabled is bool known) return known;
            try
            {
                _animationsEnabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
            }
            catch
            {
                _animationsEnabled = true;
            }
            return _animationsEnabled.Value;
        }
    }

    
    public static void Fade(FrameworkElement element, int delayMs, int durationMs = 320)
    {
        if (!AnimationsEnabled)
        {
            element.Opacity = 1;
            return;
        }

        try
        {
            element.Opacity = 0;
            var sb = new Storyboard();
            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = Ms(durationMs),
                BeginTime = Time(delayMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(fade, element);
            Storyboard.SetTargetProperty(fade, "Opacity");
            sb.Children.Add(fade);
            sb.Completed += (_, _) => element.Opacity = 1;
            sb.Begin();
        }
        catch
        {
            element.Opacity = 1;
        }
    }

    
    
    
    public static void Swap(FrameworkElement from, FrameworkElement to, bool forward,
        double distance = 60, int durationMs = 300)
    {
        try
        {
            if (!AnimationsEnabled)
            {
                from.Visibility = Visibility.Collapsed;
                from.Opacity = 1;
                from.RenderTransform = null;
                to.Visibility = Visibility.Visible;
                to.Opacity = 1;
                to.RenderTransform = null;
                return;
            }

            var direction = forward ? 1 : -1;

            to.Visibility = Visibility.Visible;
            to.Opacity = 0;
            to.IsHitTestVisible = false;
            to.RenderTransform = new TranslateTransform { X = distance * direction };

            
            
            if (from.RenderTransform is not TranslateTransform)
                from.RenderTransform = new TranslateTransform();

            Canvas.SetZIndex(to, 1);
            Canvas.SetZIndex(from, 0);

            var inSb = new Storyboard();

            var slideIn = new DoubleAnimation
            {
                From = distance * direction,
                To = 0,
                Duration = Ms(durationMs),
                BeginTime = Time(40),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(slideIn, to);
            Storyboard.SetTargetProperty(slideIn, "(UIElement.RenderTransform).(TranslateTransform.X)");

            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = Ms(Math.Max(60, durationMs - 60)),
                BeginTime = Time(40),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(fadeIn, to);
            Storyboard.SetTargetProperty(fadeIn, "Opacity");

            inSb.Children.Add(slideIn);
            inSb.Children.Add(fadeIn);
            inSb.Completed += (_, _) =>
            {
                to.IsHitTestVisible = true;
                to.RenderTransform = null;
                to.Opacity = 1;
            };
            inSb.Begin();

            var outSb = new Storyboard();

            var slideOut = new DoubleAnimation
            {
                From = 0,
                To = -distance * 0.6 * direction,
                Duration = Ms(180),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            Storyboard.SetTarget(slideOut, from);
            Storyboard.SetTargetProperty(slideOut, "(UIElement.RenderTransform).(TranslateTransform.X)");

            var fadeOut = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = Ms(140),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            Storyboard.SetTarget(fadeOut, from);
            Storyboard.SetTargetProperty(fadeOut, "Opacity");

            outSb.Children.Add(slideOut);
            outSb.Children.Add(fadeOut);
            outSb.Completed += (_, _) =>
            {
                from.Visibility = Visibility.Collapsed;
                from.Opacity = 1;
                from.RenderTransform = null;
            };
            outSb.Begin();
        }
        catch (Exception ex)
        {
            from.Visibility = Visibility.Collapsed;
            to.Visibility = Visibility.Visible;
            to.Opacity = 1;
            to.RenderTransform = null;
            to.IsHitTestVisible = true;
            Core.AppLog.Warn("oobe", "换页动画失败：" + ex.Message);
        }
    }

    private static Duration Ms(double ms) => new(TimeSpan.FromMilliseconds(ms));

    
    private static TimeSpan Time(double ms) => TimeSpan.FromMilliseconds(ms);
}
