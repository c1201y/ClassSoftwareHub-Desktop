using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Views;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 教室喊话（2026-10-25）：后台轻轮询后端的 <c>/api/announce/poll</c>，收到就弹全屏喊话窗。
///
/// <para>
/// 语义对齐设计稿 <c>docs/教室喊话功能设计.md</c>（Win7 版同源实现，后端为同一个 csh-api）：
/// 手机/电脑浏览器输入本机 6 位配对码 → 服务端暂存一条消息 → 本机轮询取走 → 全屏大字展示。
/// 本机平时零存在：不弹窗、不占前台，只有收到喊话才亮。
/// </para>
///
/// <para>
/// 轮询纪律：
/// <list type="bullet">
///   <item>间隔取设置值（默认 30 秒），每轮 ±20% 抖动 —— 避免一批教室机同拍打服务器；</item>
///   <item>连续失败按 ×2 退避（15/30/60/120 秒封顶），拿到任何 HTTP 回应即复位；</item>
///   <item>喊话窗正开着（正在播报）时本轮跳过 —— 播报期间再拉一条只会互相覆盖；</item>
///   <item>多入口回退复用 <see cref="ShellConfig.OrderedSubmitEndpoints"/>（与投稿同一套域名清单与"记住上次成功"机制）。</item>
/// </list>
/// </para>
///
/// <para>
/// ⛔ 日志只记 id / 长度 / 有无署名，<b>不记正文</b> —— 教室机日志学生可能翻得到（设计稿 5.2 节的隐私口径）。
/// </para>
/// </summary>
public static class AnnounceService
{
    /// <summary>单个入口的超时（与投稿一致：超时就换下一个入口，别让用户干等）。</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// 轮询专用客户端，超时压到 6 秒（2026-10-27）。
    /// <para>
    /// 轮询是后台循环，不是用户点出来的操作 —— 一个入口卡 10 秒、三个入口就是 30 秒，
    /// 期间状态行一直显示「正在连接」，看起来就是「服务器连不上」。
    /// 6 秒还没回应基本可以判这一个入口不通，换下一个更划算。
    /// </para>
    /// </summary>
    private static readonly HttpClient PollHttp = new() { Timeout = TimeSpan.FromSeconds(6) };

    private static readonly object Gate = new();

    // ── 配对码 ────────────────────────────────────────────────────────────────

    private static string? _pairCode;

    /// <summary>
    /// 本机配对码：设备指纹确定性导出的 6 位数字（FNV-1a → 取模）。
    /// 重装 / 清空设置后不变，无需落盘、无需服务端分配（与 Win7 版同算法，换机器才换码）。
    /// ⚠️ 换算法只改这里，界面与服务端都不用动。
    /// </summary>
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

    /// <summary>配对码格式：恰好 6 位十进制数字。</summary>
    public static bool IsValidPairCode(string? code)
    {
        if (code is null || code.Length != 6) return false;
        foreach (var c in code)
        {
            if (c < '0' || c > '9') return false;
        }
        return true;
    }

    // ── 运行状态（喊话页显示用） ──────────────────────────────────────────────

    private static bool _running;
    private static int _failStreak;      // 连续「所有入口都不可达」次数（退避用）
    private static int _backoffSec;      // 当前退避秒数；0 = 正常节奏
    private static Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;

    /// <summary>轮询是否在跑（false = 开关没开 / 刚被关掉）。</summary>
    public static bool IsRunning { get; private set; }

    /// <summary>最近一次拿到服务器回应的时刻；null = 一次都没连通过。喊话页显示「上次检查」用。</summary>
    public static DateTime? LastPollAt { get; private set; }

    /// <summary>当前退避秒数（&gt;0 = 连续失败退避中）。</summary>
    public static int BackoffSec => _backoffSec;

    // ── 启动 / 停止 ──────────────────────────────────────────────────────────

