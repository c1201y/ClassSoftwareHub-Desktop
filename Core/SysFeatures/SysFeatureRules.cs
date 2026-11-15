using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClassSoftwareHub.Desktop.Core;










public static class SysFeatureRules
{
    
    public static string RegAdd(string path, string name, int value) =>
        $"reg add \"HKLM\\{path}\" /v {name} /t REG_DWORD /d {value} /f";

    
    
    
    
    
    
    
    public static IReadOnlyList<string> PowerSettingCommands(string subGroup, string setting, int value) => new[]
    {
        $"powercfg /setacvalueindex SCHEME_CURRENT {subGroup} {setting} {value}",
        "powercfg /setactive SCHEME_CURRENT",
    };

    
    
    
    
    
    
    
    
    
    
    
    
    public static string BuildScript(IReadOnlyList<(SysFeatureDef Def, int Value)> changes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 437 >nul");

        foreach (var (def, value) in changes)
            foreach (var line in def.Command(value))
                sb.AppendLine(line);

        return sb.ToString();
    }

    
    
    
    
    
    
    
    public static List<string> FailedAfterApply(IReadOnlyList<(SysFeatureDef Def, int Value)> changes) =>
        changes.Where(c => c.Def.Read() != c.Value).Select(c => c.Def.Id).ToList();
}
