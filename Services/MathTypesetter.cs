using System.Linq.Expressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 把**表达式树**排成数学排版（分数上下堆叠、真上标、根号上划线、变量斜体、π 与 e 用字母）。
///
/// <para>
/// Nick 要的观感参照 GeoGebra：<c>x^(1/2)</c> 应当显示成 x 的 <b>½</b>（分数上标），
/// 而不是一行线性文本。<para/>
/// ⛔⛔ 路线选择：**从表达式树渲染**，不是从文本正则替换。树里"除"就是除、"幂"就是幂，
/// 排版边界天然清楚；从文本猜括号配对、猜谁套谁，遇到 <c>(a+b)/(c-d)</c> 这种必错。
/// </para>
/// <para>
/// ⛔⛔ **必须按优先级补括号**（这里踩过）：<c>2*(sin(x)+1)</c> 的树是
/// <c>Multiply(2, Add(sin x, 1))</c>，若照着树平铺就排成 <c>2 · sin(x) + 1</c> ——
/// 那是错的式子，会被当成真实表达式读。所以子节点按 <see cref="Prec"/> 判定是否包括号。
/// </para>
/// <para>
/// ⛔ 只做**显示**，不动输入：TextBox 仍是线性文本（可编辑、公式解析器吃的是它），
/// 排版层用于下方的实时预览与分析结论。
/// </para>
/// </summary>
public static class MathTypesetter
{
    /// <summary>上标/下标相对正文字号的缩放。</summary>
    private const double ScriptScale = 0.72;

    /// <summary>角度制在解析器里是 "乘 π/180" 包出来的，排版时要还原掉。</summary>
    private const double DegreeWrap = Math.PI / 180.0;

    /// <summary>反三角在角度制里是 "乘 180/π" 包出来的。</summary>
    private const double RadianWrap = 180.0 / Math.PI;

    /// <summary>隐式乘法的间隙（比普通空格窄，比紧贴宽）。</summary>
    private const string ThinSpace = "\u2009";

    /// <summary>把一个表达式渲染成排版元素。</summary>
    /// <param name="degrees">角度制时置 true：解析器塞进去的换算因子在显示时抹掉。</param>
    public static FrameworkElement Render(Expression body, double fontSize, Brush brush, bool degrees = false)
        => Build(ForDisplay(body, degrees), new Ctx(fontSize, brush, degrees), minPrec: 0);

    private readonly record struct Ctx(double Size, Brush Brush, bool Degrees)
    {
        public Ctx Script() => new(Size * ScriptScale, Brush, Degrees);
    }

    // ────────────────────────── 主分派 ──────────────────────────

    /// <summary>按最小优先级渲染：子式优先级低于 <paramref name="minPrec"/> 时补括号。</summary>
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
                // ⛔ −(a+b) 不能排成 −a+b：要求操作数本身是"一个整体"
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
                // 右边是加减必须括号：a−(b+c)
                return LooseRow(ctx, Build(l, ctx, 1), Text(" − ", ctx, false), Build(r, ctx, 2));

            case ExpressionType.Multiply:
            {
                var left = Build(l, ctx, 2);
                var right = Build(r, ctx, 2);
                // 手写式乘法不写点：2x、2π、2 sin(x)；两侧都是普通数字或右边是分式时才留 ·（分式紧跟在数字后会被误读）
                var sign = Implicit(l, r) ? ThinSpace : " · ";
                return LooseRow(ctx, left, Text(sign, ctx, false), right);
            }

            case ExpressionType.Divide:
                // 分数线自己就是分组符号，分子分母一律不再补括号
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

        // 幂：底数 + 真上标（上标本身是分式时，就是 GeoGebra 那种 x 的 ½ 次方）
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

