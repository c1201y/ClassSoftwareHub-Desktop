using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services;

public enum GithubImportErrorKind { Invalid, NotFound, RateLimit, Network, Http }

public sealed class GithubImportException : Exception
{
    public GithubImportErrorKind Kind { get; }
    public int? Status { get; }

    public GithubImportException(GithubImportErrorKind kind, string message, int? status = null) : base(message)
    {
        Kind = kind;
        Status = status;
    }
}

public sealed class GithubRepoInfo
{
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "";
    public string FullName { get; set; } = "";
    public string HtmlUrl { get; set; } = "";
    public string Description { get; set; } = "";
    public string Homepage { get; set; } = "";
    public string License { get; set; } = "";
    public bool Archived { get; set; }
    public string OwnerAvatar { get; set; } = "";
}

public sealed class GithubReleaseInfo
{
    public string TagName { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Prerelease { get; set; }
    public string PublishedAt { get; set; } = "";
    public string HtmlUrl { get; set; } = "";
}

public sealed class GithubDownloadItem
{
    public string Platform { get; set; } = "";
    public string Note { get; set; } = "";
    public string Size { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class GithubImportFacts
{
    public bool UsedPrerelease { get; set; }
    public bool NoRelease { get; set; }
    public bool ReleaseFailed { get; set; }
    public bool NoAsset { get; set; }
    public int AssetTotal { get; set; }
    public int AssetSkipped { get; set; }
    public bool Truncated { get; set; }
    public string NewerPrereleaseTag { get; set; } = "";
}

public sealed class GithubImportResult
{
    public GithubRepoInfo Repo { get; set; } = new();
    public GithubReleaseInfo? Release { get; set; }
    public List<GithubDownloadItem> Downloads { get; set; } = new();
    public string System { get; set; } = "";
    public string Via { get; set; } = "";
    public GithubImportFacts Facts { get; set; } = new();
}

/// <summary>
/// 「从 GitHub 一键读取」的服务端取数逻辑（移植自站点 src/gallery/githubImport.ts）。
/// 接口入口按顺序回退：本站 Worker 代理 → api.github.com → gh-proxy → ghfast；成功的入口记在本地下次优先。
/// ⚠️ 这里不带任何令牌 —— 站点那份源码里硬编码的 PAT 属于泄露，不要抄。
/// </summary>
public static class GithubImport
{
    private static readonly (string Label, string Prefix)[] ApiBases =
    {
        ("本站代理", "https://service.132614.xyz/api/gh"),
        ("api.github.com", "https://api.github.com"),
        ("gh-proxy.com 镜像", "https://gh-proxy.com/https://api.github.com"),
        ("ghfast.top 镜像", "https://ghfast.top/https://api.github.com"),
    };

    private const int RequestTimeoutMs = 12000;
    private static string BaseCacheFile => Path.Combine(Core.AppPaths.DataDir, "gh-api-base.txt");

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ClassSoftwareHub-Desktop");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    // ══════════ 地址解析 / 小工具 ══════════

    /// <summary>支持 https://github.com/o/r、github.com/o/r、o/r、git@github.com:o/r.git 等写法。</summary>
    public static (string Owner, string Repo)? ParseRepoInput(string raw)
    {
        var text = Regex.Replace((raw ?? "").Trim(), @"\s+", "");
        if (text.Length == 0) return null;

        text = Regex.Replace(text, "^git@github\\.com:", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "^https?://", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "^(www\\.)?github\\.com/", "", RegexOptions.IgnoreCase);
        text = text.Split('?', '#')[0];
        text = Regex.Replace(text, "\\.git$", "", RegexOptions.IgnoreCase);

        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var owner = parts[0];
        var repo = parts[1];
        if (owner.Length == 0 || repo.Length == 0) return null;

        var valid = new Regex("^[A-Za-z0-9._-]+$");
        if (!valid.IsMatch(owner) || !valid.IsMatch(repo)) return null;
        return (owner, repo);
    }

    public static string RepoToId(string repoName)
    {
        var id = Regex.Replace((repoName ?? "").ToLowerInvariant(), "[^a-z0-9]+", "-");
        id = Regex.Replace(id, "^-+|-+$", "");
        return id.Length > 48 ? id.Substring(0, 48) : id;
    }

    /// <summary>简介压成一句话（首页卡片那行）。</summary>
    public static string ToTagline(string description, int max = 60)
    {
        var text = Regex.Replace((description ?? "").Trim(), "\\s+", " ");
        if (text.Length == 0) return "";
        if (text.Length <= max) return text;
        var cut = text.Substring(0, max);
        cut = Regex.Replace(cut, @"[,;:，；：、\s]+\S*$", "");
        return cut + "…";
    }

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var digits = unit == 0 || value >= 100 ? 0 : 1;
        return value.ToString(digits == 0 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static string DetectOs(string fileName)
    {
        var n = fileName.ToLowerInvariant();
        if (Regex.IsMatch(n, @"\.(exe|msi)$") || Regex.IsMatch(n, @"(^|[-_.])win(32|64|dows)?([-_.]|$)")) return "Windows";
        if (Regex.IsMatch(n, @"\.(dmg|pkg)$") || Regex.IsMatch(n, @"(macos|darwin|osx|mac[-_]?os|[-_.]mac([-_.]|$))")) return "macOS";
        if (Regex.IsMatch(n, @"\.(deb|rpm|appimage|snap|flatpak)$") || n.Contains("linux")) return "Linux";
        if (Regex.IsMatch(n, @"\.(apk|aab)$") || n.Contains("android")) return "Android";
        if (Regex.IsMatch(n, @"\.ipa$") || Regex.IsMatch(n, @"(^|[-_.])ios([-_.]|$)")) return "iOS";
        return "";
    }

    /// <summary>从文件名猜「平台 / 架构」，例如 PowerToysSetup-0.101-x64.exe → Windows x64 安装版。</summary>
    public static string GuessPlatform(string fileName)
    {
        var n = (fileName ?? "").ToLowerInvariant();
        var os = DetectOs(n);

        var arch = Regex.IsMatch(n, "(arm64|aarch64|apple[-_]?silicon)") ? (os == "macOS" ? "Apple 芯片" : "ARM64")
            : Regex.IsMatch(n, @"(^|[-_.])arm(v7|hf|32)?([-_.]|$)") ? "ARM32"
            : Regex.IsMatch(n, "(x64|amd64|x86[-_]64)") ? (os == "macOS" ? "Intel" : "x64")
            : Regex.IsMatch(n, "(x86|ia32|i386|i686)") ? "x86"
            : n.Contains("universal") ? "通用"
            : "";

        var portable = Regex.IsMatch(n, "(portable|green|no[-_]?install)");
        var kind =
            Regex.IsMatch(n, @"\.deb$") ? ".deb"
            : Regex.IsMatch(n, @"\.rpm$") ? ".rpm"
            : Regex.IsMatch(n, @"\.appimage$") ? "AppImage"
            : Regex.IsMatch(n, @"\.snap$") ? "Snap"
            : Regex.IsMatch(n, @"\.dmg$") ? "磁盘映像"
            : Regex.IsMatch(n, @"\.pkg$") ? "安装包"
            : Regex.IsMatch(n, @"\.apk$") ? "APK"
            : Regex.IsMatch(n, @"\.ipa$") ? "IPA"
            : Regex.IsMatch(n, @"\.msi$") ? "MSI 安装版"
            : Regex.IsMatch(n, @"\.exe$") ? (portable ? "便携版" : "安装版")
            : Regex.IsMatch(n, @"\.(zip|7z|rar|tar\.gz|tgz)$") ? (portable ? "便携版" : "压缩包")
            : Regex.Match(n, @"\.([a-z0-9]+)$").Groups[1].Value.ToUpperInvariant();

        string[] parenKinds = { ".deb", ".rpm", "AppImage", "Snap", "APK", "IPA", "磁盘映像", "安装包", "MSI 安装版" };
        var parenKind = parenKinds.Contains(kind);

        if (os == "macOS")
        {
            var macArch = arch == "通用" ? "" : arch;
            var label = macArch.Length > 0 ? $"macOS（{macArch}）" : "macOS";
            if (kind.Length > 0 && kind != "压缩包") label += parenKind ? $"（{kind}）" : $" {kind}";
            return label;
        }

        var head = string.Join(" ", new[] { os.Length > 0 ? os : "其他", arch }.Where(item => item.Length > 0));
        if (kind.Length > 0) head += parenKind ? $"（{kind}）" : $" {kind}";
        return head;
    }

    /// <summary>从全部文件名里归纳「支持系统」。</summary>
    public static string GuessSystem(IEnumerable<string> assetNames)
    {
        string[] order = { "Windows", "macOS", "Linux", "Android", "iOS" };
        var found = new HashSet<string>();
        foreach (var name in assetNames)
        {
            var os = DetectOs(name);
            if (os.Length > 0) found.Add(os);
        }
        if (found.Count == 0) return "";
        return string.Join(" / ", order.Where(found.Contains));
    }

    // ══════════ 请求（带镜像回退） ══════════

    private static List<(string Label, string Prefix)> OrderedBases()
    {
        var proxy = ApiBases[0];
        var rest = ApiBases.Skip(1).ToList();
        try
        {
            if (File.Exists(BaseCacheFile))
            {
                var preferred = File.ReadAllText(BaseCacheFile).Trim();
                var hit = rest.FirstOrDefault(baseItem => baseItem.Label == preferred);
                if (hit.Label is not null)
                    return new List<(string, string)> { proxy, hit }.Concat(rest.Where(item => item.Label != hit.Label)).ToList();
            }
        }
        catch { /* 读不到就用默认顺序 */ }
        return ApiBases.ToList();
    }

    private static void RememberBase(string label)
    {
        try
        {
            Directory.CreateDirectory(Core.AppPaths.DataDir);
            File.WriteAllText(BaseCacheFile, label);
        }
        catch { /* 记不上不影响使用 */ }
    }

    private static void ForgetBase()
    {
        try { if (File.Exists(BaseCacheFile)) File.Delete(BaseCacheFile); }
        catch { /* 忽略 */ }
    }

    private static async Task<(JsonElement Data, string Via)> FetchJsonAsync(string path, CancellationToken token)
    {
        GithubImportException? lastError = null;
        var rateLimited = false;

        foreach (var (label, prefix) in OrderedBases())
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(RequestTimeoutMs);
            try
            {
                using var response = await Http.GetAsync(prefix + path, cts.Token);
                var status = (int)response.StatusCode;

                if (status == 404)
                    throw new GithubImportException(GithubImportErrorKind.NotFound, $"HTTP 404 ({label})", 404);

                var remaining = response.Headers.TryGetValues("x-ratelimit-remaining", out var values) ? values.FirstOrDefault() : null;
                if ((status == 403 || status == 429) && remaining == "0")
                {
                    rateLimited = true;
                    lastError = new GithubImportException(GithubImportErrorKind.RateLimit, $"HTTP {status} ({label})", status);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new GithubImportException(GithubImportErrorKind.Http, $"HTTP {status} ({label})", status);

                var text = await response.Content.ReadAsStringAsync(cts.Token);
                using var document = JsonDocument.Parse(text);
                var clone = document.RootElement.Clone();
                RememberBase(label);
                return (clone, label);
            }
            catch (GithubImportException exception)
            {
                if (exception.Kind == GithubImportErrorKind.NotFound) throw;
                lastError = exception;
            }
            catch (Exception exception)
            {
                lastError = new GithubImportException(GithubImportErrorKind.Network, exception.Message);
            }
        }

        ForgetBase();
        if (rateLimited)
            throw new GithubImportException(GithubImportErrorKind.RateLimit, lastError?.Message ?? "rate limited", 403);
        throw lastError ?? new GithubImportException(GithubImportErrorKind.Network, "所有镜像入口均连接失败");
    }

    private static string Str(JsonElement element, string key)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool Bool(JsonElement element, string key)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;

    private static long Num(JsonElement element, string key)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;

    /// <summary>明显不是给人下载安装包的文件：调试符号、校验值、源码、策略模板……</summary>
    private static readonly Regex[] JunkPatterns =
    {
        new(@"\.(blockmap|sha1|sha256|sha512|md5|sig|asc|pem|pdb|txt|json|yml|yaml|md)$", RegexOptions.IgnoreCase),
        new(@"(^|[-_.])(symbols?|debug|pdb)([-_.]|$)", RegexOptions.IgnoreCase),
        new(@"(^|[-_.])(sources?|src)([-_.]|\.zip$)", RegexOptions.IgnoreCase),
        new(@"group-?policy", RegexOptions.IgnoreCase),
        new(@"(^|[-_.])gpo([-_.]|$)", RegexOptions.IgnoreCase),
    };

    private static bool IsJunkAsset(string name) => JunkPatterns.Any(pattern => pattern.IsMatch(name));

    // ══════════ 主流程 ══════════

    public static async Task<GithubImportResult> FetchAsync(string rawInput, bool includePrerelease,
        int maxDownloads = 12, CancellationToken token = default)
    {
        var reference = ParseRepoInput(rawInput)
            ?? throw new GithubImportException(GithubImportErrorKind.Invalid, rawInput);

        var (owner, repo) = reference;

        var repoResult = await FetchJsonAsync($"/repos/{owner}/{repo}", token);
        var raw = repoResult.Data;
        var info = new GithubRepoInfo
        {
            Owner = owner,
            Repo = Str(raw, "name").Length > 0 ? Str(raw, "name") : repo,
            FullName = Str(raw, "full_name").Length > 0 ? Str(raw, "full_name") : $"{owner}/{repo}",
            HtmlUrl = Str(raw, "html_url").Length > 0 ? Str(raw, "html_url") : $"https://github.com/{owner}/{repo}",
            Description = Str(raw, "description").Trim(),
            Homepage = Str(raw, "homepage").Trim(),
            Archived = Bool(raw, "archived"),
        };
        if (raw.TryGetProperty("owner", out var ownerNode))
        {
            info.Owner = Str(ownerNode, "login").Length > 0 ? Str(ownerNode, "login") : owner;
            info.OwnerAvatar = Str(ownerNode, "avatar_url");
        }
        if (info.OwnerAvatar.Length == 0) info.OwnerAvatar = $"https://github.com/{owner}.png";
        if (raw.TryGetProperty("license", out var licenseNode) && licenseNode.ValueKind == JsonValueKind.Object)
            info.License = Str(licenseNode, "spdx_id").Length > 0 ? Str(licenseNode, "spdx_id") : Str(licenseNode, "name");

        // ── 版本信息（拿不到不算失败，退化成「只填仓库信息」）──
        var releases = new List<JsonElement>();
        var releaseFailed = false;
        var via = repoResult.Via;
        try
        {
            var releaseResult = await FetchJsonAsync($"/repos/{owner}/{repo}/releases?per_page=10", token);
            if (releaseResult.Data.ValueKind == JsonValueKind.Array)
                releases.AddRange(releaseResult.Data.EnumerateArray().Select(item => item.Clone()));
            via = releaseResult.Via;
        }
        catch (GithubImportException exception)
        {
            if (exception.Kind == GithubImportErrorKind.RateLimit) throw;
            releaseFailed = true;
        }

        var usable = releases.Where(item => !Bool(item, "draft")).ToList();
        var stable = usable.FirstOrDefault(item => !Bool(item, "prerelease"));
        var newest = usable.FirstOrDefault();
        var chosen = includePrerelease
            ? (newest.ValueKind == JsonValueKind.Object ? newest : stable)
            : (stable.ValueKind == JsonValueKind.Object ? stable : newest);

        var hasChosen = chosen.ValueKind == JsonValueKind.Object;
        var noRelease = !hasChosen;
        var usedPrerelease = hasChosen && Bool(chosen, "prerelease");

        // 没勾「包含预发布」时：如果最新那条其实是更新的预发布版，记下来给界面提示
        var chosenTag = hasChosen ? Str(chosen, "tag_name").Trim() : "";
        var newerPrereleaseTag = !usedPrerelease && newest.ValueKind == JsonValueKind.Object && Bool(newest, "prerelease")
                                 && Str(newest, "tag_name").Trim() != chosenTag
            ? Str(newest, "tag_name").Trim()
            : "";

        var releaseInfo = hasChosen
            ? new GithubReleaseInfo
            {
                TagName = Str(chosen, "tag_name").Trim(),
                Title = Str(chosen, "name").Trim(),
                Prerelease = Bool(chosen, "prerelease"),
                PublishedAt = Str(chosen, "published_at").Trim(),
                HtmlUrl = Str(chosen, "html_url").Length > 0 ? Str(chosen, "html_url") : $"{info.HtmlUrl}/releases",
            }
            : null;

        // ── 安装包列表 ──
        var rawAssets = new List<(string Name, long Size, string Url)>();
        if (hasChosen && chosen.TryGetProperty("assets", out var assetsNode) && assetsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsNode.EnumerateArray())
            {
                var url = Str(asset, "browser_download_url");
                if (url.Length == 0) continue;
                var name = Str(asset, "name").Trim();
                rawAssets.Add((name.Length > 0 ? name : "未命名文件", Num(asset, "size"), url));
            }
        }

        var kept = rawAssets.Where(asset => !IsJunkAsset(asset.Name)).ToList();
        var finalAssets = kept.Count > 0 ? kept : rawAssets;   // 全被判成垃圾文件时宁可全留着
        var limited = finalAssets.Take(maxDownloads).ToList();

        var downloads = limited.Select(asset => new GithubDownloadItem
        {
            Platform = GuessPlatform(asset.Name),
            Note = asset.Name,
            Size = FormatSize(asset.Size),
            Url = asset.Url,
        }).ToList();

        return new GithubImportResult
        {
            Repo = info,
            Release = releaseInfo,
            Downloads = downloads,
            System = GuessSystem(finalAssets.Select(asset => asset.Name)),
            Via = via,
            Facts = new GithubImportFacts
            {
                UsedPrerelease = usedPrerelease,
                NoRelease = noRelease,
                ReleaseFailed = releaseFailed,
                NoAsset = !noRelease && downloads.Count == 0,
                AssetTotal = rawAssets.Count,
                AssetSkipped = rawAssets.Count - finalAssets.Count,
                Truncated = finalAssets.Count > limited.Count,
                NewerPrereleaseTag = newerPrereleaseTag,
            },
        };
    }
}
