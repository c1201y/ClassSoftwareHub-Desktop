using System;
using System.Collections.Generic;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>一项系统功能的控件形态。</summary>
public enum SysFeatureKind
{
    /// <summary>开关（值只有两态）。</summary>
    Toggle,

    /// <summary>下拉框（值有多档）。</summary>
    Choice,
}

/// <summary>下拉框的一档。<paramref name="Value"/> 是写进系统的实际值。</summary>
public sealed record SysChoice(string Label, int Value);

/// <summary>
/// 一条系统功能定义（2026-10-31，需求清单第 20 条重做）。
///
/// <para>
/// 与「系统优化」页的 <c>TweakDef</c> 是两套东西，别混：
/// <list type="bullet">
///   <item>那一页全是 <b>HKCU</b> 的界面设置，不提权、改完即时生效；</item>
///   <item>这一页全是 <b>HKLM / powercfg</b>，**必须提权**，部分要重启。</item>
/// </list>
/// </para>
///
/// <para>
/// ⛔ 三项都做成委托而不是 switch，是为了让「命令怎么生成」「值怎么解释」这两段
/// 能脱离注册表被单元测试钉住 —— 写命令错一个字，用户点完 UAC 才发现没生效，最难查。
/// </para>
///
/// <para>
/// ⛔ 这里**不收 HKCU 的项**：提权批次跑在管理员令牌下，写 HKCU 会写到管理员账户去，
/// 而不是当前用户（标准账户 + 输入管理员密码时尤其明显）。HKCU 的项一律放「系统优化」页。
/// </para>
/// </summary>
public sealed class SysFeatureDef
{
    /// <summary>稳定 Id（排队、日志、测试快照都用它）。</summary>
    public required string Id { get; init; }

    /// <summary>分组标题（界面上的小标题）。</summary>
    public required string Group { get; init; }

    /// <summary>卡片标题。</summary>
    public required string Header { get; init; }

    /// <summary>卡片说明，≤ 20 字，讲后果不讲原理（见 MustRead §2.2）。</summary>
    public string? Description { get; init; }

    /// <summary>卡片图标（Segoe Fluent Icons 字形）。</summary>
    public string Glyph { get; init; } = "\uE7E8";

    public SysFeatureKind Kind { get; init; } = SysFeatureKind.Toggle;

    /// <summary>下拉框的档位；<see cref="Kind"/> 为 <see cref="SysFeatureKind.Choice"/> 时必须有。</summary>
    public IReadOnlyList<SysChoice>? Choices { get; init; }

    /// <summary>
    /// 读当前值；读不到返回 <c>null</c>（无权 / 键不存在 / 系统不支持）。
    /// ⛔ 别把 null 当 0 —— 「读不到」与「值为 0」在界面上是两件事。
    /// </summary>
    public required Func<int?> Read { get; init; }

    /// <summary>
    /// 生成写入命令（**可多条**，按顺序执行）。
    /// ⚠️ powercfg 的 <c>setacvalueindex</c> 之后必须再跑一次 <c>setactive</c>，否则不生效。
    /// </summary>
    public required Func<int, IReadOnlyList<string>> Command { get; init; }

    /// <summary>把值翻成给人看的文字（开关行的"开/关"、下拉框的档名）。</summary>
    public required Func<int, string> Label { get; init; }

    /// <summary>改完需要重启才生效。</summary>
    public bool NeedsRestart { get; init; }

    /// <summary>仅笔记本有意义（合盖行为这类），台式机上整卡停用。</summary>
    public bool LaptopOnly { get; init; }

    /// <summary>改之前要弹二次确认的那句话（为空则不确认）。</summary>
    public string? Confirm { get; init; }
}
