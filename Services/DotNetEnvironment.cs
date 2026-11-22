using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>装的东西有四种，一个通道里各占一行。</summary>
public enum DotNetKind
{
    Runtime,
    AspNetCore,
    Desktop,
    Sdk,
}

/// <summary>通道里的一个组件：最新版本、下载地址、本机已装的版本。</summary>
public sealed class DotNetComponent
{
    public DotNetKind Kind { get; init; }
    public string Channel { get; init; } = "";
    public string Label { get; init; } = "";
    public string LatestVersion { get; init; } = "";
    public string Url { get; init; } = "";
    public string Sha512 { get; init; } = "";
    public string InstallerName { get; init; } = "";
    public IReadOnlyList<string> Installed { get; private set; } = Array.Empty<string>();

    /// <summary>重新扫描后回填本通道已装的版本。</summary>
    public void SetInstalled(IReadOnlyList<string> versions) => Installed = versions;

    /// <summary>本通道里本机装得最高的那个版本，没有则为 null。</summary>
    public string? InstalledBest => Installed
        .OrderByDescending(v => DotNetEnvironment.ParseVersion(v))
        .FirstOrDefault();

    public bool InstalledAny => Installed.Count > 0;

    public bool UpToDate =>
        InstalledBest is { } best && DotNetEnvironment.ParseVersion(best) >= DotNetEnvironment.ParseVersion(LatestVersion);
}

/// <summary>一个 .NET 大版本（11.0 / 10.0 …）。</summary>
public sealed class DotNetChannel
{
    public string ChannelVersion { get; init; } = "";
    public string SupportPhase { get; init; } = "";
    public string EolDate { get; init; } = "";
    public List<DotNetComponent> Components { get; } = new();

    public string SupportText => SupportPhase switch
    {
        "active" => "正式支持",
        "lts" => "长期支持",
        "maintenance" => "维护期",
        "preview" or "rc" => "预览",
        "eol" => "已停止支持",
        _ => SupportPhase,
    };

    public string Title => $".NET {ChannelVersion}";

    public bool Expired => string.Equals(SupportPhase, "eol", StringComparison.OrdinalIgnoreCase);

    /// <summary>卡片副标题：正常通道写支持到哪天，旧通道写哪天停的支持。</summary>
    public string LifecycleText => (Expired, EolDate.Length > 0) switch
    {
        (true, true) => $"已停止支持 · {EolDate} 起不再更新",
        (true, false) => "已停止支持",
        (false, true) => $"{SupportText} · 支持至 {EolDate}",
        _ => SupportText,
    };
}

/// <summary>安装过程中的阶段。</summary>
public enum DotNetInstallPhase
{
    Downloading,
    Verifying,
    Installing,
    Done,
}

public sealed record DotNetInstallProgress(DotNetInstallPhase Phase, double Percent, string Detail);

/// <summary>
/// .NET 运行时的检测与补全。版本清单取微软官方发布元数据，
/// 安装包同样直链官方 CDN，下载后按元数据里的 SHA-512 校验再运行（2026-11-22）。
/// </summary>
public static class DotNetEnvironment
{
    private const string IndexUrl =
        "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json";

    private const string IndexUrlFallback =
        "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json";

    private static readonly HttpClient Http = CreateHttp();

    /// <summary>安装包放在这里，装完由系统自己落盘，这里只是中转。</summary>
    public static string TempDir => Path.Combine(Path.GetTempPath(), "ClassSoftwareHub", "dotnet");

    // ───────────────────────────── 检测 ─────────────────────────────

    /// <summary>本机 dotnet 根目录；优先 DOTNET_ROOT，其余按标准安装位置。</summary>
    private static IEnumerable<string> DotNetRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in new[]
                 {
                     Environment.GetEnvironmentVariable("DOTNET_ROOT"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (!Directory.Exists(candidate)) continue;
            if (!seen.Add(candidate!)) continue;
            yield return candidate!;
        }
    }

