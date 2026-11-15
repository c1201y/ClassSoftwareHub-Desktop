using System.Reflection;

namespace ClassSoftwareHub.Desktop.Services;


public static class EmbeddedAssets
{
    public static string ReadText(string fileNameEndingWith)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileNameEndingWith, StringComparison.OrdinalIgnoreCase));
        if (name is null) return string.Empty;

        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null) return string.Empty;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    
    
    
    
    public static bool ExtractTo(string fileNameEndingWith, string outPath)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(fileNameEndingWith, StringComparison.OrdinalIgnoreCase));
            if (name is null) return false;

            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) return false;

            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var file = File.Create(outPath);
            stream.CopyTo(file);
            return true;
        }
        catch
        {
            return false;
        }
    }

    
    public static string? ExtractToCache(string fileNameEndingWith, string outFileName)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(fileNameEndingWith, StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;

            
            
            var cacheDir = CacheHome.InstanceDir;
            Directory.CreateDirectory(cacheDir);
            var outPath = Path.Combine(cacheDir, outFileName);

            
            using var stream = asm.GetManifestResourceStream(name)!;
            var stale = true;
            try { stale = !File.Exists(outPath) || new FileInfo(outPath).Length != stream.Length; }
            catch { stale = true; }

            if (stale)
            {
                using var file = File.Create(outPath);
                stream.CopyTo(file);
            }
            return outPath;
        }
        catch
        {
            return null;
        }
    }

}
