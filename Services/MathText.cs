using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace ClassSoftwareHub.Desktop.Services;


















public static class MathText
{
    
    private const double ScriptScale = 0.72;

    
    private static readonly HashSet<string> Upright = new(StringComparer.Ordinal)
    {
        "sin", "cos", "tan", "cot", "sec", "csc",
        "arcsin", "arccos", "arctan", "asin", "acos", "atan",
        "sinh", "cosh", "tanh", "ln", "lg", "log", "exp",
        "abs", "max", "min", "mod", "e",
    };

    
    public static RichTextBlock Render(string? text, double fontSize, Brush brush)
    {
        var body = text ?? "";

        var block = new RichTextBlock
        {
            FontSize = fontSize,
            Foreground = brush,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,   
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
            
            Core.AppLog.Exception("plot", ex, "数学文本排版");

            
            para.Inlines.Clear();
            para.Inlines.Add(new Run { Text = body, FontSize = fontSize, Foreground = brush });
        }

        return block;
    }

    

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
                
                return new Txt(word, word.Length == 1 && !Upright.Contains(word) && IsVariableLetter(word[0]));
            }

            _i++;
            return new Txt(c.ToString(), false);
        }

        private static bool IsVariableLetter(char c)
            => c is 'x' or 'y' or 'z' or 'k' or 'a' or 'b' or 'c' or 'n' or 'm'
                or 't' or 'T' or 's' or 'u' or 'v' or 'w' or 'f' or 'g' or 'h';
    }

    

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

    
    
    
    
    
    
    
    
    private static void AlignOnMathAxis(FrameworkElement element, Ctx ctx)
    {
        var shift = new TranslateTransform();
        element.RenderTransform = shift;

        void Update()
        {
            var h = element.ActualHeight;
            if (h > 0) shift.Y = h / 2 - ctx.Size * 0.26;   
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

    

    
    private static string Normalize(string s) => s
        .Replace('（', '(').Replace('）', ')')
        .Replace('，', ',').Replace('：', ':')
        .Replace('　', ' ')
        .Replace('-', '\u2212');   

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
