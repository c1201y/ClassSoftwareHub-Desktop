using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using ClassSoftwareHub.Desktop.Services.Diagnostics;

namespace ClassSoftwareHub.Desktop.Services.HubAi;

/// <summary>
/// 一个会话的运行时：持有对话上下文与流式状态，**独立于页面存在**。
///
/// 2026-10-23 建（Nick 选方案 B：「生成中随便切，后台继续跑完，回来就有」）。
/// 在此之前 `AiChatService` 是页面字段，切会话会 `Restore()` 覆盖正在写入的上下文，
/// 切页/切会话时正在生成的回复就"腰斩"了。
///
/// 设计要点：
/// · **回调不碰 UI** —— 只把增量累积到字段，然后发 <see cref="Changed"/>；
///   谁订阅谁去渲染，页面切走就退订，运行不受影响。
/// · 运行时自己负责收尾存档，不依赖页面还活着。
/// · 同一会话只允许一条流在跑（<see cref="IsRunning"/> 挡住重复发起）。
/// </summary>
public sealed class HubAiRuntime
{
    /// <summary>按会话 id 缓存。⚠️ 新建对话（还没有 id）用一个临时键。</summary>
    private static readonly Dictionary<string, HubAiRuntime> _map = new(StringComparer.Ordinal);

    private static readonly object _gate = new();

    /// <summary>新对话（尚未存档、还没有 id）用的临时键。</summary>
    public const string DraftKey = "\u0001draft";

    /// <summary>同时留多少条会话的运行时。超了就淘汰"不在跑"的那些。</summary>
    private const int KeepMax = 12;

