using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services;

public sealed class QrFileShareServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly string? _filePath;
    private readonly string? _text;
    private readonly string? _link;
    private readonly string _token;
    private readonly string _fileName;

    private QrFileShareServer(TcpListener listener, string address, string? filePath, string? text, string? link,
        string token, string fileName, TimeSpan? lifetime)
    {
        _listener = listener;
        _filePath = filePath;
        _text = text;
        _link = link;
        _token = token;
        _fileName = fileName;

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Url = filePath is null
            ? $"http://{address}:{port}/{token}"
            : $"http://{address}:{port}/{token}/{Uri.EscapeDataString(fileName)}";
        ExpiresAt = lifetime is null ? null : DateTimeOffset.UtcNow.Add(lifetime.Value);
        _ = Task.Run(AcceptLoopAsync);
    }

    public string Url { get; }
    public DateTimeOffset? ExpiresAt { get; }
    public bool IsExpired => ExpiresAt is { } expires && DateTimeOffset.UtcNow >= expires;

    public static QrFileShareServer Start(string filePath, TimeSpan? lifetime)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("文件不存在", filePath);
        return Create(filePath, null, null, Path.GetFileName(filePath), lifetime);
    }

    public static QrFileShareServer StartText(string text, TimeSpan? lifetime)
        => Create(null, text, null, "content.txt", lifetime);

    public static QrFileShareServer StartLink(string link, TimeSpan? lifetime)
        => Create(null, null, link, "link", lifetime);

    private static QrFileShareServer Create(string? filePath, string? text, string? link,
        string fileName, TimeSpan? lifetime)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var address = FindLanAddress();
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        return new QrFileShareServer(listener, address, filePath, text, link, token, fileName, lifetime);
    }

    private async Task AcceptLoopAsync()
    {
        var token = _cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(token);
                _ = HandleClientAsync(client, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Core.AppLog.Warn("qrcode", "内容分享监听停止：" + ex.Message);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 2048, leaveOpen: true);

                var requestLine = await reader.ReadLineAsync(token);
                if (string.IsNullOrEmpty(requestLine)) return;

                var parts = requestLine.Split(' ');
                if (parts.Length < 2) return;

                var method = parts[0];
                var target = parts[1].Split('?')[0].TrimEnd('/');

                for (var i = 0; i < 40; i++)
                {
                    var line = await reader.ReadLineAsync(token);
                    if (string.IsNullOrEmpty(line)) break;
                }

                if (method is not ("GET" or "HEAD"))
                {
                    await WriteStatusAsync(stream, "405 Method Not Allowed", token);
                    return;
                }

                if (IsExpired)
                {
                    await WriteStatusAsync(stream, "410 Gone", token);
                    return;
                }

                if (!target.StartsWith("/" + _token, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteStatusAsync(stream, "404 Not Found", token);
                    return;
                }

                if (_link is not null)
                {
                    var header = "HTTP/1.1 302 Found\r\nLocation: " + _link
                        + "\r\nContent-Length: 0\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token);
                    return;
                }

                if (_text is not null)
                {
                    var bytes = Encoding.UTF8.GetBytes(_text);
                    var header = new StringBuilder()
                        .Append("HTTP/1.1 200 OK\r\n")
                        .Append("Content-Type: text/plain; charset=utf-8\r\n")
                        .Append("Content-Length: ").Append(bytes.Length).Append("\r\n")
                        .Append("Cache-Control: no-store\r\n")
                        .Append("Connection: close\r\n\r\n")
                        .ToString();
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token);
                    if (method != "HEAD") await stream.WriteAsync(bytes, token);
                    return;
                }

                if (_filePath is null)
                {
                    await WriteStatusAsync(stream, "404 Not Found", token);
                    return;
                }

                var info = new FileInfo(_filePath);
                if (!info.Exists)
                {
                    await WriteStatusAsync(stream, "404 Not Found", token);
                    return;
                }

                var fileHeader = new StringBuilder()
                    .Append("HTTP/1.1 200 OK\r\n")
                    .Append("Content-Type: ").Append(ContentType(_filePath)).Append("\r\n")
                    .Append("Content-Length: ").Append(info.Length).Append("\r\n")
                    .Append("Content-Disposition: attachment; filename=\"download")
                    .Append(Path.GetExtension(_fileName)).Append("\"; filename*=UTF-8''")
                    .Append(Uri.EscapeDataString(_fileName)).Append("\r\n")
                    .Append("Cache-Control: no-store\r\n")
                    .Append("Connection: close\r\n\r\n")
                    .ToString();

                await stream.WriteAsync(Encoding.ASCII.GetBytes(fileHeader), token);
                if (method == "HEAD") return;

                using var file = File.OpenRead(_filePath);
                await file.CopyToAsync(stream, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            Core.AppLog.Warn("qrcode", "内容分享响应失败：" + ex.Message);
        }
    }

    private static async Task WriteStatusAsync(Stream stream, string status, CancellationToken token)
    {
        var response = $"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), token);
    }

    private static string FindLanAddress()
    {
        var candidates = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                continue;

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (address.Address.Equals(IPAddress.Loopback)) continue;
                if (address.Address.ToString().StartsWith("169.254.")) continue;
                candidates.Add(address.Address);
            }
        }

        var preferred = candidates.FirstOrDefault(IsPrivateAddress) ?? candidates.FirstOrDefault();
        if (preferred is null)
            throw new InvalidOperationException("没有可用的局域网 IPv4 地址，手机无法访问本机内容");

        return preferred.ToString();
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        var text = address.ToString();
        return text.StartsWith("10.")
            || text.StartsWith("192.168.")
            || Is172Private(text);
    }

    private static bool Is172Private(string text)
    {
        if (!text.StartsWith("172.")) return false;
        var dot = text.IndexOf('.', 4);
        if (dot < 0) return false;
        return int.TryParse(text[4..dot], out var second) && second is >= 16 and <= 31;
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        ".txt" or ".md" => "text/plain; charset=utf-8",
        ".csv" => "text/csv; charset=utf-8",
        ".zip" => "application/zip",
        ".7z" => "application/x-7z-compressed",
        ".rar" => "application/vnd.rar",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".mp3" => "audio/mpeg",
        ".mp4" => "video/mp4",
        ".wav" => "audio/wav",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}
