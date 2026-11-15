using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 浮窗「贴着边滑」的按帧动画（窗口级 Move，跟侧边栏滑入同一套手感）。
///
/// ⚠️ 驱动方式（2026-10-01 修"滑动卡顿掉帧"）：**渲染循环（CompositionTarget.Rendering）**
/// 而不是 16ms 的 DispatcherQueueTimer —— 那个计时器精度低、忙时会合并 tick，一合并就是 30fps，
/// 肉眼全是掉帧感；渲染回调每个 vsync 准时一帧。挪窗口走直接 SetWindowPos（NOACTIVATE）。
/// 窗口位置不归合成器管，想丝滑只能把每一帧的位置算准：缓动 + 整数像素 + vsync 节拍。
///
/// 每个浮窗持有一个自己的实例，互相不干扰；Stop 会让在跑的那一波立刻作废（连同它的回调）。
/// </summary>
public sealed class FlyoutSlider
{
    /// <summary>滑入用的时长（ms）：稍长一点、ease-out，看着像"甩进来停住"。</summary>
    public const double SlideInMs = 220;

    /// <summary>滑出用的时长（ms）：比滑入快，收起来要利索。</summary>
    public const double SlideOutMs = 150;

    private EventHandler<object>? _frame;
    private DispatcherQueueTimer? _watchdog;
    private int _epoch;

    /// <summary>正在滑（滑入或滑出）。调用方据此避免在动画中途改窗口位置。</summary>
    public bool IsRunning { get; private set; }

    public void Stop()
    {
        _epoch++;
        if (_frame is not null)
        {
            try { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= _frame; } catch { }
            _frame = null;
        }
        _watchdog?.Stop();
        _watchdog = null;
        IsRunning = false;
    }

    /// <param name="easeIn">true = 起步慢、越走越快（滑出用）；false = 起步快、末段收着（滑入用）。</param>
    public void Run(AppWindow aw, PointInt32 from, PointInt32 to, double ms, Action? done = null, bool easeIn = false)
    {
        Stop();
        IsRunning = true;
        var epoch = ++_epoch;
        var sw = Stopwatch.StartNew();
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(aw.Id);

        EventHandler<object> onFrame = (_, _) =>
        {
            if (epoch != _epoch) return;

            var t = Math.Clamp(sw.Elapsed.TotalMilliseconds / ms, 0, 1);
            var e = easeIn ? t * t * t : 1 - Math.Pow(1 - t, 3); // ease-in / ease-out cubic
            var x = (int)Math.Round(from.X + (to.X - from.X) * e);
            var y = (int)Math.Round(from.Y + (to.Y - from.Y) * e);
            _ = SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);

            if (t < 1) return;
            Stop();
            _ = SetWindowPos(hwnd, IntPtr.Zero, to.X, to.Y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
            done?.Invoke();
        };
        _frame = onFrame;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += onFrame;

        // 兜底：渲染回调万一停摆（窗口被藏），动画不能卡死不收尾
        var watchdog = DispatcherQueue.GetForCurrentThread().CreateTimer();
        watchdog.Interval = TimeSpan.FromMilliseconds(ms + 400);
        watchdog.IsRepeating = false;
        watchdog.Tick += (_, _) =>
        {
            if (epoch != _epoch) return;
            Stop();
            _ = SetWindowPos(hwnd, IntPtr.Zero, to.X, to.Y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
            done?.Invoke();
        };
        _watchdog = watchdog;
        watchdog.Start();
    }

    // ── Win32 ────────────────────────────────────────────────
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}
