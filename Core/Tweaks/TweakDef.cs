using System;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 改完要用哪条外壳通知让界面重读（机制照 Dism++ 的 <c>ExplorerNotify</c>）。
/// <para>
/// ⚠️ 这里刻意**不叫"要不要重启资源管理器"**：2026-10-31 起默认走通知，杀 explorer 只是兜底按钮。
/// 只有 <see cref="None"/> 是"写完注册表就完事"，其余两种由 <see cref="ShellNotify"/> 执行。
/// </para>
/// </summary>
public enum TweakRefresh
{
    /// <summary>不需要通知（下次读注册表自然就是新值，比如主题、输入法设置）。</summary>
    None,

    /// <summary>任务栏 / 托盘相关：发 <c>WM_SETTINGCHANGE + "TraySettings"</c>。</summary>
    Tray,

    /// <summary>桌面与资源管理器视图相关：让桌面执行一次"刷新"。</summary>
    Desktop,
}

/// <summary>这条改动写在哪一格注册表单元（决定要不要提权）。</summary>
public enum TweakScope
{
    /// <summary>
    /// <c>HKCU</c>：写当前用户。**绝大多数界面类设置的真实落点就在这儿，不需要管理员**。
    /// <para>
    /// ⚠️ 2026-10-31 修正：一开始我把"做系统"理解成"全写 HKLM"，把任务栏靠左这种也塞进提权批次。
    /// Nick 当场反问「任务栏图标靠左为什么需要 UAC？这个系统设置一下就行了吗」——
    /// 他说得对：Dism++ 的「系统」列是给**新用户预设默认值**用的，不是唯一正确落点；
    /// 已登录用户的 HKCU 优先级更高，写 HKLM 反而可能看不到变化。
    /// </para>
    /// </summary>
    User,

    /// <summary>
    /// <c>HKLM</c>：真正的机器级设置（休眠、快速启动、分页文件这类），只有它们才需要提权。
    /// </summary>
    System,
}

/// <summary>下拉框里的一个选项（<paramref name="Value"/> 就是要写进注册表的 DWORD）。</summary>
public sealed record TweakChoice(string Label, int Value);

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
/// ⭐ 2026-10-31：**界面类项一律写当前用户（<c>HKCU</c>），不弹 UAC**。
/// 只有真正机器级的项（<c>Scope = System</c>：休眠、快速启动、分页文件）才走一次提权。
/// 界面依旧整批提交：HKCU 部分直接写、System 部分合成一份 <c>.reg</c> 提权一次。
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
    public required string Id { get; init; }

    /// <summary>界面上的分组（本产品的分组，不是 Dism++ 的）。</summary>
    public required string Group { get; init; }

    /// <summary>行标题：打开这个开关会发生什么。</summary>
    public required string Header { get; init; }

    public string Description { get; init; } = "";

    /// <summary>注册表根（决定写 HKLM 还是 HKCU、要不要提权）。默认当前用户。</summary>
    public TweakScope Scope { get; init; } = TweakScope.User;

    /// <summary>相对根的键路径（不含 <c>HKEY_LOCAL_MACHINE\</c>）。</summary>
    public required string KeyPath { get; init; }

    public required string ValueName { get; init; }

    /// <summary>打开时的 DWORD 值。</summary>
    public required int OnValue { get; init; }

    /// <summary>关闭时的 DWORD 值（键或值缺失时也按它解释）。</summary>
    public required int OffValue { get; init; }

    public TweakRefresh Refresh { get; init; } = TweakRefresh.None;

    /// <summary>
    /// 多选一的可选值（2026-10-31 加）。非空 ⇒ 界面上用下拉框，而不是开关。
    ///
    /// <para>
    /// 照 Dism++ 的 <c>Type="Combo"</c> 那套：像"任务栏搜索框"其实是隐藏 / 仅图标 / 显示搜索框 **三态**，
    /// 拿开关表达只能覆盖两态，用户还得去系统设置里补第三态。下拉框才是它本来的样子。
    /// </para>
    /// <para>
    /// ⚠️ 有 <see cref="Choices"/> 的项，<see cref="OnValue"/> / <see cref="OffValue"/> 不再表示"开/关"，
    /// 只用来解释"值缺失时算哪一档"（<see cref="OffValue"/>）与"推荐值"（<see cref="OnValue"/>）。
    /// </para>
    /// </summary>
    public IReadOnlyList<TweakChoice> Choices { get; init; } = Array.Empty<TweakChoice>();

    /// <summary>这一项是不是下拉框。</summary>
    public bool IsChoice => Choices.Count > 0;

    /// <summary>
    /// 最低适用 build（0 = 不限）。22000 = Windows 11 门槛。
    /// <para>
    /// 判定口径与 Dism++ 的 <c>&lt;Applicable&gt;&lt;OSVersion Compare="&gt;="&gt;…</c> 一致：
    /// 不满足就不在界面上出现 —— 在 Win10 上摆一个"任务栏靠左"只会让人以为程序坏了
    /// （那个键在 Win10 根本不存在，写进去等于什么都没做）。
    /// </para>
    /// </summary>
    public int MinBuild { get; init; }

    /// <summary>最高适用 build（0 = 不限）。留给将来 Win10 专有的项。</summary>
    public int MaxBuild { get; init; }

    public TweakRisk Risk { get; init; } = TweakRisk.Normal;

    /// <summary>
    /// 关闭时**删掉这个值**而不是写一个"关"的值（照 Dism++ 的做法）。
    ///
    /// <para>
    /// 为什么需要：<c>Software\Policies</c> 下的项，留一个 <c>0</c> 和"根本没有这个值"在语义上不同 ——
    /// 部分工具与环境会把"存在即已配置策略"当成已经被管理，用户之后在系统设置里改不动。
    /// 这类项关掉时应当把值整个删掉，回到从未配置过的状态。
    /// </para>
    /// <para>
    /// ⚠️ 只对"缺值 ≡ 关态"的项可用，否则删完会被读成别的意思。当前只有两条设了它。
    /// </para>
    /// </summary>
    public bool DeleteWhenOff { get; init; }

    /// <summary>完整路径（日志、.reg 生成、排查都用它）。</summary>
    public string FullPath => $@"{RootName}\{KeyPath}\{ValueName}";

    /// <summary><c>HKEY_LOCAL_MACHINE</c> / <c>HKEY_CURRENT_USER</c>（.reg 文件里要写全名）。</summary>
    public string RootName => Scope == TweakScope.System ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";
}
