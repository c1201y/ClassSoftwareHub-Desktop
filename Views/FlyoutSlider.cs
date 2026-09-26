using System;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 浮窗「贴着边滑」的按帧动画（窗口级 Move，跟侧边栏滑入同一套手感）。
///
/// 位置动画只能这样一帧一帧地 Move（16ms 一帧 ≈ 60fps）——窗口位置不归合成器管，
/// 想让它"动得丝滑"就只有把每一帧的位置算准：缓动 + 整数像素，别抖。
///
/// 每个浮窗持有一个自己的实例，互相不干扰；Stop 会让在跑的那一波立刻作废（连同它的回调）。
/// </summary>
public sealed class FlyoutSlider
{
    /// <summary>滑入用的时长（ms）：稍长一点、ease-out，看着像"甩进来停住"。</summary>
    public const double SlideInMs = 220;

    /// <summary>滑出用的时长（ms）：比滑入快，收起来要利索。</summary>
    public const double SlideOutMs = 150;

    private DispatcherQueueTimer? _timer;
    private int _epoch;

    /// <summary>正在滑（滑入或滑出）。调用方据此避免在动画中途改窗口位置。</summary>
    public bool IsRunning { get; private set; }

    public void Stop()
    {
        _epoch++;
        _timer?.Stop();
        _timer = null;
        IsRunning = false;
    }

    /// <param name="easeIn">true = 起步慢、越走越快（滑出用）；false = 起步快、末段收着（滑入用）。</param>
    public void Run(AppWindow aw, PointInt32 from, PointInt32 to, double ms, Action? done = null, bool easeIn = false)
    {
        Stop();
        IsRunning = true;
        var epoch = ++_epoch;
        var sw = Stopwatch.StartNew();

        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            if (epoch != _epoch) { timer.Stop(); return; }

            var t = Math.Clamp(sw.Elapsed.TotalMilliseconds / ms, 0, 1);
            var e = easeIn ? t * t * t : 1 - Math.Pow(1 - t, 3); // ease-in / ease-out cubic
            aw.Move(new PointInt32(
                (int)Math.Round(from.X + (to.X - from.X) * e),
                (int)Math.Round(from.Y + (to.Y - from.Y) * e)));

            if (t < 1) return;
            timer.Stop();
            _timer = null;
            IsRunning = false;
            aw.Move(to);
            done?.Invoke();
        };

        _timer = timer;
        timer.Start();
    }
}
