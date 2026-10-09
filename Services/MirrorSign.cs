using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 自建 GitHub 加速节点（开源实现 gh-stream，线上入口 <see cref="NodeBaseUrl"/>）的「换签名链接」客户端。
///
/// 节点规则（2026-10-09 在本机实测 —— ⚠️ 节点本身可达，反倒是站点后端域名走不通，所以是直连节点）：
///   · 只认限时签名链接 <c>GET /d?u=&lt;urlencode(原始链接)&gt;&amp;e=&lt;过期秒&gt;&amp;s=&lt;HMAC 签名&gt;</c>；
///     **不支持前缀拼接** —— <c>节点/&lt;原始链接&gt;</c> 这种写法对任何 Referer 都返回 403；
///   · 节点 <c>REFERER_MODE=strict</c> ⇒ 抓签名链接**必须带 Referer**（见 <see cref="Referer"/>），
///     不带一律 <c>403 Referer 不被允许</c>（白名单 classsoftwarehub.us.ci / 132614.xyz / xfane.com /
///     classsoftwarehub.cn，含子域）；
///   · 签名链 <b>15 分钟</b>过期，过期后返回 <c>410 链接已过期</c>。
///
/// ── 密钥从哪来（⚠️ 本仓库是公开的，所以密钥**绝不写进源码**）────────────────────
/// 换签名要凭服务端的 <c>SIGN_API_KEY</c>，按顺序去三处找（见 <see cref="ResolveApiKey"/>）：
///   ① 环境变量 <see cref="KeyEnvVar"/>；
///   ② exe 同目录的 <see cref="KeyFileName"/> —— **发布版靠它**，打包时注入（这个文件在 .gitignore 里）；
///   ③ 本机数据目录的同名文件 —— 开发机上放一份常驻，重编不会被冲掉。
/// 三处都没有 ⇒ 自建加速整条跳过、安静回退公益镜像：**功能不会坏，只是不走节点**。
///
/// ⛔ 也**不要在客户端本地算 HMAC**。节点 <c>SIGN_BIND=off</c> 时本地算确实也通（实测过），但那样
///    得把 <c>URL_SECRET</c> —— 一个能**自定义过期时间**的万能签名密钥 —— 塞进公开分发的安装包里；
///    <c>/sign</c> 只暴露一个「换 15 分钟签名」的钥匙，危害小得多。而且过期时间由服务端时钟说了算，
///    客户端系统时间不准也不会拿到一条出生即过期的链接。
///
/// ⚠️ 说句实话：密钥不写进 git ≠ 密钥保密。发布包里仍然能 dump 出来（客户端要能用，就得拿得到）。
///    真正的止血点是**服务端限流 + 定期轮换这把钥匙**。想彻底不见密钥只有一条路：让站点后端代签。
///
/// 本类只负责「拿到链接」，失败一律返回 null 不抛异常 —— 回退决策留给 <see cref="GithubRoute"/>。
/// </summary>
public static class MirrorSign
{
    /// <summary>日志用的 tag（下载选路相关，全走这一个）。</summary>
    private const string LogTag = "download-route";

    /// <summary>节点线上入口。换域名时只改这里一处。</summary>
    public const string NodeBaseUrl = "https://download.classsoftwarehub.cn";

    /// <summary>
    /// 抓签名链接时必须携带的 Referer（节点 Referer 白名单里的站点域）。
    /// ⚠️ 这是节点放行的**硬要求**，不是可选优化 —— 实测不带它必被 403。
    /// </summary>
    public const string Referer = "https://classsoftwarehub.us.ci/";

    /// <summary>节点密钥的文件名。⛔ **这个文件绝不进 git**（.gitignore 里已挡）。</summary>
    public const string KeyFileName = "gh-node-key.txt";

    /// <summary>节点密钥的环境变量名（本地调试 / CI 里临时给，优先级最高）。</summary>
    public const string KeyEnvVar = "CSH_GH_NODE_KEY";

