using System.Linq.Expressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace ClassSoftwareHub.Desktop.Services;




















public static class MathTypesetter
{
    
    private const double ScriptScale = 0.72;

    
    private const double DegreeWrap = Math.PI / 180.0;

    
    private const double RadianWrap = 180.0 / Math.PI;

    
    private const string ThinSpace = "\u2009";

    
    
    public static FrameworkElement Render(Expression body, double fontSize, Brush brush, bool degrees = false)
        => Build(ForDisplay(body, degrees), new Ctx(fontSize, brush, degrees), minPrec: 0);

    private readonly record struct Ctx(double Size, Brush Brush, bool Degrees)
    {
        public Ctx Script() => new(Size * ScriptScale, Brush, Degrees);
    }

    

    
    private static FrameworkElement Build(Expression e, Ctx ctx, int minPrec)
    {
        e = Strip(e);
        var el = BuildCore(e, ctx);
        return Prec(e) < minPrec ? Parens(el, ctx) : el;
    }

    private static FrameworkElement BuildCore(Expression e, Ctx ctx)
    {
        switch (e)
        {
            case ConstantExpression { Value: double v }:
                return Text(NumberText(v), ctx, italic: false);

            case ParameterExpression p:
                return Text(p.Name ?? "x", ctx, italic: true);

            case UnaryExpression { NodeType: ExpressionType.Negate } neg:
                
                return Row(ctx,
                    Text("−", ctx, italic: false),
                    Build(neg.Operand, ctx, minPrec: 4));

            case BinaryExpression b:
                return BuildBinary(b, ctx);

            case MethodCallExpression call:
                return BuildCall(call, ctx);
        }

        return Text("⋯", ctx, italic: false);
    }

    private static FrameworkElement BuildBinary(BinaryExpression b, Ctx ctx)
    {
        var l = Strip(b.Left);
        var r = Strip(b.Right);

        switch (b.NodeType)
        {
            case ExpressionType.Add:
                return LooseRow(ctx, Build(l, ctx, 1), Text(" + ", ctx, false), Build(r, ctx, 1));

            case ExpressionType.Subtract:
                
                return LooseRow(ctx, Build(l, ctx, 1), Text(" − ", ctx, false), Build(r, ctx, 2));

            case ExpressionType.Multiply:
            {
                var left = Build(l, ctx, 2);
                var right = Build(r, ctx, 2);
                
                var sign = Implicit(l, r) ? ThinSpace : " · ";
                return LooseRow(ctx, left, Text(sign, ctx, false), right);
            }

            case ExpressionType.Divide:
                
                return Fraction(Build(l, ctx, 0), Build(r, ctx, 0), ctx);

            case ExpressionType.Modulo:
                return LooseRow(ctx, Build(l, ctx, 2), Text(" mod ", ctx, false), Build(r, ctx, 2));
        }

        return LooseRow(ctx, Build(l, ctx, 1), Text(" ? ", ctx, false), Build(r, ctx, 1));
    }

