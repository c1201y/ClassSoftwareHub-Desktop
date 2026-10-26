using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>同网段发现到的一台电脑。PairCode 是它自己广播出来的 6 位码，Ips 是它的候选收件地址。</summary>
public sealed record LanPeer(string Name, string Label, string PairCode,
                             IReadOnlyList<string> Ips, int Port, DateTime LastSeen)
{
    /// <summary>列表主行：有标识显示标识（高28班），没有就退回机器名。</summary>
    public string Display => string.IsNullOrWhiteSpace(Label) ? Name : Label;

    /// <summary>列表副行：机器名 + 配对码，用来区分同名班级的不同机器。</summary>
    public string Detail => string.IsNullOrWhiteSpace(Label)
        ? $"配对码 {PairCode}"
        : $"{Name} · 配对码 {PairCode}";

    /// <summary>
    /// 是不是本机自己。
    /// ⚠️ 故意**不**把自己从列表里滤掉（2026-10-25）：只有一台机器时也需要能自测「直连 → 全屏」，
    ///    否则功能在装第二台机之前永远验证不了。列表上标「（本机）」免得用户误以为是别人。
    /// </summary>
    public bool IsSelf => string.Equals(PairCode, AnnounceService.MyPairCode, StringComparison.Ordinal);

    /// <summary>
    /// 实际要连的地址序列。
    /// ⚠️ 一台机器会有多块网卡（Wi-Fi / 有线 / WSL / Hyper-V 虚拟网卡），广播在每块网卡上各发一份，
    ///    收到的源地址各不相同 —— 2026-10-25 实测就是存到了虚拟网卡地址，发送直接超时。
    ///    所以这里保留全部候选，逐个试；本机固定先走回环（回环不受防火墙入站规则影响）。
    /// </summary>
    public IReadOnlyList<string> Candidates
    {
        get
        {
            var list = new List<string>(Ips);
            if (IsSelf)
            {
                list.Remove("127.0.0.1");
                list.Insert(0, "127.0.0.1");
            }
            return list;
        }
    }
}

/// <summary>
/// 教室喊话的局域网通道（2026-10-25，Nick 提：多一条路走，别过度依赖服务器）。
///
/// <para>
/// 三件事：
/// <list type="number">
///   <item><b>发现</b>：开了「局域网接收」的机器每 5 秒广播一条心跳
///     （应用标识 / 机器名 / 本机标识 / 配对码 / 收件端口）；所有客户端监听同一 UDP 端口，
///     维护一张 15 秒过期的设备表。</item>
///   <item><b>收件</b>：本机 TCP 端口上跑一个极简 HTTP，只认 <c>POST /announce</c>，
///     校验配对码 + 每 10 秒一条限流，然后交给与服务器通道同一个展示入口。</item>
///   <item><b>发送</b>：选中的局域网设备直接 HTTP 投递；失败由调用方回落服务器。</item>
/// </list>
/// </para>
///
/// <para>
/// ⛔ 安全边界（写死，别扩）：只收固定格式的喊话，不传文件、不回传任何本机信息、不执行命令；
/// 配对码不符一律 403。监听端口只在「局域网接收」打开时存在。
/// </para>
///
/// <para>
/// ⚠️ 为何不用 mDNS：Windows 上要么引库要么自己写 responder，而这里只需要「同应用的机器 + 一个码」，
/// UDP 广播的报文完全可控，成本低一个数量级。
/// </para>
/// </summary>
public static class AnnounceLanService
{
    public const string AppTag = "ClassSoftwareHub.Announce";
    public const int DiscoveryPort = 47820;   // UDP 发现 / 心跳
    public const int ReceivePort = 47821;     // TCP 收件

    private const int BroadcastIntervalMs = 5000;
    private const int PeerTtlMs = 15000;
    private const int MaxBodyBytes = 8192;
    private const int RateLimitMs = 10_000;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, LanPeer> _peers = new();   // key = pairCode
    private static readonly Dictionary<string, DateTime> _peerSeen = new();

    private static UdpClient? _udp;           // 收心跳
    private static UdpClient? _sender;        // 发心跳（独立实例，避免与接收端互相影响）
    private static Socket? _socket;
    private static CancellationTokenSource? _cts;

    private static DateTime _lastAccepted = DateTime.MinValue;

