using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 反馈中心的**免登录提交**：走「提交软件」「回声洞投稿」同一套自建服务
/// （Cloudflare Worker <c>classhub</c>，令牌在服务端），<c>POST {入口}/api/feedback</c>。
/// 提交成功 = 议题已经建好在 <see cref="Data.Feedback.RepoUrl"/> 里，不需要用户登录 GitHub。
///
/// ── 客户端 → 服务端的契约（2026-10-04 对着线上服务端实测反推出来的）──────────────
/// 请求体（JSON）：
/// <code>
/// {
///   "kind":    "report" | "suggestion",            // 反馈类型（必填）
///   "subKind": "interaction" | "link" | "other",   // 问题类型，仅 kind=report 时有值（必填）
///   "appId":   "diskgenius" | "",                  // 涉及的软件 id，空串 = 未指定（选填）
///   "title":   "一句话标题",                        // ⛔ 不含 [类型] 前缀 —— 服务端自己拼
///   "detail":  "详细描述",                          // ⛔ 不含模板 —— 服务端自己套引用块与表格
///   "contact": "age 密文" | ""                      // 选填，客户端已加密
/// }
/// </code>
/// 服务端自己生成 Issue 的标题（<c>[报告问题 · 逻辑交互] 标题</c>）、正文模板
/// （引用块 + 「类型」「涉及软件」表格 + 联系方式代码块）和标签 —— 所以这里只发**原始字段**，
/// 不要像 GitHub 链接那条路那样先拼好标题与正文，否则会套两层。
///
/// 响应（与 /api/submit、/api/echocave 同一套形状，客户端就是这么解析的）：
///   · 成功 → <c>{"success": true, "message": "反馈已提交！会尽快出现在公开议题列表里。"}</c>
///   · 失败 → <c>{"error": "人话原因"}</c>（实测如「请选择反馈类型」「请填写详细描述」）
/// ⚠️ 实测服务端**不回 url**，所以界面上不给「查看议题」按钮。
/// ⚠️ 服务端没这条路由 / 连不上时，<see cref="SubmitAsync"/> 返回 <c>Ok=false</c> + 人话，
///    界面据此把用户引到「在 GitHub 上提交」那条兜底路。
/// </summary>
public static class FeedbackSubmit
{
    /// <summary>服务端路由（与 /api/submit、/api/echocave 并列）。</summary>
    public const string Route = "/api/feedback";

    /// <summary>提交结果。<see cref="Url"/> 是服务端建好的议题地址（服务端可以不回）。</summary>
    public sealed record Result(bool Ok, string Message, string? Url);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // 正文里有中文、也有 <、>、&（引用块与表格），中文不转义更好读，HTML 字符照常转义
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 把一条反馈提交到服务端。**不抛异常**（取消失常除外）。
    /// 六个字段全部原样取自 <see cref="Data.Feedback.Draft"/> —— 标题不加前缀、正文不套模板，
    /// 那是服务端的活儿。
    /// </summary>
    public static async Task<Result> SubmitAsync(
        string kind, string subKind, string appId,
        string title, string detail, string contact,
        CancellationToken ct = default)
    {
        if (title.Trim().Length == 0 || detail.Trim().Length == 0)
            return new Result(false, "内容不完整，无法提交。", null);

        var payload = JsonSerializer.Serialize(
            new { kind, subKind, appId, title, detail, contact }, JsonOpts);

        var reply = await SubmitClient
            .PostAsync(Route, payload, "已提交，维护者会尽快处理。", ct)
            .ConfigureAwait(false);

        return reply is null
            ? new Result(false, "连不上提交服务（网络或地区限制），可改用旁边的方式提交。", null)
            : new Result(reply.Ok, reply.Message, reply.Url);
    }
}
