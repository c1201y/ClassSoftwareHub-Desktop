using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 矩形树图（squarified treemap）布局：把一组权重铺进一个矩形，面积严格正比于权重。
///
/// <para>
/// 为什么用 squarified 而不是"横着切 / 竖着切"：后者会把磁盘里最常见的小文件切成一条条细线，
/// 既看不清也点不中。squarified 会尽量让每块接近正方形 —— 这是 WinDirStat、SpaceSniffer 一类
/// 工具的标准做法。
/// </para>
/// <para>纯函数，可脱离界面断言（见 %TEMP%\csh-math）。</para>
/// </summary>
public static class TreemapLayout
{
    public readonly record struct Tile(int Index, double X, double Y, double Width, double Height);

    /// <summary>
    /// <paramref name="weights"/> 需按从大到小排好序（调用方排）。返回的矩形坐标系与入参一致。
    /// </summary>
    public static List<Tile> Squarify(IReadOnlyList<double> weights, double x, double y, double width, double height)
    {
        var result = new List<Tile>();
        if (weights.Count == 0 || width <= 0 || height <= 0) return result;

        var total = weights.Where(w => w > 0).Sum();
        if (total <= 0) return result;

        var area = width * height;
        var scaled = weights.Select(w => w <= 0 ? 0 : w / total * area).ToList();

        var cursorX = x;
        var cursorY = y;
        var restW = width;
        var restH = height;

        var row = new List<int>();
        var i = 0;

        while (i < scaled.Count)
        {
            var side = Math.Min(restW, restH);

            if (row.Count == 0 || Worst(row, scaled, side) >= WorstWith(row, i, scaled, side))
            {
                row.Add(i);
                i++;
                continue;
            }

            LayoutRow(row, scaled, result, ref cursorX, ref cursorY, ref restW, ref restH);
            row.Clear();
        }

        if (row.Count > 0)
            LayoutRow(row, scaled, result, ref cursorX, ref cursorY, ref restW, ref restH);

        return result;
    }

    /// <summary>把一行铺在最靠边的地方，然后收窄剩余矩形。</summary>
    private static void LayoutRow(
        List<int> row,
        List<double> scaled,
        List<Tile> output,
        ref double cursorX,
        ref double cursorY,
        ref double restW,
        ref double restH)
    {
        var rowArea = row.Sum(idx => scaled[idx]);
        if (rowArea <= 0) return;

        if (restW >= restH)
        {
            // 竖着排成一列，列宽由面积定
            var columnWidth = rowArea / restH;
            var offsetY = cursorY;

            foreach (var idx in row)
            {
                var tileHeight = restH * (scaled[idx] / rowArea);
                output.Add(new Tile(idx, cursorX, offsetY, columnWidth, tileHeight));
                offsetY += tileHeight;
            }

            cursorX += columnWidth;
            restW -= columnWidth;
        }
        else
        {
            // 横着排成一行，行高由面积定
            var rowHeight = rowArea / restW;
            var offsetX = cursorX;

            foreach (var idx in row)
            {
                var tileWidth = restW * (scaled[idx] / rowArea);
                output.Add(new Tile(idx, offsetX, cursorY, tileWidth, rowHeight));
                offsetX += tileWidth;
            }

            cursorY += rowHeight;
            restH -= rowHeight;
        }
    }

    /// <summary>这一行里最差的"长宽比"（越小越方）。</summary>
    private static double Worst(List<int> row, List<double> scaled, double side)
    {
        if (row.Count == 0 || side <= 0) return double.MaxValue;

        double sum = 0, max = 0, min = double.MaxValue;
        foreach (var idx in row)
        {
            var a = scaled[idx];
            sum += a;
            if (a > max) max = a;
            if (a < min) min = a;
        }

        if (sum <= 0 || min <= 0) return double.MaxValue;

        var s2 = side * side;
        var sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
    }

    private static double WorstWith(List<int> row, int candidate, List<double> scaled, double side)
    {
        var copy = new List<int>(row) { candidate };
        return Worst(copy, scaled, side);
    }
}
