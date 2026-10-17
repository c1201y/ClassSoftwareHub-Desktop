using System;
using System.IO;
using System.Reflection;
using System.Threading;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 把快捷入口那几个 SVG 图标按**当前主题色**染出来。
///
/// ⚠️ 为什么不是直接 <c>SvgImageSource(uri)</c> 原样加载：
///    那三个 SVG 是单色图标（GitHub/QQ 是填充、主页是描边），原色是黑 / <c>#141520</c>，
///    深色界面上直接糊成一片看不见。所以这里把 SVG 里的 <c>__C__</c> 占位符替换成主题文字色
///    （跟同页 FontIcon 用的 <c>TextFillColorPrimaryBrush</c> 同一套），浅色/深色各自一套，
///    结果按「资源名 + 颜色」缓存，主题切换时换一张即可。
/// </summary>
public static class QuickLinkIconLoader
{
    private static readonly object Gate = new();
    private static readonly System.Collections.Generic.Dictionary<string, SvgImageSource> Cache = new();

    /// <summary>取染好色的图标源；资源缺失或染不出来返回 <c>null</c>（调用方退回 FontIcon）。</summary>
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
