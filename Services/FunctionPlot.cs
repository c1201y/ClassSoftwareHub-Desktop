using System;
using System.Collections.Generic;
using System.Globalization;
using Windows.Foundation;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 绘图视图。
///
/// <para>
/// ⛔⛔ <b>只存「中心 + 横向跨度」，纵向跨度是按绘图区宽高比算出来的</b> —— 这样无论怎么缩放、
/// 窗口怎么拉，x 与 y 的缩放比例永远相同，圆画出来就是圆。用户单独把 y 拉长拉扁虽然有时想看，
/// 但那是"图变形"不是"函数变了"，第一版不做，免得每次都要解释"为什么这条线看起来不一样"。
/// </para>
/// </summary>
public sealed class PlotViewport
{
    /// <summary>视图中心（世界坐标）。</summary>
    public double CenterX { get; set; }
    public double CenterY { get; set; }

    /// <summary>横向跨度（世界坐标）。缩放就是在改它。</summary>
    public double XSpan { get; set; } = 20;

    public double XMin => CenterX - XSpan / 2;
    public double XMax => CenterX + XSpan / 2;

    public double YSpan(PlotFrame f) => f.Width > 0 ? XSpan * f.Height / f.Width : XSpan;
    public double YMin(PlotFrame f) => CenterY - YSpan(f) / 2;
    public double YMax(PlotFrame f) => CenterY + YSpan(f) / 2;

    /// <summary>缩放：<paramref name="factor"/> &lt; 1 是放大（跨度变小）。</summary>
    public void Zoom(double factor, double anchorX, double anchorY, PlotFrame f)
    {
        factor = Math.Clamp(factor, 0.02, 50);

        // 锚点在世界坐标里的位置，缩放前后保持不动
        var wx = XMin + anchorX / f.Width * XSpan;
        var wy = YMin(f) + anchorY / f.Height * YSpan(f);

        XSpan *= factor;
        CenterX = wx + (CenterX - wx) * factor;
        CenterY = wy + (CenterY - wy) * factor;
    }

    /// <summary>按屏幕像素位移平移视图。</summary>
    public void Pan(double dxPixels, double dyPixels, PlotFrame f)
    {
        if (f.Width <= 0 || f.Height <= 0) return;
        CenterX -= dxPixels / f.Width * XSpan;
        CenterY += dyPixels / f.Height * YSpan(f);
    }

    public void Reset()
    {
        CenterX = 0;
        CenterY = 0;
        XSpan = 20;
    }
}

/// <summary>绘图区在画布里的位置（画布本地坐标）。</summary>
public readonly record struct PlotFrame(double Left, double Top, double Width, double Height);

/// <summary>
/// 函数图像的采样与刻度计算。
///
/// <para>
/// ⛔⛔ <b>调用方拿到的是"已经断好的若干段"</b>，不是一长串点 —— 不连续处（<c>NaN</c>、<c>±∞</c>、
/// 以及跨过渐近线的跳变）必须断开，否则 <c>tan(x)</c>、<c>1/x</c> 会被连出一条竖直的假线，
/// 看上去像函数真在那里取过这些值。
/// </para>
/// </summary>
public static class FunctionPlot
{
    /// <summary>每个像素采几个点。1 已经足够平滑（再多只是白烧 CPU）。</summary>
    public const double SamplesPerPixel = 1.0;

    /// <summary>采样点数的上下限（窗口极宽 / 极窄时的保险）。</summary>
    private const int MinSamples = 2;
    private const int MaxSamples = 4000;

