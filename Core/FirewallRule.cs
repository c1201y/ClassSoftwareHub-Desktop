using System;
using System.Collections.Generic;

namespace ClassSoftwareHub.Desktop.Core;



















public sealed class FirewallRule
{
    
    public string? Action { get; private init; }

    
    public string? Name { get; private init; }

    
    public string? App { get; private init; }

    
    public bool ActiveInbound { get; private init; }

    
    
    
    
    
    public bool CoversPublic { get; private init; }

    
    
    
    public static FirewallRule? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!text.StartsWith("v2.", StringComparison.Ordinal)) return null;   

        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in text.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = segment.IndexOf('=');
            if (idx <= 0) continue;

            var key = segment[..idx].Trim();
            var value = segment[(idx + 1)..].Trim();
            if (key.Length == 0) continue;

            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<string>();
            list.Add(value);
        }

        if (map.Count == 0) return null;

        return new FirewallRule
        {
            Action = First(map, "Action"),
            Name = First(map, "Name"),
            App = First(map, "App"),
            ActiveInbound = HasValue(map, "Active", "TRUE") && HasValue(map, "Dir", "In"),
            CoversPublic = ProfileCoversPublic(map),
        };
    }

    
    private static bool ProfileCoversPublic(Dictionary<string, List<string>> map)
    {
        
        
        
        
        if (!map.TryGetValue("Profile", out var profiles) || profiles.Count == 0) return true;

        foreach (var profile in profiles)
        {
            if (profile.Equals("Any", StringComparison.OrdinalIgnoreCase)) return true;
            if (profile.Equals("All", StringComparison.OrdinalIgnoreCase)) return true;
            if (profile.Equals("Public", StringComparison.OrdinalIgnoreCase)) return true;
            if (profile.Equals("0x7fffffff", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static string? First(Dictionary<string, List<string>> map, string key) =>
        map.TryGetValue(key, out var list) && list.Count > 0 ? list[0] : null;

    private static bool HasValue(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var list)) return false;

        foreach (var item in list)
            if (item.Equals(value, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }
}