    /// <summary>某个共享框架或 SDK 在本机装到的版本（去重，按版本升序）。</summary>
    public static List<string> InstalledVersions(DotNetKind kind)
    {
        var sub = kind switch
        {
            DotNetKind.Runtime => Path.Combine("shared", "Microsoft.NETCore.App"),
            DotNetKind.AspNetCore => Path.Combine("shared", "Microsoft.AspNetCore.App"),
            DotNetKind.Desktop => Path.Combine("shared", "Microsoft.WindowsDesktop.App"),
            _ => "sdk",
        };

        var versions = new List<string>();
        foreach (var root in DotNetRoots())
        {
            var dir = Path.Combine(root, sub);
            if (!Directory.Exists(dir)) continue;

            try
            {
                foreach (var path in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(path);
                    if (name.Length == 0) continue;
                    if (!versions.Contains(name, StringComparer.OrdinalIgnoreCase)) versions.Add(name);
                }
            }
            catch
            {
                // 某个目录读不了就跳过，其余照常
            }
        }

        return versions.OrderBy(ParseVersion).ToList();
    }

    /// <summary>把安装目录里的版本回填到组件上。</summary>
    public static void RefreshInstalled(IEnumerable<DotNetChannel> channels)
    {
        foreach (var component in channels.SelectMany(c => c.Components))
        {
            var all = InstalledVersions(component.Kind);
            var prefix = component.Channel + ".";
            var mine = all.Where(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();

            component.SetInstalled(mine);
        }
    }

    // ───────────────────────────── 版本清单 ─────────────────────────────

    /// <summary>
    /// 拉取全部通道的清单，包括已停止支持的旧版本 ——
    /// 有些软件只认旧运行时，把 eol 通道藏起来等于逼用户自己去官网翻（2026-11-22）。
    /// </summary>
    public static async Task<List<DotNetChannel>> FetchChannelsAsync(CancellationToken ct = default)
    {
        var indexJson = await GetStringAsync(new[] { IndexUrl, IndexUrlFallback }, ct).ConfigureAwait(false);

        var channels = new List<DotNetChannel>();
        using (var doc = JsonDocument.Parse(indexJson))
        {
            foreach (var entry in doc.RootElement.GetProperty("releases-index").EnumerateArray())
            {
                var phase = Str(entry, "support-phase");
                var channel = Str(entry, "channel-version");
                if (!int.TryParse(channel.Split('.')[0], out var major) || major < 3) continue;

                var url = Str(entry, "releases.json");
                if (url.Length == 0) continue;

                channels.Add(new DotNetChannel
                {
                    ChannelVersion = channel,
                    SupportPhase = phase,
                    EolDate = Str(entry, "eol-date"),
                });
            }
        }

        // 每个通道的明细单独一个文件，一次并发拉三四个就够了。
        using var gate = new SemaphoreSlim(4);
        var tasks = channels.Select(async channel =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await FillChannelAsync(channel, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Core.AppLog.Warn("dotnet", $"读取 .NET {channel.ChannelVersion} 清单失败：{ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        channels = channels.Where(c => c.Components.Count > 0).ToList();
        foreach (var channel in channels) channel.Components.Sort((a, b) => a.Kind.CompareTo(b.Kind));
        return channels.OrderByDescending(c => ParseVersion(c.ChannelVersion + ".0")).ToList();
    }

    private static async Task FillChannelAsync(DotNetChannel channel, CancellationToken ct)
    {
        var url = $"https://builds.dotnet.microsoft.com/dotnet/release-metadata/{channel.ChannelVersion}/releases.json";
        var fallback = $"https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/{channel.ChannelVersion}/releases.json";
        var json = await GetStringAsync(new[] { url, fallback }, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("releases", out var releases)) return;
        if (releases.GetArrayLength() == 0) return;

        var latest = releases[0];

        Add(latest, "runtime", DotNetKind.Runtime, ".NET 运行时");
        Add(latest, "aspnetcore-runtime", DotNetKind.AspNetCore, "ASP.NET Core 运行时");
        Add(latest, "windowsdesktop", DotNetKind.Desktop, "Windows 桌面运行时");
        Add(latest, "sdk", DotNetKind.Sdk, "SDK");

        void Add(JsonElement release, string property, DotNetKind kind, string label)
        {
            if (!release.TryGetProperty(property, out var node)) return;

            var version = Str(node, "version");
            if (version.Length == 0) return;

            if (!node.TryGetProperty("files", out var files)) return;

            JsonElement? exe = null;
            foreach (var file in files.EnumerateArray())
            {
                var rid = Str(file, "rid");
                var name = Str(file, "name");
                if (!string.Equals(rid, "win-x64", StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                exe = file;
                break;
            }

            if (exe is null) return;

            var component = new DotNetComponent
            {
                Kind = kind,
                Channel = channel.ChannelVersion,
                Label = label,
                LatestVersion = version,
                Url = Str(exe.Value, "url"),
                Sha512 = Str(exe.Value, "hash"),
                InstallerName = Str(exe.Value, "name"),
            };

            component.SetInstalled(InstalledVersions(kind)
                .Where(v => v.StartsWith(channel.ChannelVersion + ".", StringComparison.OrdinalIgnoreCase))
                .ToList());

            channel.Components.Add(component);
        }
    }

    // ───────────────────────────── 安装 ─────────────────────────────

    /// <summary>
    /// 下载并安装一个组件。安装程序需要管理员权限，会弹一次系统提权确认。
    /// 返回给用户看的一句话结果。
    /// </summary>
    public static async Task<string> InstallAsync(
        DotNetComponent component,
        IProgress<DotNetInstallProgress>? progress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(component.Url))
            throw new InvalidOperationException("这个组件没有可用的下载地址。");

        Directory.CreateDirectory(TempDir);
        var fileName = component.InstallerName.Length > 0
            ? component.InstallerName
            : DownloadService.ResolveFileName(component.Url, null);
        var target = Path.Combine(TempDir, fileName);

        progress?.Report(new DotNetInstallProgress(DotNetInstallPhase.Downloading, 0, "正在下载安装包"));

        var downloadProgress = new Progress<DownloadProgress>(p =>
            progress?.Report(new DotNetInstallProgress(DotNetInstallPhase.Downloading, p.Percent, p.SizeText)));

        if (File.Exists(target)) TryDelete(target);

        var file = await DownloadService.DownloadAsync(
            component.Url,
            fileName,
            TempDir,
            downloadProgress,
            ct,
            DownloadRoute.Official).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        progress?.Report(new DotNetInstallProgress(DotNetInstallPhase.Verifying, 100, "正在校验文件"));

        if (component.Sha512.Length > 0 && !await VerifySha512Async(file.Path, component.Sha512, ct).ConfigureAwait(false))
            throw new InvalidOperationException("安装包校验失败，文件可能在下载过程中损坏，请重试。");

        progress?.Report(new DotNetInstallProgress(DotNetInstallPhase.Installing, 100, "正在安装"));

        var exitCode = await RunInstallerAsync(file.Path, ct).ConfigureAwait(false);

        progress?.Report(new DotNetInstallProgress(DotNetInstallPhase.Done, 100, "已完成"));

        return exitCode switch
        {
            0 => "安装完成。",
            3010 => "安装完成，需要重启电脑后生效。",
            1602 => "已取消安装。",
            _ => $"安装程序返回代码 {exitCode}，可能没有装成功。",
        };
    }

    private static async Task<bool> VerifySha512Async(string path, string expectedHex, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var hash = await SHA512.HashDataAsync(stream, ct).ConfigureAwait(false);
            var actual = Convert.ToHexString(hash);
            return string.Equals(actual, expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("dotnet", "校验安装包失败：" + ex.Message);
            return false;
        }
    }

    private static async Task<int> RunInstallerAsync(string installer, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(installer, "/install /quiet /norestart")
            {
                UseShellExecute = true,      // 走 shell 才能带 runas
                Verb = "runas",
            },
        };

        try
        {
            if (!process.Start()) throw new InvalidOperationException("安装程序没有启动。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("已取消系统提权，安装没有进行。");
        }

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    // ───────────────────────────── 工具 ─────────────────────────────

    /// <summary>把 "10.0.404" 这类字符串转成可比较的版本，解析不了按 0 处理。</summary>
    public static Version ParseVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new Version(0, 0);

        var clean = text.Trim();
        var dash = clean.IndexOf('-');
        if (dash > 0) clean = clean[..dash];      // 去掉 -rc.1 / -preview.2 之类的后缀

        return Version.TryParse(clean, out var version) ? version : new Version(0, 0);
    }

    private static string Str(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static async Task<string> GetStringAsync(IEnumerable<string> urls, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException("读取 .NET 版本清单失败：" + (last?.Message ?? "网络不可用"), last);
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ClassSoftwareHub/1.2");
        return client;
    }
}
