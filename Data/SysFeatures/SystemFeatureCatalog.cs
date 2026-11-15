using System;
using System.Collections.Generic;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Data.SysFeatures;
























public static class SystemFeatureCatalog
{
    public const string GroupPower = "电源与启动";
    public const string GroupDisk = "磁盘与内存";
    public const string GroupRecovery = "故障与恢复";
    public const string GroupPrivacy = "隐私与共享";

    
    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";
    private const string BootPowerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
    private const string MemoryKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string FileSystemKey = @"SYSTEM\CurrentControlSet\Control\FileSystem";
    private const string CrashKey = @"SYSTEM\CurrentControlSet\Control\CrashControl";
    private const string WerKey = @"SOFTWARE\Microsoft\Windows\Windows Error Reporting";
    private const string SqmKey = @"SOFTWARE\Microsoft\SQMClient\Windows";
    private const string RdpKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server";

    
    private const string SubVideo = "7516b95f-f776-4464-8c53-06167f40cc99";
    private const string VideoIdle = "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e";
    private const string SubSleep = "238c9fa8-0aad-41ed-83f4-97be242c8f20";
    private const string StandbyIdle = "29f6c1db-86da-48c5-9fdb-f2b67b1f44da";
    private const string SubButtons = "4f971e89-eebd-4455-a8de-9e59040e7347";
    private const string PowerButtonAction = "7648efa3-dd9c-4e3e-b566-50f929386280";

    
    public const string PlanBalanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string PlanHighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string PlanSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";