    /// <summary>
    /// 把 <paramref name="f"/> 在视图的 x 区间上采样成若干段屏幕坐标折线。
    /// </summary>
    public static List<List<Point>> Sample(
        Func<double, double> f, PlotViewport vp, PlotFrame frame, double samplesPerPixel = SamplesPerPixel)
    {
        var result = new List<List<Point>>();
        if (frame.Width <= 0 || frame.Height <= 0) return result;

        var n = (int)Math.Clamp(frame.Width * samplesPerPixel, MinSamples, MaxSamples);

        var yMin = vp.YMin(frame);
        var yMax = vp.YMax(frame);
        var yScale = frame.Height / (yMax - yMin);

        var xMin = vp.XMin;
        var dx = vp.XSpan / (n - 1);
        var pxStep = frame.Width / (n - 1);

        // 纵向钳位：只留 **1.5 倍屏高**的余量。
        // ⛔⛔ 曾经是 20 倍 —— 缩小视图后越界点被钳到 ±近万像素，大坐标进 PathGeometry
        //    之后光栅化裁剪会**整条失败**：连视口内的 23 个点都一起消失（实测缩小到
        //    x 跨度 ~900 就复现）。1.5 倍足够保证"穿越视口的线段方向正确"——
        //    采样密度 1 点/像素，余量内的直线近似误差远小于 1 像素；越界部分本就被 Clip 裁掉。
        var lowLimit = frame.Top - frame.Height * 0.5;
        var highLimit = frame.Top + frame.Height * 1.5;

        // 判"跨过渐近线"的阈值：跳变幅度超过 3 个视口高才算。
        var jumpLimit = (yMax - yMin) * 3;

        List<Point>? current = null;
        var prevY = double.NaN;

        for (var i = 0; i < n; i++)
        {
            var x = xMin + i * dx;

            double y;
            try { y = f(x); }
            catch { y = double.NaN; }

            var ok = double.IsFinite(y);

            // ⛔⛔ 渐近线：相邻两点数值跳变极大**且**分居 0 的两侧。
            //    光看跳变幅度不行（y=1000x 这种陡直线也会跳很大），必须同时跨过 0
            //    —— ±∞ 必然跨 0，而连续陡增不会。
            if (ok && double.IsFinite(prevY) && (y > 0) != (prevY > 0) && Math.Abs(y - prevY) > jumpLimit)
                ok = false;

            if (!ok)
            {
                current = null;
                prevY = y;
                continue;
            }

            var py = Math.Clamp(frame.Top + (yMax - y) * yScale, lowLimit, highLimit);

            if (current is null)
            {
                current = new List<Point>(n);
                result.Add(current);
            }

            current.Add(new Point(frame.Left + i * pxStep, py));
            prevY = y;
        }

        // 单点段画不出线，丢掉
        result.RemoveAll(seg => seg.Count < 2);
        return result;
    }

    /// <summary>取一个「好看」的刻度步长：1 / 2 / 5 × 10ⁿ。</summary>
    public static double NiceStep(double range, int targetTicks)
    {
        if (!double.IsFinite(range) || range <= 0 || targetTicks <= 0) return 1;

        var raw = range / targetTicks;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        if (!double.IsFinite(mag) || mag <= 0) return 1;

        var norm = raw / mag;
        var mult = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10;
        return mag * mult;
    }

    /// <summary>
    /// 生成 <c>[min, max]</c> 内的刻度值。
    /// </summary>
    public static List<double> Ticks(double min, double max, double step)
    {
        var list = new List<double>();
        if (!double.IsFinite(step) || step <= 0 || !double.IsFinite(min) || !double.IsFinite(max) || max <= min)
            return list;

        var first = Math.Ceiling(min / step);
        var last = Math.Floor(max / step);

        // 保险：步长算歪时不生成上万条刻度
        if (last - first > 2000) return list;

        for (var k = first; k <= last; k++) list.Add(k * step);
        return list;
    }

    /// <summary>
    /// 刻度标签。<b>按步长决定小数位</b>，否则会出现 <c>0.30000000000000004</c> 这种东西。
    /// </summary>
    public static string FormatTick(double value, double step)
    {
        if (Math.Abs(value) < Math.Abs(step) * 1e-6) return "0";   // 消掉「-0」

        var abs = Math.Abs(value);
        if (abs >= 1e5 || abs < 1e-4)
            return value.ToString("0.###e+0", CultureInfo.InvariantCulture);

        var exp = (int)Math.Floor(Math.Log10(Math.Abs(step)));
        var decimals = exp >= 0 ? 0 : Math.Min(-exp, 8);
        return value.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }
}
