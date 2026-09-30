using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 一套命名的时钟外观（2026-09-27 Nick 要的「预设」）。
/// 字段与 <see cref="ClockSettings"/> 一一对应 —— 存的是值，不是引用，
/// 所以预设之间互不影响。
/// </summary>
public sealed class ClockPreset
{
    public string Name { get; set; } = "";
    public string BackgroundImagePath { get; set; } = "";
    public ClockVeil Veil { get; set; }
    public double VeilStrength { get; set; } = 55;
    public ClockTone Tone { get; set; }
    public ClockInk Ink { get; set; }
    public string FontFamily { get; set; } = "Bahnschrift";
    public double Scale { get; set; } = 1.0;
    public bool ShowSeconds { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public bool Hour12 { get; set; }
    public DateTime SavedAt { get; set; } = DateTime.Now;

    public static ClockPreset From(string name, ClockSettings s) => new()
    {
        Name = name,
        BackgroundImagePath = s.BackgroundImagePath,
        Veil = s.Veil,
        VeilStrength = s.VeilStrength,
        Tone = s.Tone,
        Ink = s.Ink,
        FontFamily = s.FontFamily,
        Scale = s.Scale,
        ShowSeconds = s.ShowSeconds,
        ShowDate = s.ShowDate,
        Hour12 = s.Hour12,
    };

    public ClockSettings ToSettings() => new()
    {
        BackgroundImagePath = BackgroundImagePath,
        Veil = Veil,
        VeilStrength = VeilStrength,
        Tone = Tone,
        Ink = Ink,
        FontFamily = FontFamily,
        Scale = Scale,
        ShowSeconds = ShowSeconds,
        ShowDate = ShowDate,
        Hour12 = Hour12,
    };

    /// <summary>列表里那行小字：背景图 / 蒙版 / 底色 / 字体，让人一眼认出是哪套。</summary>
    public string Summary()
    {
        var bits = new List<string>
        {
            string.IsNullOrWhiteSpace(BackgroundImagePath)
                ? "无背景图"
                : "背景图 " + Path.GetFileName(BackgroundImagePath),
            Veil == ClockVeil.None
                ? "无蒙版"
                : $"{ClockRender.VeilLabels[(int)Veil]} {Math.Round(VeilStrength)}%",
            ClockRender.ToneLabels[(int)Tone],
            ClockRender.FontLabels[Math.Max(0, Array.IndexOf(ClockRender.FontFamilies, FontFamily))],
        };
        return string.Join(" · ", bits);
    }
}

/// <summary>
/// 预设与「上次使用的设置」的落盘（%LOCALAPPDATA%\ClassSoftwareHub\clock-presets.json）。
/// 分两块：<see cref="Data.Presets"/> 是用户主动命名的多套方案；
/// <see cref="Data.LastUsed"/> 是自动记忆 —— 关掉应用再进来，外观还是你上次调的样子。
/// </summary>
public static class ClockPresetStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassSoftwareHub");

    private static string FilePath => Path.Combine(Dir, "clock-presets.json");

    public sealed class Data
    {
        public ClockSettings? LastUsed { get; set; }

        /// <summary>
        /// 上次"正在使用的那套预设"的名字；空 = 上次没在用预设（外观是手调/默认的）。
        ///
        /// ⚠️ 2026-09-28（Nick）：用来实现"每次启动遵循上次"——
        /// 非空且这套预设还在 → 启动就按**那套预设**铺（顺便把列表里那套标成"使用中"）；
        /// 空 → 就按 <see cref="LastUsed"/> 那个外观铺，**不强行套任何预设**。
        /// 用户手动改了外观就会把它清空（那时已经是"自定义"了）。
        /// </summary>
        public string LastUsedPreset { get; set; } = "";

        public List<ClockPreset> Presets { get; set; } = new();
    }

    public static Data Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Data();
            var json = File.ReadAllText(FilePath);
            var data = JsonSerializer.Deserialize<Data>(json, JsonOpts) ?? new Data();
            data.Presets ??= new List<ClockPreset>();
            return data;
        }
        catch (Exception ex)
        {
            // 文件坏了就当没存过 —— 不能让一个坏的 json 把工具页卡在启动阶段
            Log("读取预设失败，按空处理：" + ex.Message);
            return new Data();
        }
    }

    public static void Save(Data data)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // 先写临时文件再替换：写到一半被打断也不会留下半个 json
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOpts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log("保存预设失败：" + ex.Message);
        }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(Path.Combine(Dir, "clock-presets.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { /* 日志本身写不进去就算了，别反过来影响主流程 */ }
    }
}
