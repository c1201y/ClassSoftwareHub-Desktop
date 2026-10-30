using System.Collections.Generic;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Data.Tweaks;

/// <summary>
/// 系统优化项清单（2026-10-31，需求清单第 20 条，对齐 Dism++「系统优化」的能力）。
///
/// <para>
/// ⭐ 口径（Nick 2026-10-31）：**做系统级**，与 Dism++ 的「系统」那一列对应 ——
/// 写 <c>HKLM</c>，整机所有账户生效；不做「新用户」那一列（学校不常建账户）。
/// 少数项在 <c>HKLM</c> 没有真实落点（窗口动画、开机声音这类本来就是每账户一份），
/// 如实标成 <see cref="TweakScope.User"/> 并在说明里写清"仅当前账户"。
/// </para>
///
/// <para>
/// ⛔ 收录标准：<b>键路径能确认</b>。Dism++ 里那些拿不准落点的（"锁定任务栏""快速访问显示最近文件"
/// 等）一律不抄 —— 写错键等于给用户一个什么都不做的开关，比没有更糟。
/// </para>
/// <para>
/// ⛔ 明确不抄的整类：安全相关（UAC / SmartScreen / Defender）、服务优化（SysMain / 搜索索引 /
/// 打印后台…）、Windows Update 策略。理由写在 <c>docs/</c> 与记忆里：班级机器上这些一旦生效，
/// 用户只会看到"打印机打不了""找不到文件"，而想不到是这里改的。
/// </para>
/// </summary>
public static class TweakCatalog
{
    public const string GroupTaskbar = "任务栏与开始菜单";
    public const string GroupExplorer = "资源管理器";
    public const string GroupVisual = "外观与动画";
    public const string GroupInput = "输入法与记事本";

    /// <summary>各组在界面上的先后顺序。</summary>
    public static readonly string[] GroupOrder =
    {
        GroupTaskbar, GroupExplorer, GroupVisual, GroupInput,
    };

