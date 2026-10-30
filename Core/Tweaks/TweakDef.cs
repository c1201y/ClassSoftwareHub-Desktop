using System;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>一项改动什么时候生效。</summary>
public enum TweakApply
{
    /// <summary>改完立刻生效（下一次读注册表就是新值）。</summary>
    Instant,

    /// <summary>要重启资源管理器（explorer.exe）才看得出效果。</summary>
    RestartExplorer,
}

/// <summary>这条改动写在哪一格注册表单元（决定要不要提权）。</summary>
public enum TweakScope
{
    /// <summary><c>HKLM</c>：整机生效，所有账户都受影响。**要提权**（2026-10-31 Nick 定的口径：做系统级）。</summary>
    System,

    /// <summary><c>HKCU</c>：只影响当前账户。不需要提权，留作少数确实按账户走的项。</summary>
    User,
}

/// <summary>风险级别。<see cref="Caution"/> 的项在切换前要弹二次确认。</summary>
public enum TweakRisk
{
    Normal,
    Caution,
}

/// <summary>
/// 一条系统优化项（2026-10-31，对应需求清单第 20 条，按 Dism++「系统优化」的能力对齐）。
///
/// <para>
/// ⭐ 2026-10-31 二改：**做系统级（<c>HKLM</c>）**，与 Dism++ 的「系统」那一列对齐 ——
/// Nick 原话「不是当前用户，是什么，是系统！」。学校机器不做「新用户」那一列，但改动要整机生效。
/// ⇒ 写 <c>HKLM</c> 必须提权，所以界面**不逐项弹 UAC**：切开关只排队，
/// 「应用更改」时把整批改动写成一份 <c>.reg</c>，**一次** <c>reg import</c> 提权提交（同 Dism++ 的交互）。
/// </para>
/// <para>
/// 表述约定：<see cref="Header"/> 描述"这个开关打开后会发生什么"，
/// <see cref="OnValue"/> / <see cref="OffValue"/> 与之对应；值缺失时按 <see cref="OffValue"/> 解释
/// （Windows 大多数界面开关的默认态就是关）。
/// </para>
/// <para>
/// ⛔ 只收录**键路径能确认**的项。拿不准的（比如"锁定任务栏""快速访问显示最近文件"，
/// 真实落点不在 Advanced 下）宁可不做 —— 写错键等于给用户一个什么都不做的开关。
/// </para>
/// </summary>
public sealed record TweakDef
{
    private const string ExplorerAdvanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    public required string Id { get; init; }

    /// <summary>界面上的分组（本产品的分组，不是 Dism++ 的）。</summary>
    public required string Group { get; init; }

    /// <summary>行标题：打开这个开关会发生什么。</summary>
    public required string Header { get; init; }

    public string Description { get; init; } = "";

    /// <summary>注册表根（决定写 HKLM 还是 HKCU、要不要提权）。</summary>
    public TweakScope Scope { get; init; } = TweakScope.System;

    /// <summary>相对根的键路径（不含 <c>HKEY_LOCAL_MACHINE\</c>）。</summary>
    public required string KeyPath { get; init; }

    public required string ValueName { get; init; }

    /// <summary>打开时的 DWORD 值。</summary>
    public required int OnValue { get; init; }

    /// <summary>关闭时的 DWORD 值（键或值缺失时也按它解释）。</summary>
    public required int OffValue { get; init; }

    public TweakApply Apply { get; init; } = TweakApply.Instant;

    public TweakRisk Risk { get; init; } = TweakRisk.Normal;

    /// <summary>只在某个系统版本上有意义时写一句，界面会跟在标题后面。</summary>
    public string ScopeNote { get; init; } = "";

    /// <summary>完整路径（日志、.reg 生成、排查都用它）。</summary>
    public string FullPath => $@"{RootName}\{KeyPath}\{ValueName}";

    /// <summary><c>HKEY_LOCAL_MACHINE</c> / <c>HKEY_CURRENT_USER</c>（.reg 文件里要写全名）。</summary>
    public string RootName => Scope == TweakScope.System ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";

    // ── 常用键路径的简写（只给确实常用的那条，别在这里铺一长串常量表）──
    public static string Advanced(string? sub = null) =>
        sub is null ? ExplorerAdvanced : ExplorerAdvanced + "\\" + sub;
}