    /// <summary>最近一次给对方回单播心跳的时刻（key = 对方 IP），用于节流，见 <see cref="ReplyOnce"/>。</summary>
    private static readonly Dictionary<string, DateTime> _replyAt = new();

    /// <summary>同一个 IP 最多这么长时间回一次单播心跳。</summary>
    private const int ReplyIntervalSec = 30;

    /// <summary>UI 调度器（Start 在 UI 线程上调用，抓一次）：登记/解除登记要改设置，得回 UI 线程。</summary>
    private static Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;

    /// <summary>设备表变化（发现/过期/信息更新）时触发；在后台线程上，订阅方要自己回 UI 线程。</summary>
    public static event Action? PeersChanged;

    /// <summary>「谁保存了本机配对码」的名单有变化（收到登记 / 被删除）。同样在后台线程上触发。</summary>
    public static event Action? BindersChanged;

    /// <summary>本机「已保存的电脑」被对方远程移除后触发（发送列表要跟着刷新）。</summary>
    public static event Action? SavedPeersChanged;

    public static bool IsRunning { get; private set; }

    /// <summary>最近一次启动失败的原因（端口被占 / 权限不足等），页面显示用。</summary>
    public static string? LastError { get; private set; }

    /// <summary>当前设备表快照（按机器名排序）。</summary>
    public static IReadOnlyList<LanPeer> Peers
    {
        get
        {
            lock (Gate) return _peers.Values.OrderBy(p => p.Display, StringComparer.CurrentCulture).ToList();
        }
    }

    // ════════════════════════ 启停 ════════════════════════

    public static void Start()
    {
        lock (Gate)
        {
            if (IsRunning) return;
            if (!App.Settings.Current.AnnounceLanEnabled)
            {
                AppLog.Info("announce-lan", "局域网通道未启用，不启动");
                return;
            }

            _cts = new CancellationTokenSource();
            LastError = null;
            _uiQueue ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            try
            {
                StartDiscovery(_cts.Token);
                StartReceiver(_cts.Token);
                IsRunning = true;
                AppLog.Begin("announce-lan", $"局域网通道已启动（发现 {DiscoveryPort} / 收件 {ReceivePort}，本机标识「{LabelOrDefault}」）");

                // 起来之后顺手看一眼防火墙（2026-10-27）。
                // 被拦的时候通道「启动成功、日志正常」，可外面一个包都进不来 —— 只有一条明确记录
                // 才能把这种「看着正常其实不通」的情况在现场区分开。⚠️ 只写日志，不弹任何窗口。
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var state = await FirewallGateway.CheckAsync();
                        if (state is FirewallState.PortRulesMissing or FirewallState.ProgramBlocked)
                            AppLog.Warn("announce-lan", "防火墙正在拦截局域网通道：" + FirewallGateway.Describe(state));
                        else if (state == FirewallState.Ok)
                            AppLog.Step("announce-lan", "防火墙检查通过（端口规则覆盖公用网络，无入站拦截）");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn("announce-lan", "防火墙检查失败：" + ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                AppLog.Fail("announce-lan", ex, "局域网通道启动失败");
                Stop();   // 半开状态比不开更糟
            }
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (!IsRunning && _cts is null) return;

            try { _cts?.Cancel(); } catch { }

            try { _udp?.Dispose(); } catch { }
            try { _sender?.Dispose(); } catch { }
            try { _socket?.Close(); } catch { }

            _udp = null;
            _sender = null;
            _socket = null;
            _cts = null;
            IsRunning = false;

            var hadPeers = _peers.Count > 0;
            _peers.Clear();
            _peerSeen.Clear();

            AppLog.Info("announce-lan", "局域网通道已停止");
            if (hadPeers) PeersChanged?.Invoke();
        }
    }

    private static string LabelOrDefault => string.IsNullOrWhiteSpace(App.Settings.Current.AnnounceLabel)
        ? Environment.MachineName
        : App.Settings.Current.AnnounceLabel.Trim();

    // ════════════════════════ 发现（UDP） ════════════════════════

    private static void StartDiscovery(CancellationToken token)
    {
        // 收心跳：ReuseAddress 让同机多进程/重启后立刻可绑
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

        // 发心跳：独立实例，只负责发
        _sender = new UdpClient { EnableBroadcast = true };
        _sender.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _sender.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        _ = Task.Run(() => ReceiveLoopAsync(token));
        _ = Task.Run(() => BroadcastLoopAsync(token));
        _ = Task.Run(() => PruneLoopAsync(token));
    }

    private static async Task ReceiveLoopAsync(CancellationToken token)
    {
        var client = _udp;
        if (client is null) return;

        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await client.ReceiveAsync();
            }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            catch (Exception ex)
            {
                AppLog.Fail("announce-lan", ex, "接收心跳失败");
                return;
            }

            try
            {
                OnHeartbeat(packet.Buffer, packet.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                // 单条脏包不该打断整个发现循环
                AppLog.Warn("announce-lan", "心跳解析失败：" + ex.Message);
            }
        }
    }

