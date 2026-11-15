using System;
using System.IO;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;


public enum ToolCarrierKind
{
    
    Web,

    
    Native,
}







public sealed record ToolHostRequest(
    string WebToolId, string EntryFile, string Folder, Type NativePage, object? NativeParameter);


















public static class ToolCarrier
{
    
    public const string HostName = "csh.tools";

    
    
    
    
    
    public static string Root => Path.Combine(AppPaths.DataDir, "tools");

    
    private static string ContentRoot => Path.Combine(AppPaths.DataDir, "content", "工具");

    
    
    
    
    
    
    
    
    public static string BundledRoot => Path.Combine(AppContext.BaseDirectory, "WebTools");

    
    public static string? EntryOf(string? webToolId)
    {
        if (string.IsNullOrWhiteSpace(webToolId)) return null;

        
        foreach (var root in new[] { Root, ContentRoot, BundledRoot })
        {
            try
            {
                var entry = Path.Combine(root, webToolId, "index.html");
                if (File.Exists(entry)) return entry;
            }
            catch {  }
        }

        return null;
    }

    
    public static bool CanHost(string? webToolId) =>
        WebViewRuntimeService.IsRuntimeInstalled() && EntryOf(webToolId) is not null;

    
    
    
    
    
    
    
    
    
    
    
    public static bool KeepsAlive(string? webToolId) =>
        string.Equals(webToolId, "function-plot", StringComparison.OrdinalIgnoreCase);

    
    
    
    public static ToolHostRequest? RequestFor(string? webToolId, Type nativePage, object? nativeParameter)
    {
        var entry = EntryOf(webToolId);
        if (entry is null) return null;
        if (!WebViewRuntimeService.IsRuntimeInstalled()) return null;

        return new ToolHostRequest(
            webToolId!,
            entry,
            Path.GetDirectoryName(entry) ?? Root,
            nativePage,
            nativeParameter);
    }
}
