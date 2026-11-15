using System;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>一轮问答为什么停下来（2026-10-30，按时长限制取代轮次限制时抽出）。</summary>
public enum AiStopReason
{
    /// <summary>不需要停，继续跑下一轮。</summary>
    None = 0,

    /// <summary>跑满了时长预算（<see cref="AiTimeBudget.Default"/>）。</summary>
    OutOfTime,

    /// <summary>撞上轮次兜底（防"工具秒失败→秒重试"的空转），不是体验上限。</summary>
    RoundGuard,

    /// <summary>单次请求超时（网络/服务商没响应），远没到预算。</summary>
    RequestTimeout,
}

/// <summary>
/// AI 一轮问答的时间账（2026-10-30）。
///
/// <para>
/// 为什么单独抽出来：<c>AiChatService</c> 依赖 App / 网络 / 工具，测试工程里跑不起来，
/// 但"什么时候该停、停下来算什么原因"是最容易写错、也最影响用户观感的一段判断
/// （第一次写就踩了：把 120 秒的 HTTP 超时当成了"8 分钟预算用尽"，会骗用户）。
/// 把它做成纯函数就能在 E 盘那份测试里直接跑。
/// </para>
/// </summary>
public static class AiTimeBudget
{
    /// <summary>一轮问答（含全部工具往返）的默认时长上限：8 分钟。</summary>
    public static readonly TimeSpan Default = TimeSpan.FromMinutes(8);

    /// <summary>
    /// 轮次兜底。⚠️ 不是体验上限：正常链路离它很远，只有"工具立刻失败→模型立刻重试"
    /// 那种秒级死循环才撞得到（8 分钟能跑上千轮，纯烧 token）。
    /// </summary>
    public const int RoundGuard = 240;

    /// <summary>
    /// 判定"这次取消是预算到点还是单次请求超时"的容差。
    /// <para>
    /// 依据：单次请求的 HTTP 超时是 120 秒（<c>AiClient</c> 里那个 <c>HttpClient.Timeout</c>），
    /// 攒到 8 分钟至少要 4 次连续超时，中途还会经过每轮的耗时检查 ——
    /// 所以"离预算还差 20 秒以上"就一定是请求超时，不是预算。
    /// </para>
    /// </summary>
    public static readonly TimeSpan Slack = TimeSpan.FromSeconds(20);

    /// <summary>每轮开始前问一次：还该继续吗？</summary>
    public static AiStopReason CheckBeforeRound(TimeSpan elapsed, int round)
        => CheckBeforeRound(elapsed, round, Default, RoundGuard);

    /// <summary>同上，参数可换（测试用）。先判超时、再判轮次 —— 超时的提示对用户更有用。</summary>
    public static AiStopReason CheckBeforeRound(TimeSpan elapsed, int round, TimeSpan budget, int guard)
    {
        if (budget > TimeSpan.Zero && elapsed >= budget) return AiStopReason.OutOfTime;
        if (guard > 0 && round >= guard) return AiStopReason.RoundGuard;
        return AiStopReason.None;
    }

    /// <summary>
    /// 抓到 <see cref="OperationCanceledException"/> 之后问一句：这是用户按的停止，还是我们掐的？
    /// <para>
    /// ⚠️ 三种来源都抛同一种异常，只能靠这两个线索区分：
    /// 用户令牌有没有被取消（<paramref name="userCancelled"/>）、已耗时离预算有多远。
    /// 判错的后果是给用户一句错误的解释（"跑到 8 分钟上限"实际只跑了 2 分钟），比不说更糟。
    /// </para>
    /// </summary>
    /// <returns>
    /// <see cref="AiStopReason.None"/> = 用户取消（调用方原样往外抛，由上层保留半截内容）；
    /// 其余两种 = 宿主自己掐的，必须给用户一句说明。
    /// </returns>
    public static AiStopReason ClassifyCancel(bool userCancelled, TimeSpan elapsed)
        => ClassifyCancel(userCancelled, elapsed, Default, Slack);

    /// <inheritdoc cref="ClassifyCancel(bool, TimeSpan)"/>
    public static AiStopReason ClassifyCancel(bool userCancelled, TimeSpan elapsed, TimeSpan budget, TimeSpan slack)
    {
        if (userCancelled) return AiStopReason.None;
        if (budget <= TimeSpan.Zero) return AiStopReason.RequestTimeout;

        return elapsed + slack >= budget ? AiStopReason.OutOfTime : AiStopReason.RequestTimeout;
    }

    /// <summary>停下来之后给用户看的那句话（口语化会让界面变质，这里一律书面语）。</summary>
    public static string Describe(AiStopReason reason, TimeSpan budget)
    {
        var minutes = (int)Math.Max(1, Math.Round(budget.TotalMinutes));

        return reason switch
        {
            AiStopReason.OutOfTime =>
                $"\n\n（这一轮已经跑到 {minutes} 分钟上限，先停在这里。上面是已经查到的部分；" +
                "尚未完成的话，再追问一次即可接着做。）",

            AiStopReason.RoundGuard =>
                "\n\n（这一轮反复调用工具已达上限，先停在这里。上面是已经查到的部分；" +
                "若明显还有没查完的内容，请把问题范围缩小后重新提问。）",

            AiStopReason.RequestTimeout =>
                "\n\n（模型服务在本次请求上超时未响应，先停在这里。可稍后重试；若反复出现，" +
                "请检查网络或代理设置，或在设置中更换模型。）",

            _ => "",
        };
    }
}
