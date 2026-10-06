using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 投稿选的图标，在上传前先缩小、重编码（对应网页端 <c>src/gallery/iconResize.ts</c>）。
///
/// 为什么非缩不可：图标在站内只以 44 / 72 px 的磁贴出现，但用户随手选的多半是相机原图或几百 KB 的 PNG。
/// 原图会被原样存进 OSS，再被 <c>/api/icon</c> 原样转发给每一个访客 —— 换不来任何清晰度，
/// 只让卡片白等、让中继白出一次流量。所以进桶之前先压到 <see cref="MaxEdge"/>。
///
/// 为什么是 128 而不是 64：多留一倍余量，方便维护者后续再本地化一次。
///
/// 降级策略：压缩只是优化，绝不能变成上传的门槛 —— 解码不了、编不出来、
/// 压完反而更大，任何一步出问题都返回 null，让调用方直接用原文件。
/// </summary>
public static class IconResize
{
    /// <summary>缩放后的最长边（像素）。</summary>
    public const int MaxEdge = 128;

    /// <summary>原图已经这么小就不值得重新编码了（再编一次很可能反而变大）。</summary>
    private const long SkipBelowBytes = 48 * 1024;

    /// <summary>
    /// 把图片缩到 <see cref="MaxEdge"/> 以内并重编码成 PNG，写到临时文件。
    /// </summary>
    /// <returns>
    /// 压缩后的**临时文件路径**（调用方用完可删）；不需要或压不动时返回 <c>null</c>，
    /// 此时请直接用原文件上传。
    /// </returns>
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

            // 压完反而更大（小尺寸高细节图会这样）就别折腾了
            if (new FileInfo(tempPath).Length >= sourceBytes)
            {
                TryDelete(tempPath);
                return null;
            }
            return tempPath;
        }
        catch
        {
            // 任何一步失败（不是图片、格式不认、没有编解码器）都退回原文件
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 删不掉就留给系统清 */ }
    }
}
