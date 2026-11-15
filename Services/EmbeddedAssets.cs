using System.Reflection;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>读取内嵌的注入脚本 / 图标资源（单文件发布下也能用）。</summary>
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

    /// <summary>
    /// 把内嵌资源**原样**写到指定路径（「下载示例文件」用）。
    /// 与 <see cref="ExtractToCache"/> 的区别：这个不比较新旧、直接覆盖，因为用户是自己点了"保存到这儿"。
    /// </summary>
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

    /// <summary>把内嵌图片释放到本地缓存目录，返回文件路径（BitmapImage 用）。</summary>
    public static string? ExtractToCache(string fileNameEndingWith, string outFileName)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(fileNameEndingWith, StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;

            // ⚠️ 缓存统一进「按版本分的子目录」：cache\dv{版本}-{yyyyMMddHHmm}\（见 CacheHome）。
            //    以前全堆在 cache\ 根下、靠文件名区分版本，越攒越乱、也没法按版本回收。
            var cacheDir = CacheHome.InstanceDir;
            Directory.CreateDirectory(cacheDir);
            var outPath = Path.Combine(cacheDir, outFileName);

            // 缓存里的跟内嵌的对不上（换了图）就重写一份 —— 不然换了 banner 还显示旧图
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
