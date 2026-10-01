using System;
using System.Diagnostics;
using System.IO;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 轻量性能探针：把「切页到底花多久」写成数字，落到
/// <c>%LOCALAPPDATA%\ClassSoftwareHub\perf.log</c>。
///
/// 为什么要它：2026-10-01 Nick 反馈"部分页面跳转有卡卡的感觉"。手感没法当验收依据 ——
/// 改之前先拿到每页的真实耗时，改完才有东西可比。探针本身不影响功能：
/// 只在 **Debug 编译** 或设了环境变量 <c>CSH_PERF=1</c> 时才写，正式版默认整个关掉。
///
/// 记三段：
///   ① <see cref="Mark"/>        任意里程碑（启动、数据加载完…）
///   ② <see cref="NavBegin"/>/<see cref="NavEnd"/>  切页：Navigating → Navigated（构造 + XAML 解析 + 首次布局）
///   ③ 首帧                                                 Navigated → 下一次 CompositionTarget.Rendering（真画出来了）
/// </summary>
public static class PerfLog
{
#if DEBUG
    /// <summary>Debug 编译默认开（本地排障用）。</summary>
    public static bool Enabled { get; set; } = true;
#else
    /// <summary>正式版默认关；要开就设环境变量 <c>CSH_PERF=1</c>。</summary>
    public static bool Enabled { get; set; } =
        string.Equals(Environment.GetEnvironmentVariable("CSH_PERF"), "1", StringComparison.Ordinal);
#endif

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Gate = new();

    private static Stopwatch? _nav;
    private static string _navTag = "";

    private static string LogPath => Path.Combine(SettingsStore.Dir, "perf.log");

    /// <summary>随便记一笔（带自启动以来的毫秒数，方便看整条时间线）。</summary>
    public static void Mark(string message)
    {
        if (!Enabled) return;
        Write($"{Clock.ElapsedMilliseconds,7}ms  {message}");
    }

    /// <summary>切页开始（Frame.Navigating 时调）。</summary>
    public static void NavBegin(string tag)
    {
        if (!Enabled) return;
        _navTag = tag;
        _nav = Stopwatch.StartNew();
    }

    /// <summary>切页落定（Frame.Navigated 时调）：先记构造耗时，再等这一页画出第一帧。</summary>
    public static void NavEnd(object? page)
    {
        if (!Enabled || _nav is null) return;

        // ⚠️ 本次导航的计时器**立刻抓成局部变量**，静态字段同时清空 ——
        //    首帧回调是挂在全局 Rendering 上的，连着切两次页时两个回调会撞进同一帧：
        //    若继续用静态字段，第一个回调把它清掉，第二个读到的就是 null（2026-10-01 实测崩过一次）。
        var sw = _nav;
        var tag = _navTag;
        var pageName = page?.GetType().Name ?? "?";
        var constructMs = sw.ElapsedMilliseconds;
        _nav = null;

        var done = false;                     // 首帧回调与超时兜底都可能到，只认第一次
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
            // 挂不上帧回调（极早期）就只记构造耗时，别把导航搞崩
            Finish("    ?");
            return;
        }

        try
        {
            // 窗口被收进托盘 / 最小化时 Rendering 根本不触发 —— 必须有个超时兜底，
            // 否则每切一次页就白白挂一个帧回调在那儿（探针自己变成泄漏源就太难看了）。
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

    /// <summary>可复用的计时块：<c>using (PerfLog.Scope("加载软件清单")) { ... }</c>。</summary>
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
                Directory.CreateDirectory(SettingsStore.Dir);
                File.AppendAllText(LogPath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {line}\r\n",
                    System.Text.Encoding.UTF8);
            }
        }
        catch
        {
            // 探针写不进去绝不影响功能
        }
    }
}
