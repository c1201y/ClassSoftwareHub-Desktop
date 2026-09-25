using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 截图自动落盘：存到「设置 → 常用工具」里那个目录（**默认桌面**），文件名 `截图_yyyyMMdd_HHmmss.png`。
/// 撞名自动加序号，不覆盖旧图；闯了什么错都只写日志，绝不往外抛（截图本身不能因为存盘失败就没了）。
/// </summary>
public static class ShotSaver
{
    /// <summary>设置里那个目录；没设 / 不在了 → 系统桌面。</summary>
    public static string Dir()
    {
        try
        {
            var d = App.Settings.Current.ShotSaveDir;
            if (!string.IsNullOrWhiteSpace(d) && Directory.Exists(d)) return d;
        }
        catch { /* 读设置失败就走桌面 */ }

        try { return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
        catch { return ""; }
    }

    /// <summary>显示用：设置里的目录（不管存不存在）。</summary>
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

    /// <summary>存 PNG（自动防撞名）。返回落盘路径；失败 null。</summary>
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

    /// <summary>把 ScreenCapture 给的 BMP 字节转成 PNG 字节（落盘的一般是 PNG）。</summary>
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

    /// <summary>截一块（ScreenFrame + 裁剪区）直接存成 PNG。返回落盘路径；失败 null。</summary>
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