    private static FrameworkElement BuildCall(MethodCallExpression call, Ctx ctx)
    {
        var name = call.Method.Name;
        var arg = call.Arguments.Count == 1 ? Strip(call.Arguments[0]) : null;

        
        if (name == nameof(Math.Pow) && call.Arguments.Count == 2)
        {
            return Superscript(
                Build(call.Arguments[0], ctx, minPrec: 4),
                Build(call.Arguments[1], ctx.Script(), minPrec: 0),
                ctx);
        }

        if (arg is not null)
        {
            switch (name)
            {
                case nameof(Math.Sqrt):
                    return Radical(Build(arg, ctx, 0), ctx);

                case nameof(Math.Cbrt):
                    return Radical(Build(arg, ctx, 0), ctx, index: "3");

                case nameof(Math.Abs):
                    return LooseRow(ctx, Text("|", ctx, false), Build(arg, ctx, 0), Text("|", ctx, false));

                case nameof(Math.Exp):
                    return Superscript(Text("e", ctx, italic: false), Build(arg, ctx.Script(), 0), ctx);

                case nameof(Math.Log):
                    return LooseRow(ctx, Text("ln", ctx, false), Parens(Build(arg, ctx, 0), ctx));

                case nameof(Math.Log10):
                    return LooseRow(ctx, Text("lg", ctx, false), Parens(Build(arg, ctx, 0), ctx));

                case nameof(Math.Log2):
                    return LooseRow(ctx,
                        Text("log", ctx, false),
                        Subscript(Text("2", ctx.Script(), false), ctx),
                        Parens(Build(arg, ctx, 0), ctx));

                case nameof(Math.Sin):
                    return Fn("sin", arg, ctx);
                case nameof(Math.Cos):
                    return Fn("cos", arg, ctx);
                case nameof(Math.Tan):
                    return Fn("tan", arg, ctx);
                case nameof(Math.Asin):
                    return Fn("arcsin", arg, ctx);
                case nameof(Math.Acos):
                    return Fn("arccos", arg, ctx);
                case nameof(Math.Atan):
                    return Fn("arctan", arg, ctx);
                case nameof(Math.Sinh):
                    return Fn("sinh", arg, ctx);
                case nameof(Math.Cosh):
                    return Fn("cosh", arg, ctx);
                case nameof(Math.Tanh):
                    return Fn("tanh", arg, ctx);
                case nameof(Math.Floor):
                    return Fn("floor", arg, ctx);
                case nameof(Math.Ceiling):
                    return Fn("ceil", arg, ctx);
                case nameof(Math.Round):
                    return Fn("round", arg, ctx);
                case nameof(Math.Truncate):
                    return Fn("trunc", arg, ctx);
                case nameof(Math.Sign):
                    return Fn("sgn", arg, ctx);
            }
        }

        
        var parts = new List<FrameworkElement> { Text(name, ctx, italic: false), Text("(", ctx, false) };
        for (var i = 0; i < call.Arguments.Count; i++)
        {
            if (i > 0) parts.Add(Text(", ", ctx, false));
            parts.Add(Build(call.Arguments[i], ctx, 0));
        }
        parts.Add(Text(")", ctx, false));
        return Row(ctx, parts.ToArray());
    }

    
    private static FrameworkElement Fn(string name, Expression arg, Ctx ctx)
        => LooseRow(ctx, Text(name, ctx, false), Parens(Build(arg, ctx, 0), ctx));

    

    
    private static int Prec(Expression e) => Strip(e).NodeType switch
    {
        ExpressionType.Add or ExpressionType.Subtract => 1,
        ExpressionType.Multiply or ExpressionType.Divide or ExpressionType.Modulo => 2,
        ExpressionType.Negate or ExpressionType.UnaryPlus => 3,
        _ => 4,   
    };

    private static bool Implicit(Expression l, Expression r)
    {
        
        if (r is BinaryExpression { NodeType: ExpressionType.Divide }) return false;

        
        if (l is ConstantExpression && r is not ConstantExpression) return true;

        
        if (l is ConstantExpression && r is ConstantExpression { Value: double rv } && (IsPi(rv) || IsE(rv)))
            return true;

        
        if (l is ParameterExpression && r is MethodCallExpression) return true;

        return false;
    }

    

    
    private static FrameworkElement Fraction(FrameworkElement numerator, FrameworkElement denominator, Ctx ctx)
    {
        var grid = new Grid
        {
            RowSpacing = 1,
            Margin = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        numerator.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetRow(numerator, 0);
        grid.Children.Add(numerator);

        var bar = new Rectangle
        {
            Height = Math.Max(1, ctx.Size / 14),
            Fill = ctx.Brush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(1, 0, 1, 0),
        };
        Grid.SetRow(bar, 1);
        grid.Children.Add(bar);

        denominator.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetRow(denominator, 2);
        grid.Children.Add(denominator);

        return grid;
    }

    
    private static FrameworkElement Superscript(FrameworkElement baseEl, FrameworkElement exponent, Ctx ctx)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        baseEl.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(baseEl);

        
        exponent.VerticalAlignment = VerticalAlignment.Top;
        exponent.Margin = new Thickness(1, exponent is Grid ? 0 : -ctx.Size * 0.10, 0, 0);
        panel.Children.Add(exponent);

        return panel;
    }

    
    private static FrameworkElement Subscript(FrameworkElement element, Ctx ctx)
    {
        element.VerticalAlignment = VerticalAlignment.Bottom;
        element.Margin = new Thickness(0, 0, 0, ctx.Size * 0.06);
        return element;
    }

    
    private static FrameworkElement Radical(FrameworkElement radicand, Ctx ctx, string? index = null)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (index is not null)
        {
            var idx = Text(index, ctx.Script(), false);
            idx.VerticalAlignment = VerticalAlignment.Top;
            idx.Margin = new Thickness(0, -ctx.Size * 0.10, -ctx.Size * 0.18, 0);
            panel.Children.Add(idx);
        }

