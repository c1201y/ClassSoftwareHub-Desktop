using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ClassSoftwareHub.Desktop.Services;


public sealed class QrRenderResult
{
    public required WriteableBitmap Bitmap { get; init; }
    public required byte[] Pixels { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}


public static class QrCodeImage
{
    private const byte Dark = 0x1A;
    private const byte Light = 0xFF;

    
    public static QrRenderResult Render(string text, int modulePixels = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M, forceUtf8: true);

        var matrix = data.ModuleMatrix;
        var modules = matrix.Count;
        var quiet = 4;
        var pixelsPerModule = Math.Max(2, modulePixels);
        var size = (modules + quiet * 2) * pixelsPerModule;
        var pixels = new byte[size * size * 4];

        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = Light;
            pixels[i + 1] = Light;
            pixels[i + 2] = Light;
            pixels[i + 3] = 0xFF;
        }

        for (var y = 0; y < modules; y++)
        {
            var row = matrix[y];
            for (var x = 0; x < modules; x++)
            {
                if (!row[x]) continue;
                FillBlock(pixels, size, (x + quiet) * pixelsPerModule, (y + quiet) * pixelsPerModule,
                    pixelsPerModule, Dark);
            }
        }

        var bitmap = new WriteableBitmap(size, size);
        using (var stream = bitmap.PixelBuffer.AsStream())
            stream.Write(pixels, 0, pixels.Length);
        bitmap.Invalidate();

        return new QrRenderResult
        {
            Bitmap = bitmap,
            Pixels = pixels,
            Width = size,
            Height = size,
        };
    }

    
    public static async Task<byte[]> EncodePngAsync(byte[] pixels, int width, int height)
    {
        var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();

        var bytes = new byte[(int)stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static void FillBlock(byte[] pixels, int width, int left, int top, int size, byte value)
    {
        for (var y = 0; y < size; y++)
        {
            var row = (top + y) * width;
            for (var x = 0; x < size; x++)
            {
                var offset = (row + left + x) * 4;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 0xFF;
            }
        }
    }
}
