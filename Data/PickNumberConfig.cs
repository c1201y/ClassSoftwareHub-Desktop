using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClassSoftwareHub.Desktop.Data;










public sealed class PickNumberConfig
{
    public static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassSoftwareHub", "pick-number.json");

    
    public int From { get; set; } = 1;
    public int To { get; set; } = 50;
    public int Count { get; set; } = 1;
    public bool NoRepeat { get; set; } = true;
    public List<int> Used { get; set; } = new();

    
    
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
        catch {  }

        
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
        catch {  }
    }
}
