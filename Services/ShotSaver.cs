using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ClassSoftwareHub.Desktop.Services;





public static class ShotSaver
{
    
    public static string Dir()
    {
        try
        {
            var d = App.Settings.Current.ShotSaveDir;
            if (!string.IsNullOrWhiteSpace(d) && Directory.Exists(d)) return d;
        }
        catch {  }

        try { return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
        catch { return ""; }
    }

    
    public static string DirSetting()
    {
        try
        {
            var d = App.Settings.Current.ShotSaveDir;
            if (!string.IsNullOrWhiteSpace(d)) return d;
        }
        catch { }
        return Dir();
    }

    
    public static string? Save(byte[] png, DateTime? at = null)
    {
        try
        {
            var dir = Dir();
            if (string.IsNullOrWhiteSpace(dir)) return null;
            Directory.CreateDirectory(dir);

            var stamp = (at ?? DateTime.Now).ToString("yyyyMMdd_HHmmss");
            var path = Path.Combine(dir, $"截图_{stamp}.png");
            for (var i = 2; i < 200 && File.Exists(path); i++)
                path = Path.Combine(dir, $"截图_{stamp}_{i}.png");

            File.WriteAllBytes(path, png);
            ScreenCapture.Log("截图已自动保存：" + path);
            return path;
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("截图自动保存失败: " + ex.Message);
            return null;
        }
    }

    
    public static async Task<byte[]?> BmpToPngAsync(byte[] bmp)
    {
        try
        {
            var ras = new InMemoryRandomAccessStream();
            var dw = new DataWriter(ras);
            dw.WriteBytes(bmp);
            await dw.StoreAsync();
            await dw.FlushAsync();
            dw.DetachStream();
            ras.Seek(0);

            var dec = await BitmapDecoder.CreateAsync(BitmapDecoder.BmpDecoderId, ras);
            using var soft = await dec.GetSoftwareBitmapAsync();

            var outRas = new InMemoryRandomAccessStream();
            var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outRas);
            enc.SetSoftwareBitmap(soft);
            await enc.FlushAsync();

            var bytes = new byte[(int)outRas.Size];
            using var dr = new DataReader(outRas.GetInputStreamAt(0));
            await dr.LoadAsync((uint)outRas.Size);
            dr.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("截图转 PNG 失败: " + ex.Message);
            return null;
        }
    }

    
    public static async Task<string?> SaveFrameAsync(ScreenFrame frame, int cx, int cy, int cw, int ch)
    {
        try
        {
            var bmp = ScreenCapture.ToBmp(frame, cx, cy, cw, ch);
            if (bmp is null) return null;
            var png = await BmpToPngAsync(bmp);
            if (png is null) return null;
            return Save(png);
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("截图自动保存失败: " + ex.Message);
            return null;
        }
    }
}
