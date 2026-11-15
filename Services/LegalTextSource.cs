using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 条款正文的数据源：**在线优先 → 本地缓存 → 程序内置兜底**。
///
/// <para>
/// 为什么这么改（2026-11-01 Nick 提的）：以前条款是构建期由 <c>tools/sync-legal.mjs</c>
/// 同步成 <see cref="LegalText"/> 常量打进程序集，后果是**改一次条款就得发一次版**，
/// 装在用户机器上的旧版本永远显示旧条款 —— 而条款恰恰是最该「改完立刻生效」的东西。
/// 现在站点把同一份内容额外导出成 <c>public/legal.json</c>（见站点仓库
/// <c>scripts/gen-legal-json.mjs</c>），客户端开机后拉一次，站点改完提交即生效。
/// </para>
///
/// <para>
/// ⛔ 别直接去拉站点那份 <c>法律文本.ts</c>：那是 TypeScript 源码，单引号、<c>\'</c> 转义、
/// 行内注释、尾逗号，在 C# 里啃它得手写一个解析器，站点哪天改个写法就全崩。json 才是数据格式。
/// </para>
///
/// <para>
/// ⚠️ 内置那份**不删**。教室网离线是常态，删掉兜底等于断网时连协议都看不了一眼；
/// 它现在的角色从「唯一来源」降级成「拉不到时的那一份」，所以也不必再跟着每次改条款重新发版。
/// </para>
///
/// <para>
/// ⚠️ 只认「比手上这份新」的。<c>generated</c> 是站点生成时间，不新就直接跳过该线路继续试下一条 ——
/// jsDelivr 有 CDN 缓存、站点部署也可能滞后，旧版覆盖新版等于把条款倒退回去。
/// </para>
/// </summary>
public static class LegalTextSource
{
    /// <summary>缓存位置：<c>%LOCALAPPDATA%\ClassSoftwareHub\legal\legal.json</c>。跟设置放同一层，卸载时一起留着。</summary>
    public static string CachePath => Path.Combine(AppPaths.DataDir, "legal", "legal.json");

    /// <summary>
    /// 条款拉回来之后的当前生效版本。第一次访问时同步读一次本地缓存（几 KB），之后就是内存里的。
    /// </summary>
    public static IReadOnlyList<LegalDoc> All
    {
        get
        {
            EnsureLoaded();
            lock (Gate) return _all;
        }
    }

