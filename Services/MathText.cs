using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 把**纯文本的数学结论**排成数学视图（π/2 是上下堆叠的分数、x² 是真上标、√2 带顶线、变量斜体）。
///
/// <para>
/// 用途：函数分析面板里的每一项结论本来就是字符串（<c>(-π/2 + 2kπ, π/2 + 2kπ)</c> 这种），
/// 直接塞进 TextBlock 就是一行线性文本 —— Nick 点名「依旧是线性文本，不是函数数学视图」。
/// </para>
/// <para>
/// ⛔ 这是给**我们自己生成的**结论字符串用的排版器，不是通用数学输入解析器：
/// 文法只覆盖结论里会出现的记号（四则、^ _ / √、集合与区间括号、∈ ≠ ∪ \、° · π 等）。
/// 认不出来的字符一律原样当普通文本输出，绝不吃掉 —— 排错了比排不出更糟。
/// </para>
/// <para>
/// ⛔⛔ 用 <see cref="RichTextBlock"/> + <see cref="InlineUIContainer"/> 而不是横向 StackPanel：
/// 分析栏只有约 150px 宽，长结论必须能自动折行；StackPanel 不会换行，直接横向溢出。
/// </para>
/// </summary>
public static class MathText
{
    /// <summary>上标/下标相对正文字号的缩放。</summary>
    private const double ScriptScale = 0.72;

    /// <summary>函数名等应当**正体**的单词（斜体就成变量了）。</summary>
    private static readonly HashSet<string> Upright = new(StringComparer.Ordinal)
    {
        "sin", "cos", "tan", "cot", "sec", "csc",
        "arcsin", "arccos", "arctan", "asin", "acos", "atan",
        "sinh", "cosh", "tanh", "ln", "lg", "log", "exp",
        "abs", "max", "min", "mod", "e",
    };

    /// <summary>把一段结论字符串排成可换行的数学视图。</summary>
    public static RichTextBlock Render(string? text, double fontSize, Brush brush)
    {
        var body = text ?? "";

        var block = new RichTextBlock
        {
            FontSize = fontSize,
            Foreground = brush,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,   // 结论要能选中复制
        };

        var para = new Paragraph();
        block.Blocks.Add(para);

        try
        {
            var ctx = new Ctx(fontSize, brush);
            foreach (var piece in new Parser(Normalize(body)).ParseAll())
                Append(para, piece, ctx);
        }
        catch (Exception ex)
        {
            // ⛔ 不许静默吞：排版退化本身不致命，但"为什么这一项是平文本"必须查得到
            Core.AppLog.Exception("plot", ex, "数学文本排版");

            // 排版出了任何岔子都退回纯文本：宁可不好看，不能空白
            para.Inlines.Clear();
            para.Inlines.Add(new Run { Text = body, FontSize = fontSize, Foreground = brush });
        }

        return block;
    }

    // ────────────────────────── 片段模型 ──────────────────────────

    private readonly record struct Ctx(double Size, Brush Brush)
    {
        public Ctx Script() => new(Size * ScriptScale, Brush);
    }

    private abstract record Piece;

    private sealed record Txt(string Text, bool Italic) : Piece;

    private sealed record Frac(Piece Num, Piece Den) : Piece;

    private sealed record Script(Piece Base, Piece Sup, bool Sub) : Piece;

    private sealed record Radical(Piece Body) : Piece;

    private sealed record Group(List<Piece> Items) : Piece;

    // ────────────────────────── 分词 + 组装 ──────────────────────────

    /// <summary>
    /// 递归下降：<c>项 := 原子 ('/' 原子 | '^' 原子 | '_' 原子)*</c>，
    /// 分数与幂只结合**紧邻的原子**，所以 <c>π/2 + 2kπ</c> 的分数线只盖住 π 与 2。
    /// </summary>
    private sealed class Parser(string s)
    {
        private int _i;

        public List<Piece> ParseAll()
        {
            var list = new List<Piece>();
            while (_i < s.Length) list.Add(ParseTerm());
            return list;
        }

