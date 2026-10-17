using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 应用图标本地磁盘缓存。
/// 把 http(s) 图标下载并落盘到本地（按 URL 哈希命名，扩展名按原地址后缀判定），
/// 之后一律从本地 <c>file://</c> 加载，不再每次打开页面都重新联网下载
/// （也绕开了 GitHub raw 的限流/超时 —— 之前那几个图标 20s 取不到、只能显示首字母兜底，正是这个原因）。
///
/// 用法（见 <see cref="Data.SoftwareApp.IconImage"/>）：
///   · <see cref="TryGet"/>           —— 同步：本地有文件就返回 file:// 源（还会再 memo 一次），并告诉你**新不新鲜**
///   · <see cref="EnsureCachedAsync"/> —— 后台下载并存本地（fire-and-forget；按 URL 去重；走 GithubRoute 换线路）
///   · <see cref="Invalidate"/>        —— 站点数据更新后调用：整批标记陈旧，下次访问按需重下一次
///
/// **容错顺序**（Nick 定的："网络通道先下载下来，超时了再用本地的"）：
///   1. 本地新鲜       → 直接用，零网络；
///   2. 陈旧 / 没有    → 先用网络源（走换路后的通道）取最新；
///   3. 网络拿不到     → 退回本机那张旧图显示，绝不留占位字形（见 SoftwareApp 的 fallback）。
///
/// ⚠️ 这两个方法都会创建 XAML 对象（BitmapImage / SvgImageSource），必须在 **UI 线程**上调用。
///    SoftwareApp.IconImage 本来就在 UI 线程访问，直接调即可。
/// </summary>
public static class AppIconCache
{
    private static readonly string Dir =
        Path.Combine(ShellConfig.CachedContentDir, "icons");

    // 进程内二次加速：已建好的源直接复用，省掉再读一次文件 + 再解码。
    private static readonly ConcurrentDictionary<string, ImageSource> _memo = new();
    // 同一 URL 同时只下一个（避免列表一页几十个图标重复拉同一张）。
    private static readonly ConcurrentDictionary<string, byte> _inflight = new();

    /// <summary>
    /// 缓存「纪元」标记文件。**它的最后修改时间就是本轮缓存的起点**：
    /// 下载时间早于它的缓存文件 = 上一轮（站点换过数据之前）留下的，视为陈旧、按需重下一次。
    ///
    /// 为什么用 mtime 而不是给文件名加版本号：加版本号会让每一次失效都留下一批旧文件垃圾，
    /// 而且还得想办法回收；一个标记文件的 mtime 零垃圾、跨进程天然持久。
    /// </summary>
    private static string EpochFile => Path.Combine(Dir, ".epoch");

    private static DateTime? _epochUtc;

    private static bool IsHttp(string url) =>
        !string.IsNullOrWhiteSpace(url) &&
        url.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    private static string ExtOf(string url)
    {
        var q = url.IndexOf('?');
        var path = q >= 0 ? url[..q] : url;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".svg" or ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp"
            ? ext : ".img";
    }

