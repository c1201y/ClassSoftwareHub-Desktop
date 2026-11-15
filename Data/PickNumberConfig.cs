using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClassSoftwareHub.Desktop.Data;

/// <summary>
/// 随机抽号的本地存档（%LOCALAPPDATA%\ClassSoftwareHub\pick-number.json）。
///
/// ⚠️ 为什么要有这个共用类型（2026-09-27 加名单模式时发现的隐患）：
///   原先「工具页」和「置顶浮窗」各写了一份**自己的**嵌套 Config 类，字段还不一样。
///   两边共用同一个文件，谁保存都会整份覆盖 —— 字段一旦不同步，
///   后保存的那边就会把另一边刚存的字段悄悄抹掉（比如浮窗抽一次号，就可能把导入的名单清空）。
///   现在两边都只用这一个类型，加字段不会再漏。
/// </summary>
public sealed class PickNumberConfig
{
    public static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassSoftwareHub", "pick-number.json");

    // ── 号码模式 ──
    public int From { get; set; } = 1;
    public int To { get; set; } = 50;
    public int Count { get; set; } = 1;
    public bool NoRepeat { get; set; } = true;
    public List<int> Used { get; set; } = new();

    // ── 名单模式（2026-09-27 加）──
    /// <summary>0 = 按号码范围，1 = 按名单。</summary>
    public int Mode { get; set; }
    public List<string> Roster { get; set; } = new();
    public List<string> UsedNames { get; set; } = new();
    public string RosterSource { get; set; } = "";

    public static PickNumberConfig Load()
    {
        var cfg = new PickNumberConfig();
        try
        {
            if (File.Exists(StorePath))
                cfg = JsonSerializer.Deserialize<PickNumberConfig>(File.ReadAllText(StorePath)) ?? new PickNumberConfig();
        }
        catch { /* 存档坏了就用默认值 */ }

        // 老存档里没有这几个字段 / 手改过 json 填了 null —— 统一兜住，调用方就不用到处判空
        cfg.Used ??= new List<int>();
        cfg.Roster ??= new List<string>();
        cfg.UsedNames ??= new List<string>();
        cfg.RosterSource ??= "";
        return cfg;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(this));
        }
        catch { /* 存不上不影响使用 */ }
    }
}