        // 其他（min/max/pow 之类）：函数名 + 逗号分隔的参数
        var parts = new List<FrameworkElement> { Text(name, ctx, italic: false), Text("(", ctx, false) };
        for (var i = 0; i < call.Arguments.Count; i++)
        {
            if (i > 0) parts.Add(Text(", ", ctx, false));
            parts.Add(Build(call.Arguments[i], ctx, 0));
        }
        parts.Add(Text(")", ctx, false));
        return Row(ctx, parts.ToArray());
    }

    /// <summary>常见函数：名字正体 + 括号（括号是排版的一部分，不能省）。</summary>
    private static FrameworkElement Fn(string name, Expression arg, Ctx ctx)
        => LooseRow(ctx, Text(name, ctx, false), Parens(Build(arg, ctx, 0), ctx));

    // ────────────────────────── 优先级与隐式乘法 ──────────────────────────

    /// <summary>数值越大结合越紧。用于判断子式是否需要括号。</summary>
    private static int Prec(Expression e) => Strip(e).NodeType switch
    {
        ExpressionType.Add or ExpressionType.Subtract => 1,
        ExpressionType.Multiply or ExpressionType.Divide or ExpressionType.Modulo => 2,
        ExpressionType.Negate or ExpressionType.UnaryPlus => 3,
        _ => 4,   // 常量 / 变量 / 函数调用 —— 自成一体
    };

    private static bool Implicit(Expression l, Expression r)
    {
        // 右边是分式：2 · (1/x)，不留点会被读成 2 又 1/x
        if (r is BinaryExpression { NodeType: ExpressionType.Divide }) return false;

        // 2x、2 sin(x)、½π：数字在前、后面不是纯数字
        if (l is ConstantExpression && r is not ConstantExpression) return true;

        // 2 · π、2 · e：两边都是常量，但右边排出来是字母，照样省点
        if (l is ConstantExpression && r is ConstantExpression { Value: double rv } && (IsPi(rv) || IsE(rv)))
            return true;

        // x sin(x)：变量乘函数，手写也不写点
        if (l is ParameterExpression && r is MethodCallExpression) return true;

        return false;
    }

    // ────────────────────────── 排版零件 ──────────────────────────

    /// <summary>分数：分子在上、分母在下，中间一条横线，整体垂直居中。</summary>
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

    /// <summary>上标：底数 + 抬高的缩小元素。</summary>
    private static FrameworkElement Superscript(FrameworkElement baseEl, FrameworkElement exponent, Ctx ctx)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        baseEl.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(baseEl);

        // 上标往上抬一点；分式上标自带高度，别再额外抬
        exponent.VerticalAlignment = VerticalAlignment.Top;
        exponent.Margin = new Thickness(1, exponent is Grid ? 0 : -ctx.Size * 0.10, 0, 0);
        panel.Children.Add(exponent);

        return panel;
    }

    /// <summary>下标（log₂ 这种）。</summary>
    private static FrameworkElement Subscript(FrameworkElement element, Ctx ctx)
    {
        element.VerticalAlignment = VerticalAlignment.Bottom;
        element.Margin = new Thickness(0, 0, 0, ctx.Size * 0.06);
        return element;
    }

    /// <summary>根号：√ 字形 + 被开方数（顶上加一条线，近似根号的上划线）。</summary>
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

    /// <summary>括号组（低优先级子式的显式分组）。</summary>
    private static FrameworkElement Parens(FrameworkElement inner, Ctx ctx)
        => LooseRow(ctx, Text("(", ctx, italic: false), inner, Text(")", ctx, italic: false));

    /// <summary>紧凑横排。</summary>
    private static FrameworkElement Row(Ctx ctx, params FrameworkElement[] parts) => LooseRow(ctx, parts);

    /// <summary>
    /// 横排，元素一律垂直居中（分数、根号、多层堆叠都靠这个对齐），
    /// 外层再撑开时不会把邻居顶偏。
    /// </summary>
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
        // 数学排版行高比正文紧一点，堆叠起来才像公式
        LineHeight = ctx.Size * 1.34,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };

    // ────────────────────────── 角度制还原 ──────────────────────────

    /// <summary>
    /// 解析器在角度制下会把 sin 的参数包成 "×π/180"、反三角的结果包成 "×180/π"。
    /// 排版要给用户看他**键入的那个式子**，所以显示前把这些换算因子摘掉。
    /// </summary>
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

    // ────────────────────────── 杂项 ──────────────────────────

    private static Expression Strip(Expression e)
        => e is UnaryExpression { NodeType: ExpressionType.Convert } u ? Strip(u.Operand) : e;

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

    private static bool IsPi(double v) => Near(v, Math.PI);

    private static bool IsE(double v) => Near(v, Math.E);

    /// <summary>
    /// 数字排版：π、e 给字母，整数不给小数点，其余保留 4 位。
    /// ⛔ 常量在树里是 double —— 不认识的 π 会排成 3.1416，一眼就"机械"。
    /// </summary>
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
