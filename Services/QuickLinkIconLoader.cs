using System;
using System.IO;
using System.Reflection;
using System.Threading;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace ClassSoftwareHub.Desktop.Services;










public static class QuickLinkIconLoader
{
    private static readonly object Gate = new();
    private static readonly System.Collections.Generic.Dictionary<string, SvgImageSource> Cache = new();

    
    public static ImageSource? GetIcon(string assetName, Color color)
    {
        var hex = "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
        var key = assetName + "|" + hex;

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var hit)) return hit;
        }

        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = "ClassSoftwareHub.Desktop.Assets.QuickLinks." + assetName;
            using var stream = asm.GetManifestResourceStream(resName);
            if (stream is null) return null;

            string svg;
            using (var reader = new StreamReader(stream))
                svg = reader.ReadToEnd();

            svg = svg.Replace("__C__", hex);

            var dir = Path.Combine(ShellConfig.CachedContentDir, "quickicons");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, key.Replace("|", "_") + ".svg");
            File.WriteAllText(file, svg);

            var src = new SvgImageSource(new Uri(file));
            lock (Gate) Cache[key] = src;
            return src;
        }
        catch
        {
            return null;
        }
    }
}
