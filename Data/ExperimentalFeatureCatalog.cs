using System;
using System.Collections.Generic;

namespace ClassSoftwareHub.Desktop.Data;























public static class ExperimentalFeatureCatalog
{
    public static readonly IReadOnlyList<ExperimentalFeature> All = new List<ExperimentalFeature>
    {
        
        
        

        
        
        new ExperimentalFeature
        {
            Id = "procguard", Name = "程序专杀",
            Desc = "为指定程序设定结束时间点，到点结束其后驻留进程",
            Glyph = "\uE7E8", Tag = "procguard"
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