        var glyph = Text("√", new Ctx(ctx.Size * 1.15, ctx.Brush, ctx.Degrees), italic: false);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        glyph.Margin = new Thickness(0, 0, -1, 0);
        panel.Children.Add(glyph);

        var body = new Border
        {
            BorderBrush = ctx.Brush,
            BorderThickness = new Thickness(0, Math.Max(1, ctx.Size / 16), 0, 0),
            Padding = new Thickness(2, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        body.Child = radicand;
        panel.Children.Add(body);

        return panel;
    }

    
    private static FrameworkElement Parens(FrameworkElement inner, Ctx ctx)
        => LooseRow(ctx, Text("(", ctx, italic: false), inner, Text(")", ctx, italic: false));

    
    private static FrameworkElement Row(Ctx ctx, params FrameworkElement[] parts) => LooseRow(ctx, parts);

    
    
    
    
    private static FrameworkElement LooseRow(Ctx ctx, params FrameworkElement[] parts)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        foreach (var p in parts)
        {
            p.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(p);
        }

        return panel;
    }

    private static TextBlock Text(string text, Ctx ctx, bool italic) => new()
    {
        Text = text,
        FontSize = ctx.Size,
        Foreground = ctx.Brush,
        FontStyle = italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
        VerticalAlignment = VerticalAlignment.Center,
        
        LineHeight = ctx.Size * 1.34,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };

    

    
    
    
    
    private static Expression ForDisplay(Expression e, bool degrees)
    {
        if (!degrees) return e;

        switch (e)
        {
            case BinaryExpression b:
                if (b.NodeType == ExpressionType.Multiply)
                {
                    if (IsRadianWrap(b.Right) && IsInverseTrig(Strip(b.Left))) return Strip(b.Left);
                    if (IsRadianWrap(b.Left) && IsInverseTrig(Strip(b.Right))) return Strip(b.Right);
                }
                return b.Update(ForDisplay(b.Left, degrees), null, ForDisplay(b.Right, degrees));

            case UnaryExpression u when u.NodeType is ExpressionType.Negate or ExpressionType.Convert:
                return u.Update(ForDisplay(u.Operand, degrees));

            case MethodCallExpression c:
                var args = new Expression[c.Arguments.Count];
                for (var i = 0; i < args.Length; i++) args[i] = ForDisplay(c.Arguments[i], degrees);

                if (args.Length == 1 && IsTrig(c.Method.Name) && TryUnwrapDegrees(args[0], out var inner))
                    args[0] = inner;

                return c.Update(null, args);
        }

        return e;
    }

    private static bool TryUnwrapDegrees(Expression arg, out Expression inner)
    {
        if (Strip(arg) is BinaryExpression { NodeType: ExpressionType.Multiply } m)
        {
            if (IsDegreeWrap(m.Right)) { inner = Strip(m.Left); return true; }
            if (IsDegreeWrap(m.Left)) { inner = Strip(m.Right); return true; }
        }

        inner = arg;
        return false;
    }

    private static bool IsDegreeWrap(Expression e)
        => Strip(e) is ConstantExpression { Value: double v } && Near(v, DegreeWrap);

    private static bool IsRadianWrap(Expression e)
        => Strip(e) is ConstantExpression { Value: double v } && Near(v, RadianWrap);

    private static bool IsTrig(string name)
        => name is nameof(Math.Sin) or nameof(Math.Cos) or nameof(Math.Tan);

    private static bool IsInverseTrig(Expression e)
        => Strip(e) is MethodCallExpression c
           && c.Method.Name is nameof(Math.Asin) or nameof(Math.Acos) or nameof(Math.Atan);

    

    private static Expression Strip(Expression e)
        => e is UnaryExpression { NodeType: ExpressionType.Convert } u ? Strip(u.Operand) : e;

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

    private static bool IsPi(double v) => Near(v, Math.PI);

    private static bool IsE(double v) => Near(v, Math.E);

    
    
    
    
    private static string NumberText(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        if (double.IsPositiveInfinity(v)) return "+∞";
        if (double.IsNegativeInfinity(v)) return "−∞";

        if (IsPi(v)) return "π";
        if (IsE(v)) return "e";

        if (Math.Abs(v - Math.Round(v)) < 1e-12)
            return Math.Round(v).ToString("0").Replace('-', '−');

        return v.ToString("0.####").Replace('-', '−');
    }
}
