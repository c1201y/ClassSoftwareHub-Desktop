namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 自建后端（Cloudflare Worker <c>classhub</c>）的入口清单 —— **唯一的定义处**。
///
/// <para>
/// 为什么单独一个文件：这份清单被两处消费 —— 喊话 / 投稿 / 回声洞 / 反馈走
/// <see cref="ShellConfig.OrderedSubmitEndpoints"/>，投稿页与 OSS 直传走
/// <see cref="Services.SubmitEndpoint.Ordered"/>。两边**各抄一份**的时候就出现过"改一处忘一处"
/// 的隐患（2026-10-26 核对时两处都在，只是内容暂时一样）。现在两个入口都指向这里，
/// 用例 <c>ServiceEndpointsTests</c> 再把内容钉住，就不会再漂。
/// </para>
/// <para>
/// 顺序即优先级：按顺序试，第一个拿到业务 JSON 响应的就停手，并把成功的那个记进本机文件
/// （<see cref="ShellConfig.SubmitEndpointFile"/>），下次直接先试它。
/// </para>
/// </summary>
public static class ServiceEndpoints
{
    /// <summary>
    /// 入口按顺序试。
    ///
    /// <para>
    /// 2026-10-27 重排（Nick 反馈「有用户说服务器连不上」后实测）：
    /// <c>cshapi.132614.xyz</c> 走的是第三方中转（DNS 里 CNAME 到 <c>*.world.speedonline.xyz</c>），
    /// 那家中转的订单已到期，整条域名现在返回 **512「订单到期暂停」** —— 与地区无关，所有人的第一条
    /// 入口都在白等。而 <c>submit.132614.xyz</c> 是直接解析到 Cloudflare 的，仍是活的。
    /// 所以把活的提到第一，挂了的降到最后两条里靠前的位置（它返回得快，代价小）。
    /// </para>
    ///
    /// <para>
    /// 第三条 <c>api.classsoftwarehub.cn</c> 是 2026-10-26 补的直连域名，但至今**没有任何 A 记录**
    /// （实测 DoH 查询为空），放第一会每次白等一次 DNS 超时，所以固定排在最后。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 顺序不是唯一防线：每轮都会按「上次成功优先」重排（见 <c>ShellConfig.OrderedSubmitEndpoints</c>），
    /// 一条入口只要成功过一次，之后的轮询就直接先试它。
    /// </para>
    /// </summary>
    public static readonly string[] All =
    {
        "https://submit.132614.xyz",
        "https://cshapi.132614.xyz",
        "https://api.classsoftwarehub.cn",
    };
}
