using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 首页公告的来源（需求：2026-11-05 Nick 定）。
///
/// <para>
/// 公告文件放在**桌面端仓库** <c>c1201y/ClassSoftwareHub-Desktop</c> 的 <c>公告/</c> 目录：
/// <list type="bullet">
///   <item><c>公告/all.json</c> —— 所有版本都能看到；</item>
///   <item><c>公告/{版本号}.json</c> —— 只有这个版本能看到，文件名就是 <see cref="ShellConfig.ShellVersion"/>。</item>
/// </list>
/// 发一条公告 = 往那个目录 push 一个 JSON，客户端下次启动自己就看到了。**不需要后端配合**。
/// </para>
///
/// <para>
/// ⚠️ 为什么复用 GitHub 而不是自家接口：公告的价值在于"后端挂了也能发出去"。走 GitHub 的话，
///    后端迁移、接口改版、服务器宕机都不影响这条通道 —— 这正是它要干的事。
/// </para>
///
/// <para>
/// ⛔ 这里只**读**，不写、不上报。别加任何"已读回传"，那会让它变成需要后端配合的云控，
///    这条通道最大的优点（零对接）当场消失。
/// </para>
/// </summary>
public static class NoticeFeed
{
    /// <summary>公告所在仓库。与软件数据用的站点仓库不是一个，别混。</summary>
    public const string RepoOwner = "c1201y";
    public const string RepoName = "ClassSoftwareHub-Desktop";

    /// <summary>仓库里的目录名。中文目录在 URL 里要单独编码，见 <see cref="FileUrlPath"/>。</summary>
    private const string DirName = "公告";

    /// <summary>
    /// 内容入口，按顺序回退。与 <c>GithubContentSync.RawTemplates</c> 同一套线路，
    /// 但那边要 commit sha（先查一次接口），这里用分支名 —— 公告不需要精确到某个提交，
    /// 少一次请求更重要（首页每次启动都会来问一次）。
    ///
    /// ⚠️ 顺序有意为之：raw 最新（CDN 缓存约 5 分钟）；jsDelivr/fastly 在校园网更通但可能有更久的缓存；
    ///    gh-proxy 放最后当兜底。本机实测过 raw 会被 hosts 掐掉，所以后三条不是摆设。
    /// </summary>
    private static readonly string[] Templates =
    {
        "https://raw.githubusercontent.com/{0}/{1}/main/{2}",
        "https://cdn.jsdelivr.net/gh/{0}/{1}@main/{2}",
        "https://fastly.jsdelivr.net/gh/{0}/{1}@main/{2}",
        "https://gh-proxy.com/https://raw.githubusercontent.com/{0}/{1}/main/{2}",
    };

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>拉到的原始列表（未按时间窗过滤，过滤在返回前做，见 <see cref="Visible"/>）。</summary>
    private static IReadOnlyList<NoticeItem> _raw = Array.Empty<NoticeItem>();

    /// <summary>上次**成功**拉取的时刻。</summary>
    private static DateTimeOffset _lastOk = DateTimeOffset.MinValue;

    /// <summary>
    /// 上次**尝试**的时刻（成败都算）。没成功过时用它做退避，避免每进一次首页就白等一轮超时。
    /// </summary>
    private static DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    /// <summary>成功之后的拉取间隔。公告时效以天计，10 分钟足够，别让首页每次切换都去敲网络。</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);

    /// <summary>一直没拉成功时的重试间隔。短一点：刚开机网络可能还没起来，不能一失败就锁十分钟。</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(2);

    /// <summary>该不该再拉一次。</summary>
    private static bool ShouldFetch(DateTimeOffset now, bool force)
    {
        if (force) return true;
        if (_lastOk != DateTimeOffset.MinValue && now - _lastOk < MinInterval) return false;
        if (_lastAttempt != DateTimeOffset.MinValue && now - _lastAttempt < RetryInterval) return false;
        return true;
    }

    private static string CacheFile => Path.Combine(AppPaths.DataDir, "notice-cache.json");

