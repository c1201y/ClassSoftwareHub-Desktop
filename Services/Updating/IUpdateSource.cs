using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services.Updating;

/// <summary>
/// 更新源：能列出某个通道下的发布列表。
/// 现在的实现是 GitHub Releases（Latest = 正式版，Pre-release = 预览版）；
/// 以后想换成网盘静态清单（自己写 manifest.json），只要再实现一个这个接口即可，
/// 上层（UpdateService / 设置页）一行都不用改。
/// </summary>
public interface IUpdateSource
{
    /// <summary>源的名字（界面上用来展示，比如 "GitHub"）。</summary>
    string DisplayName { get; }

    /// <summary>这个源现在能不能用（比如没配仓库地址就先说不可用，避免无谓的网络请求）。</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// 列出该通道下最新的若干条发布（新的在前）。
    /// Stable = 只取非预发布；Insider = 取预发布（同时也保留正式版，方便预览版用户跟上正式版）。
    /// </summary>
    Task<IReadOnlyList<UpdateRelease>> GetReleasesAsync(UpdateChannel channel, int max, CancellationToken ct = default);
}
