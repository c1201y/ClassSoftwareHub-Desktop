using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClassSoftwareHub.Desktop.Core;






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






public static class ClockPresetStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassSoftwareHub");

    private static string FilePath => Path.Combine(Dir, "clock-presets.json");

    public sealed class Data
    {
        public ClockSettings? LastUsed { get; set; }

        
        
        
        
        
        
        
        
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
            
            Log("读取预设失败，按空处理：" + ex.Message);
            return new Data();
        }
    }

    public static void Save(Data data)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOpts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log("保存预设失败：" + ex.Message);
        }
    }

    private static void Log(string message) => AppLog.Info("clock-presets", message);
}
