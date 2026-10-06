using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 提交服务（自建 Cloudflare Worker）的入口清单，以及「记住上次成功的那个」的选路逻辑。
///
/// 投稿（<c>/api/submit</c>）和投稿页的 OSS 直传签名（<c>/api/oss-sign</c>）挂在同一个 Worker 上，
/// 所以入口只在这里写一份 —— 两处各留一份的话，迟早会改一处忘一处
/// （网页端对应 <c>src/gallery/submitEndpoints.ts</c>，同一套设计）。
///
/// 为什么要两个入口、还要记路：.workers.dev 域名在国内打不开，面向访客的接口必须走自定义域；
/// 而自定义域哪天没生效或临时抽风时得能自动换下一条。所以按顺序试，第一个拿到业务 JSON 响应的就停手，
/// 并把成功的那个记进本机文件，下次直接先试它，省掉一次必然失败的等待。
/// </summary>
public static class SubmitEndpoint
{
    /// <summary>入口按顺序试：先 CDN 新域，再 CF 直连（与网页版一致）。</summary>
    public static readonly string[] All =
    {
        "https://cshapi.132614.xyz",
        "https://submit.132614.xyz",
    };

    /// <summary>单个入口的超时时间：连不上时尽快换下一个入口，不让用户干等（两个入口最坏 20 秒）。</summary>
    public const int TimeoutMs = 10000;

    private static string FilePath => Path.Combine(Core.AppPaths.DataDir, "submit-endpoint.txt");

    /// <summary>按顺序返回要试的入口，上次成功过的排最前。</summary>
    public static List<string> Ordered()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var remembered = File.ReadAllText(FilePath).Trim();
                if (All.Contains(remembered))
                    return new List<string> { remembered }
                        .Concat(All.Where(item => item != remembered)).ToList();
            }
        }
        catch { /* 读不到就用默认顺序 */ }
        return All.ToList();
    }

    /// <summary>记住这次成功的入口（写不进去不影响功能）。</summary>
    public static void Remember(string baseUrl)
    {
        try
        {
            Directory.CreateDirectory(Core.AppPaths.DataDir);
            File.WriteAllText(FilePath, baseUrl);
        }
        catch { /* 记不上不影响提交 */ }
    }
}
