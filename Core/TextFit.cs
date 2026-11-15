using System;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>字号自适应的结果：选定的字号、对应行高、以及该字号下文字的实际高度。</summary>
public readonly record struct TextFitResult(double FontSize, double LineHeight, double Height);

/// <summary>
/// 全屏喊话窗的字号自适应 —— 抽成纯函数是为了能单测。
///
/// <para>
/// 为什么值得单抽：这一段连续出过两次「长文本被裁」的线上问题（2026-10-25、2026-10-26），
/// 两次都是**判断条件写错**而不是渲染错，而它们全都能用一行数字复现：
/// ① 探针返回 0 时被当成「放得下」⇒ 直接采用 96 号，六行文字溢出屏幕；
/// ② 只信真实测量 ⇒ 开场那一刻布局没跑完、只量到「一行」（实测 88 号 6 行只回 117），
///    同样判成放得下。
/// 这里的规则只有一条：<b>估算与测量取较大值，宁可字号偏小，绝不越界</b>。
/// </para>
/// </summary>
public static class TextFit
{
    /// <summary>起始字号上限（再大在 1080p 上放不下一行中文正文）。</summary>
    public const double MaxSize = 96;

    /// <summary>最小字号：再小就失去「全屏喊话」的意义，宁可超出也停在这里。</summary>
    public const double MinSize = 30;

    /// <summary>每次缩小的步长。</summary>
    public const double Step = 3;

    /// <summary>行高 = 字号 × 该系数。中文字面本身占满行框，1.22 是实测下不挤不散的值。</summary>
    public const double LineHeightRatio = 1.22;

    /// <summary>把字号换算成行高。</summary>
    public static double LineHeightFor(double fontSize) => Math.Round(fontSize * LineHeightRatio);

    /// <summary>
    /// 保守估算高度：中文基本是方块字，一个字宽≈字号，据此算每行几个字、共几行，再乘行高。
    ///
    /// <para>
    /// 对中文准确、对英文偏保守（英文实际更窄，估出来的行数会偏多 ⇒ 字号偏小），
    /// 这个方向是刻意选的：字号小一点只是不够气派，估小了就会溢出被裁。
    /// </para>
    /// </summary>
    public static double EstimateHeight(string? text, double fontSize, double lineHeight, double width)
    {
        if (string.IsNullOrEmpty(text) || width <= 0 || fontSize <= 0 || lineHeight <= 0) return 0;

        var perLine = Math.Max(1, (int)Math.Floor(width / fontSize));
        var lines = Math.Max(1, (int)Math.Ceiling(text.Length / (double)perLine));
        return lines * lineHeight;
    }

    /// <summary>
    /// 某个字号下的高度 = max(真实测量, 保守估算)。
    ///
    /// <para>
    /// <paramref name="measure"/> 由调用方提供（窗口里是挂在视觉树上的探针）。
    /// ⛔⛔ 不能只信它：开场那一刻布局还没跑完，它常常只返回一行的高度。
    /// ⛔⛔ 也不能把它的 0 当成「放得下」—— 那正是第一次裁切的成因。
    /// </para>
    /// </summary>
    public static double HeightOf(
        string? text, double fontSize, double lineHeight, double width,
        Func<string, double, double, double, double> measure)
    {
        var estimate = EstimateHeight(text, fontSize, lineHeight, width);
        if (estimate <= 0) return 0;

        var real = measure(text ?? "", fontSize, lineHeight, width);
        return Math.Max(real, estimate);
    }

    /// <summary>
    /// 选一个放得下的字号：先试 <paramref name="startSize"/>（多数情况一次命中），
    /// 放不下再逐档缩到 <see cref="MinSize"/>；全都放不下就用最小字号。
    /// </summary>
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
