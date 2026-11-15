using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services.License;
using ClassSoftwareHub.Desktop.Views;

namespace ClassSoftwareHub.Desktop.Services;
























public static class AnnounceService
{
    
    private static readonly HttpClient Http = new(new AuthHandler()) { Timeout = TimeSpan.FromSeconds(10) };

    
    
    
    
    
    
    
    
    private static readonly HttpClient PollHttp = new(new AuthHandler()) { Timeout = TimeSpan.FromSeconds(6) };

    private static readonly object Gate = new();

    

    private static string? _pairCode;

    
    
    
    
    
    public static string MyPairCode => _pairCode ??= DerivePairCode(DeviceFingerprint.Value);

    private static string DerivePairCode(string deviceId)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in deviceId)
            {
                h ^= c;
                h *= 16777619;
            }
            return ((int)(h % 1000000)).ToString("D6");
        }
    }

    
    public static bool IsValidPairCode(string? code)
    {
        if (code is null || code.Length != 6) return false;
        foreach (var c in code)
        {
            if (c < '0' || c > '9') return false;
        }
        return true;
    }

    

    private static bool _running;
    private static int _failStreak;      
    private static int _backoffSec;      
    private static Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;

    
    public static bool IsRunning { get; private set; }

    
    public static DateTime? LastPollAt { get; private set; }

    
    public static int BackoffSec => _backoffSec;

    

    
    public static void Start()
    {
        lock (Gate)
        {
            if (!App.Settings.Current.AnnounceEnabled)
            {
                AppLog.Info("announce", "喊话轮询未启用（设置开关关闭），不启动");
                return;
            }
            if (_running) return;

            _running = true;
            IsRunning = true;
            _failStreak = 0;
            _backoffSec = 0;
            
            _uiQueue ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        }

        AppLog.Begin("announce", $"启动喊话轮询（间隔 {App.Settings.Current.AnnounceIntervalSec}s，本机配对码 {MyPairCode}）");
        _ = Task.Run(LoopAsync);
    }

    
    public static void Stop()
    {
        lock (Gate)
        {
            if (!_running) return;
            _running = false;
            IsRunning = false;
        }
        AppLog.Info("announce", "喊话轮询已停止");
    }

    
    public static void RefreshSettings() => Start();

    

    private static async Task LoopAsync()
    {
        while (true)
        {
            lock (Gate)
            {
                if (!_running) { IsRunning = false; return; }
            }

            try
            {
                await Task.Delay(NextDelaySec() * 1000);
            }
            catch (OperationCanceledException) { return; }

            lock (Gate)
            {
                if (!_running) { IsRunning = false; return; }
            }

            
            if (AnnounceWindow.IsShowing) continue;

            try
            {
                await PollOnceAsync();
            }
            catch (Exception ex)
            {
                AppLog.Fail("announce", ex, "轮询循环意外异常");
                lock (Gate) { _failStreak++; _backoffSec = 60; }
            }
        }
    }

    
    private static int NextDelaySec()
    {
        if (_backoffSec > 0) return _backoffSec;

        var baseSec = Math.Clamp(App.Settings.Current.AnnounceIntervalSec, 10, 120);
        var jitter = baseSec * 0.2;
        return Math.Max(5, (int)Math.Round(baseSec + (Random.Shared.NextDouble() * 2 - 1) * jitter));
    }

    

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private static async Task<(HttpResponseMessage? Response, string? Error)> RequestWithRetryAsync(
        Func<Task<HttpResponseMessage>> send)
    {
        string? last = null;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                return (await send(), null);
            }
            catch (Exception ex)
            {
                last = ex.Message;
                if (attempt == 2 || IsTimeout(ex)) break;
                await Task.Delay(150);
            }
        }

        return (null, last);
    }

    
    private static bool IsTimeout(Exception ex) =>
        ex is TimeoutException
        || ex.InnerException is TimeoutException
        || ex is TaskCanceledException { InnerException: not null };

    private static async Task PollOnceAsync()
    {
        var name = Uri.EscapeDataString((Environment.MachineName ?? "").Trim());
        var query = $"deviceId={DeviceFingerprint.Value}&pairCode={MyPairCode}&name={name}";
        var gotAnswer = false;   
        var tried = 0;               
        var failures = new List<string>();   

        foreach (var baseUrl in ShellConfig.OrderedSubmitEndpoints())
        {
            tried++;

            var (resp, error) = await RequestWithRetryAsync(
                () => PollHttp.GetAsync(baseUrl.TrimEnd('/') + "/api/announce/poll?" + query));

            if (resp is null)
            {
                failures.Add($"{new Uri(baseUrl).Host} {Shorten(error)}");
                continue;   
            }

            gotAnswer = true;   

            using (resp)
            {
                
                if (resp.StatusCode == System.Net.HttpStatusCode.NoContent)
                {
                    MarkOk(baseUrl);
                    return;
                }

                if (resp.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    var msg = AnnounceMessage.Parse(body);
                    MarkOk(baseUrl);
                    if (msg is null)
                    {
                        AppLog.Warn("announce", $"poll 200 但内容解不出来（长度 {body.Length}）");
                        return;
                    }
                    OnMessage(msg);
                    return;
                }

                if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    
                    
                    AppLog.Warn("announce", "poll 403：服务端拒绝本机（语义可能已变化）");
                    lock (Gate) { _backoffSec = 60; }
                    return;
                }

                if ((int)resp.StatusCode >= 500)
                {
                    continue;   
                }

                
                AppLog.Warn("announce", $"poll 返回 {(int)resp.StatusCode}，本轮放弃");
                return;
            }
        }

        if (!gotAnswer)
        {
            lock (Gate)
            {
                _failStreak++;
                _backoffSec = Math.Min(120, 15 * (1 << Math.Min(_failStreak, 3)));   
                if (_failStreak == 1 || _failStreak % 5 == 0)
                    AppLog.Warn("announce",
                        $"所有入口均不可达（连续第 {_failStreak} 轮，试了 {tried}/{ShellConfig.SubmitEndpoints.Length} 个入口），"
                        + $"退避 {_backoffSec}s"
                        + (failures.Count > 0 ? "。逐条：" + string.Join("；", failures) : ""));
            }
        }
    }

    
    
    
    
    private static string Shorten(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "未知原因";

        var text = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 90 ? text : text[..90] + "…";
    }

    private static void MarkOk(string baseUrl)
    {
        lock (Gate)
        {
            _failStreak = 0;
            _backoffSec = 0;
            LastPollAt = DateTime.Now;
        }
        ShellConfig.RememberSubmitEndpoint(baseUrl);   
    }

    private static readonly System.Collections.Generic.Queue<string> _recentIds = new();

    
    
    
    
    
    public static void DispatchMessage(AnnounceMessage msg, string via)
    {
        lock (Gate)
        {
            if (!string.IsNullOrEmpty(msg.Id))
            {
                if (_recentIds.Contains(msg.Id))
                {
                    AppLog.Info("announce", $"重复消息已忽略（{via}）id={msg.Id}");
                    return;
                }
                _recentIds.Enqueue(msg.Id);
                while (_recentIds.Count > 20) _recentIds.Dequeue();
            }
        }

        AppLog.Begin("announce", $"收到喊话（{via}）id={msg.Id} 长度={msg.Text.Length} 时长={msg.Duration}s"
            + (string.IsNullOrEmpty(msg.From) ? "" : " 有署名"));

        var queue = _uiQueue;
        if (queue is null)
        {
            AppLog.Warn("announce", "UI 调度器没抓到，喊话无法展示");
            return;
        }

        queue.TryEnqueue(() =>
        {
            try
            {
                var s = App.Settings.Current;
                AnnounceWindow.Show(msg, quiet: s.AnnounceQuietMode, speak: s.AnnounceSpeakText);
            }
            catch (Exception ex)
            {
                AppLog.Fail("announce", ex, "弹出喊话窗失败");
            }
        });
    }

    
    private static void OnMessage(AnnounceMessage msg) => DispatchMessage(msg, "服务器");

    

    

    
    public sealed record SendResult(bool Ok, string Error, int Devices, int Failed);

    
    
    
    
    
    public static async Task<SendResult> SendAsync(string text, string from, int duration, IReadOnlyList<string>? targetCodes = null)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0 || text.Length > 100) return new SendResult(false, "bad_request", 0, 0);

        var codes = targetCodes?.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList() ?? new List<string>();
        if (codes.Count == 0) return new SendResult(false, "no_target", 0, 0);

        AppLog.Begin("announce", $"发送喊话 长度={text.Length} 时长={duration}s 目标 {codes.Count} 台"
            + (string.IsNullOrWhiteSpace(from) ? "" : " 有署名"));

        var devices = 0;
        var failed = new List<string>();
        foreach (var code in codes)
        {
            var (ok, err, dev) = await SendToOneAsync(code, text, from, duration);
            if (ok) devices += dev;
            else failed.Add(code + (string.IsNullOrEmpty(err) ? "" : $"({err})"));
        }

        if (failed.Count == 0)
        {
            AppLog.Done("announce", $"发送成功，将送达 {devices} 台设备");
            return new SendResult(true, "", devices, 0);
        }

        AppLog.Warn("announce", $"发送完成：成功 {codes.Count - failed.Count} 台，失败 {failed.Count} 台（{string.Join(", ", failed)}）");
        return new SendResult(failed.Count < codes.Count, failed[0], devices, failed.Count);
    }

    private static async Task<(bool Ok, string Error, int Devices)> SendToOneAsync(string code, string text, string from, int duration)
    {
        var payload = JsonSerializer.Serialize(new
        {
            pairCode = code,
            text,
            from = (from ?? "").Trim(),
            duration,
        });

        foreach (var baseUrl in ShellConfig.OrderedSubmitEndpoints())
        {
            var (resp, _) = await RequestWithRetryAsync(async () =>
            {
                
                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                try
                {
                    return await Http.PostAsync(baseUrl.TrimEnd('/') + "/api/announce/send", content);
                }
                finally
                {
                    content.Dispose();
                }
            });

            if (resp is null) continue;   

            using (resp)
            {
                var body = await resp.Content.ReadAsStringAsync();
                if ((int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300)
                {
                    var devices = 0;
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("devices", out var d) && d.TryGetInt32(out var n)) devices = n;
                    }
                    catch {  }
                    ShellConfig.RememberSubmitEndpoint(baseUrl);
                    return (true, "", devices);
                }

                var err = "";
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? "";
                }
                catch { }

                
                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    return (false, string.IsNullOrEmpty(err) ? "rejected" : err, 0);

                if ((int)resp.StatusCode >= 500) continue;   

                return (false, string.IsNullOrEmpty(err) ? "rejected" : err, 0);
            }
        }

        return (false, "unreachable", 0);
    }

    
    
    
    
    

    
    public sealed record ServerBinder(string Id, string Name, string Label, long Ts);

    
    public static async Task<bool> BindAsync(string pairCode, string name, string label)
    {
        var payload = JsonSerializer.Serialize(new
        {
            pairCode,
            senderId = DeviceFingerprint.Value,
            name = (name ?? "").Trim(),
            label = (label ?? "").Trim(),
        });

        foreach (var baseUrl in ShellConfig.OrderedSubmitEndpoints())
        {
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var resp = await Http.PostAsync(baseUrl.TrimEnd('/') + "/api/announce/bind", content);

                if ((int)resp.StatusCode is >= 200 and < 300)
                {
                    ShellConfig.RememberSubmitEndpoint(baseUrl);
                    AppLog.Step("announce", "已在服务端登记本机的保存关系");
                    return true;
                }

                AppLog.Warn("announce", $"服务端登记返回 {(int)resp.StatusCode}（未实现时属正常）");
                return false;
            }
            catch (Exception ex)
            {
                AppLog.Warn("announce", "服务端登记失败：" + ex.Message);
            }
        }

        return false;
    }

    
    
    
    
    public static async Task<IReadOnlyList<ServerBinder>?> ListBindersAsync()
    {
        var query = $"pairCode={MyPairCode}&deviceId={DeviceFingerprint.Value}";

        foreach (var baseUrl in ShellConfig.OrderedSubmitEndpoints())
        {
            try
            {
                using var resp = await Http.GetAsync(baseUrl.TrimEnd('/') + "/api/announce/binders?" + query);
                if (!resp.IsSuccessStatusCode) return null;

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (!doc.RootElement.TryGetProperty("binders", out var arr)
                    || arr.ValueKind != JsonValueKind.Array)
                    return null;

                var list = new List<ServerBinder>();
                foreach (var e in arr.EnumerateArray())
                {
                    var id = e.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                    if (id.Length == 0) continue;

                    list.Add(new ServerBinder(
                        id,
                        e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        e.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
                        e.TryGetProperty("ts", out var t) && t.TryGetInt64(out var ts) ? ts : 0));
                }

                ShellConfig.RememberSubmitEndpoint(baseUrl);
                return list;
            }
            catch
            {
                
            }
        }

        return null;
    }

    
    
    
    
    public static async Task<bool> UnbindAsync(string? senderId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            pairCode = MyPairCode,
            deviceId = DeviceFingerprint.Value,
            senderId,
        });

        foreach (var baseUrl in ShellConfig.OrderedSubmitEndpoints())
        {
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var resp = await Http.PostAsync(baseUrl.TrimEnd('/') + "/api/announce/unbind", content);

                if ((int)resp.StatusCode is >= 200 and < 300)
                {
                    ShellConfig.RememberSubmitEndpoint(baseUrl);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                AppLog.Warn("announce", "服务端解除登记失败：" + ex.Message);
            }
        }

        return false;
    }
}