    /// <summary>取（或创建）某个会话的运行时。</summary>
    public static HubAiRuntime For(string? sessionId)
    {
        var key = string.IsNullOrEmpty(sessionId) ? DraftKey : sessionId;
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var runtime))
            {
                runtime = new HubAiRuntime(key);
                _map[key] = runtime;
                if (_map.Count > KeepMax) TrimLocked();
            }
            return runtime;
        }
    }

    /// <summary>
    /// 淘汰空闲的运行时（2026-10-23 补）。
    /// 淘汰是**安全**的：空闲会话的上下文每次切回去都会从存档 `Restore` 一遍，
    /// 内存里这份只是缓存。⚠️ 正在跑的、以及还没存档的 draft 一律不动。
    /// </summary>
    private static void TrimLocked()
    {
        var idle = _map
            .Where(kv => !kv.Value.IsRunning && kv.Key != DraftKey)
            .Select(kv => kv.Key)
            .Take(_map.Count - KeepMax)
            .ToList();

        foreach (var key in idle) _map.Remove(key);
    }

    /// <summary>会话被删除时清掉它的运行时（否则上下文会一直挂在内存里）。</summary>
    public static void Drop(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        HubAiRuntime? dead = null;
        lock (_gate)
        {
            if (_map.Remove(sessionId, out var runtime)) dead = runtime;
        }

        // 删的时候可能还在生成 —— 顺手停掉，别让它在后台白跑
        dead?.Stop();
    }

    /// <summary>
    /// 会话存档后 id 会变（新对话第一次保存才生成 id）—— 把 draft 运行时挂到真实 id 上，
    /// 免得刚聊完的那一段在切换时被当成另一条。
    /// </summary>
    public static void AdoptDraft(string newSessionId)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(DraftKey, out var draft))
            {
                _map.Remove(DraftKey);
                _map[newSessionId] = draft;
                draft.SessionId = newSessionId;
            }
        }
    }

    private HubAiRuntime(string sessionId) => SessionId = sessionId;

    public string SessionId { get; private set; }

    /// <summary>本会话的对话上下文。⚠️ 只有运行时该持有它，页面不要再自己 new。</summary>
    public AiChatService Chat { get; } = new();

    /// <summary>是否有流在跑。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>正在生成的正文（切回来时靠它把未完成的那段画出来）。</summary>
    public string LiveBody { get; private set; } = "";

    /// <summary>正在生成的思考过程。</summary>
    public string LiveReasoning { get; private set; } = "";

    /// <summary>当前状态行文字（"思考中" 这类）。</summary>
    public string? LiveStatus { get; private set; }

    /// <summary>内容或状态有任何变化时触发。回调在**发起线程**上，订阅方自己切 UI 线程。</summary>
    public event Action? Changed;

    /// <summary>某个工具调用结束（参数是给用户看的一句话，如"已扫描磁盘目录"）。</summary>
    public event Action<string>? ToolDone;

    private CancellationTokenSource? _cts;
    private readonly StringBuilder _body = new();
    private readonly StringBuilder _reasoning = new();

    /// <summary>
    /// 发起一轮问答。⚠️ 立刻返回，不等结果 —— 调用方（页面）不持有任务，
    /// 页面关掉/切走都不会影响它跑完。
    /// </summary>
    public void Start(
        AiConfig cfg,
        MachineSnapshot snapshot,
        IReadOnlyList<ToolDefinition> tools,
        string userText,
        Action<AiUsage>? onCompleted,
        Action<Exception>? onFailed,
        bool appendUserMessage = true,
        Func<string, string, Task<bool>>? approveTool = null)
    {
        if (IsRunning) return;

        IsRunning = true;
        _cts = new CancellationTokenSource();
        _body.Clear();
        _reasoning.Clear();
        LiveBody = "";
        LiveReasoning = "";
        LiveStatus = "思考中";
        Changed?.Invoke();

        var ct = _cts.Token;

        _ = Task.Run(async () =>
        {
            // ⛔ 必须是 `AiUsage.Empty`，不能写 `default` —— AiUsage 是 record（引用类型），
            //    `default` 是 null；用户在生成中途点了停止 / 新建对话，走的是
            //    OperationCanceledException 那条路，usage 仍然是 null，
            //    于是 finally 里 `onCompleted(null)` → 页面 EstimateCost(usage) 直接空引用崩。
            //    （2026-10-23 修，编译器 CS8600 / CS8604 指的就是这里。）
            var usage = AiUsage.Empty;
            Exception? failure = null;
            try
            {
                usage = await Chat.RunAsync(
                    cfg, snapshot, tools, userText,
                    onDelta: chunk =>
                    {
                        _body.Append(chunk);
                        LiveBody = _body.ToString();
                        Changed?.Invoke();
                    },
                    onStatus: s =>
                    {
                        LiveStatus = s;
                        Changed?.Invoke();
                    },
                    onNewBubble: () =>
                    {
                        // 一次工具调用之后另起一段 —— 正文不清空，只把思考段切开
                        _reasoning.Clear();
                        LiveReasoning = "";
                        LiveStatus = null;
                        Changed?.Invoke();
                    },
                    ct: ct,
                    onReasoning: chunk =>
                    {
                        _reasoning.Append(chunk);
                        LiveReasoning = _reasoning.ToString();
                        Changed?.Invoke();
                    },
                    onToolDone: note =>
                    {
                        // 工具一调完，当前思考段就结束了 —— 下一段思考另起一块
                        _reasoning.Clear();
                        LiveReasoning = "";
                        ToolDone?.Invoke(note);
                        Changed?.Invoke();
                    },
                    appendUserMessage: appendUserMessage,
                    approveTool: approveTool);
            }
            catch (OperationCanceledException) { /* 用户停止：保留已生成的部分 */ }
            catch (Exception ex) { failure = ex; }
            finally
            {
                LiveStatus = null;
                IsRunning = false;
                Changed?.Invoke();

                _cts?.Dispose();
                _cts = null;

                if (failure is not null) onFailed?.Invoke(failure);
                else onCompleted?.Invoke(usage);
            }
        });
    }

    /// <summary>停止生成（保留已写出来的部分，与成熟工具一致）。</summary>
    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
    }

    /// <summary>清空这个会话（新建对话用）。</summary>
    public void Reset()
    {
        Stop();
        Chat.Reset();
        _body.Clear();
        _reasoning.Clear();
        LiveBody = "";
        LiveReasoning = "";
        LiveStatus = null;
        Changed?.Invoke();
    }
}
