using System;

namespace ClassSoftwareHub.Desktop.Core;


public readonly record struct TextFitResult(double FontSize, double LineHeight, double Height);













public static class TextFit
{
    
    public const double MaxSize = 96;

    
    public const double MinSize = 30;

    
    public const double Step = 3;

    
    public const double LineHeightRatio = 1.22;

    
    public static double LineHeightFor(double fontSize) => Math.Round(fontSize * LineHeightRatio);

    
    
    
    
    
    
    
    
    public static double EstimateHeight(string? text, double fontSize, double lineHeight, double width)
    {
        if (string.IsNullOrEmpty(text) || width <= 0 || fontSize <= 0 || lineHeight <= 0) return 0;

        var perLine = Math.Max(1, (int)Math.Floor(width / fontSize));
        var lines = Math.Max(1, (int)Math.Ceiling(text.Length / (double)perLine));
        return lines * lineHeight;
    }

    
    
    
    
    
    
    
    
    
    public static double HeightOf(
        string? text, double fontSize, double lineHeight, double width,
        Func<string, double, double, double, double> measure)
    {
        var estimate = EstimateHeight(text, fontSize, lineHeight, width);
        if (estimate <= 0) return 0;

        var real = measure(text ?? "", fontSize, lineHeight, width);
        return Math.Max(real, estimate);
    }

    
    
    
    
    public static TextFitResult PickFontSize(
        string? text, double width, double maxHeight, double startSize,
        Func<string, double, double, double, double> measure)
    {
        var start = startSize > 0 ? startSize : 88;
        var startLine = LineHeightFor(start);

        if (string.IsNullOrEmpty(text) || width <= 0 || maxHeight <= 0)
            return new TextFitResult(start, startLine, 0);

        var height = HeightOf(text, start, startLine, width, measure);
        if (height <= maxHeight) return new TextFitResult(start, startLine, height);

        for (var size = Math.Min(start, MaxSize) - Step; size >= MinSize; size -= Step)
        {
            var lineHeight = LineHeightFor(size);
            height = HeightOf(text, size, lineHeight, width, measure);
            if (height <= maxHeight) return new TextFitResult(size, lineHeight, height);
        }

        var lastLine = LineHeightFor(MinSize);
        return new TextFitResult(MinSize, lastLine, HeightOf(text, MinSize, lastLine, width, measure));
    }
}
