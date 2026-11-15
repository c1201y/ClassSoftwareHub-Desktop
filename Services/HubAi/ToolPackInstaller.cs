using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.HubAi;


public sealed record ToolPackInstallResult(bool Ok, string Message, ToolPack? Pack);














public static class ToolPackInstaller
{
    
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
