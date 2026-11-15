using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using ClassSoftwareHub.Desktop.Services.Diagnostics;

namespace ClassSoftwareHub.Desktop.Services.HubAi;














public sealed class HubAiRuntime
{
    
    private static readonly Dictionary<string, HubAiRuntime> _map = new(StringComparer.Ordinal);

    private static readonly object _gate = new();

    
    public const string DraftKey = "\u0001draft";

    
    
    
    
    
    private const string FlyoutDraftPrefix = DraftKey + "#";

    private static int _flyoutSeq;

    
    private const int KeepMax = 12;

    
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

    
    
    
    
    
    private static void TrimLocked()
    {
        var idle = _map
            .Where(kv => !kv.Value.IsRunning && kv.Key != DraftKey)
            .Select(kv => kv.Key)
            .Take(_map.Count - KeepMax)
            .ToList();

        foreach (var key in idle) _map.Remove(key);
    }

    
    
    
    
    
    
    
    
    
    
    
    
    public static HubAiRuntime NewDraft()
    {
        lock (_gate)
        {
            
            foreach (var key in _map.Where(kv => kv.Key.StartsWith(FlyoutDraftPrefix, StringComparison.Ordinal)
                                                 && !kv.Value.IsRunning)
                                    .Select(kv => kv.Key)
                                    .ToList())
            {
                _map.Remove(key);
            }

            var runtime = new HubAiRuntime(FlyoutDraftPrefix + (++_flyoutSeq));
            _map[runtime.SessionId] = runtime;
            return runtime;
        }
    }

    
    public static void Drop(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        HubAiRuntime? dead = null;
        lock (_gate)
        {
            if (_map.Remove(sessionId, out var runtime)) dead = runtime;
        }

        
        dead?.Stop();
    }

    
    
    
    
    public static void AdoptDraft(string newSessionId) => Adopt(DraftKey, newSessionId);

    
    
    
    
    public static void Adopt(string runtimeKey, string newSessionId)
    {
        if (string.IsNullOrEmpty(runtimeKey) || string.IsNullOrEmpty(newSessionId)) return;
        if (string.Equals(runtimeKey, newSessionId, StringComparison.Ordinal)) return;

        lock (_gate)
        {
            if (_map.TryGetValue(runtimeKey, out var draft))
            {
                _map.Remove(runtimeKey);
                _map[newSessionId] = draft;
                draft.SessionId = newSessionId;
            }
        }
    }

    private HubAiRuntime(string sessionId) => SessionId = sessionId;

    public string SessionId { get; private set; }

    
    public AiChatService Chat { get; } = new();

    
    public bool IsRunning { get; private set; }

    
    public string LiveBody { get; private set; } = "";

    
    public string LiveReasoning { get; private set; } = "";

    
    public string? LiveStatus { get; private set; }

    
    public event Action? Changed;

    
    public event Action<string>? ToolDone;

    private CancellationTokenSource? _cts;
    private readonly StringBuilder _body = new();
    private readonly StringBuilder _reasoning = new();

    
    
    
    
    public void Start(
        AiConfig cfg,
        MachineSnapshot snapshot,
        IReadOnlyList<ToolDefinition> tools,
        string userText,
        Action<AiUsage>? onCompleted,
        Action<Exception>? onFailed,
        bool appendUserMessage = true,
        Func<string, string, Task<bool>>? approveTool = null,
        string? pageContext = null)
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
                        
                        _reasoning.Clear();
                        LiveReasoning = "";
                        ToolDone?.Invoke(note);
                        Changed?.Invoke();
                    },
                    appendUserMessage: appendUserMessage,
                    approveTool: approveTool,
                    pageContext: pageContext);
            }
            catch (OperationCanceledException) {  }
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

    
    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
    }

    
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
