using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services;





























public static class MirrorSign
{
    
    private const string LogTag = "download-route";

    
    public const string NodeBaseUrl = "https://download.classsoftwarehub.cn";

    
    
    
    
    public const string Referer = "https://classsoftwarehub.us.ci/";

    
    public const string KeyFileName = "gh-node-key.txt";

    
    public const string KeyEnvVar = "CSH_GH_NODE_KEY";

    
    private const int RequestTimeoutMs = 8000;

    
    
    
    
    private const int SignEndpointTimeoutMs = 5000;

    
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(5);

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,     
    };

    private static readonly object Gate = new();

    
    
    
    
    
    
    private static readonly Dictionary<string, (string Signed, DateTime Expiry)> Cache = new(StringComparer.Ordinal);
    private static DateTime _retryAfter = DateTime.MinValue;

    private static readonly object KeyGate = new();
    private static bool _keyProbed;
    private static string? _apiKey;
    private static string? _keySource;

    
    
    
    
    public static DownloadCandidate Candidate(string signedUrl) =>
        new(signedUrl, Referer, ResolveApiKey());

    
    
    
    
    
    
    public static async Task<string?> GetSignedUrlAsync(string githubUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(githubUrl)) return null;

        
        lock (Gate)
        {
            if (Cache.TryGetValue(githubUrl, out var hit) && hit.Expiry > DateTime.UtcNow.AddMinutes(1))
            {
                Core.AppLog.Info(LogTag, "自建加速：复用缓存签名（未过期）");
                return hit.Signed;
            }
        }

        
        
        
        
        
        
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var viaServer = await TryServerSignAsync(githubUrl, ct).ConfigureAwait(false);
            if (viaServer is not null)
            {
                RememberSigned(githubUrl, viaServer);
                return viaServer;
            }

            if (attempt < 2)
            {
                try { await Task.Delay(300, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
            }
        }

        
        var apiKey = ResolveApiKey();
        if (apiKey is null) return null;

        lock (Gate)
        {
            if (DateTime.UtcNow < _retryAfter) return null;    
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(RequestTimeoutMs);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                NodeBaseUrl + "/sign?url=" + Uri.EscapeDataString(githubUrl));
            request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);

            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Cool();
                Core.AppLog.Info(LogTag,
                    $"自建加速：/sign → HTTP {(int)response.StatusCode}{StatusHint((int)response.StatusCode)}，改用公益镜像");
                return null;
            }

            var signed = Parse(await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false));
            if (signed is null)
            {
                Cool();
                Core.AppLog.Info(LogTag, "自建加速：/sign 没返回可用的 url，改用公益镜像");
                return null;
            }

            Core.AppLog.Info(LogTag, $"自建加速：已取得限时签名链接（{NodeBaseUrl}）");
            return signed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;                                              
        }
        catch (Exception ex)
        {
            Cool();
            Core.AppLog.Info(LogTag,
                $"自建加速：未取得签名（{ex.Message}），{FailureCooldown.TotalMinutes:0} 分钟内不再重试，改用公益镜像");
            return null;
        }
    }

    
    
    
    
    public static void LogKeyStatus()
    {
        if (ResolveApiKey() is not null)
        {
            Core.AppLog.Info(LogTag, $"自建加速：节点密钥已就位（来源：{_keySource}）");
            return;
        }

        Core.AppLog.Info(LogTag,
            $"自建加速：未配置节点密钥（找过环境变量 {KeyEnvVar}、exe 同目录与本机数据目录的 {KeyFileName}）" +
            "；自建加速不可用，GitHub 下载一律改用公益镜像");
    }

    
    
    
    
    private static string? ResolveApiKey()
    {
        lock (KeyGate)
        {
            if (_keyProbed) return _apiKey;
            _keyProbed = true;

            var sources = new (string Name, string? Raw)[]
            {
                ($"环境变量 {KeyEnvVar}", Environment.GetEnvironmentVariable(KeyEnvVar)),
                ("exe 同目录", ReadIfExists(Path.Combine(AppContext.BaseDirectory, KeyFileName))),
                ("本机数据目录", ReadIfExists(Path.Combine(Core.AppPaths.DataDir, KeyFileName))),
            };

            foreach (var (name, raw) in sources)
            {
                var key = raw?.Trim();
                if (!string.IsNullOrEmpty(key))
                {
                    _apiKey = key;
                    _keySource = name;
                    break;
                }
            }

            return _apiKey;
        }
    }

    private static string? ReadIfExists(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;                                        
        }
    }

    private static void Cool()
    {
        lock (Gate) _retryAfter = DateTime.UtcNow + FailureCooldown;
    }

    
    private static string StatusHint(int code) => code switch
    {
        400 => "（目标链接不在可加速范围）",
        401 or 403 => "（密钥无效或已轮换，检查 gh-node-key.txt）",
        410 => "（签名已过期）",
        429 => "（节点限流）",
        503 => "（节点未配置 SIGN_API_KEY）",
        _ => "",
    };

    
    
    
    
    
    
    
    
    private static async Task<string?> TryServerSignAsync(string githubUrl, CancellationToken ct)
    {
        var query = "?url=" + Uri.EscapeDataString(githubUrl);

        var endpoints = new[]
        {
            NodeBaseUrl + "/api/mirror-sign",

            "https://sign.classsoftwarehub.us.ci/api/mirror-sign",

            "https://cshapi.132614.xyz/api/mirror-sign",
            "https://submit.132614.xyz/api/mirror-sign",
        };

        foreach (var endpoint in endpoints)
        {
            
            
            using var perEndpoint = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perEndpoint.CancelAfter(SignEndpointTimeoutMs);
            var token = perEndpoint.Token;

            try
            {
                using var response = await Http.GetAsync(endpoint + query, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Core.AppLog.Info(LogTag, $"代签 {endpoint} → HTTP {(int)response.StatusCode}，试下一条");
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("success", out var ok) && ok.ValueKind == System.Text.Json.JsonValueKind.True
                    && root.TryGetProperty("url", out var u) && u.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var signed = u.GetString();
                    if (!string.IsNullOrWhiteSpace(signed))
                    {
                        Core.AppLog.Info(LogTag, $"自建加速：已通过服务端代签取得链接（{endpoint}）");
                        return signed;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;                                  
            }
            catch (OperationCanceledException)
            {
                Core.AppLog.Info(LogTag,
                    $"代签 {endpoint} 超过 {SignEndpointTimeoutMs / 1000} 秒未响应，试下一条");
            }
            catch (Exception ex)
            {
                
                
                
                
                var chain = new System.Text.StringBuilder(ex.GetType().Name + " - " + ex.Message);
                for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                    chain.Append(" | ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);

                Core.AppLog.Warn(LogTag, $"代签 {endpoint} 失败：{chain}");
            }
        }

        return null;
    }

    
    private static void RememberSigned(string githubUrl, string signedUrl)
    {
        var expiry = DateTime.UtcNow.AddMinutes(12);

        try
        {
            var query = new Uri(signedUrl).Query.TrimStart('?');
            foreach (var pair in query.Split('&'))
            {
                if (!pair.StartsWith("e=", StringComparison.Ordinal)) continue;

                if (long.TryParse(pair[2..], out var seconds))
                    expiry = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

                break;
            }
        }
        catch {  }

        lock (Gate)
        {
            if (Cache.Count > 40) Cache.Clear();   
            Cache[githubUrl] = (signedUrl, expiry);
        }
    }

    private static string? Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            
            if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False) return null;
            if (!root.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String) return null;

            var value = url.GetString();
            if (string.IsNullOrWhiteSpace(value)) return null;

            return Uri.TryCreate(value, UriKind.Absolute, out var parsed)
                   && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
                ? value
                : null;
        }
        catch
        {
            return null;
        }
    }
}
