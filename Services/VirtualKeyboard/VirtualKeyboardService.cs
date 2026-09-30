using System;
using System.Threading;
using ClassSoftwareHub.Desktop.Views;
using Microsoft.UI.Dispatching;

namespace ClassSoftwareHub.Desktop.Services.VirtualKeyboard;

/// <summary>
/// 虚拟键盘的接线：**什么时候该弹、什么时候该收**。
///
/// <b>单闸门</b>：<see cref="Start"/> 第一句就是 <c>if (!VirtualKeyboardEnabled) return;</c> ——
/// 功能关着的时候，触摸钩子、UIA 探测、注册表接管**一个都不会上电**。所以程序自启动也不会碰系统。
///
/// <b>触发方式：只有真触摸</b>（Nick 2026-09-30 定：鼠标不该弹）。链路是
/// 触摸监听 → 落点判定 → 能打字就弹 / 不能打字就收。
///
/// <b>⛔⛔ 两条防事故条款</b>（上一版就是在这栽的）：
///   ① <see cref="MinDwellMs"/> —— 任何一次"显示/收起"之后至少 <b>400ms</b> 内不许再翻转。
///      上一版"落点判定"和"被顶掉的系统键盘回调"两条来路互相触发，成了每 200ms 一轮的正反馈，
///      键盘疯狂显隐、整机卡死。
///   ② **不做任何"盯住系统进程"的事**。想让系统键盘别来抢，靠注册表那条自动弹开关，
///      绝不结束 <c>TabTip.exe</c>（它是 Win+H 的执行体，杀它 = Win+H 失效）。
///
/// <b>跑在哪</b>：判定循环在一条后台线程上（UIA 查询会阻塞几百毫秒，扔 UI 线程会卡界面）；
/// 真正"显示/收起"经 <see cref="_ui"/> 派回 UI 线程。
/// </summary>
public static class VirtualKeyboardService
{
    /// <summary>判定轮询间隔。</summary>
    private const int PollMs = 120;

    /// <summary>两次状态翻转之间至少隔这么久（去抖，见类注释）。</summary>
    private const long MinDwellMs = 400;

    private static DispatcherQueue? _ui;
    private static Thread? _worker;
    private static volatile bool _running;
    private static long _handledTouch;
    private static long _lastTransition;

    public static bool IsRunning => _running;

    // ── 生命周期 ────────────────────────────────────────────

    /// <summary>程序启动 / 设置里打开功能时调。**幂等**。</summary>
    public static void Start()
    {
        var settings = App.Settings.Current;
        if (!settings.VirtualKeyboardEnabled)
        {
            VkbdLog.Write("虚拟键盘：设置里没开，不启动");
            return;
        }

        if (_running) return;

        _ui = App.MainWindow?.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();

        TouchWatcher.Start();
        FocusProbe.Start();

        if (settings.VirtualKeyboardCaptureSystem) SystemKeyboardCapture.Apply();

        _running = true;
        _worker = new Thread(Loop)
        {
            IsBackground = true,
            Name = "CSH-VirtualKeyboard",
        };
        _worker.Start();

        VkbdLog.Write("虚拟键盘：服务已启动");
    }

    /// <summary>关掉功能。**必须调** —— 它负责摘钩子、停探测、还原注册表。</summary>
    public static void Stop()
    {
        if (_running)
        {
            _running = false;
            _worker?.Join(600);
            _worker = null;
            VkbdLog.Write("虚拟键盘：服务已停止");
        }

        TouchWatcher.Stop();
        FocusProbe.Stop();

        // ⛔ 关功能时必须把"系统键盘不自动弹"还回去，否则用户会以为"功能都关了系统键盘怎么还是不出来"。
        SystemKeyboardCapture.Restore();

        Dispatch(VirtualKeyboardWindow.HideKeyboard);
    }

    /// <summary>设置页改了任何一项之后调它，把状态推到最新。</summary>
    public static void Apply()
    {
        var settings = App.Settings.Current;

        if (!settings.VirtualKeyboardEnabled)
        {
            Stop();
        }
        else
        {
            Start();     // 幂等

            // 接管系统键盘是唯一会写注册表的开关 —— 默认**关**，用户显式打开才写。
            if (settings.VirtualKeyboardCaptureSystem) SystemKeyboardCapture.Apply();
            else SystemKeyboardCapture.Restore();
        }

        Dispatch(VirtualKeyboardWindow.ApplySettings);
    }

    // ── 判定循环（后台线程）────────────────────────────────

    private static void Loop()
    {
        while (_running)
        {
            try
            {
                if (TouchWatcher.TryTake(ref _handledTouch, out var x, out var y))
                    Evaluate(x, y);
            }
            catch (Exception ex)
            {
                VkbdLog.Write("⚠️ 键盘判定出错：" + ex.Message);
            }

            Thread.Sleep(PollMs);
        }
    }

    private static void Evaluate(int x, int y)
    {
        // ⛔ 落在键盘自己身上：不判定。否则"按键盘"这件事本身会被当成"点了不可输入的地方"，一按就自收。
        if (VirtualKeyboardWindow.IsPointInside(x, y)) return;

        var verdict = FocusProbe.ProbeAtPoint(x, y);
        if (verdict == Typability.Skip) return;      // 判不了就别动 —— 落在我们自己进程上也是这一种

        var now = Environment.TickCount64;
        var visible = VirtualKeyboardWindow.IsVisibleNow;

        if (verdict == Typability.Yes)
        {
            if (visible) return;
            if (now - _lastTransition < MinDwellMs) return;

            _lastTransition = now;
            Dispatch(() => VirtualKeyboardWindow.ShowKeyboard(userInitiated: false));
        }
        else
        {
            if (!visible) return;
            // 用户主动开的（点侧边栏「打开键盘」）不越权收 —— 只能由他自己收。
            if (VirtualKeyboardWindow.ShownByUser) return;
            if (now - _lastTransition < MinDwellMs) return;

            _lastTransition = now;
            Dispatch(VirtualKeyboardWindow.HideKeyboard);
        }
    }

    private static void Dispatch(Action action)
    {
        var queue = _ui;
        if (queue is null) return;

        queue.TryEnqueue(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                VkbdLog.Write("⚠️ UI 线程上执行键盘动作失败：" + ex.Message);
            }
        });
    }
}
