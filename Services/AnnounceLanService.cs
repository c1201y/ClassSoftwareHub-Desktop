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


public sealed record LanPeer(string Name, string Label, string PairCode,
                             IReadOnlyList<string> Ips, int Port, DateTime LastSeen)
{
    
    public string Display => string.IsNullOrWhiteSpace(Label) ? Name : Label;

    
    public string Detail => string.IsNullOrWhiteSpace(Label)
        ? $"配对码 {PairCode}"
        : $"{Name} · 配对码 {PairCode}";

    
    
    
    
    
    public bool IsSelf => string.Equals(PairCode, AnnounceService.MyPairCode, StringComparison.Ordinal);

    
    
    
    
    
    
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


























public static class AnnounceLanService
{
    public const string AppTag = "ClassSoftwareHub.Announce";
    public const int DiscoveryPort = 47820;   
    public const int ReceivePort = 47821;     

    private const int BroadcastIntervalMs = 5000;
    private const int PeerTtlMs = 15000;
    private const int MaxBodyBytes = 8192;
    private const int RateLimitMs = 10_000;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, LanPeer> _peers = new();   
    private static readonly Dictionary<string, DateTime> _peerSeen = new();

    private static UdpClient? _udp;           
    private static UdpClient? _sender;        
    private static Socket? _socket;
    private static CancellationTokenSource? _cts;

    private static DateTime _lastAccepted = DateTime.MinValue;

    
    private static readonly Dictionary<string, DateTime> _replyAt = new();

    
    private const int ReplyIntervalSec = 30;

    
    private static Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;

    
    public static event Action? PeersChanged;

    
    public static event Action? BindersChanged;

    
    public static event Action? SavedPeersChanged;

    public static bool IsRunning { get; private set; }

    
    public static string? LastError { get; private set; }

    
    public static IReadOnlyList<LanPeer> Peers
    {
        get
        {
            lock (Gate) return _peers.Values.OrderBy(p => p.Display, StringComparer.CurrentCulture).ToList();
        }
    }

    

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

                
                
                
                
                
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                AppLog.Fail("announce-lan", ex, "局域网通道启动失败");
                Stop();   
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

    

    private static void StartDiscovery(CancellationToken token)
    {
        
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

        
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
        

        var peerName = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var label = root.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
        var port = root.TryGetProperty("port", out var p) && p.TryGetInt32(out var pt) ? pt : ReceivePort;
        if (port <= 0 || port > 65535) port = ReceivePort;

        
        RetryPendingUnbind(from.Address.ToString());

        lock (Gate)
        {
            _peers.TryGetValue(code, out var old);

            
            var ips = old is null ? new List<string>() : new List<string>(old.Ips);
            var ip = from.Address.ToString();
            var isNewIp = !ips.Contains(ip);
            if (isNewIp && ips.Count < 12) ips.Add(ip);

            
            
            
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

        
        
        ReplyOnce(from.Address);
    }

    
    
    
    
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

    
    
    
    
    
    
    
    
    private static byte[] BuildHeartbeat() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        app = AppTag,
        name = Environment.MachineName,
        label = App.Settings.Current.AnnounceLabel ?? "",
        code = AnnounceService.MyPairCode,
        port = ReceivePort,
        ips = LocalIPv4(),
    });

    
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
            
        }
    }

    
    
    
    
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

    

    private static void StartReceiver(CancellationToken token)
    {
        
        
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.Bind(new IPEndPoint(IPAddress.Any, ReceivePort));
        _socket.Listen(16);

        AppLog.Step("announce-lan", $"收件端口已监听 {_socket.LocalEndPoint}（handle={_socket.Handle}）");

        _ = Task.Run(() => AcceptLoopAsync(token));
        _ = Task.Run(() => SelfCheckAsync(token));   
    }

    
    
    
    
    
    
    
    
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
        catch (OperationCanceledException) {  }
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

    
    
    
    
    private static async Task HandleClientAsync(Socket client, CancellationToken token)
    {
        using (client)
        using (var stream = new NetworkStream(client, ownsSocket: false))
        {
            try
            {
                
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

                
                
                
                
                
                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                    && (path == "/" || path.Equals("/test", StringComparison.OrdinalIgnoreCase)))
                {
                    await WriteHtmlAsync(stream, TestPageHtml);
                    return;
                }

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

    
    
    
    
    
    
    
    
    private static async Task OnAnnounceAsync(string json, NetworkStream stream)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            await WriteResponseAsync(stream, 400, "{\"error\":\"bad_request\"}");
            return;
        }

        
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

                while (list.Count > 30) list.RemoveAt(0);   
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
            return;   
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

    
    
    
    
    
    
    
    
    
    
    
    
    

    

    
    private static async Task WriteHtmlAsync(NetworkStream stream, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        var head = "HTTP/1.1 200 OK\r\n"
                   + "Content-Type: text/html; charset=utf-8\r\n"
                   + $"Content-Length: {bytes.Length}\r\n"
                   + "Cache-Control: no-store\r\n"
                   + "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    
    
    
    
    
    
    
    
    
    
    
    private const string TestPageHtml = """
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<meta name="color-scheme" content="light dark">
<title>局域网喊话</title>
<style>
:root{--bg:#f6f7f9;--card:#fff;--text:#16181d;--sub:#6b7280;--line:#e3e6eb;--accent:#2b6df6;--ok:#137a4b;--err:#b91c1c}
@media (prefers-color-scheme:dark){:root{--bg:#14161a;--card:#1c1f25;--text:#f2f4f7;--sub:#9aa3af;--line:#2b3037;--accent:#2b6df6;--ok:#4ade80;--err:#f87171}}

body{margin:0;padding:20px 16px 40px;background:var(--bg);color:var(--text);
font:15px/1.6 -apple-system,"Segoe UI","Microsoft YaHei",system-ui,sans-serif}
h1{margin:4px 0 2px;font-size:20px;font-weight:650}
.sub{margin:0 0 18px;font-size:13px;color:var(--sub)}
.card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:16px;margin-bottom:14px}
label{display:block;font-size:13px;color:var(--sub);margin:0 0 6px}
input,textarea,select{width:100%;padding:11px 12px;font:inherit;color:var(--text);background:var(--bg);
border:1px solid var(--line);border-radius:10px;outline:none;-webkit-appearance:none}
input:focus,textarea:focus,select:focus{border-color:var(--accent)}
textarea{min-height:88px;resize:vertical}
.f+.f{margin-top:14px}
.row{display:flex;gap:10px}.row>*{flex:1;min-width:0}
button{width:100%;padding:15px;font:inherit;font-size:16px;font-weight:600;color:#fff;
background:var(--accent);border:0;border-radius:12px}
button:disabled{opacity:.55}
#s{margin-top:14px;padding:12px;border-radius:10px;font-size:14px;display:none}
#s.on{display:block}
#s.ok{background:rgba(19,122,75,.12);color:var(--ok)}
#s.err{background:rgba(185,28,28,.12);color:var(--err)}
</style>
</head>
<body>
<h1>局域网喊话</h1>
<p class="sub">同一局域网内直连本机，不经过服务器。</p>
<div class="card">
<div class="f"><label>配对码（6 位）</label>
<input id="code" inputmode="numeric" maxlength="6" placeholder="见接收端界面" autocomplete="off"></div>
<div class="f"><label>内容（不超过 100 字）</label>
<textarea id="text" maxlength="100">手机直连测试：这条消息从手机发到了这台电脑</textarea></div>
<div class="f row">
<div><label>署名</label><input id="from" maxlength="20" value="手机"></div>
<div><label>展示时长</label><select id="dur">
<option value="15">15 秒</option><option value="30" selected>30 秒</option><option value="60">60 秒</option>
</select></div>
</div>
</div>
<button id="b">发送喊话</button>
<div id="s"></div>
<script>
(function(){
var b=document.getElementById('b'),s=document.getElementById('s');
function show(k,t){s.className='on '+k;s.textContent=t;}
b.onclick=function(){
  var code=document.getElementById('code').value.trim();
  var text=document.getElementById('text').value.trim();
  var from=document.getElementById('from').value.trim();
  var dur=parseInt(document.getElementById('dur').value,10)||30;
  if(!/^\d{6}$/.test(code)){show('err','配对码必须是 6 位数字');return;}
  if(!text){show('err','内容不能为空');return;}
  b.disabled=true;show('ok','正在发送……');
  fetch('/announce',{method:'POST',headers:{'Content-Type':'application/json'},
    body:JSON.stringify({pairCode:code,text:text,from:from,duration:dur,
      id:'web-'+Math.random().toString(16).slice(2,14)})})
  .then(function(r){return r.text().then(function(t){return {c:r.status,b:t};});})
  .then(function(res){
    if(res.c===200){show('ok','已送达。电脑上应当已经弹出这条喊话。');}
    else if(res.c===403){show('err','配对码不正确。');}
    else if(res.c===429){show('err','发送过于频繁 —— 同一个配对码 10 秒内只接受一条。');}
    else{show('err','服务端返回 '+res.c+'：'+res.b);}
  })
  .catch(function(e){show('err','发送失败：'+e.message);})
  .then(function(){b.disabled=false;});
};
})();
</script>
</body>
</html>
""";
}