        private Piece ParseTerm()
        {
            var p = ParseAtom();

            while (_i < s.Length)
            {
                var c = s[_i];

                if (c == '/')
                {
                    _i++;
                    p = new Frac(p, ParseAtom());
                    continue;
                }

                if (c == '^')
                {
                    _i++;
                    p = new Script(p, ParseAtom(), Sub: false);
                    continue;
                }

                if (c == '_')
                {
                    _i++;
                    p = new Script(p, ParseAtom(), Sub: true);
                    continue;
                }

                // 上标字符（x²、10⁻³）：直接吸进上标
                if (IsSuperscript(c))
                {
                    var start = _i;
                    while (_i < s.Length && IsSuperscript(s[_i])) _i++;
                    p = new Script(p, new Txt(UnSuperscript(s[start.._i]), false), Sub: false);
                    continue;
                }

                break;
            }

            return p;
        }

        private Piece ParseAtom()
        {
            if (_i >= s.Length) return new Txt("", false);

            var c = s[_i];

            if (c is '(' or '[' or '{')
            {
                var close = c == '(' ? ')' : c == '[' ? ']' : '}';
                _i++;
                var items = new List<Piece> { new Txt(c.ToString(), false) };
                while (_i < s.Length && s[_i] != close) items.Add(ParseTerm());
                if (_i < s.Length) { _i++; items.Add(new Txt(close.ToString(), false)); }
                return new Group(items);
            }

            if (c == '√')
            {
                _i++;
                return new Radical(ParseAtom());
            }

            if (char.IsDigit(c) || (c == '.' && _i + 1 < s.Length && char.IsDigit(s[_i + 1])))
            {
                var start = _i;
                while (_i < s.Length && (char.IsDigit(s[_i]) || s[_i] == '.')) _i++;
                return new Txt(s[start.._i], false);
            }

            if (char.IsLetter(c))
            {
                var start = _i;
                while (_i < s.Length && char.IsLetter(s[_i])) _i++;
                var word = s[start.._i];
                // 单个字母是变量（斜体）；函数名、常数、中文词一律正体
                return new Txt(word, word.Length == 1 && !Upright.Contains(word) && IsVariableLetter(word[0]));
            }

            _i++;
            return new Txt(c.ToString(), false);
        }

        private static bool IsVariableLetter(char c)
            => c is 'x' or 'y' or 'z' or 'k' or 'a' or 'b' or 'c' or 'n' or 'm'
                or 't' or 'T' or 's' or 'u' or 'v' or 'w' or 'f' or 'g' or 'h';
    }

    // ────────────────────────── 渲染 ──────────────────────────

    private static void Append(Paragraph para, Piece piece, Ctx ctx)
    {
        switch (piece)
        {
            case Txt t:
                if (t.Text.Length == 0) return;
                para.Inlines.Add(new Run
                {
                    Text = t.Text,
                    FontSize = ctx.Size,
                    Foreground = ctx.Brush,
                    FontStyle = t.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                });
                return;

            case Group g:
                foreach (var item in g.Items) Append(para, item, ctx);
                return;

            case Frac f:
                para.Inlines.Add(new InlineUIContainer { Child = FractionElement(f, ctx) });
                return;

            case Script sc:
                para.Inlines.Add(new InlineUIContainer { Child = ScriptElement(sc, ctx) });
                return;

            case Radical r:
                para.Inlines.Add(new InlineUIContainer { Child = RadicalElement(r, ctx) });
                return;
        }
    }

    /// <summary>把一个片段摊成横排元素（分数内部用，不需要换行）。</summary>
    private static void Flatten(StackPanel panel, Piece piece, Ctx ctx)
    {
        switch (piece)
        {
            case Txt t:
                if (t.Text.Length == 0) return;
                panel.Children.Add(new TextBlock
                {
                    Text = t.Text,
                    FontSize = ctx.Size,
                    Foreground = ctx.Brush,
                    FontStyle = t.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                    VerticalAlignment = VerticalAlignment.Center,
                    // 堆叠件内部收紧行距：默认行高会把"分子/分母"撑得过高，分数显得又高又散
                    LineHeight = ctx.Size * 1.25,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                });
                return;

            case Group g:
                foreach (var item in g.Items) Flatten(panel, item, ctx);
                return;

            case Frac f:
                panel.Children.Add(FractionElement(f, ctx));
                return;

            case Script sc:
                panel.Children.Add(ScriptElement(sc, ctx));
                return;

            case Radical r:
                panel.Children.Add(RadicalElement(r, ctx));
                return;
        }
    }

