using System;
using System.Collections.Generic;
using System.Globalization;
using Windows.Foundation;

namespace ClassSoftwareHub.Desktop.Services;










public sealed class PlotViewport
{
    
    public double CenterX { get; set; }
    public double CenterY { get; set; }

    
    public double XSpan { get; set; } = 20;

    public double XMin => CenterX - XSpan / 2;
    public double XMax => CenterX + XSpan / 2;

    public double YSpan(PlotFrame f) => f.Width > 0 ? XSpan * f.Height / f.Width : XSpan;
    public double YMin(PlotFrame f) => CenterY - YSpan(f) / 2;
    public double YMax(PlotFrame f) => CenterY + YSpan(f) / 2;

    
    public void Zoom(double factor, double anchorX, double anchorY, PlotFrame f)
    {
        factor = Math.Clamp(factor, 0.02, 50);

        
        var wx = XMin + anchorX / f.Width * XSpan;
        var wy = YMin(f) + anchorY / f.Height * YSpan(f);

        XSpan *= factor;
        CenterX = wx + (CenterX - wx) * factor;
        CenterY = wy + (CenterY - wy) * factor;
    }

    
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


public readonly record struct PlotFrame(double Left, double Top, double Width, double Height);










public static class FunctionPlot
{
    
    public const double SamplesPerPixel = 1.0;

    
    private const int MinSamples = 2;
    private const int MaxSamples = 4000;

    
    
    
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

        
        
        
        
        
        var lowLimit = frame.Top - frame.Height * 0.5;
        var highLimit = frame.Top + frame.Height * 1.5;

        
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

        
        result.RemoveAll(seg => seg.Count < 2);
        return result;
    }

    
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

    
    
    
    public static List<double> Ticks(double min, double max, double step)
    {
        var list = new List<double>();
        if (!double.IsFinite(step) || step <= 0 || !double.IsFinite(min) || !double.IsFinite(max) || max <= min)
            return list;

        var first = Math.Ceiling(min / step);
        var last = Math.Floor(max / step);

        
        if (last - first > 2000) return list;

        for (var k = first; k <= last; k++) list.Add(k * step);
        return list;
    }

    
    
    
    public static string FormatTick(double value, double step)
    {
        if (Math.Abs(value) < Math.Abs(step) * 1e-6) return "0";   

        var abs = Math.Abs(value);
        if (abs >= 1e5 || abs < 1e-4)
            return value.ToString("0.###e+0", CultureInfo.InvariantCulture);

        var exp = (int)Math.Floor(Math.Log10(Math.Abs(step)));
        var decimals = exp >= 0 ? 0 : Math.Min(-exp, 8);
        return value.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }
}
