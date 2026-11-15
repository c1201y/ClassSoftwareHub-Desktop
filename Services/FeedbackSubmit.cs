using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;





























public static class FeedbackSubmit
{
    
    public const string Route = "/api/feedback";

    
    public sealed record Result(bool Ok, string Message, string? Url);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    
    
    
    
    
    public static async Task<Result> SubmitAsync(
        string kind, string subKind, string appId,
        string title, string detail, string contact,
        CancellationToken ct = default)
    {
        
        
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Core.AppLog.Begin("submit", $"提交反馈 · 类型={kind}/{subKind} · 标题「{title}」");

        if (title.Trim().Length == 0 || detail.Trim().Length == 0)
        {
            Core.AppLog.Warn("submit", "标题或正文为空，本次没有发出去");
            return new Result(false, "内容不完整，无法提交。", null);
        }

        var payload = JsonSerializer.Serialize(
            new { kind, subKind, appId, title, detail, contact }, JsonOpts);

        var reply = await SubmitClient
            .PostAsync(Route, payload, "已提交，维护者会尽快处理。", ct)
            .ConfigureAwait(false);

        if (reply is null)
        {
            Core.AppLog.Fail("submit", "提交反馈失败：所有入口都不通（网络或地区限制）");
            return new Result(false, "连不上提交服务（网络或地区限制），可改用旁边的方式提交。", null);
        }

        if (reply.Ok) Core.AppLog.Done("submit", $"反馈已提交 · 服务端回应「{reply.Message}」", sw.Elapsed);
        else Core.AppLog.Fail("submit", $"服务端没有接受这条反馈：{reply.Message}");

        return new Result(reply.Ok, reply.Message, reply.Url);
    }
}
