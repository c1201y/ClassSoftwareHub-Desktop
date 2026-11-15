using System;

namespace ClassSoftwareHub.Desktop.Core;


public enum AiStopReason
{
    
    None = 0,

    
    OutOfTime,

    
    RoundGuard,

    
    RequestTimeout,
}











public static class AiTimeBudget
{
    
    public static readonly TimeSpan Default = TimeSpan.FromMinutes(8);

    
    
    
    
    public const int RoundGuard = 240;

    
    
    
    
    
    
    
    
    public static readonly TimeSpan Slack = TimeSpan.FromSeconds(20);

    
    public static AiStopReason CheckBeforeRound(TimeSpan elapsed, int round)
        => CheckBeforeRound(elapsed, round, Default, RoundGuard);

    
    public static AiStopReason CheckBeforeRound(TimeSpan elapsed, int round, TimeSpan budget, int guard)
    {
        if (budget > TimeSpan.Zero && elapsed >= budget) return AiStopReason.OutOfTime;
        if (guard > 0 && round >= guard) return AiStopReason.RoundGuard;
        return AiStopReason.None;
    }

    
    
    
    
    
    
    
    
    
    
    
    
    public static AiStopReason ClassifyCancel(bool userCancelled, TimeSpan elapsed)
        => ClassifyCancel(userCancelled, elapsed, Default, Slack);

    
    public static AiStopReason ClassifyCancel(bool userCancelled, TimeSpan elapsed, TimeSpan budget, TimeSpan slack)
    {
        if (userCancelled) return AiStopReason.None;
        if (budget <= TimeSpan.Zero) return AiStopReason.RequestTimeout;

        return elapsed + slack >= budget ? AiStopReason.OutOfTime : AiStopReason.RequestTimeout;
    }

    
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