    private static FrameworkElement Wrap(Piece piece, Ctx ctx)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Flatten(panel, piece, ctx);
        return panel;
    }

    /// <summary>
    /// 把堆叠类元素（分数、根号）下沉到**数学轴**上。
    /// <para>
    /// ⛔⛔ 不这么做的话 <see cref="InlineUIContainer"/> 是按**底边贴基线**摆的：
    /// 一个两行高的分式会整个被顶到基线以上，看起来"飘"在半空、跟后面的数字错位
    /// （Nick 截图点名）。行框本身已经按对象高度撑开了，所以下沉不会压到下一行。
    /// </para>
    /// </summary>
    private static void AlignOnMathAxis(FrameworkElement element, Ctx ctx)
    {
        var shift = new TranslateTransform();
        element.RenderTransform = shift;

        void Update()
        {
            var h = element.ActualHeight;
            if (h > 0) shift.Y = h / 2 - ctx.Size * 0.26;   // 数学轴约在基线以上 0.26em
        }

        element.Loaded += (_, _) => Update();
        element.SizeChanged += (_, _) => Update();
    }

    private static FrameworkElement FractionElement(Frac f, Ctx ctx)
    {
        var grid = new Grid
        {
            RowSpacing = 1,
            Margin = new Thickness(2, 0, 2, 0),
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var num = Wrap(f.Num, ctx);
        num.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetRow(num, 0);
        grid.Children.Add(num);

        var bar = new Rectangle
        {
            Height = Math.Max(1, ctx.Size / 14),
            Fill = ctx.Brush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(1, 0, 1, 0),
        };
        Grid.SetRow(bar, 1);
        grid.Children.Add(bar);

        var den = Wrap(f.Den, ctx);
        den.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetRow(den, 2);
        grid.Children.Add(den);

        AlignOnMathAxis(grid, ctx);
        return grid;
    }

    private static FrameworkElement ScriptElement(Script sc, Ctx ctx)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var body = Wrap(sc.Base, ctx);
        body.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(body);

        // ⚠️ 上标往上抬、下标往下压，都不能额外撑高行
        var script = Wrap(sc.Sup, ctx.Script());
        script.Margin = new Thickness(1, sc.Sub ? ctx.Size * 0.30 : -ctx.Size * 0.34, 0, 0);
        script.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(script);

        return panel;
    }

    private static FrameworkElement RadicalElement(Radical r, Ctx ctx)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        panel.Children.Add(new TextBlock
        {
            Text = "√",
            FontSize = ctx.Size * 1.15,
            Foreground = ctx.Brush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, -1, 0),
        });

        panel.Children.Add(new Border
        {
            BorderBrush = ctx.Brush,
            BorderThickness = new Thickness(0, Math.Max(1, ctx.Size / 16), 0, 0),
            Padding = new Thickness(2, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Wrap(r.Body, ctx),
        });

        AlignOnMathAxis(panel, ctx);
        return panel;
    }

    // ────────────────────────── 预处理与字符表 ──────────────────────────

    /// <summary>全角标点收成半角（粘贴进来的结论偶尔带全角括号）。</summary>
    private static string Normalize(string s) => s
        .Replace('（', '(').Replace('）', ')')
        .Replace('，', ',').Replace('：', ':')
        .Replace('　', ' ')
        .Replace('-', '\u2212');   // 结论里混着 ASCII 连字符与数学负号，统一成 −

    private static bool IsSuperscript(char c) => c is '⁰' or '¹' or '²' or '³' or '⁴'
        or '⁵' or '⁶' or '⁷' or '⁸' or '⁹' or '⁻' or '⁺';

    private static string UnSuperscript(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '⁻' => '-',
                '⁺' => '+',
                '⁰' => '0',
                '¹' => '1',
                '²' => '2',
                '³' => '3',
                '⁴' => '4',
                '⁵' => '5',
                '⁶' => '6',
                '⁷' => '7',
                '⁸' => '8',
                '⁹' => '9',
                _ => c,
            });
        }
        return sb.ToString();
    }
}