    private static void OnHeartbeat(byte[] buffer, IPEndPoint from)
    {
        using var doc = JsonDocument.Parse(buffer);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        if (!root.TryGetProperty("app", out var app) || app.GetString() != AppTag) return;

        var code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
        if (!AnnounceService.IsValidPairCode(code)) return;
        // ⚠️ 不跳过自己：单机也要能在列表里看到自己并自测（见 LanPeer.IsSelf 的注释）

        var peerName = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var label = root.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
        var port = root.TryGetProperty("port", out var p) && p.TryGetInt32(out var pt) ? pt : ReceivePort;
        if (port <= 0 || port > 65535) port = ReceivePort;

        // 对方出现了：把之前没发出去的「解除登记」补发一次
        RetryPendingUnbind(from.Address.ToString());

        lock (Gate)
        {
            _peers.TryGetValue(code, out var old);

            // 合并候选地址：同一台机每块网卡各广播一份，源地址都不一样（Wi-Fi / 有线 / 虚拟网卡）
            var ips = old is null ? new List<string>() : new List<string>(old.Ips);
            var ip = from.Address.ToString();
            var isNewIp = !ips.Contains(ip);
            if (isNewIp && ips.Count < 12) ips.Add(ip);

            // 2026-10-27：对方心跳里自带的本机地址列表也一并收下。
            // 为什么需要：只靠「包的源地址」推断对方可连地址，在交换机端口隔离 / 双网卡 / 中间设备
            // 转发的情况下会拿到一个连不通的地址 —— 现象就是「列表里看得到它，但发过去超时」。
            if (root.TryGetProperty("ips", out var ipsEl) && ipsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ipsEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var candidate = item.GetString();
                    if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 45) continue;
                    if (ips.Contains(candidate) || ips.Count >= 12) continue;
                    ips.Add(candidate);
                    isNewIp = true;
                }
            }

            var peer = new LanPeer(peerName, label, code, ips, port, DateTime.Now);
            var changed = old is null || old.Name != peer.Name || old.Label != peer.Label || isNewIp;

            _peers[code] = peer;
            _peerSeen[code] = peer.LastSeen;