    public static IReadOnlyList<SysFeatureDef> All { get; } = new List<SysFeatureDef>
    {
        
        new()
        {
            Id = "hibernate", Group = GroupPower,
            Header = "休眠",
            Description = "关闭可释放与内存等量的磁盘空间。",
            Glyph = "\uE7E8",
            Read = () => SystemFeatures.Hibernate() switch
            {
                FeatureState.On => 1,
                FeatureState.Off => 0,
                _ => null,
            },
            Command = on => new[] { $"powercfg /hibernate {(on != 0 ? "on" : "off")}" },
            Label = v => v != 0 ? "已开启" : "已关闭",
            Confirm = "关闭休眠会删除休眠文件，快速启动随之失效。",
        },
        new()
        {
            Id = "fast-startup", Group = GroupPower,
            Header = "快速启动",
            Description = "依赖休眠，关闭休眠后本项失效。",
            Glyph = "\uE945",
            Read = () => SystemFeatures.FastStartup() switch
            {
                FeatureState.On => 1,
                FeatureState.Off => 0,
                _ => null,
            },
            Command = v => new[]
            {
                SysFeatureRules.RegAdd(BootPowerKey, "HiberbootEnabled", v),
            },
            Label = v => v != 0 ? "已开启" : "已关闭",
        },
        new()
        {
            Id = "power-plan", Group = GroupPower,
            Header = "电源计划",
            Description = "高性能会提高功耗与发热。",
            Glyph = "\uE9D9",
            Kind = SysFeatureKind.Choice,
            Choices = new SysChoice[]
            {
                new("平衡", 0),
                new("高性能", 1),
                new("节能", 2),
            },
            Read = SystemFeatures.ReadActivePlan,
            Command = v => new[]
            {
                $"powercfg /setactive {v switch { 1 => PlanHighPerf, 2 => PlanSaver, _ => PlanBalanced }}",
            },
            Label = v => v switch { 1 => "高性能", 2 => "节能", _ => "平衡" },
        },
        new()
        {
            Id = "screen-off", Group = GroupPower,
            Header = "关闭显示器",
            Description = "无操作后自动关屏，投影时省一点。",
            Glyph = "\uE7F4",
            Kind = SysFeatureKind.Choice,
            Choices = new SysChoice[]
            {
                new("从不", 0),
                new("5 分钟", 300),
                new("10 分钟", 600),
                new("15 分钟", 900),
                new("30 分钟", 1800),
                new("1 小时", 3600),
            },
            Read = () => SystemFeatures.ReadPowerSetting(SubVideo, VideoIdle),
            Command = v => SysFeatureRules.PowerSettingCommands(SubVideo, VideoIdle, v),
            Label = SecLabel,
        },
        new()
        {
            Id = "sleep-timeout", Group = GroupPower,
            Header = "自动睡眠",
            Description = "无操作后进入睡眠。",
            Glyph = "\uE708",
            Kind = SysFeatureKind.Choice,
            Choices = new SysChoice[]
            {
                new("从不", 0),
                new("10 分钟", 600),
                new("30 分钟", 1800),
                new("1 小时", 3600),
            },
            Read = () => SystemFeatures.ReadPowerSetting(SubSleep, StandbyIdle),
            Command = v => SysFeatureRules.PowerSettingCommands(SubSleep, StandbyIdle, v),
            Label = SecLabel,
        },
        new()
        {
            Id = "power-button", Group = GroupPower,
            Header = "电源按钮",
            Description = "按下机箱电源键时的行为。",
            Glyph = "\uE7E8",
            Kind = SysFeatureKind.Choice,
            Choices = new SysChoice[]
            {
                new("不操作", 0),
                new("睡眠", 1),
                new("关机", 3),
            },
            Read = () => SystemFeatures.ReadPowerSetting(SubButtons, PowerButtonAction),
            Command = v => SysFeatureRules.PowerSettingCommands(SubButtons, PowerButtonAction, v),
            Label = v => v switch { 0 => "不操作", 1 => "睡眠", 3 => "关机", _ => $"未知（{v}）" },
        },

        
        new()
        {
            Id = "memory-compression", Group = GroupDisk,
            Header = "内存压缩",
            Description = "关掉可省一点 CPU，但换页会变多。",
            Glyph = "\uE950",
            Read = () => SystemFeatures.ReadRegistryInt(MemoryKey, "DisableCompression") ?? 0,
            Command = v => new[] { SysFeatureRules.RegAdd(MemoryKey, "DisableCompression", v) },
            Label = v => v != 0 ? "已关闭" : "已开启",
            NeedsRestart = true,
        },
        new()
        {
            Id = "long-paths", Group = GroupDisk,
            Header = "NTFS 长路径",
            Description = "解开路径过长导致的解压失败。",
            Glyph = "\uE8A5",
            Read = () => SystemFeatures.ReadRegistryInt(FileSystemKey, "LongPathsEnabled") ?? 0,
            Command = v => new[] { SysFeatureRules.RegAdd(FileSystemKey, "LongPathsEnabled", v) },
            Label = v => v != 0 ? "已开启" : "已关闭",
        },

        
        new()
        {
            Id = "crash-dump", Group = GroupRecovery,
            Header = "崩溃转储",
            Description = "转储越大越占系统盘空间。",
            Glyph = "\uE7BA",
            Kind = SysFeatureKind.Choice,
            Choices = new SysChoice[]
            {
                new("不记录", 0),
                new("小内存转储", 3),
                new("内核转储", 2),
                new("完整转储", 1),
            },
            Read = () => SystemFeatures.ReadRegistryInt(CrashKey, "CrashDumpEnabled"),
            Command = v => new[] { SysFeatureRules.RegAdd(CrashKey, "CrashDumpEnabled", v) },
            Label = v => v switch
            {
                0 => "不记录",
                3 => "小内存转储",
                2 => "内核转储",
                1 => "完整转储",
                _ => $"自动（{v}）",
            },
        },
        new()
        {
            Id = "auto-reboot", Group = GroupRecovery,
            Header = "蓝屏后自动重启",
            Description = "关闭后蓝屏会停在错误界面。",
            Glyph = "\uE777",
            Read = () => SystemFeatures.ReadRegistryInt(CrashKey, "AutoReboot"),
            Command = v => new[] { SysFeatureRules.RegAdd(CrashKey, "AutoReboot", v) },
            Label = v => v != 0 ? "已开启" : "已关闭",
        },

        
        new()
        {
            Id = "error-reporting", Group = GroupPrivacy,
            Header = "Windows 错误报告",
            Description = "关闭后崩溃信息不再上报。",
            Glyph = "\uE730",
            
            Read = () => SystemFeatures.ReadRegistryInt(WerKey, "Disabled") ?? 0,
            Command = v => new[] { SysFeatureRules.RegAdd(WerKey, "Disabled", v) },
            Label = v => v != 0 ? "已关闭" : "已开启",
        },
        new()
        {
            Id = "ceip", Group = GroupPrivacy,
            Header = "客户体验改善计划",
            Description = "关闭后不再上传使用情况统计。",
            Glyph = "\uE9D2",
            Read = () => SystemFeatures.ReadRegistryInt(SqmKey, "CEIPEnable") ?? 0,
            Command = v => new[] { SysFeatureRules.RegAdd(SqmKey, "CEIPEnable", v) },
            Label = v => v != 0 ? "已开启" : "已关闭",
        },
        new()
        {
            Id = "remote-desktop", Group = GroupPrivacy,
            Header = "远程桌面",
            Description = "教室机建议保持关闭。",
            Glyph = "\uE8AF",
            
            Read = () => SystemFeatures.ReadRegistryInt(RdpKey, "fDenyTSConnections"),
            Command = v => new[] { SysFeatureRules.RegAdd(RdpKey, "fDenyTSConnections", v) },
            Label = v => v != 0 ? "已关闭" : "已开启",
        },
    };

    
    private static string SecLabel(int seconds) => seconds switch
    {
        0 => "从不",
        < 60 => $"{seconds} 秒",
        < 3600 => $"{seconds / 60} 分钟",
        _ => seconds % 3600 == 0 ? $"{seconds / 3600} 小时" : $"{seconds / 60} 分钟",
    };
}
