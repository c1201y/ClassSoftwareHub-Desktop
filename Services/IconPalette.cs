using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace ClassSoftwareHub.Desktop.Services;













public static class IconPalette
{
    private static readonly Dictionary<string, Color?> Cache = new(StringComparer.OrdinalIgnoreCase);

    
    private static readonly Dictionary<string, Task<Color?>> Pending = new(StringComparer.OrdinalIgnoreCase);

    
    private const int SampleSize = 48;

    
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2.5) };

    
    public static bool TryGetCached(string? url, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(url)) return false;

        lock (Cache)
        {
            if (Cache.TryGetValue(url, out var cached) && cached is { } c)
            {
                color = c;
                return true;
            }
        }

        return false;
    }

    
    
    
    
    public static void Preload(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        lock (Cache)
        {
            if (Cache.ContainsKey(url)) return;
        }

        _ = PickAsync(url);   
    }

    public static Task<Color?> PickAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<Color?>(null);
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return Task.FromResult<Color?>(null);

        
        if (url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return Task.FromResult<Color?>(null);

        lock (Cache)
        {
            if (Cache.TryGetValue(url, out var cached)) return Task.FromResult(cached);
        }

        lock (Pending)
        {
            if (Pending.TryGetValue(url, out var running)) return running;

            var task = FetchAsync(url);
            Pending[url] = task;
            return task;
        }
    }

    private static async Task<Color?> FetchAsync(string url)
    {
        Color? picked = null;
        try
        {
            
            
            
            picked = await PickFromLocalAsync(url).ConfigureAwait(false);

            
            
            if (picked is null)
            {
                try
                {
                    var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                    if (bytes.Length > 0) picked = await FromBytesAsync(bytes).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Core.AppLog.Exception("palette", ex, "取图标主色");
                }

                if (picked is null) picked = await PickFromLocalAsync(url).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (Pending) { Pending.Remove(url); }
        }

        lock (Cache)
        {
            if (Cache.Count > 128) Cache.Clear();
            Cache[url] = picked;
        }

        return picked;
    }

    
    
    
    
    
    
    private static async Task<Color?> PickFromLocalAsync(string url)
    {
        if (!AppIconCache.TryGetLocalPath(url, out var path)) return null;

        try
        {
            var bytes = await System.IO.File.ReadAllBytesAsync(path).ConfigureAwait(false);
            if (bytes.Length == 0) return null;

            var color = await FromBytesAsync(bytes).ConfigureAwait(false);
            if (color is not null)
                Core.AppLog.Debug("palette", "本机缓存取色：" + System.IO.Path.GetFileName(path));

            return color;
        }
        catch { return null; }
    }

    private static async Task<Color?> FromBytesAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();

        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        var transform = new BitmapTransform
        {
            ScaledWidth = SampleSize,
            ScaledHeight = SampleSize,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };

        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        var px = data.DetachPixelData();

        double sumR = 0, sumG = 0, sumB = 0, sumW = 0;

        for (var i = 0; i + 3 < px.Length; i += 4)
        {
            double b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
            if (a < 128) continue;

            ToHsv(r, g, b, out _, out var s, out var v);

            if (s < 0.18) continue;          
            if (v < 0.12 || v > 0.96) continue;

            
            var weight = s * (1 - Math.Abs(v - 0.55) * 1.4);
            if (weight <= 0) continue;

            sumR += r * weight;
            sumG += g * weight;
            sumB += b * weight;
            sumW += weight;
        }

        if (sumW < 1e-6) return null;

        var cr = sumR / sumW;
        var cg = sumG / sumW;
        var cb = sumB / sumW;

        
        ToHsv(cr, cg, cb, out var h, out var sat, out var val);
        sat = Math.Clamp(Math.Max(sat, 0.42), 0.42, 0.85);
        val = Math.Clamp(val, 0.46, 0.72);

        return FromHsv(h, sat, val);
    }

    
    
    
    
    
    public static LinearGradientBrush Gradient(Color color)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0.5, 0),
            EndPoint = new Windows.Foundation.Point(0.5, 1),
        };

        brush.GradientStops.Add(new GradientStop { Color = WithAlpha(color, 0xB4), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = WithAlpha(color, 0x66), Offset = 0.52 });
        brush.GradientStops.Add(new GradientStop { Color = WithAlpha(color, 0x00), Offset = 1 });

        return brush;
    }

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static void ToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        r /= 255; g /= 255; b /= 255;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        v = max;
        s = max <= 0 ? 0 : delta / max;

        if (delta <= 0) { h = 0; return; }

        if (max == r) h = 60 * (((g - b) / delta) % 6);
        else if (max == g) h = 60 * ((b - r) / delta + 2);
        else h = 60 * ((r - g) / delta + 4);

        if (h < 0) h += 360;
    }

    private static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        var m = v - c;

        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };

        return Color.FromArgb(255,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
