using System.Collections.Generic;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Data.Tweaks;

































public static class TweakCatalog
{
    public const string GroupTaskbar = "任务栏与开始菜单";
    public const string GroupExplorer = "资源管理器";
    public const string GroupVisual = "外观与动画";
    public const string GroupInput = "输入法与记事本";

    
    public const string GroupRecommend = "推荐内容与建议";
    public const string GroupDesktop = "桌面图标";

    
    
    
    
    
    internal const int Win11Build = 22000;

    private const string ExplorerAdvanced = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ExplorerSearch = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search";
    private const string ExplorerCabinet = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\CabinetState";
    private const string PolicyExplorer = @"SOFTWARE\Policies\Microsoft\Windows\Explorer";
    private const string Personalize = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    
    private const string ExplorerRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer";
    private const string AutoplayHandlers = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers";
    private const string ContentDelivery = @"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string DesktopIcons = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
    private const string ImeSettings = @"SOFTWARE\Microsoft\InputMethod\Settings\CHS";
    private const string NotepadKey = @"Software\Microsoft\Notepad";

    public static readonly List<TweakDef> All = new()
    {
        
        new TweakDef
        {
            Id = "taskbar-align-left", Group = GroupTaskbar,
            Header = "任务栏图标靠左对齐",
            Description = "Windows 11 默认把任务栏图标放中间；打开后恢复成左侧对齐（Win10 的位置）。仅 Windows 11 生效。",
            KeyPath = ExplorerAdvanced, ValueName = "TaskbarAl",
            OnValue = 0, OffValue = 1, Refresh = TweakRefresh.Tray,
            MinBuild = Win11Build,
        },
        new TweakDef
        {
            Id = "taskbar-hide-search", Group = GroupTaskbar,
            Header = "任务栏搜索框",
            Description = "选择任务栏上搜索入口的显示方式。隐藏或只留图标能腾出位置给已固定的程序。",
            KeyPath = ExplorerSearch, ValueName = "SearchboxTaskbarMode",
            OnValue = 0, OffValue = 1, Refresh = TweakRefresh.Tray,
            
            Choices = new TweakChoice[]
            {
                new("隐藏", 0),
                new("仅显示图标", 1),
                new("显示搜索框", 2),
                new("显示搜索框和标签", 3),
            },
        },
        new TweakDef
        {
            Id = "taskbar-hide-taskview", Group = GroupTaskbar,
            Header = "隐藏任务视图按钮",
            Description = "任务栏上的「任务视图」按钮（多桌面入口）。这个功能仍可用 Win+Tab 调出。",
            KeyPath = ExplorerAdvanced, ValueName = "ShowTaskViewButton",
            OnValue = 0, OffValue = 1, Refresh = TweakRefresh.Tray,
        },
        new TweakDef
        {
            Id = "taskbar-hide-widgets", Group = GroupTaskbar,
            Header = "隐藏小组件按钮",
            Description = "Windows 11 任务栏左下角的小组件入口；关闭后不再推送天气与资讯。仅 Windows 11 生效。",
            KeyPath = ExplorerAdvanced, ValueName = "TaskbarDa",
            OnValue = 0, OffValue = 1, Refresh = TweakRefresh.Tray,
            MinBuild = Win11Build,
        },
        new TweakDef
        {
            Id = "taskbar-merge-never", Group = GroupTaskbar,
            Header = "任务栏按钮合并方式",
            Description = "选「从不合并」时，同一程序的多个窗口各自占一个按钮并显示标题，窗口多时会显得拥挤。Windows 11 需 22H2 及以上才支持这一档。",
            KeyPath = ExplorerAdvanced, ValueName = "TaskbarGlomLevel",
            OnValue = 2, OffValue = 0, Refresh = TweakRefresh.Tray,
            Choices = new TweakChoice[]
            {
                new("始终合并", 0),
                new("任务栏占满时合并", 1),
                new("从不合并", 2),
            },
        },
        new TweakDef
        {
            Id = "taskbar-clock-seconds", Group = GroupTaskbar,
            Header = "任务栏时钟显示秒",
            Description = "任务栏右下角的时间精确到秒。会略微增加刷新开销。",
            KeyPath = ExplorerAdvanced, ValueName = "ShowSecondsInSystemClock",
            OnValue = 1, OffValue = 0, Refresh = TweakRefresh.Tray, DeleteWhenOff = true,
        },
        new TweakDef
        {
            Id = "start-no-notification-center", Group = GroupTaskbar,
            Header = "关闭通知中心",
            Description = "关闭当前账户右下角的通知中心与横幅。开启后所有程序的通知都不会再弹出。",
            KeyPath = PolicyExplorer, ValueName = "DisableNotificationCenter",
            OnValue = 1, OffValue = 0, Refresh = TweakRefresh.None, DeleteWhenOff = true,
            Risk = TweakRisk.Caution,
        },

        
        new TweakDef
        {
            Id = "explorer-show-ext", Group = GroupExplorer,
            Header = "显示文件扩展名",
            Description = "让 .exe / .docx / .jpg 这类扩展名可见，也更容易识别改过扩展名的伪装文件。",
            KeyPath = ExplorerAdvanced, ValueName = "HideFileExt",
            OnValue = 0, OffValue = 1, Refresh = TweakRefresh.Desktop,
        },
        new TweakDef
        {
            Id = "explorer-show-hidden", Group = GroupExplorer,
            Header = "显示隐藏文件",
            Description = "显示被标记为隐藏的文件与文件夹（半透明显示）。",
            KeyPath = ExplorerAdvanced, ValueName = "Hidden",
            OnValue = 1, OffValue = 2, Refresh = TweakRefresh.Desktop,
        },
        new TweakDef
        {
            Id = "explorer-show-superhidden", Group = GroupExplorer,
            Header = "显示受保护的系统文件",
            Description = "连系统属性文件一起显示。这些文件误删会影响系统，仅在排查问题时打开。",
            KeyPath = ExplorerAdvanced, ValueName = "ShowSuperHidden",
            OnValue = 1, OffValue = 0, Refresh = TweakRefresh.Desktop,
            Risk = TweakRisk.Caution,
        },
        new TweakDef
        {
            Id = "explorer-launch-thispc", Group = GroupExplorer,
            Header = "打开资源管理器时显示",
            Description = "决定资源管理器启动后落在「快速访问」还是磁盘列表（此电脑）。",
            KeyPath = ExplorerAdvanced, ValueName = "LaunchTo",
            OnValue = 1, OffValue = 2, Refresh = TweakRefresh.None,
            Choices = new TweakChoice[]
            {
                new("此电脑", 1),
                new("快速访问", 2),
            },
        },
        new TweakDef
        {
            Id = "explorer-nav-expand", Group = GroupExplorer,
            Header = "导航窗格展开到当前文件夹",
            Description = "打开某个深层目录时，左侧导航树自动展开到它所在的位置，便于定位。",
            KeyPath = ExplorerAdvanced, ValueName = "NavPaneExpandToCurrentFolder",
            OnValue = 1, OffValue = 0, Refresh = TweakRefresh.None,
        },
        new TweakDef
        {
            Id = "explorer-no-thumb-cache", Group = GroupExplorer,
            Header = "不使用缩略图缓存",
            Description = "每次重新生成缩略图，不再读写 thumbcache 文件。磁盘占用更小，但图片多的目录打开会变慢。",
            KeyPath = ExplorerAdvanced, ValueName = "DisableThumbnailCache",
            OnValue = 1, OffValue = 0, Refresh = TweakRefresh.None,
        },
        new TweakDef
        {
            Id = "explorer-full-path", Group = GroupExplorer,
            Header = "标题栏显示完整路径",
            Description = "资源管理器窗口标题直接显示完整路径，而不是当前文件夹名。",
            KeyPath = ExplorerCabinet, ValueName = "FullPath",
            OnValue = 1, OffValue = 0, Refresh = TweakRefresh.None,
        },

        
        new TweakDef
        {
            Id = "visual-apps-dark", Group = GroupVisual,
            Header = "应用使用深色模式",
            Description = "应用窗口与控件走深色配色。系统级的默认值，用户自己选过颜色时会以用户选择为准。",
            KeyPath = Personalize, ValueName = "AppsUseLightTheme",
            OnValue = 0, OffValue = 1, Refresh = TweakRefresh.None,
        },
        new TweakDef
        {
            Id = "visual-system-dark", Group = GroupVisual,
            Header = "系统界面使用深色模式",
            Description = "任务栏、开始菜单、操作中心等系统外壳走深色。",
            KeyPath = Personalize, ValueName = "SystemUsesLightTheme",
            OnValue = 0, OffValue = 1, Refresh = TweakRefresh.None,
        },
        new TweakDef
        {
            Id = "visual-transparency", Group = GroupVisual,
            Header = "关闭透明效果",
            Description = "关闭开始菜单、任务栏的毛玻璃透明。老旧机器上能减少一点合成开销。",
            KeyPath = Personalize, ValueName = "EnableTransparency",
            OnValue = 0, OffValue = 1, Refresh = TweakRefresh.None,
        },

        
        new TweakDef
        {
            Id = "ime-no-cloud", Group = GroupInput,
            Header = "关闭拼音输入法的云候选",
            Description = "候选词只来自本地词库，不再向微软发送输入内容、也不再联网取候选词。",
            
            
            
            
            
            KeyPath = @"SOFTWARE\Microsoft\InputMethod\Settings\CHS",
            ValueName = "Enable Cloud Candidate",
            OnValue = 0, OffValue = 1, Refresh = TweakRefresh.None,
            Risk = TweakRisk.Caution,
        },

        
        
        
        
        

        
        new TweakDef
        {
            Id = "taskbar-all-tray-icons", Group = GroupTaskbar,
            Header = "始终显示所有托盘图标",
            Description = "通知区域不再把图标收进「^」里，全部平铺在任务栏上。",
            KeyPath = ExplorerRoot, ValueName = "EnableAutoTray",
            OnValue = 0, OffValue = 1, Refresh = TweakRefresh.Tray,
        },
        new TweakDef
        {
            Id = "taskbar-small-icons", Group = GroupTaskbar,
            Header = "任务栏使用小图标",
            Description = "任务栏按钮改用小图标，能多放下几个窗口。Windows 11 已移除这个选项，仅 Windows 10 有效。",
            KeyPath = ExplorerAdvanced, ValueName = "TaskbarSmallIcons",
            OnValue = 1, OffValue = 0, Refresh = TweakRefresh.Tray,
            MaxBuild = Win11Build - 1,
        },
        new TweakDef
        {
            Id = "taskbar-hide-people", Group = GroupTaskbar,
            Header = "隐藏任务栏上的人脉按钮",
            Description = "去掉任务栏右端的「人脉」入口。Windows 11 任务栏没有这个按钮，仅 Windows 10 有效。",
            KeyPath = ExplorerAdvanced + @"\People", ValueName = "PeopleBand",
            OnValue = 0, OffValue = 1, Refresh = TweakRefresh.Tray,
            MaxBuild = Win11Build - 1,
        },

        
        new TweakDef
        {
            Id = "start-no-suggestions", Group = GroupRecommend,
            Header = "开始菜单不显示推荐建议",
            Description = "关闭开始菜单里的小贴士与推荐条目，只保留自己固定的程序。",
            KeyPath = ContentDelivery, ValueName = "SystemPaneSuggestionsEnabled",
            OnValue = 0, OffValue = 1, Refresh = TweakRefresh.None,
        },
        new TweakDef
        {
            Id = "start-no-recommended-apps", Group = GroupRecommend,
            Header = "关闭商店应用推广",
            Description = "不再在开始菜单推荐并预先下载商店里的应用。",
            KeyPath = ContentDelivery, ValueName = "PreInstalledAppsEnabled",
            OnValue = 0, OffValue = 1, Refresh = TweakRefresh.None,
        },
        new TweakDef
        {
            Id = "start-no-silent-install", Group = GroupRecommend,
            Header = "禁止自动安装推荐的应用",
            Description = "系统不再在后台自行装好推荐应用，避免多出用不到的软件占磁盘。",
            KeyPath = ContentDelivery, ValueName = "SilentInstalledAppsEnabled",
            OnValue = 0, OffValue = 1, Refresh = TweakRefresh.None,
        },
        new TweakDef
        {
            Id = "lockscreen-no-spotlight", Group = GroupRecommend,
            Header = "关闭锁屏的聚焦推广",
            Description = "锁屏背景固定为所选图片，不再每天推送带宣传性质的壁纸与提示。",
            KeyPath = ContentDelivery, ValueName = "RotatingLockScreenEnable",
            OnValue = 0, OffValue = 1, Refresh = TweakRefresh.None,
        },
        new TweakDef
        {
            Id = "windows-no-tips", Group = GroupRecommend,
            Header = "关闭使用技巧与建议提示",
            Description = "不再弹出「使用 Windows 时获取技巧和建议」这类系统提示。",
            KeyPath = ContentDelivery, ValueName = "SoftLandingEnabled",
            OnValue = 0, OffValue = 1, Refresh = TweakRefresh.None,
        },
        new TweakDef
        {
            Id = "start-no-new-app-highlight", Group = GroupRecommend,
            Header = "关闭新安装程序的突出显示",
            Description = "新装的程序不在开始菜单里高亮标记，减少被误点的机会。",
            KeyPath = ExplorerAdvanced, ValueName = "Start_NotifyNewApps",
            OnValue = 0, OffValue = 1, Refresh = TweakRefresh.Tray,
        },

        
        new TweakDef
        {
            Id = "explorer-no-autoplay", Group = GroupExplorer,
            Header = "禁止自动播放",
            Description = "插入 U 盘、光盘后不再自动运行其中的内容。机房环境建议开启。",
            KeyPath = AutoplayHandlers, ValueName = "DisableAutoplay",
            OnValue = 1, OffValue = 0, Refresh = TweakRefresh.None,
        },
        new TweakDef
        {
            Id = "explorer-separate-process", Group = GroupExplorer,
            Header = "每个文件夹窗口独立进程",
            Description = "一个窗口卡死不会拖住整个资源管理器，代价是多占一些内存。",
            KeyPath = ExplorerAdvanced, ValueName = "SeparateProcess",
            OnValue = 1, OffValue = 0, Refresh = TweakRefresh.None,
        },
        new TweakDef
        {
            Id = "explorer-quickaccess-no-frequent", Group = GroupExplorer,
            Header = "快速访问不显示常用文件夹",
            Description = "快速访问只留自己固定的目录，不再自动罗列近期常开的文件夹。",
            KeyPath = ExplorerRoot, ValueName = "ShowFrequent",
            OnValue = 0, OffValue = 1, Refresh = TweakRefresh.None,
            DeleteWhenOff = true,
        },
        new TweakDef
        {
            Id = "explorer-quickaccess-no-recent", Group = GroupExplorer,
            Header = "快速访问不显示最近文件",
            Description = "不列出最近打开过的文件，公用机器上也不会留下他人的使用痕迹。",
            KeyPath = ExplorerRoot, ValueName = "ShowRecent",
            OnValue = 0, OffValue = 1, Refresh = TweakRefresh.None,
            DeleteWhenOff = true,
        },
        new TweakDef
        {
            Id = "explorer-always-menu-bar", Group = GroupExplorer,
            Header = "资源管理器常显菜单栏",
            Description = "菜单栏（文件 / 编辑 / 查看）固定显示，不用每次按 Alt 调出。",
            KeyPath = ExplorerAdvanced, ValueName = "AlwaysShowMenus",
            OnValue = 1, OffValue = 0, Refresh = TweakRefresh.None,
        },

        
        
        new TweakDef
        {
            Id = "desktop-show-thispc", Group = GroupDesktop,
            Header = "桌面显示「此电脑」",
            Description = "在桌面上放一个「此电脑」图标，方便直接进磁盘。",
            KeyPath = DesktopIcons, ValueName = "{20D04FE0-3AEA-1069-A2D8-08002B30309D}",
            OnValue = 0, OffValue = 1, Refresh = TweakRefresh.Desktop,
        },
        new TweakDef
        {
            Id = "desktop-show-recyclebin", Group = GroupDesktop,
            Header = "桌面显示「回收站」",
            Description = "在桌面上放一个「回收站」图标。",
            KeyPath = DesktopIcons, ValueName = "{645FF040-5081-101B-9F08-00AA002F954E}",
            OnValue = 0, OffValue = 1, Refresh = TweakRefresh.Desktop,
        },

        
        new TweakDef
        {
            Id = "visual-accent-on-taskbar", Group = GroupVisual,
            Header = "强调色显示到任务栏与开始菜单",
            Description = "任务栏与开始菜单跟随主题强调色着色，而不是固定的深灰。",
            KeyPath = Personalize, ValueName = "ColorPrevalence",
            OnValue = 1, OffValue = 0, Refresh = TweakRefresh.Tray,
        },

        
        new TweakDef
        {
            Id = "ime-default-english", Group = GroupInput,
            Header = "拼音输入法默认英文",
            Description = "新开输入框时先出英文，避免中文输入法抢先上屏。机房环境常用。",
            KeyPath = ImeSettings, ValueName = "Default Mode",
            OnValue = 1, OffValue = 0, Refresh = TweakRefresh.None,
        },
        new TweakDef
        {
            Id = "ime-fuzzy", Group = GroupInput,
            Header = "开启拼音模糊音",
            Description = "不区分平翘舌与前后鼻音，拼音打错也能出候选词。",
            KeyPath = ImeSettings, ValueName = "Enable Fuzzy Input",
            OnValue = 1, OffValue = 0, Refresh = TweakRefresh.None,
        },
        new TweakDef
        {
            Id = "notepad-wrap", Group = GroupInput,
            Header = "记事本自动换行",
            Description = "长行自动折到下一行，不用左右拖动查看。",
            KeyPath = NotepadKey, ValueName = "fWrap",
            OnValue = 1, OffValue = 0, Refresh = TweakRefresh.None,
        },
        new TweakDef
        {
            Id = "notepad-statusbar", Group = GroupInput,
            Header = "记事本显示状态栏",
            Description = "底部显示当前行列号与字符数。",
            KeyPath = NotepadKey, ValueName = "StatusBar",
            OnValue = 1, OffValue = 0, Refresh = TweakRefresh.None,
        },
    };
}
