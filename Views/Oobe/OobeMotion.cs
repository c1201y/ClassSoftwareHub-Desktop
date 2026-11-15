using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace ClassSoftwareHub.Desktop.Views.Oobe;

/// <summary>
/// 向导里的两段通用动效：单元素淡入、完成页卡片横滑。
/// 页面本体的进出场交给 Frame 的左右滑动转场，这里不再做整页上浮。
/// </summary>
public static class OobeMotion
{
    private static bool? _animationsEnabled;

    /// <summary>系统「显示动画」开关。关掉时一切直接落到终值，别硬播。</summary>
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

    /// <summary>只淡入、不位移。开场标题落位后，周围的文字用它亮出来。</summary>
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

    /// <summary>
    /// 两页/两张卡之间横滑换位（完成页轮播用）。前进从右侧来，后退从左侧来。
    /// </summary>
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

            // 出去那一页也必须先挂上 TranslateTransform：属性路径在 RenderTransform
            // 为 null 时解析不了，Storyboard.Begin() 会直接抛 0x800F1000（踩过）。
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

    /// <summary>BeginTime 在 WinUI 3 里是 <see cref="TimeSpan"/>?，不是 Duration。</summary>
    private static TimeSpan Time(double ms) => TimeSpan.FromMilliseconds(ms);
}
