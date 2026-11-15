using System.Collections.Generic;

namespace ClassSoftwareHub.Desktop.Data;















public sealed class UiText
{
    
    public Dictionary<string, string> All { get; } = new();

    
    public string T(string key, string fallback) =>
        All.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    
    public string AppTitle { get; set; } = "";

    
    public string HomeTitle { get; set; } = "";

    
    public string HomeSubtitle { get; set; } = "";

    
    public string AppVersion { get; set; } = "";

    
    public string ShortVersion
    {
        get
        {
            var v = AppVersion.Trim();
            if (v.Length == 0) return Core.ShellConfig.SiteVersionTarget;
            var end = v.IndexOfAny(new[] { ' ', '-', '(' });
            return end > 0 ? v.Substring(0, end).Trim() : v;
        }
    }
}