    /// <summary>单次换签请求的超时。这是个几百字节的 JSON 接口，不该按文件的超时算。</summary>
    private const int RequestTimeoutMs = 8000;

    /// <summary>整轮失败后多久内不再重试 —— 否则网络不通时每次下载都要白等 8 秒。</summary>
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(5);

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,     // 靠 CancellationToken 控制单次超时
    };

    private static readonly object Gate = new();
    private static DateTime _retryAfter = DateTime.MinValue;

    private static readonly object KeyGate = new();
    private static bool _keyProbed;
    private static string? _apiKey;
    private static string? _keySource;

    /// <summary>
    /// 把一条签名链接包成下载候选 —— <c>Referer</c> 与 <c>X-Api-Key</c> 跟它绑在一起走，
    /// 谁都不会漏带（节点要 Referer、签名若绑了密钥还要 Key，二者都必须随链接发出）。
    /// </summary>
    public static DownloadCandidate Candidate(string signedUrl) =>
        new(signedUrl, Referer, ResolveApiKey());

    /// <summary>
    /// GitHub 文件直链 → 节点签发的限时下载链接。取不到一律返回 null（调用方回退公益镜像），**不抛异常**。
    ///
    /// 失败（含 401 密钥失效 / 400 目标不在范围 / 429 限流 / 503 服务端没配密钥 / 网络不通）
    /// 后 5 分钟内不再重试，省掉每次下载的 8 秒白等。
    /// </summary>
    public static async Task<string?> GetSignedUrlAsync(string githubUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(githubUrl)) return null;

        // 没配密钥就直接放弃，连请求都不发（启动时 LogKeyStatus 已经说明过原因）
        var apiKey = ResolveApiKey();
        if (apiKey is null) return null;

        lock (Gate)
        {
            if (DateTime.UtcNow < _retryAfter) return null;    // 刚失败过，先别试
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
            throw;                                              // 用户自己取消的，照实往上抛
        }
        catch (Exception ex)
        {
            Cool();
            Core.AppLog.Info(LogTag,
                $"自建加速：未取得签名（{ex.Message}），{FailureCooldown.TotalMinutes:0} 分钟内不再重试，改用公益镜像");
            return null;
        }
    }

    /// <summary>
    /// 启动时写一行「密钥就位情况」到日志（**只说找了哪儿、绝不打印密钥本身**）。
    /// 排障时一眼看出「这版包到底带没带钥匙」，不用靠一次真下载去试。
    /// </summary>
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

    /// <summary>
    /// 找节点密钥。顺序：环境变量 → exe 同目录 → 本机数据目录。**只读文件、绝不联网取**，
    /// 免得变成「拿不到配置就连下载都做不了」。结果缓存，一个进程内只探一次。
    /// </summary>
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
            return null;                                        // 读不到就当没有，不打断下载
        }
    }

    private static void Cool()
    {
        lock (Gate) _retryAfter = DateTime.UtcNow + FailureCooldown;
    }

    /// <summary>把常见失败码翻译成一句人话，写进日志，方便远程排查到底卡在哪。</summary>
    private static string StatusHint(int code) => code switch
    {
        400 => "（目标链接不在可加速范围）",
        401 or 403 => "（密钥无效或已轮换，检查 gh-node-key.txt）",
        410 => "（签名已过期）",
        429 => "（节点限流）",
        503 => "（节点未配置 SIGN_API_KEY）",
        _ => "",
    };

    /// <summary>
    /// 解析 <c>{"url":"https://download.classsoftwarehub.cn/d?u=…&amp;e=…&amp;s=…", "expiresAt":…, "ttl":900}</c>。
    /// 只认绝对 http(s) 地址 —— 拿到的必须是能直接抓的链接，别的都当失败。
    /// </summary>
    private static string? Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // 节点直接给 url；老的代签代理会多包一层 success（留着不吃亏）
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