    private static readonly JsonSerializerOptions DiskJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    /// <summary>
    /// 取当前该显示的公告（已按时间窗过滤、按 id 去重）。**不会抛异常**，最差返回空列表。
    ///
    /// <para>
    /// ⚠️ <paramref name="version"/> 传 <see cref="ShellConfig.ShellVersion"/>。预览版的号里带
    ///    <c>-insider1.9</c>，所以预览版要单独建一个同名文件才看得到版本专属公告 —— 这是有意的：
    ///    预览版和正式版面对的公告本来就不该一样。
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<NoticeItem>> LoadAsync(
        string version, bool force = false, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (!ShouldFetch(now, force))
            return Visible(_raw, now);

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 拿到锁之后再判一次：并发进来的第二个请求直接吃第一个的结果
            now = DateTimeOffset.UtcNow;
            if (!ShouldFetch(now, force))
                return Visible(_raw, now);

            _lastAttempt = now;

            // 版本专属的排前面：它比通用公告更具体、多半也更急。
            // 去重是"先到先得"，所以同一个 id 多处都写了的话，以更具体的那份为准。
            var files = new List<string>();
            if (version.Length > 0) files.Add(version + ".json");

            // ⚠️ 预览版的号长这样：1.2.0-insider1.9。公告要按发行版本（1.2.0）写 ——
            //    否则每推一个预览版都得复制一份公告，两份迟早写出不一样的正文。
            //    所以再收敛一层：完整号 → 主版本 → all.json，越往后越宽。
            var dash = version.IndexOf('-');
            if (dash > 0)
            {
                var major = version[..dash] + ".json";
                if (!files.Contains(major, StringComparer.OrdinalIgnoreCase)) files.Add(major);
            }

            files.Add("all.json");

            var merged = new List<NoticeItem>();
            var anyOk = false;

            foreach (var file in files)
            {
                var text = await FetchAsync(file, ct).ConfigureAwait(false);
                if (text is null) continue;          // 文件不存在 = 这个版本没有这类公告，很正常
                anyOk = true;
                merged.AddRange(NoticeItem.Parse(text));
            }

            if (anyOk)
            {
                _raw = merged;
                _lastOk = DateTimeOffset.UtcNow;
                SaveCache(merged);
                AppLog.Step("notice", $"公告已更新 · {merged.Count} 条（{string.Join(" + ", files)}）");
            }
            else if (_raw.Count == 0)
            {
                // 网络全挂：用上次落盘的，仍按时间窗过滤 —— 过期的公告不会因此复活。
                var disk = LoadCache();
                if (disk.Count > 0)
                {
                    _raw = disk;
                    AppLog.Step("notice", $"公告拉取失败，使用本地缓存 {disk.Count} 条");
                }
            }

            return Visible(_raw, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            return Visible(_raw, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            AppLog.Warn("notice", "公告拉取异常：" + ex.Message);
            return Visible(_raw, DateTimeOffset.UtcNow);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// 过滤出此刻该显示的条目：同一 id 只认第一个、在时间窗内、标题或正文不为空。
    /// 关掉过的由调用方按存档再滤一遍（那要读设置，不该塞进这里）。
    /// </summary>
    public static IReadOnlyList<NoticeItem> Visible(IReadOnlyList<NoticeItem> all, DateTimeOffset now)
    {
        if (all.Count == 0) return Array.Empty<NoticeItem>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<NoticeItem>(all.Count);

        foreach (var n in all)
        {
            if (!seen.Add(n.Id)) continue;
            if (n.StartsAt is { } start && now < start) continue;
            if (n.EndsAt is { } end && now >= end) continue;
            list.Add(n);
        }

        return list;
    }

    // ────────────────────────── 取一个文件 ──────────────────────────

    /// <summary>上次成功的入口序号。-1 = 还没成功过。</summary>
    private static string BaseFile => Path.Combine(AppPaths.DataDir, "notice-base.txt");

    /// <summary>
    /// 按「上次成功的优先」排可能性。
    ///
    /// ⚠️ 为什么必须记：本机实测 raw 硬超时（6 秒连接超时）、jsDelivr 的 SSL 直接断，
    ///    只有 gh-proxy 通。不记的话每次启动都要白等那两个失败的，公告要七八秒后才冒出来。
    ///    跟 <c>GithubContentSync</c> 的 content-base.txt 是同一个套路。
    /// </summary>
    private static IEnumerable<int> Candidates()
    {
        var preferred = ReadPreferredBase();
        if (preferred >= 0)
        {
            yield return preferred;
            for (var i = 0; i < Templates.Length; i++)
                if (i != preferred) yield return i;
            yield break;
        }

        for (var i = 0; i < Templates.Length; i++) yield return i;
    }

    private static int ReadPreferredBase()
    {
        try
        {
            if (!File.Exists(BaseFile)) return -1;
            var text = File.ReadAllText(BaseFile).Trim();
            return int.TryParse(text, out var index) && index >= 0 && index < Templates.Length ? index : -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>记下这次成功的入口。写盘失败无所谓，下次多试几条而已。</summary>
    private static void RememberBase(int index)
    {
        try
        {
            if (ReadPreferredBase() == index) return;
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(BaseFile, index.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 取仓库里 <c>公告/{file}</c> 的正文。文件不存在返回 <c>null</c>；
    /// 所有线路都不通也返回 <c>null</c>（调用方按"没有公告"处理，不弹错误）。
    /// </summary>
    private static async Task<string?> FetchAsync(string file, CancellationToken ct)
    {
        // 中文目录名要编码，但路径分隔符不能编 —— EscapeDataString 会把 '/' 也变成 %2F，
        // 那样服务端就找不到了。所以目录和文件名分开编码。
        var urlPath = Uri.EscapeDataString(DirName) + "/" + Uri.EscapeDataString(file);

        Exception? last = null;
        foreach (var index in Candidates())
        {
            var url = string.Format(Templates[index], RepoOwner, RepoName, urlPath);
            try
            {
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                // 404 只可能是"这个文件不存在"，换镜像不会有另一个答案 —— 立刻收手，
                // 不然每个没有专属公告的版本都要白试四条线路。
                if (resp.StatusCode == HttpStatusCode.NotFound) return null;
                if (!resp.IsSuccessStatusCode)
                {
                    last = new HttpRequestException($"HTTP {(int)resp.StatusCode}");
                    continue;
                }

                RememberBase(index);
                return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        if (last is not null)
            AppLog.Warn("notice", $"公告文件 {file} 所有入口均不可用：{last.Message}");
        return null;
    }

    // ────────────────────────── 本地缓存 ──────────────────────────

    /// <summary>
    /// 落盘一份，供断网时用。写失败不报错 —— 公告拉不到缓存不影响任何功能，
    /// 为它弹提示反而打扰用户。
    /// </summary>
    private static void SaveCache(List<NoticeItem> items)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(CacheFile,
                JsonSerializer.Serialize(items, DiskJson), new UTF8Encoding(false));
        }
        catch
        {
            // 忽略
        }
    }

    private static List<NoticeItem> LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return new List<NoticeItem>();
            var text = File.ReadAllText(CacheFile);
            return JsonSerializer.Deserialize<List<NoticeItem>>(text, DiskJson) ?? new List<NoticeItem>();
        }
        catch
        {
            return new List<NoticeItem>();
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(6),
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(12),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClassSoftwareHub-Desktop");
        return client;
    }
}
