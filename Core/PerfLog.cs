using System;
using System.Diagnostics;
using System.IO;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Core;














public static class PerfLog
{
#if DEBUG
    
    public static bool Enabled { get; set; } = true;
#else
    
    public static bool Enabled { get; set; } =
        string.Equals(Environment.GetEnvironmentVariable("CSH_PERF"), "1", StringComparison.Ordinal);
#endif

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Gate = new();

    private static Stopwatch? _nav;
    private static string _navTag = "";

    
    public static void Mark(string message)
    {
        if (!Enabled) return;
        Write($"{Clock.ElapsedMilliseconds,7}ms  {message}");
    }

    
    public static void NavBegin(string tag)
    {
        if (!Enabled) return;
        _navTag = tag;
        _nav = Stopwatch.StartNew();
    }

    
    public static void NavEnd(object? page)
    {
        if (!Enabled || _nav is null) return;

        
        
        
        var sw = _nav;
        var tag = _navTag;
        var pageName = page?.GetType().Name ?? "?";
        var constructMs = sw.ElapsedMilliseconds;
        _nav = null;

        var done = false;                     
        EventHandler<object>? frameHandler = null;
        Microsoft.UI.Dispatching.DispatcherQueueTimer? guard = null;

        void Finish(string frameText)
        {
            if (done) return;
            done = true;
            if (frameHandler is not null)
            {
                try { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= frameHandler; } catch { }
            }
            if (guard is not null)
            {
                try { guard.Stop(); } catch { }
            }
            Write($"{Clock.ElapsedMilliseconds,7}ms  切页 {tag,-16} → {pageName,-20} 构造+解析 {constructMs,5}ms   到首帧 {frameText}");
        }

        frameHandler = (_, _) => Finish($"{sw.ElapsedMilliseconds,5}ms");

        try { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += frameHandler; }
        catch
        {
            
            Finish("    ?");
            return;
        }

        try
        {
            
            
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (queue is not null)
            {
                guard = queue.CreateTimer();
                guard.Interval = TimeSpan.FromMilliseconds(2000);
                guard.IsRepeating = false;
                guard.Tick += (_, _) => Finish("  超时");
                guard.Start();
            }
        }
        catch { }
    }

    
    public static IDisposable Scope(string name) => new TimingScope(name);

    private sealed class TimingScope : IDisposable
    {
        private readonly string _name;
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        public TimingScope(string name)
        {
            _name = name;
            if (Enabled) Mark($"… {_name} 开始");
        }

        public void Dispose() => Mark($"√ {_name} {_sw.ElapsedMilliseconds}ms");
    }

    private static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                AppLog.Info("perf", line);
            }
        }
        catch
        {
            
        }
    }
}
