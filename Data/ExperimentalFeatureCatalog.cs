using System;
using System.Collections.Generic;

namespace ClassSoftwareHub.Desktop.Data;























public static class ExperimentalFeatureCatalog
{
    public static readonly IReadOnlyList<ExperimentalFeature> All = new List<ExperimentalFeature>
    {
        
        
        
        new ExperimentalFeature
        {
            Id = "machinecheck", Name = "本机核实",
            Desc = "对照本机的已安装程序记录，看清单里的软件装没装",
            Glyph = "\uE7F4", Tag = "machinecheck"
        },

        
        
        new ExperimentalFeature
        {
            Id = "procguard", Name = "程序专杀",
            Desc = "为指定程序设定结束时间点，到点结束其后驻留进程",
            Glyph = "\uE7E8", Tag = "procguard"
        },

        
        
        new ExperimentalFeature
        {
            Id = "uninstaller", Name = "应用卸载",
            Desc = "扫描本机已安装的软件，可卸载其中的任意一款",
            Glyph = "\uE74D", Tag = "uninstaller"
        },

        
        
        
        
        new ExperimentalFeature
        {
            Id = "startup", Name = "开机启动项",
            Desc = "查看开机时会自动启动的程序，并逐个关掉",
            Glyph = "\uE945", Tag = "startup"
        },

        
        
        
        
        
        
        
    };

    
    
    
    
    
    
    
    
    
    public static ExperimentalFeature? Find(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        foreach (var def in All)
        {
            if (string.Equals(def.Tag, tag, StringComparison.OrdinalIgnoreCase)) return def;
        }

        return null;
    }
}