    private const string ExplorerAdvanced = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ExplorerSearch = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search";
    private const string ExplorerCabinet = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\CabinetState";
    private const string PolicyExplorer = @"SOFTWARE\Policies\Microsoft\Windows\Explorer";
    private const string PolicyCloud = @"SOFTWARE\Policies\Microsoft\Windows\CloudContent";
    private const string Personalize = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static readonly List<TweakDef> All = new()
    {
        // ───────────── 任务栏与开始菜单 ─────────────
        new TweakDef
        {
            Id = "taskbar-align-left", Group = GroupTaskbar,
            Header = "任务栏图标靠左对齐",
            Description = "Windows 11 默认把任务栏图标放中间；打开后恢复成左侧对齐（Win10 的位置）。",
            KeyPath = ExplorerAdvanced, ValueName = "TaskbarAl",
            OnValue = 0, OffValue = 1, Apply = TweakApply.RestartExplorer,
            ScopeNote = "仅 Windows 11",
        },
        new TweakDef
        {
            Id = "taskbar-hide-search", Group = GroupTaskbar,
            Header = "隐藏任务栏搜索框",
            Description = "打开后任务栏上不再显示搜索框，腾出位置给已固定的程序。",
            KeyPath = ExplorerSearch, ValueName = "SearchboxTaskbarMode",
            OnValue = 0, OffValue = 1, Apply = TweakApply.RestartExplorer,
        },
        new TweakDef
        {
            Id = "taskbar-hide-taskview", Group = GroupTaskbar,
            Header = "隐藏任务视图按钮",
            Description = "任务栏上的「任务视图」按钮（多桌面入口）。这个功能仍可用 Win+Tab 调出。",
            KeyPath = ExplorerAdvanced, ValueName = "ShowTaskViewButton",
            OnValue = 0, OffValue = 1, Apply = TweakApply.RestartExplorer,
        },
        new TweakDef
        {
            Id = "taskbar-hide-widgets", Group = GroupTaskbar,
            Header = "隐藏小组件按钮",
            Description = "Windows 11 任务栏左下角的小组件入口；关闭后不再推送天气与资讯。",
            KeyPath = ExplorerAdvanced, ValueName = "TaskbarDa",
            OnValue = 0, OffValue = 1, Apply = TweakApply.RestartExplorer,
            ScopeNote = "仅 Windows 11",
        },
        new TweakDef
        {
            Id = "taskbar-merge-never", Group = GroupTaskbar,
            Header = "任务栏按钮从不合并",
            Description = "同一程序的多个窗口各自占一个按钮并显示标题。窗口多时会显得拥挤。",
            KeyPath = ExplorerAdvanced, ValueName = "TaskbarGlomLevel",
            OnValue = 2, OffValue = 0, Apply = TweakApply.RestartExplorer,
            Risk = TweakRisk.Caution,
            ScopeNote = "Windows 11 的 22H2 起只在部分版本可用",
        },
        new TweakDef
        {
            Id = "taskbar-clock-seconds", Group = GroupTaskbar,
            Header = "任务栏时钟显示秒",
            Description = "任务栏右下角的时间精确到秒。会略微增加刷新开销。",
            KeyPath = ExplorerAdvanced, ValueName = "ShowSecondsInSystemClock",
            OnValue = 1, OffValue = 0, Apply = TweakApply.RestartExplorer,
        },
        new TweakDef
        {
            Id = "start-no-consumer-features", Group = GroupTaskbar,
            Header = "关闭开始菜单的推荐内容",
            Description = "关闭「建议」「为你推荐」这类推广位与应用推广通知（企业策略项，整机生效）。",
            KeyPath = PolicyCloud, ValueName = "DisableWindowsConsumerFeatures",
            OnValue = 1, OffValue = 0, Apply = TweakApply.RestartExplorer,
            Risk = TweakRisk.Caution,
        },
        new TweakDef
        {
            Id = "start-no-notification-center", Group = GroupTaskbar,
            Header = "关闭通知中心",
            Description = "整机关闭右下角通知中心与横幅。开启后所有程序的通知都不会再弹出。",
            KeyPath = PolicyExplorer, ValueName = "DisableNotificationCenter",
            OnValue = 1, OffValue = 0, Apply = TweakApply.RestartExplorer,
            Risk = TweakRisk.Caution,
        },

        // ───────────── 资源管理器 ─────────────
        new TweakDef
        {
            Id = "explorer-show-ext", Group = GroupExplorer,
            Header = "显示文件扩展名",
            Description = "让 .exe / .docx / .jpg 这类扩展名可见，也更容易识别改过扩展名的伪装文件。",
            KeyPath = ExplorerAdvanced, ValueName = "HideFileExt",
            OnValue = 0, OffValue = 1, Apply = TweakApply.RestartExplorer,
        },
        new TweakDef
        {
            Id = "explorer-show-hidden", Group = GroupExplorer,
            Header = "显示隐藏文件",
            Description = "显示被标记为隐藏的文件与文件夹（半透明显示）。",
            KeyPath = ExplorerAdvanced, ValueName = "Hidden",
            OnValue = 1, OffValue = 2, Apply = TweakApply.RestartExplorer,
        },
        new TweakDef
        {
            Id = "explorer-show-superhidden", Group = GroupExplorer,
            Header = "显示受保护的系统文件",
            Description = "连系统属性文件一起显示。这些文件误删会影响系统，仅在排查问题时打开。",
            KeyPath = ExplorerAdvanced, ValueName = "ShowSuperHidden",
            OnValue = 1, OffValue = 0, Apply = TweakApply.RestartExplorer,
            Risk = TweakRisk.Caution,
        },
        new TweakDef
        {
            Id = "explorer-launch-thispc", Group = GroupExplorer,
            Header = "打开时定位到「此电脑」",
            Description = "资源管理器启动后直接显示磁盘列表，而不是「快速访问」的最近文件。",
            KeyPath = ExplorerAdvanced, ValueName = "LaunchTo",
            OnValue = 1, OffValue = 2, Apply = TweakApply.RestartExplorer,
        },
        new TweakDef
        {
            Id = "explorer-nav-expand", Group = GroupExplorer,
            Header = "导航窗格展开到当前文件夹",
            Description = "打开某个深层目录时，左侧导航树自动展开到它所在的位置，便于定位。",
            KeyPath = ExplorerAdvanced, ValueName = "NavPaneExpandToCurrentFolder",
            OnValue = 1, OffValue = 0, Apply = TweakApply.RestartExplorer,
        },
        new TweakDef
        {
            Id = "explorer-no-thumb-cache", Group = GroupExplorer,
            Header = "不使用缩略图缓存",
            Description = "每次重新生成缩略图，不再读写 thumbcache 文件。磁盘占用更小，但图片多的目录打开会变慢。",
            KeyPath = ExplorerAdvanced, ValueName = "DisableThumbnailCache",
            OnValue = 1, OffValue = 0, Apply = TweakApply.RestartExplorer,
        },
        new TweakDef
        {
            Id = "explorer-full-path", Group = GroupExplorer,
            Header = "标题栏显示完整路径",
            Description = "资源管理器窗口标题直接显示完整路径，而不是当前文件夹名。",
            KeyPath = ExplorerCabinet, ValueName = "FullPath",
            OnValue = 1, OffValue = 0, Apply = TweakApply.RestartExplorer,
        },

        // ───────────── 外观与动画 ─────────────
        new TweakDef
        {
            Id = "visual-apps-dark", Group = GroupVisual,
            Header = "应用使用深色模式",
            Description = "应用窗口与控件走深色配色。系统级的默认值，用户自己选过颜色时会以用户选择为准。",
            KeyPath = Personalize, ValueName = "AppsUseLightTheme",
            OnValue = 0, OffValue = 1, Apply = TweakApply.RestartExplorer,
        },
        new TweakDef
        {
            Id = "visual-system-dark", Group = GroupVisual,
            Header = "系统界面使用深色模式",
            Description = "任务栏、开始菜单、操作中心等系统外壳走深色。",
            KeyPath = Personalize, ValueName = "SystemUsesLightTheme",
            OnValue = 0, OffValue = 1, Apply = TweakApply.RestartExplorer,
        },
        new TweakDef
        {
            Id = "visual-transparency", Group = GroupVisual,
            Header = "关闭透明效果",
            Description = "关闭开始菜单、任务栏的毛玻璃透明。老旧机器上能减少一点合成开销。",
            KeyPath = Personalize, ValueName = "EnableTransparency",
            OnValue = 0, OffValue = 1, Apply = TweakApply.RestartExplorer,
        },

        // ───────────── 输入法与记事本 ─────────────
        new TweakDef
        {
            Id = "ime-no-cloud", Group = GroupInput,
            Header = "关闭拼音输入法的云候选",
            Description = "候选词只来自本地词库，不再向微软发送输入内容、也不再联网取候选词。",
            KeyPath = @"SOFTWARE\Policies\Microsoft\InputPersonalization",
            ValueName = "AllowInputPersonalization",
            OnValue = 0, OffValue = 1, Apply = TweakApply.Instant,
            Risk = TweakRisk.Caution,
        },
    };

    /// <summary>按分组取（界面左侧一块一块摆，顺序同 <see cref="GroupOrder"/>）。</summary>
    public static List<TweakDef> InGroup(string group) =>
        All.FindAll(t => string.Equals(t.Group, group, System.StringComparison.Ordinal));
}
