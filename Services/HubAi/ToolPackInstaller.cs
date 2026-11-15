using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.HubAi;

/// <summary>安装结果。<see cref="Message"/> 直接拿去显示，已经是书面语。</summary>
public sealed record ToolPackInstallResult(bool Ok, string Message, ToolPack? Pack);

/// <summary>
/// 工具包的远程安装（2026-10-18）。
///
/// <para>
/// 走的是**和软件下载同一条线路**：<see cref="GithubRoute.ResolveAsync"/> 会按用户的设置生成
/// 「GitHub 源 / 自建加速」多路候选，这里逐个试，第一个成功的即采用。
/// ⛔ 别自己拼加速前缀 —— 旧方案（2026-10 之前）那样做过，已经作废。
/// </para>
/// <para>
/// ⚠️ 下载下来的是 <b>JSON 声明</b>，不是代码。加载前必须过 <see cref="ToolPackStore.Parse"/>
/// 的校验（格式版本、必填字段），校验不过就当没装。
/// </para>
/// </summary>
public static class ToolPackInstaller
{
    /// <summary>包体上限。正常只有几 KB，超过这个数说明拿到的不是工具包（可能是网页或错误页）。</summary>
    private const int MaxBytes = 512 * 1024;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    public static async Task<ToolPackInstallResult> InstallAsync(CancellationToken ct = default)
    {
        AppLog.Begin("hubai", "下载 AI 工具包");

        IReadOnlyList<DownloadCandidate> candidates;
        try
        {
            candidates = await GithubRoute.ResolveAsync(ShellConfig.HubAiToolPackUrl, ct);
        }
        catch (Exception ex)
        {
            AppLog.Fail("hubai", ex, "生成工具包下载线路");
            return new ToolPackInstallResult(false, "无法生成下载线路，请检查网络设置。", null);
        }

        if (candidates.Count == 0)
            return new ToolPackInstallResult(false, "没有可用的下载线路。", null);

        var failures = 0;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var text = await TryFetchAsync(candidate, ct);
                if (string.IsNullOrWhiteSpace(text))
                {
                    failures++;
                    continue;
                }

                if (text.Length > MaxBytes)
                {
                    AppLog.Warn("hubai", $"工具包内容过大（{text.Length} 字符），已拒绝");
                    failures++;
                    continue;
                }

                var pack = ToolPackStore.Parse(text);
                if (pack is null)
                {
                    // 常见于线路返回了 404 网页或登录页：不是网络问题，是内容不对，换下一条
                    AppLog.Warn("hubai", $"线路 {candidate.Url} 返回的内容不是有效的工具包");
                    failures++;
                    continue;
                }

                if (!ToolPackStore.Save(text))
                    return new ToolPackInstallResult(false, "下载成功但保存失败，详情见日志。", null);

                AppLog.Done("hubai", $"工具包已安装 v{pack.Version}（{pack.Tools.Count} 个工具）");
                return new ToolPackInstallResult(
                    true,
                    $"已安装 {pack.Tools.Count} 个工具（v{pack.Version}）。",
                    pack);
            }
            catch (OperationCanceledException ex)
            {
                // ⚠️ 同一个异常类型要分两种情况：用户主动取消 → 往上抛（调用方要收摊）；
                //    HttpClient.Timeout 触发的超时 → 记一条、换下一条线路。
                if (ct.IsCancellationRequested) throw;

                AppLog.Exception("hubai", ex, $"下载工具包超时 {candidate.Url}");
                failures++;
            }
            catch (HttpRequestException ex)
            {
                AppLog.Exception("hubai", ex, $"下载工具包 {candidate.Url}");
                failures++;
            }
            catch (Exception ex)
            {
                AppLog.Exception("hubai", ex, $"下载工具包 {candidate.Url}");
                failures++;
            }
        }

        AppLog.Fail("hubai", $"所有线路都失败（{failures} 条）");
        return new ToolPackInstallResult(
            false,
            "所有线路都未能获取工具包，请稍后重试或检查网络。",
            null);
    }

    private static async Task<string?> TryFetchAsync(DownloadCandidate candidate, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, candidate.Url);

        // 自建加速节点的校验依赖这两个头（和软件下载保持一致）
        if (!string.IsNullOrWhiteSpace(candidate.Referer))
            request.Headers.Referrer = new Uri(candidate.Referer);

        if (!string.IsNullOrWhiteSpace(candidate.ApiKey))
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + candidate.ApiKey);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            AppLog.Debug("hubai", $"线路 {candidate.Url} 返回 HTTP {(int)response.StatusCode}");
            return null;
        }

        return await response.Content.ReadAsStringAsync(ct);
    }
}