    /// <summary>按 key（agreement / privacy / disclaimer）取一份；找不到返回 null。</summary>
    public static LegalDoc? Find(string? key)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(key)) return null;

        lock (Gate)
        {
            return _all.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>当前用的是不是随程序内置的那份（= 一次都没拉到过）。界面据此决定要不要提示「可能不是最新」。</summary>
    public static bool IsBuiltIn
    {
        get
        {
            EnsureLoaded();
            lock (Gate) return _origin == OriginBuiltIn;
        }
    }

    /// <summary>
    /// 拉一次条款。**不抛异常**：六条线路全失败就返回 false，界面继续显示手上这份。
    ///
    /// <para>
    /// 调用点：主窗口首帧之后（后台跑，不挡任何界面）。用户打开条款窗时读到的就是这次的结果。
    /// </para>
    /// </summary>
    public static async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        EnsureLoaded();

        lock (Gate)
        {
            // 同一时刻只跑一份：首帧和设置页有可能各调一次，重复拉没意义还多占配额
            if (_refreshing) return false;
            _refreshing = true;
        }

        try
        {
            foreach (var url in Endpoints())
            {
                if (ct.IsCancellationRequested) return false;

                string json;
                try
                {
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    budget.CancelAfter(TimeSpan.FromSeconds(6));
                    json = await Http.GetStringAsync(url, budget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    AppLog.Warning("legal", $"{HostOf(url)} 超时（6 秒），换下一条线路");
                    continue;
                }
                catch (Exception ex)
                {
                    AppLog.Warning("legal", $"{HostOf(url)} 取不到：{ex.Message}");
                    continue;
                }

                if (!TryParse(json, out var docs, out var generated))
                {
                    AppLog.Warning("legal", $"{HostOf(url)} 返回的内容不是可用的条款 json，已忽略");
                    continue;
                }

                bool newest;
                lock (Gate)
                {
                    // 站点（第一条）就回了和手上一样的版本 ⇒ 已经是最新。
                    // ⛔ 这里必须直接收工，不能当成「旧版」继续试后面几条 —— 后面是 jsDelivr / gh-proxy / raw，
                    //    内容只会更旧或一样，而 raw 在本机是硬超时，白搭 6 秒（2026-11-01 实测踩到）。
                    if (generated == _generated)
                    {
                        AppLog.Info("legal", $"条款已是最新（站点生成于 {generated:yyyy-MM-dd HH:mm}）");
                        return false;
                    }

                    if (generated < _generated) newest = false;   // 真的是旧版（CDN 缓存 / 站点部署滞后），试下一条
                    else
                    {
                        _all = docs;
                        _generated = generated;
                        _origin = "在线";
                        newest = true;
                    }
                }

                if (!newest) continue;

                SaveCache(json);
                AppLog.Info("legal", $"条款已从 {HostOf(url)} 更新，站点生成时间 {generated:yyyy-MM-dd HH:mm}");
                return true;
            }

            AppLog.Info("legal", "本次没取到条款，继续用手上这份");
            return false;
        }
        finally
        {
            lock (Gate) _refreshing = false;
        }
    }

    // ── 线路 ─────────────────────────────────────────────────────────────

    private const string OriginBuiltIn = "程序内置";

    /// <summary>
    /// 取 json 的线路，按「大概率最快 → 兜底」排。
    ///
    /// <para>
    /// ① 站点自己：<c>classsoftwarehub.us.ci/legal.json</c> —— 文件在 <c>public/</c> 下，
    ///    Vite 原样复制到 dist，所以合同页同域名就能取到，走的是用户平时访问站点的那条路；
    /// ② 备用域名：<c>classsoftwarehub.cn</c>；
    /// ③④ jsDelivr 两个域名：GitHub 拉不动时国内通常能过，代价是 CDN 有缓存（靠 generated 判新旧兜住）；
    /// ⑤ gh-proxy：raw 直连经常不通，它是最实际的那条兜底；
    /// ⑥ raw.githubusercontent：官方原始地址，放最后。
    /// </para>
    ///
    /// <para>
    /// ⚠️ gh-proxy 排在 raw **前面**（2026-11-01 实测）：本机 raw 是硬超时（连不上，要白等满 6 秒），
    /// 而 gh-proxy 1.5 秒就回；顺序反过来的话每次启动都要多耗 6 秒才轮到能用的那条。
    /// 海外网络下 raw 更快，但这个应用的用户是国内的教室电脑，按大概率排。
    /// </para>
    /// </summary>
    private static IEnumerable<string> Endpoints()
    {
        var owner = ShellConfig.SiteRepoOwner;
        var repo = ShellConfig.SiteRepoName;
        var branch = ShellConfig.SiteRepoBranch;
        var rawPath = $"{owner}/{repo}/{branch}/public/legal.json";

        yield return ShellConfig.SiteUrl + "/legal.json";
        yield return "https://classsoftwarehub.cn/legal.json";
        yield return $"https://fastly.jsdelivr.net/gh/{owner}/{repo}@{branch}/public/legal.json";
        yield return $"https://cdn.jsdelivr.net/gh/{owner}/{repo}@{branch}/public/legal.json";
        yield return "https://gh-proxy.com/https://raw.githubusercontent.com/" + rawPath;
        yield return "https://raw.githubusercontent.com/" + rawPath;
    }

    /// <summary>日志里只写域名，完整 URL 一长串看着累。</summary>
    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    // ── 状态 ─────────────────────────────────────────────────────────────

    private static readonly object Gate = new();

    /// <summary>
    /// ⚠️ Timeout 兜的是「连上了但一个字节都不给」的死连接；每条线路外面还套了 6 秒的 CancellationToken，
    /// 两者都在才算真兜住（单靠 Timeout 要等满 8 秒，六条线路最坏 48 秒）。
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static IReadOnlyList<LegalDoc> _all = LegalText.All;
    private static DateTimeOffset _generated = DateTimeOffset.MinValue;
    private static string _origin = OriginBuiltIn;
    private static bool _loaded;
    private static bool _refreshing;

    /// <summary>第一次用到时同步读一次缓存。失败（文件没有 / 坏了）就一直用内置那份，不抛。</summary>
    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                if (!File.Exists(CachePath)) return;

                var text = File.ReadAllText(CachePath);
                if (!TryParse(text, out var docs, out var generated))
                {
                    // 坏文件留着只会每次启动都失败一次，直接删掉，下次拉到了自然重写
                    AppLog.Warning("legal", "本地条款缓存解析失败，已删除并改用内置版本");
                    try { File.Delete(CachePath); } catch { /* 删不掉就下次再说 */ }
                    return;
                }

                _all = docs;
                _generated = generated;
                _origin = "本地缓存";
                AppLog.Info("legal", $"条款缓存已载入（{docs.Count} 份，站点生成于 {generated:yyyy-MM-dd HH:mm}）");
            }
            catch (Exception ex)
            {
                AppLog.Warning("legal", "读本地条款缓存失败：" + ex.Message);
            }
        }
    }

    private static void SaveCache(string json)
    {
        try
        {
            var dir = Path.GetDirectoryName(CachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 先写临时文件再换名：写到一半断电也不会留下一份半截 json 让下次启动解析崩
            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warning("legal", "写条款缓存失败：" + ex.Message);
        }
    }

    // ── 解析 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 解析站点那份 <c>legal.json</c>。
    ///
    /// <para>
    /// ⛔ 用 <see cref="JsonDocument"/> 手工读而不是反序列化成 DTO：要读的结构就三层，
    /// 手工读能顺手把「三份必须齐全」「小节不能为空」这些校验写在一起 —— 少一份文档，
    /// 界面上的标签就会有一档点不动，必须在进内存之前就拦住。
    /// </para>
    /// </summary>
    private static bool TryParse(string json, out IReadOnlyList<LegalDoc> docs, out DateTimeOffset generated)
    {
        docs = Array.Empty<LegalDoc>();
        generated = default;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("docs", out var arr) || arr.ValueKind != JsonValueKind.Array) return false;

            var list = new List<LegalDoc>();
            foreach (var item in arr.EnumerateArray())
            {
                var one = ReadDoc(item);
                if (one is not null) list.Add(one);
            }

            if (list.Count == 0) return false;

            // 三份必须齐全 —— 以内置那份的 key 为准（以后加第四份文档时这里自动跟着走）
            foreach (var expected in LegalText.All)
            {
                if (!list.Any(d => string.Equals(d.Key, expected.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    AppLog.Warning("legal", $"条款 json 里缺少「{expected.Title}」，整份丢弃");
                    return false;
                }
            }

            if (root.TryGetProperty("generated", out var g) && g.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(g.GetString(), out var when))
            {
                generated = when;
            }

            docs = list;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static LegalDoc? ReadDoc(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;

        var key = Str(e, "key");
        if (key.Length == 0) return null;

        var sections = new List<LegalSection>();
        if (e.TryGetProperty("sections", out var secs) && secs.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in secs.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;

                var items = new List<string>();
                if (s.TryGetProperty("items", out var its) && its.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in its.EnumerateArray())
                    {
                        if (t.ValueKind == JsonValueKind.String) items.Add(t.GetString() ?? "");
                    }
                }

                sections.Add(new LegalSection { Heading = Str(s, "heading"), Items = items });
            }
        }

        // 一条正文都没有的文档等于没内容，宁可当它不存在（TryParse 那边会把整份丢掉）
        if (sections.Count == 0) return null;

        return new LegalDoc
        {
            Key = key,
            Version = Str(e, "version"),
            Effective = Str(e, "effective"),
            Title = Str(e, "title"),
            Intro = Str(e, "intro"),
            Sections = sections,
        };
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "")
            : "";
}
