using System;

namespace ClassSoftwareHub.Desktop.Core;


public enum AppUriKind
{
    
    Settings,

    
    Page,

    
    Tool,

    
    Article,

    
    System,
}


public sealed record AppUriTarget(AppUriKind Kind, string Target, string Raw);


























public static class AppUri
{
    
    public const string Scheme = "csh";

    
    public const string SystemSchemePrefix = "ms-settings:";

    
    
    
    
    
    
    
    public static AppUriTarget? Parse(string? raw)
    {
        var text = (raw ?? "").Trim().Trim('"');
        if (text.Length == 0) return null;

        if (text.StartsWith(SystemSchemePrefix, StringComparison.OrdinalIgnoreCase))
            return new AppUriTarget(AppUriKind.System, text[SystemSchemePrefix.Length..], text);

        if (!text.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase)) return null;

        
        
        var rest = text[(Scheme.Length + 1)..];
        if (!rest.StartsWith("//", StringComparison.Ordinal)) rest = "//" + rest;

        if (!Uri.TryCreate(Scheme + ":" + rest, UriKind.Absolute, out var uri)) return null;

        var kind = uri.Host.ToLowerInvariant() switch
        {
            "settings" => AppUriKind.Settings,
            "page" => AppUriKind.Page,
            "tool" => AppUriKind.Tool,
            "article" => AppUriKind.Article,
            _ => (AppUriKind?)null,
        };
        if (kind is null) return null;

        
        var target = uri.AbsolutePath.Trim('/');
        if (target.Length == 0) return null;

        return new AppUriTarget(kind.Value, target, text);
    }

    
    public static string ForSettings(string section) => $"{Scheme}://settings/{section}";

    
    public static string ForPage(string tag) => $"{Scheme}://page/{tag}";

    
    public static string ForTool(string id) => $"{Scheme}://tool/{id}";

    
    public static string ForArticle(string id) => $"{Scheme}://article/{id}";
}
