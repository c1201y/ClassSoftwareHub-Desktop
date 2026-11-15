using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace ClassSoftwareHub.Desktop.Views;











public sealed class FlyoutSlider
{
    
    public const double SlideInMs = 220;

    
    public const double SlideOutMs = 150;

    private EventHandler<object>? _frame;
    private DispatcherQueueTimer? _watchdog;
    private int _epoch;

    
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
            var e = easeIn ? t * t * t : 1 - Math.Pow(1 - t, 3); 
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

    
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}