    /// <summary>启动轮询（App 启动与喊话页打开开关时调）。幂等：已在跑就什么都不做。</summary>
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
            // 在 UI 线程上抓一次调度器：来电时刻要回 UI 线程才能建窗口
            _uiQueue ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        }

        AppLog.Begin("announce", $"启动喊话轮询（间隔 {App.Settings.Current.AnnounceIntervalSec}s，本机配对码 {MyPairCode}）");
        _ = Task.Run(LoopAsync);
    }

    /// <summary>停止轮询（关掉开关时调）。正在播报的窗口不关，让它自然播完。</summary>
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

    /// <summary>改了间隔档之后调一下：未运行则拉起，已运行则下一轮自然生效。</summary>
    public static void RefreshSettings() => Start();

    // ── 轮询主循环 ────────────────────────────────────────────────────────────

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

            // 正在播报就跳过这一轮：全屏窗开着，再拉一条只会把它顶掉
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

    /// <summary>下一轮等待秒数：正常节奏 = 设置档 ±20% 抖动；有退避则用退避值。</summary>
    private static int NextDelaySec()
    {
        if (_backoffSec > 0) return _backoffSec;

        var baseSec = Math.Clamp(App.Settings.Current.AnnounceIntervalSec, 10, 120);
        var jitter = baseSec * 0.2;
        return Math.Max(5, (int)Math.Round(baseSec + (Random.Shared.NextDouble() * 2 - 1) * jitter));
    }

    private static async Task PollOnceAsync()
    {
        var name = Uri.EscapeDataString((Environment.MachineName ?? "").Trim());
        var query = $"deviceId={DeviceFingerprint.Value}&pairCode={MyPairCode}&name={name}";
        var gotAnswer = false;   // 只要有一个入口回了 HTTP 状态就算"网络通"
        string? firstError = null;   // 第一次失败的原因（只用于日志：光说"不可达"排不出是断网还是被拦）
        var tried = 0;               // 这一轮实际试了几个入口（入口清单改了要能一眼看出来）

        foreach (var baseUrl in ShellConfig.OrderedSubmitEndpoints())
        {
            System.Net.Http.HttpResponseMessage resp;
            tried++;
            try
            {
                resp = await PollHttp.GetAsync(baseUrl.TrimEnd('/') + "/api/announce/poll?" + query);
            }
            catch (Exception ex)
            {
                firstError ??= ex.Message;
                continue;   // 这个入口不通，换下一个
            }

            gotAnswer = true;   // 拿到 HTTP 回应就算"网络通"，之后的判断只看状态码

            using (resp)
            {
                // 204 = 没有新喊话（最常见的一态）
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
                    // 固定码广播方案下 poll 不应出现 403。真见到就是服务端语义变了 —— Warn 加退避，
                    // 但别把轮询停死（停了之后没人会去重新拉起来）。
                    AppLog.Warn("announce", "poll 403：服务端拒绝本机（语义可能已变化）");
                    lock (Gate) { _backoffSec = 60; }
                    return;
                }

                if ((int)resp.StatusCode >= 500)
                {
                    continue;   // 服务端抖了，换下一个入口
                }

                // 其他 4xx：参数是我们自己拼的，重试也不会好，记一笔就回
                AppLog.Warn("announce", $"poll 返回 {(int)resp.StatusCode}，本轮放弃");
                return;
            }
        }

        if (!gotAnswer)
        {
            lock (Gate)
            {
                _failStreak++;
                _backoffSec = Math.Min(120, 15 * (1 << Math.Min(_failStreak, 3)));   // 15/30/60/120 封顶
                if (_failStreak == 1 || _failStreak % 5 == 0)
                    AppLog.Warn("announce",
                        $"所有入口均不可达（连续第 {_failStreak} 轮，试了 {tried}/{ShellConfig.SubmitEndpoints.Length} 个入口），"
                        + $"退避 {_backoffSec}s" + (string.IsNullOrEmpty(firstError) ? "" : $"：{firstError}"));
            }
        }
    }

    private static void MarkOk(string baseUrl)
    {
        lock (Gate)
        {
            _failStreak = 0;
            _backoffSec = 0;
            LastPollAt = DateTime.Now;
        }
        ShellConfig.RememberSubmitEndpoint(baseUrl);   // 与投稿共享入口健康度
    }

    private static readonly System.Collections.Generic.Queue<string> _recentIds = new();

    /// <summary>
    /// 统一的展示入口：服务器通道与局域网通道共用（2026-10-25）。
    /// 两条路可能把同一条消息各送一次（本机既开着接收又在轮询），按 id 去重。
    /// ⛔ 日志只记 id / 长度 / 有无署名，不记正文 —— 教室机日志学生可能翻得到。
    /// </summary>
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

    /// <summary>收到喊话（服务器通道）：转给统一展示入口。</summary>
    private static void OnMessage(AnnounceMessage msg) => DispatchMessage(msg, "服务器");

    // ── 已配对设备查询（发送区用） ────────────────────────────────────────────

    // ── 发送（桌面端发送区用；手机场景走网页端） ──────────────────────────────

    /// <summary>SendAsync 结果。Devices = 送达设备数；Failed = 失败的码数；Error = 首个失败码。</summary>
    public sealed record SendResult(bool Ok, string Error, int Devices, int Failed);

    /// <summary>
    /// 发一条喊话（与网页端同一组接口，多入口回退一致）。
    /// <paramref name="targetCodes"/> = 勾选设备的配对码名单，逐码顺序发送 —— 服务端限流按码计，不同码互不影响。
    /// ⛔ 日志只记长度与结果，不记正文。
    /// </summary>
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
            System.Net.Http.HttpResponseMessage resp;
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                resp = await Http.PostAsync(baseUrl.TrimEnd('/') + "/api/announce/send", content);
            }
            catch
            {
                continue;
            }

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
                    catch { /* 解不出就当 0，不影响成功判定 */ }
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

                // 限流 / 无设备这类确定性失败不用换入口重试，直接回
                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    return (false, string.IsNullOrEmpty(err) ? "rejected" : err, 0);

                if ((int)resp.StatusCode >= 500) continue;   // 服务端抖了，换下一个

                return (false, string.IsNullOrEmpty(err) ? "rejected" : err, 0);
            }
        }

        return (false, "unreachable", 0);
    }

    // ── 服务端登记：谁保存了本机配对码（2026-10-25） ──────────────────────────
    // 局域网内由 AnnounceLanService 直连对端解决（已经能用）；不在同一网段时走这里。
    // ⚠️ 服务端目前**还没实现**这三个端点，协议见 docs/教室喊话-服务端接口.md。
    //    404 / 网络不通一律按「服务端暂不支持」降级：不抛错、不打扰用户，
    //    功能退回「只有局域网能管理登记」，等服务端上线后自动生效。

    /// <summary>服务端返回的一条登记。</summary>
    public sealed record ServerBinder(string Id, string Name, string Label, long Ts);

    /// <summary>向服务端登记「本机保存了这台电脑」。false = 服务端未实现或不可达（属正常情况）。</summary>
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

    /// <summary>
    /// 拉取「谁保存了本机配对码」的服务端名单。
    /// 返回 null = 服务端不支持或不可达（调用方保持现有本地记录，不做任何清理）。
    /// </summary>
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
                // 换下一个入口
            }
        }

        return null;
    }

    /// <summary>
    /// 让服务端移除某台机器对本机码的登记（senderId 为 null = 全部清空）。
    /// 服务端据此把该发送端标记为 revoked，它下次发送会收到 403 revoked 并自行删除本地记录。
    /// </summary>
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
