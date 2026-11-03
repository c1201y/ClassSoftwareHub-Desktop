using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.License;

/// <summary>
/// 给发往自建后端的请求统一补身份头、收取令牌、识别闸门拒绝。
///
/// <para>
/// 为什么做成 <see cref="DelegatingHandler"/> 而不是在每个调用点手动调：
/// 受保护的路由有五个（投稿 / 回声洞 / 反馈 / 上传签名 / 喊话），
/// 每个服务里还有"多入口轮询"的循环，逐处加必然漏。
/// 挂在 <see cref="HttpClient"/> 上就只有一处，而且以后新加接口自动带上。
/// </para>
///
/// <para>
/// ⚠️ 只对自建后端的域名生效（<see cref="ClientAuth.IsOwnBackend"/> 判的），
/// GitHub、词典、任意第三方都看不到这些头。
/// </para>
/// </summary>
public sealed class AuthHandler : DelegatingHandler
{
    /// <summary>用默认的 <see cref="HttpClientHandler"/> 当内层。</summary>
    public AuthHandler() : this(new HttpClientHandler()) { }

    public AuthHandler(HttpMessageHandler inner) : base(inner) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ClientAuth.Apply(request);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        ClientAuth.Capture(response);

        // ⚠️ 只在 403/401 时才把正文读出来判 code：
        //    正文是小 JSON，读一份再塞回去没有代价；而下载那条链路是**文件流**，
        //    无条件读会把几百兆灌进内存。下载走的是 /api/dl，不会 403 到这里。
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            try
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                response.Content = new StringContent(text, Encoding.UTF8, "application/json");

                if (ClientAuth.HasLicenseCode(text)) ClientAuth.RaiseActivationRequired();
            }
            catch
            {
                // 读不动就算了，让调用方按普通 403 处理
            }
        }

        return response;
    }
}
