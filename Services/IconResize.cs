using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ClassSoftwareHub.Desktop.Services;













public static class IconResize
{
    
    public const int MaxEdge = 128;

    
    private const long SkipBelowBytes = 48 * 1024;

    
    
    
    
    
    
    
    public static async Task<string?> ShrinkToTempAsync(string sourcePath, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(sourcePath)) return null;
            var sourceBytes = new FileInfo(sourcePath).Length;
            if (sourceBytes <= SkipBelowBytes) return null;

            var source = await StorageFile.GetFileFromPathAsync(sourcePath);
            using var input = await source.OpenAsync(FileAccessMode.Read);

            var decoder = await BitmapDecoder.CreateAsync(input);
            var width = decoder.PixelWidth;
            var height = decoder.PixelHeight;
            if (width == 0 || height == 0) return null;

            var scale = Math.Min(1.0, MaxEdge / (double)Math.Max(width, height));
            var targetWidth = Math.Max(1u, (uint)Math.Round(width * scale));
            var targetHeight = Math.Max(1u, (uint)Math.Round(height * scale));

            var transform = new BitmapTransform
            {
                ScaledWidth = targetWidth,
                ScaledHeight = targetHeight,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };

            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            using var memory = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memory);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();

            memory.Seek(0);
            var tempPath = Path.Combine(Path.GetTempPath(), "csh-icon-" + Guid.NewGuid().ToString("N") + ".png");
            await using (var file = File.Create(tempPath))
            {
                await memory.AsStreamForRead().CopyToAsync(file, ct).ConfigureAwait(false);
            }

            
            if (new FileInfo(tempPath).Length >= sourceBytes)
            {
                TryDelete(tempPath);
                return null;
            }
            return tempPath;
        }
        catch
        {
            
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch {  }
    }
}
