using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 化学式的排版：<c>H2O</c> 排成 H₂O（真下标）、<c>SO4^2-</c> 排成 SO₄²⁻（真上标）。
///
/// <para>
/// ⛔⛔ 不能把 "H2O" 原样丢进 TextBlock —— 那是计算机写法，写在黑板上是错的。
/// 这里按字符分类（元素符号正常、元素后面的数字下沉成下标、^ 之后是上标），
/// 与函数图像页的数学排版是同一套思路。
/// </para>
/// </summary>
public static class ChemicalNotation
{
    public enum Kind { Normal, Sub, Super }

    public sealed record Piece(string Text, Kind Kind);

    /// <summary>
    /// 把化学式拆成片段。
    /// 规则：元素符号后面的数字是下标；<c>·</c> 后面的数字是结晶水倍数（正常字号）；
    /// <c>^</c> 之后到结尾是电荷上标。
    /// </summary>
    public static List<Piece> Tokenize(string formula)
    {
        var pieces = new List<Piece>();
        var normal = new StringBuilder();
        var script = new StringBuilder();
        var seenAtom = false;
        var superMode = false;
        var scriptIsSuper = false;

        void FlushNormal()
        {
            if (normal.Length == 0) return;
            pieces.Add(new Piece(normal.ToString(), Kind.Normal));
            normal.Clear();
        }

        void FlushScript()
        {
            if (script.Length == 0) return;
            pieces.Add(new Piece(script.ToString(), scriptIsSuper ? Kind.Super : Kind.Sub));
            script.Clear();
        }

        foreach (var ch in formula)
        {
            if (ch == '^')
            {
                FlushScript();
                FlushNormal();
                superMode = true;
                scriptIsSuper = true;
                continue;
            }

            if (char.IsDigit(ch) || (superMode && (ch == '+' || ch == '-')))
            {
                if (superMode)
                {
                    script.Append(ch == '-' ? '−' : ch);
                    continue;
                }

                if (seenAtom)
                {
                    FlushNormal();
                    script.Append(ch);
                }
                else
                {
                    normal.Append(ch);   // 行首系数：正常字号
                }

                continue;
            }

            FlushScript();
            if (scriptIsSuper) { scriptIsSuper = false; superMode = false; }

            normal.Append(ch);

            if (ch is '·' or '*')
                seenAtom = false;        // 结晶水倍数用正常字号
            else if (char.IsLetter(ch) || ch is ')' or ']')
                seenAtom = true;
        }

        FlushScript();
        FlushNormal();
        return pieces;
    }

    /// <summary>
    /// 纯文本版：把下标 / 上标换成 Unicode 上下标字符（<c>SO4^2-</c> → <c>SO₄²⁻</c>）。
    ///
    /// <para>
    /// 用在**不能排版**的地方：按钮提示、复制到剪贴板、日志。
    /// ⛔ 这些地方直接出 <c>^</c> 是计算机写法（Nick 2026-10-17 点名离子根上的 ^）。
    /// </para>
    /// </summary>
    public static string Plain(string formula)
    {
        var text = new StringBuilder(formula.Length);

        foreach (var piece in Tokenize(formula))
        {
            if (piece.Kind == Kind.Normal)
            {
                text.Append(piece.Text);
                continue;
            }

            foreach (var ch in piece.Text)
            {
                var mapped = piece.Kind == Kind.Sub ? Subscript(ch) : Superscript(ch);
                text.Append(mapped ?? ch);
            }
        }

        return text.ToString();
    }

    private static char? Subscript(char ch) => ch switch
    {
        '0' => '\u2080', '1' => '\u2081', '2' => '\u2082', '3' => '\u2083', '4' => '\u2084',
        '5' => '\u2085', '6' => '\u2086', '7' => '\u2087', '8' => '\u2088', '9' => '\u2089',
        _ => null,
    };