            if (changed) PeersChanged?.Invoke();
        }

        // 收到谁的心跳就回谁一条（单播，30 秒一次）：
        // 有些交换机/AP 只允许一侧主动发包，单向广播通的时候，对方列表里就一直看不到本机。
        ReplyOnce(from.Address);
    }

    /// <summary>
    /// 给对方回一条单播心跳。节流到 <see cref="ReplyIntervalSec"/> 秒一次，避免多机互回形成风暴。
    /// 用单独的发包 socket（<see cref="_sender"/>），不进接收循环。
    /// </summary>
    private static void ReplyOnce(IPAddress address)
    {
        var key = address.ToString();

        lock (Gate)
        {
            if (_replyAt.TryGetValue(key, out var last) && (DateTime.Now - last).TotalSeconds < ReplyIntervalSec)
                return;

            _replyAt[key] = DateTime.Now;

            if (_replyAt.Count > 128)
            {
                var cutoff = DateTime.Now.AddMinutes(-10);
                foreach (var stale in _replyAt.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                    _replyAt.Remove(stale);
            }
        }

        SendRaw(BuildHeartbeat(), new IPEndPoint(address, DiscoveryPort));
    }

    private static async Task BroadcastLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var payload = BuildHeartbeat();
                foreach (var target in BroadcastTargets())
                    SendRaw(payload, new IPEndPoint(target, DiscoveryPort));
            }
            catch (Exception ex)
            {
                AppLog.Warn("announce-lan", "广播心跳失败：" + ex.Message);
            }

            try
            {
                await Task.Delay(BroadcastIntervalMs, token);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// 一条心跳的内容。
    /// <para>
    /// <c>ips</c> 是本机全部 IPv4（2026-10-27 补）：对方据此得到一组"能直接连"的地址候选，
    /// 不再只依赖广播包的源地址 —— 多网卡、交换机端口隔离、中间设备转发的情况下，
    /// 源地址经常不是可连地址，症状是「列表里看得到它，发过去却超时」。
    /// </para>
    /// </summary>
    private static byte[] BuildHeartbeat() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        app = AppTag,
        name = Environment.MachineName,
        label = App.Settings.Current.AnnounceLabel ?? "",
        code = AnnounceService.MyPairCode,
        port = ReceivePort,
        ips = LocalIPv4(),
    });

    /// <summary>本机全部 IPv4（跳过回环与未启用的网卡）。</summary>
    private static List<string> LocalIPv4()
    {
        var list = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;

                    var text = ua.Address.ToString();
                    if (!list.Contains(text) && list.Count < 12) list.Add(text);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("announce-lan", "枚举本机地址失败（心跳不带地址列表）：" + ex.Message);
        }

        return list;
    }

    /// <summary>
    /// 发一个 UDP 包（心跳用）。同步发送、失败一律吞掉 —— 某块网卡不通不能影响其它网卡。
    /// ⚠️ 加锁：广播循环和「收到心跳后的回包」会同时用同一个 socket。
    /// </summary>
    private static void SendRaw(byte[] payload, IPEndPoint to)
    {
        var sender = _sender;
        if (sender is null) return;

        try
        {
            lock (Gate) sender.Send(payload, payload.Length, to);
        }
        catch
        {
            // 单个目标不可达：跳过
        }
    }

    /// <summary>
    /// 广播目标：全局广播 + 每块网卡的定向广播地址。
    /// ⚠️ 只有全局广播时，部分路由/AP 会直接丢掉，定向广播能让局域网内的机器收到。
    /// </summary>
    private static IEnumerable<IPAddress> BroadcastTargets()
    {
        var set = new HashSet<string> { IPAddress.Broadcast.ToString() };
        var list = new List<IPAddress> { IPAddress.Broadcast };

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (ua.IPv4Mask is null) continue;

                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    var broadcast = new byte[4];
                    for (var i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);

                    var addr = new IPAddress(broadcast);
                    if (set.Add(addr.ToString())) list.Add(addr);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("announce-lan", "枚举网卡失败（只用全局广播）：" + ex.Message);
        }

        return list;
    }

    private static async Task PruneLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000, token);
            }
            catch (OperationCanceledException) { return; }

            var changed = false;
            lock (Gate)
            {
                var cutoff = DateTime.Now.AddMilliseconds(-PeerTtlMs);
                foreach (var code in _peerSeen.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                {
                    _peerSeen.Remove(code);
                    _peers.Remove(code);
                    changed = true;
                }
            }

            if (changed)
            {
                AppLog.Step("announce-lan", "有设备心跳超时，已从列表移除");
                PeersChanged?.Invoke();
            }
        }
    }

    // ════════════════════════ 收件（极简 HTTP） ════════════════════════

    private static void StartReceiver(CancellationToken token)
    {
        // 2026-10-25 改用裸 Socket：TcpListener 在这台机器上出现过「isBound=True 却连不上」的怪状，
        // 这里显式 bind/listen，状态一目了然，也少一层封装可怀疑。
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.Bind(new IPEndPoint(IPAddress.Any, ReceivePort));
        _socket.Listen(16);

        AppLog.Step("announce-lan", $"收件端口已监听 {_socket.LocalEndPoint}（handle={_socket.Handle}）");

        _ = Task.Run(() => AcceptLoopAsync(token));
        _ = Task.Run(() => SelfCheckAsync(token));   // 起来后自己打一条，确认收件链路真的通
    }

    /// <summary>
    /// 自检（2026-10-25）：从进程内部 POST 一条 health 请求到自己的收件端口。
    /// <para>
    /// 为什么需要它：外部探针会被沙箱网络隔离干扰，而用户的机器上又不能每次都靠人点一遍。
    /// 自检跑在同一进程、同一网络栈里，结果直接写日志 —— 收件链路坏没坏，一眼就能看出来。
    /// health 请求不会弹窗（处理器看到 health=true 直接回 200）。
    /// </para>
    /// </summary>
    private static async Task SelfCheckAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(1200, token);

            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var payload = "{\"health\":true}";
            using var content = new System.Net.Http.StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync($"http://127.0.0.1:{ReceivePort}/announce", content);

            if (resp.IsSuccessStatusCode)
                AppLog.Done("announce-lan", "收件自检通过（进程内部 POST 成功）");
            else
                AppLog.Warn("announce-lan", $"收件自检返回 {(int)resp.StatusCode}");
        }
        catch (OperationCanceledException) { /* 通道被关掉，正常 */ }
        catch (Exception ex)
        {
            AppLog.Fail("announce-lan", ex, "收件自检失败（本机自己的请求都进不去）");
        }
    }

    private static async Task AcceptLoopAsync(CancellationToken token)
    {
        AppLog.Step("announce-lan", $"收件循环启动（监听 {_socket?.LocalEndPoint}）");

        while (!token.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _socket!.AcceptAsync(token);
            }
            catch (OperationCanceledException)
            {
                AppLog.Step("announce-lan", "收件循环退出（取消）");
                return;
            }
            catch (ObjectDisposedException)
            {
                AppLog.Step("announce-lan", "收件循环退出（监听已关闭）");
                return;
            }
            catch (Exception ex)
            {
                // 2026-10-25：以前这里只 continue，循环一旦持续抛错就变成静默空转（连日志都没有）
                AppLog.Fail("announce-lan", ex, "接受连接失败");
                try { await Task.Delay(300, token); } catch { return; }
                continue;
            }

            AppLog.Step("announce-lan", $"收到连接 {SafeEndpoint(client)}");
            _ = Task.Run(() => HandleClientAsync(client, token));
        }
    }

    private static string SafeEndpoint(Socket client)
    {
        try { return client.RemoteEndPoint?.ToString() ?? "?"; }
        catch { return "?"; }
    }

    /// <summary>
    /// 手写最小 HTTP：只认 POST /announce。
    /// ⚠️ 故意不引 Kestrel / ASP.NET：这里只有一个路由，几百行架子换不来任何东西，还多一处攻击面。
    /// </summary>
    private static async Task HandleClientAsync(Socket client, CancellationToken token)
    {
        using (client)
        using (var stream = new NetworkStream(client, ownsSocket: false))
        {
            try
            {
                // 读请求头最多等 5 秒：不说话的连接不能把处理线程永久挂住（以前就是这么挂的）
                using var headCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                headCts.CancelAfter(TimeSpan.FromSeconds(5));

                var (headBytes, leftover) = await Core.HttpHeadReader.ReadAsync(stream, headCts.Token);
                if (headBytes is null)
                {
                    AppLog.Warn("announce-lan", "请求头没读到（连接空转或超时）");
                    return;
                }

                var lines = Encoding.ASCII.GetString(headBytes).Split("\r\n");
                var requestLine = lines.FirstOrDefault() ?? "";
                AppLog.Step("announce-lan", $"请求行：{requestLine}");
                var parts = requestLine.Split(' ');
                if (parts.Length < 2)
                {
                    await WriteResponseAsync(stream, 400, "{\"error\":\"bad_request\"}");
                    return;
                }

                var method = parts[0];
                var path = parts[1].Split('?')[0];

                var isBind = path.Equals("/bind", StringComparison.OrdinalIgnoreCase);
                var isUnbind = path.Equals("/unbind", StringComparison.OrdinalIgnoreCase);
                if (!isBind && !isUnbind && !path.Equals("/announce", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, 404, "{\"error\":\"not_found\"}");
                    return;
                }

                if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, 405, "{\"error\":\"method_not_allowed\"}");
                    return;
                }

                var length = 0;
                foreach (var line in lines.Skip(1))
                {
                    var idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    if (!line[..idx].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!int.TryParse(line[(idx + 1)..].Trim(), out length)) length = 0;
                }

                if (length <= 0 || length > MaxBodyBytes)
                {
                    await WriteResponseAsync(stream, 400, "{\"error\":\"bad_length\"}");
                    return;
                }

                var body = new byte[length];
                // ⚠️ 先用请求头那一包里已经带进来的字节，再继续读剩下的（见 Core/HttpHeadReader 的说明）
                var read = Math.Min(leftover.Length, length);
                if (read > 0) Array.Copy(leftover, body, read);
                while (read < length)
                {
                    var n = await stream.ReadAsync(body.AsMemory(read, length - read), token);
                    if (n <= 0) return;
                    read += n;
                }

                var json = Encoding.UTF8.GetString(body);
                if (isBind) await OnBindAsync(json, stream);
                else if (isUnbind) await OnUnbindAsync(json, stream);
                else await OnAnnounceAsync(json, stream);
            }
            catch (Exception ex)
            {
                AppLog.Warn("announce-lan", "收件处理失败：" + ex.Message);
            }
        }
    }

    /// <summary>
    /// 读 HTTP 请求头，并把它后面**已经读进来的字节**原样交回（body 前缀）。
    /// <para>
    /// ⛔ 2026-10-25 的坑：HttpClient 的请求头和小 body 往往在同一个 TCP 包里，一次 Read 就把 body 也读走了。
    /// 老实现把整段内容当请求头丢掉，之后按 Content-Length 再去等 body 就永远等不到 ——
    /// 现象是「连接成功、服务端永不应答」，从客户端看就是超时。局域网喊话卡了一下午就是它。
    /// </para>
    /// </summary>
    private static async Task OnAnnounceAsync(string json, NetworkStream stream)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            await WriteResponseAsync(stream, 400, "{\"error\":\"bad_request\"}");
            return;
        }

        // 自检请求（进程内部发的）：只要收件链路通就回 200，不弹窗、不登记
        if (root.TryGetProperty("health", out var health) && health.ValueKind == JsonValueKind.True)
        {
            await WriteResponseAsync(stream, 200, "{\"ok\":true}");
            return;
        }

        var code = root.TryGetProperty("pairCode", out var c) ? c.GetString() ?? "" : "";
        if (code != AnnounceService.MyPairCode)
        {
            AppLog.Warn("announce-lan", "局域网收件被拒：配对码不匹配");
            await WriteResponseAsync(stream, 403, "{\"error\":\"unknown_pair\"}");
            return;
        }

        var text = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(text) || text.Length > 100)
        {
            await WriteResponseAsync(stream, 400, "{\"error\":\"bad_text\"}");
            return;
        }

        // 限流：与服务器同一口径（1 条 / 10 秒），局域网也不给刷屏的口子
        if (DateTime.Now - _lastAccepted < TimeSpan.FromMilliseconds(RateLimitMs))
        {
            await WriteResponseAsync(stream, 429, "{\"error\":\"rate_limited\"}");
            return;
        }
        _lastAccepted = DateTime.Now;

        var msg = new AnnounceMessage
        {
            Id = root.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "",
            Text = text.Trim(),
            From = root.TryGetProperty("from", out var f) ? (f.GetString() ?? "") : "",
            Duration = root.TryGetProperty("duration", out var d) && d.TryGetInt32(out var dur) ? dur : 30,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        await WriteResponseAsync(stream, 200, "{\"ok\":true}");
        AnnounceService.DispatchMessage(msg, "局域网");
    }

    // ════════════════════════ 登记：谁保存了本机配对码 ════════════════════════

    /// <summary>
    /// 处理对方的登记请求（<c>POST /bind</c>）：对方在发送端保存了本机的配对码，把它记下来，
    /// 供本机在「谁保存了本机配对码」里查看与删除（2026-10-25 Nick：防滥用）。
    /// ⚠️ 仍然要求配对码正确 —— 不知道本机码的人连登记都做不了，名单不会被随便塞满。
    /// </summary>
    private static async Task OnBindAsync(string json, NetworkStream stream)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            await WriteResponseAsync(stream, 400, "{\"error\":\"bad_request\"}");
            return;
        }

        var code = root.TryGetProperty("pairCode", out var c) ? c.GetString() ?? "" : "";
        if (code != AnnounceService.MyPairCode)
        {
            await WriteResponseAsync(stream, 403, "{\"error\":\"unknown_pair\"}");
            return;
        }

        var name = root.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
        var label = root.TryGetProperty("label", out var l) ? (l.GetString() ?? "") : "";
        if (name.Length > 64) name = name[..64];
        if (label.Length > 24) label = label[..24];

        await WriteResponseAsync(stream, 200, "{\"ok\":true}");
        RecordBinder(name, label);
    }

    /// <summary>把登记写进设置（回 UI 线程改，避免后台线程与页面同时写 settings.json）。</summary>
    private static void RecordBinder(string name, string label)
    {
        var queue = _uiQueue;
        if (queue is null) return;

        queue.TryEnqueue(() =>
        {
            try
            {
                var list = App.Settings.Current.AnnounceBinders;
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                var existing = list.FirstOrDefault(b =>
                    string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    list.Add(new AnnounceBinder
                    {
                        Code = AnnounceService.MyPairCode,
                        Name = name,
                        Label = label,
                        SeenTicks = now,
                    });
                }
                else
                {
                    existing.Label = label;
                    existing.SeenTicks = now;
                }

                while (list.Count > 30) list.RemoveAt(0);   // 只留最近 30 条，名单别无限长
                App.Settings.Save();
                AppLog.Step("announce-lan", $"有新电脑保存了本机配对码：{(string.IsNullOrWhiteSpace(label) ? name : label)}");
                BindersChanged?.Invoke();
            }
            catch (Exception ex)
            {
                AppLog.Fail("announce-lan", ex, "记录登记失败");
            }
        });
    }

    /// <summary>
    /// 通知对方「我保存了你的配对码」（发送端保存时调用）。
    /// 只有对方此刻在同一局域网里才发得出去 —— 发不出去不算错，只是接收端的名单看不到这条。
    /// </summary>
    public static async Task TryBindAsync(LanPeer peer, string name, string label)
    {
        var payload = JsonSerializer.Serialize(new { pairCode = peer.PairCode, name, label });

        foreach (var ip in peer.Candidates.Take(2))
        {
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using var content = new System.Net.Http.StringContent(payload, Encoding.UTF8, "application/json");
                using var resp = await http.PostAsync($"http://{ip}:{peer.Port}/bind", content);
                if (resp.IsSuccessStatusCode)
                {
                    AppLog.Step("announce-lan", $"已通知 {peer.Display} 登记本机");
                    return;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("announce-lan", $"登记通知失败 @ {ip}:{peer.Port} — {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 处理对方的「解除登记」请求（<c>POST /unbind</c>）：对方（码的主人）把本机的登记删掉了，
    /// 本机也要把「已保存的电脑」里那条删掉 —— 单边删除会让两边对不上（2026-10-25 Nick 指出）。
    /// </summary>
    private static async Task OnUnbindAsync(string json, NetworkStream stream)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var code = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("pairCode", out var c)
            ? c.GetString() ?? ""
            : "";

        if (!AnnounceService.IsValidPairCode(code))
        {
            await WriteResponseAsync(stream, 400, "{\"error\":\"bad_request\"}");
            return;
        }

        await WriteResponseAsync(stream, 200, "{\"ok\":true}");
        RemoveSavedPeer(code);
    }

    /// <summary>把某个配对码从「已保存的电脑」移除（回 UI 线程改设置）。</summary>
    private static void RemoveSavedPeer(string code)
    {
        var queue = _uiQueue;
        if (queue is null) return;

        queue.TryEnqueue(() =>
        {
            try
            {
                var removed = App.Settings.Current.AnnounceSavedPeers.RemoveAll(p => p.Code == code);
                if (removed == 0) return;

                App.Settings.Save();
                AppLog.Step("announce-lan", $"对方删除了本机的登记，已把配对码 {code} 从「已保存的电脑」移除");
                SavedPeersChanged?.Invoke();
            }
            catch (Exception ex)
            {
                AppLog.Fail("announce-lan", ex, "移除已保存的电脑失败");
            }
        });
    }

    /// <summary>
    /// 通知某台电脑「把我的登记删掉」（接收端删除时调用）。返回是否通知成功。
    /// 对方不在线就返回 false，调用方把它记进「待通知」，等对方下次出现再补发。
    /// </summary>
    public static async Task<bool> TryUnbindAsync(string ip, string code)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;

        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var content = new System.Net.Http.StringContent(
                JsonSerializer.Serialize(new { pairCode = code }), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync($"http://{ip}:{ReceivePort}/unbind", content);

            if (resp.IsSuccessStatusCode)
            {
                AppLog.Done("announce-lan", $"已通知 {ip} 移除本机的登记");
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("announce-lan", $"解除登记通知失败 @ {ip} — {ex.Message}");
        }

        return false;
    }

    /// <summary>对方下次广播心跳时，把攒下的「待通知移除」补发一次。</summary>
    private static void RetryPendingUnbind(string ip)
    {
        List<string> codes;
        try
        {
            codes = App.Settings.Current.AnnouncePendingUnbinds
                .Where(p => p.Ip == ip)
                .Select(p => p.Code)
                .ToList();
        }
        catch
        {
            return;   // 设置正在被 UI 线程改写时读到半成品，下一拍再试
        }

        if (codes.Count == 0) return;

        _ = Task.Run(async () =>
        {
            foreach (var code in codes)
            {
                if (!await TryUnbindAsync(ip, code)) continue;

                _uiQueue?.TryEnqueue(() =>
                {
                    App.Settings.Current.AnnouncePendingUnbinds.RemoveAll(p => p.Ip == ip && p.Code == code);
                    App.Settings.Save();
                });
            }
        });
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string json)
    {
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            429 => "Too Many Requests",
            _ => "Error",
        };

        var bytes = Encoding.UTF8.GetBytes(json);
        var head = $"HTTP/1.1 {status} {reason}\r\n"
                   + "Content-Type: application/json; charset=utf-8\r\n"
                   + $"Content-Length: {bytes.Length}\r\n"
                   + "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    // ════════════════════════ 发送（直连） ════════════════════════

    /// <summary>
    /// 直接投递到某一台局域网设备。返回 true = 对方已收到（HTTP 200）；
    /// false = 连不上/被拒，调用方应回落服务器通道。
    /// <para>
    /// ⚠️ 逐个试对方的候选地址（多网卡机器会广播出多个源地址），单个地址超时 2 秒：
    ///    局域网内连不上就是连不上，不能让用户陪它等服务器那条路的 10 秒。
    ///    对方明确回 4xx（码不符 / 限流）时立刻放弃 —— 换个地址也是同样的结果。
    /// </para>
    /// </summary>
    public static async Task<bool> TrySendAsync(LanPeer peer, AnnounceMessage msg)
    {
        var payload = JsonSerializer.Serialize(new
        {
            pairCode = peer.PairCode,
            text = msg.Text,
            from = msg.From,
            duration = msg.Duration,
            id = msg.Id,
        });

        foreach (var ip in peer.Candidates.Take(3))
        {
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using var content = new System.Net.Http.StringContent(payload, Encoding.UTF8, "application/json");
                using var resp = await http.PostAsync($"http://{ip}:{peer.Port}/announce", content);

                if (resp.IsSuccessStatusCode)
                {
                    AppLog.Done("announce-lan", $"局域网直达 {peer.Display}（{ip}:{peer.Port}）");
                    return true;
                }

                AppLog.Warn("announce-lan", $"局域网直达被拒 {(int)resp.StatusCode}：{peer.Display} @ {ip}");
                return false;
            }
            catch (Exception ex)
            {
                AppLog.Warn("announce-lan", $"局域网直达失败 @ {ip}:{peer.Port} — {ex.Message}");
            }
        }

        return false;
    }

    // ════════════════════════ 防火墙 ════════════════════════
    //
    // 2026-10-27 整体搬到 Services/FirewallGateway.cs。旧实现（就在这个位置）用 netsh 的中文输出
    // 做关键字匹配，在 UTF-8 代码页下读到的是 GBK 乱码，判据恒为 false；而且只看「规则存在」，
    // 没看它覆不覆盖公用网络 —— 这台机器上留下的旧规则是「域,专用」，校园网下等于没加。
    // 两件事叠起来，用户看到的就是「局域网搜不到、每次都要再点一次放行、点了还弹 UAC」。
    //
    // 现在由 FirewallGateway 负责：查询走 PowerShell（-EncodedCommand，与代码页无关），
    // 判据是「存在 + 已启用 + Profile 含 Public」，另外单独查本程序有没有入站 Block 规则
    // （Block 永远优先于 Allow，这条不查出来，端口规则再怎么对也收不到包）。
}