    private static string NameOf(string url)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
        var hex = Convert.ToHexString(bytes)[..16];   // 16 位足够区分，文件名也短
        return hex + ExtOf(url);
    }

    private static string PathOf(string url) => Path.Combine(Dir, NameOf(url));

    /// <summary>
    /// 同步尝试从本地缓存取图标源。
    /// 命中（文件在，无论新鲜与否）返回 true —— **陈旧也照样返回**，调用方据此决定要不要联网换新的。
    /// </summary>
    /// <param name="stale">true = 这是上一纪元留下的，内容可能已经换了，值得一刷。</param>
    public static bool TryGet(string url, out ImageSource? source, out bool stale)
    {
        source = null;
        stale = false;
        if (!IsHttp(url)) return false;
        if (_memo.TryGetValue(url, out var cached))
        {
            source = cached;
            return true;
        }
        var p = PathOf(url);
        if (!File.Exists(p)) return false;
        stale = IsStale(p);
        source = MakeSource(p);
        _memo[url] = source!;
        return true;
    }

    /// <summary>
    /// 使本机图标缓存集体失效（标记为陈旧），下次被访问到时按需重下一次。
    /// 由 <see cref="GithubContentSync"/> 在「站点数据确实有变化」时调用 —— 软件清单换过，
    /// 里面的图标地址 / 图标本身就可能已经换了，老缓存不能当永久的用。
    ///
    /// ⛔ **故意不删文件**：删了之后、到重新下载成功之前有一段空窗，界面只能显示占位字形。
    ///    标记陈旧时旧文件还在，界面先直接走远程源显示（<see cref="Data.SoftwareApp.IconImage"/> 的分支 ②），
    ///    后台静默重下覆盖文件 —— 视觉不断档；万一这次下载失败，旧 mtime 还在，下次再来还会重试。
    /// </summary>
    public static void Invalidate()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(EpochFile, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            _epochUtc = SafeMtimeUtc(EpochFile);
            _memo.Clear();   // ⛔ 进程内 memo 会绕过上面的文件检查，必须同时清掉
            Core.AppLog.Info("cache", "软件数据已更新 → 图标缓存标记为陈旧，下次访问时按需刷新");
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("cache", ex, "提升图标缓存纪元");
        }
    }

    /// <summary>这个缓存文件是不是上一纪元留下的（下载时间早于纪元起点）⇒ 内容可能已经变了。</summary>
    private static bool IsStale(string path)
    {
        var epoch = EpochUtc();
        if (epoch is null) return false;               // 还没发生过失效 ⇒ 谁都不算陈旧
        var wrote = SafeMtimeUtc(path);
        return wrote is { } w && w < epoch.Value;
    }

    private static DateTime? EpochUtc()
    {
        // ⚠️ 惰性读一次就够：纪元只会由本进程的 Invalidate 抬高，别的进程不动它（单例 + 同一按用户陷入同一目录）
        if (_epochUtc is null)
            _epochUtc = File.Exists(EpochFile) ? SafeMtimeUtc(EpochFile) : null;
        return _epochUtc;
    }

    private static DateTime? SafeMtimeUtc(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null; }
        catch { return null; }
    }

    /// <summary>单条线路的超时上限。取 8 秒：图标是小文件，8 秒干不完基本就是这条线走不通了。</summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(8);

    /// <summary>上次走通的公益镜像（进程内记住即可，下次优先试它，不必每次都从头陪一遍超时）。</summary>
    private static MirrorChannel? _lastGoodMirror;

    /// <summary>
    /// 后台把图标下载到本地缓存（幂等：新鲜或正在下则跳过）。失败静默，只在真的全线路走不通时记一条。
    /// </summary>
    public static async Task EnsureCachedAsync(string url, CancellationToken ct = default)
    {
        if (!IsHttp(url)) return;
        var target = PathOf(url);
        if (File.Exists(target) && !IsStale(target)) return;   // 有且没过期
        if (!_inflight.TryAdd(url, 0)) return;        // 别人正在下，别重复拉
        try
        {
            var bytes = await DownloadAsync(url, ct).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
            {
                Core.AppLog.Warn("cache", $"图标取不到（各条线路均失败）→ 暂用本机旧图：{Host(url)}");
                return;
            }
            Directory.CreateDirectory(Dir);
            // 覆盖写 ⇒ 文件 mtime 自动变新 ⇒ 下次 IsStale 判定为"已刷新"，不会重复拉
            await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);
            // ⛔ memo 里存的还是按旧文件建的源，得踢掉让下次按新文件重建。
            //    这里不在后台线程 new ImageSource —— 那东西必须在 UI 线程建。
            _memo.TryRemove(url, out _);
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("cache", ex, $"缓存图标 {Host(url)}");
        }
        finally
        {
            _inflight.TryRemove(url, out _);
        }
    }

    /// <summary>日志里只留域名就够定位了（整条 URL 可能很长还带签名）。</summary>
    private static string Host(string url)
    {
        try { return new Uri(url).Host; } catch { return url; }
    }

    /// <summary>
    /// 走网络通道取字节：**GitHub 系地址交给 <see cref="GithubRoute"/> 换线路**（官方直连 → 公益镜像 → 自建节点，
    /// 一条不通换下一条），非 GitHub 地址不做改写、直接下。
    ///
    /// 为什么这里值得多走一层：本机 raw.githubusercontent.com 常年超时（之前几个软件图标取不到、
    /// 只能显示名称首字，根子就在线路不通而不是图有问题）。
    /// ⛔ 只有 <see cref="Core.GithubMirror.IsMirrorableUrl"/> 认的地址才进换路流程 ——
    ///    既守"链接不许乱改写"的规矩，也避开了给每个普通 CDN 图标都刷一条换路日志。
    /// </summary>
    private static async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        if (Core.GithubMirror.IsMirrorableUrl(url))
        {
            var tried = new HashSet<string>(StringComparer.Ordinal);

            // ① 先按用户在设置里选的线路来
            foreach (var candidate in await GithubRoute.ResolveAsync(url, ct).ConfigureAwait(false))
            {
                tried.Add(candidate.Url);
                var bytes = await FetchAsync(candidate.Url, ct).ConfigureAwait(false);
                if (bytes is { Length: > 0 })
                {
                    _lastGoodMirror = null;         // 官方/自建这条通了就别惦记镜像了
                    return bytes;
                }
            }

            // ② 还是不行 ⇒ 公益镜像兜底一台一台试
            //    ⚠️ 只有图标允许这么干。安装包/更新包牵着完整性（要验 MD5/SHA256），
            //       线路必须严格按用户选的来，一步都不能让（见 Services/Updating/UpdateService）。
            //       图标是纯展示小图，取不到只会剩个占位字形，没有安全性可言。
            //    📏 本机实测（2026-10-17）：直连 raw.githubusercontent.com 12 秒超时拿不到字节，
            //       ghfast 也超时，gh-proxy 2.6 秒 200 —— 不逐个试，就永远以为"这个图标坏了"。
            var ordered = new List<MirrorChannel>();
            var good = _lastGoodMirror;                                  // 上次走通的排最前
            if (good is not null) ordered.Add(good);
            foreach (var channel in Core.GithubMirror.Channels)
                if (!ReferenceEquals(channel, good)) ordered.Add(channel);
            foreach (var channel in ordered)
            {
                var mirror = Core.GithubMirror.MirrorUrl(url, channel);
                if (!tried.Add(mirror)) continue;
                var bytes = await FetchAsync(mirror, ct).ConfigureAwait(false);
                if (bytes is { Length: > 0 })
                {
                    _lastGoodMirror = channel;
                    return bytes;
                }
            }
            return null;
        }
        return await FetchAsync(url, ct).ConfigureAwait(false);
    }

    /// <summary>单条线路的下载尝试。失败返回 null（由调用方继续换线 / 兜底），不单独记日志。</summary>
    private static async Task<byte[]?> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = FetchTimeout };
            return await http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;   // 逐线路降级属于正常流程，最终由 EnsureCachedAsync 统一处置
        }
    }

    /// <summary>把本地文件变成能绑到界面的源。⚠️ 必须在 UI 线程调。</summary>
    private static ImageSource MakeSource(string file)
    {
        var uri = new Uri("file:///" + file.Replace('\\', '/'));
        if (file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            return new SvgImageSource(uri);
        return new BitmapImage(uri) { DecodePixelWidth = 128 };
    }
}