    private static char? Superscript(char ch) => ch switch
    {
        '0' => '\u2070', '1' => '\u00B9', '2' => '\u00B2', '3' => '\u00B3', '4' => '\u2074',
        '5' => '\u2075', '6' => '\u2076', '7' => '\u2077', '8' => '\u2078', '9' => '\u2079',
        '+' => '\u207A', '−' => '\u207B', _ => null,
    };

    /// <summary>
    /// 单个化学式（用于物质卡片）：横向排，下标/上标靠字号与位移做出来。
    /// </summary>
    public static FrameworkElement Formula(string formula, double fontSize, Brush brush)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        foreach (var piece in Tokenize(formula))
        {
            var block = new TextBlock
            {
                Text = piece.Text,
                FontSize = piece.Kind == Kind.Normal ? fontSize : fontSize * 0.66,
                Foreground = brush,
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (piece.Kind != Kind.Normal)
            {
                block.RenderTransform = new TranslateTransform
                {
                    Y = piece.Kind == Kind.Sub ? fontSize * 0.2 : -fontSize * 0.34,
                };
            }

            panel.Children.Add(block);
        }

        return panel;
    }

    /// <summary>
    /// 整条方程式（会折行）：左 + 左 → 右 + 右，箭头上面写反应条件。
    /// </summary>
    public static RichTextBlock Equation(
        IReadOnlyList<(string Formula, int Coeff)> reactants,
        IReadOnlyList<(string Formula, int Coeff)> products,
        IReadOnlyList<string> conditions,
        double fontSize,
        Brush brush)
    {
        var block = new RichTextBlock
        {
            FontSize = fontSize,
            Foreground = brush,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = fontSize * 1.5,
        };

        var paragraph = new Paragraph();
        AppendSide(paragraph, reactants, fontSize, brush);
        paragraph.Inlines.Add(new InlineUIContainer { Child = ArrowElement(conditions, fontSize, brush) });
        AppendSide(paragraph, products, fontSize, brush);

        block.Blocks.Add(paragraph);
        return block;
    }

    private static void AppendSide(Paragraph paragraph, IReadOnlyList<(string Formula, int Coeff)> terms,
                                   double fontSize, Brush brush)
    {
        for (var i = 0; i < terms.Count; i++)
        {
            if (i > 0) paragraph.Inlines.Add(new Run { Text = "  +  ", Foreground = brush });

            var (formula, coeff) = terms[i];
            if (coeff != 1) paragraph.Inlines.Add(new Run { Text = coeff.ToString(), Foreground = brush });

            foreach (var piece in Tokenize(formula))
            {
                if (piece.Kind == Kind.Normal)
                {
                    paragraph.Inlines.Add(new Run { Text = piece.Text, Foreground = brush });
                    continue;
                }

                // 上下标必须做成行内对象：Run 本身没有基线偏移能力
                var script = new TextBlock
                {
                    Text = piece.Text,
                    FontSize = fontSize * 0.66,
                    Foreground = brush,
                    RenderTransform = new TranslateTransform
                    {
                        Y = piece.Kind == Kind.Sub ? fontSize * 0.2 : -fontSize * 0.34,
                    },
                };

                paragraph.Inlines.Add(new InlineUIContainer { Child = script });
            }
        }
    }

    /// <summary>反应箭头：条件写在箭头上方（化学式子里就是这么写的）。</summary>
    private static FrameworkElement ArrowElement(IReadOnlyList<string> conditions, double fontSize, Brush brush)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };

        if (conditions.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = string.Join("、", conditions),
                FontSize = Math.Max(9.5, fontSize * 0.42),
                Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = 0.75,
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = "→",
            FontSize = fontSize,
            Foreground = brush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(fontSize * 0.18, 0, fontSize * 0.18, 0),
        });

        // 行内对象是按底边贴基线摆的；带条件时整体会被顶高，往下推一点让箭头落在文字中间
        panel.RenderTransform = new TranslateTransform { Y = conditions.Count > 0 ? fontSize * 0.28 : fontSize * 0.1 };
        return panel;
    }
}
