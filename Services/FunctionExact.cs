using System.Linq.Expressions;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 函数分析的**精确结论**入口。原则只有一条（Nick 定）：
/// <b>给出的每一项都必须是函数本身的性质，不随视窗移动而变</b>。
///
/// <para>
/// ⛔⛔ 曾经的「数值观察（当前视口内）」是错的：老师上课把视图拖一下，定义域、值域、单调区间
/// 全跟着变 —— 那不是函数性质，是采样窗口的坐标。现在**整条数值观察链路已删除**，
/// 拆不出来的项宁可不列，并在末尾说明为什么没列。
/// </para>
/// <para>
/// 覆盖面按"能证明才说"扩展：
/// <list type="number">
/// <item>标准形式与线性包裹（sin(x)、x²、ln(x)、sin(x)−6 …）→ 交给 <see cref="FunctionClosedForm"/>。</item>
/// <item>商式 <c>N(x)/D(x)</c>（D 为一次式）：定义域、零点、竖直渐近线、与 y 轴交点、奇偶性 —— 全部闭式。</item>
/// <item>其余：只给能严格确证的定义域（整条式子处处有定义时为 ℝ），证不出就返回 null。</item>
/// </list>
/// </para>
/// </summary>
public static class FunctionExact
{
    /// <summary>尝试给出精确结论；给不出返回 null（调用方显示"无解析结论"，绝不落回采样）。</summary>
    public static ClosedForm? Match(LambdaExpression lambda, AngleMode mode)
    {
        // ① 标准形式与线性包裹
        var standard = FunctionClosedForm.Match(lambda, mode);
        if (standard is not null) return standard;

        var param = lambda.Parameters[0];
        var body = Strip(lambda.Body);

        // ② 商式 N/D
        if (body is BinaryExpression { NodeType: ExpressionType.Divide } div)
        {
            var quotient = Quotient(Strip(div.Left), Strip(div.Right), param, mode);
            if (quotient is not null) return quotient;
        }

        // ③ 兜底：只说得清"处处有定义"这一件事
        return IsTotal(body, param)
            ? new ClosedForm(Prune(new List<(string Label, string? Value)> { ("定义域", "x ∈ ℝ") }),
                "本式没有可解析给出的其他结论；为避免给出随视窗变化的近似值，这里不作数值猜测。")
            : null;
    }

    // ────────────────────────── 商式 ──────────────────────────

    private static ClosedForm? Quotient(Expression num, Expression den, ParameterExpression param, AngleMode mode)
    {
        // 分母必须是一次式：这是能给出**精确**零点/渐近线的前提，否则整段不做
        if (LinearIn(den) is not { } d || Math.Abs(d.A) < 1e-12) return null;
        if (!IsTotal(num, param) || !IsTotal(den, param)) return null;

        var root = -d.B / d.A;
        var rootText = ExactNum(root);

        var fields = new List<(string Label, string? Value)>
        {
            ("定义域", $"x ≠ {rootText}"),
        };

        // 零点：分子为零处；若该点同时是分母的零点则是**可去间断点**，不算零点
        var removable = Math.Abs(EvalAt(num, param, root)) < 1e-9;
        var zeros = ZerosOf(num, param, mode);

        if (zeros is not null)
        {
            fields.Add(("零点", removable
                ? $"{zeros}（其中 x = {rootText} 处无定义，为可去间断点）"
                : zeros));
        }

        fields.Add(("渐近线", removable ? "无" : $"x = {rootText}"));

        // 与 y 轴交点
        if (Math.Abs(root) > 1e-12)
        {
            var y = EvalAt(num, param, 0) / EvalAt(den, param, 0);
            if (double.IsFinite(y))
                fields.Add(("与 y 轴交点", $"(0, {ExactNum(y)})"));
        }

        // 奇偶性：分母是一次式 ⇒ 只有当它的零点在原点（定义域才对称）时才可能奇/偶
        fields.Add(("奇偶性", Math.Abs(root) > 1e-12
            ? "非奇非偶"
            : CombineParity(ParityOf(num, param, mode), ParityOf(den, param, mode))));

        return new ClosedForm(Prune(fields),
            "值域、单调性、极值、拐点需要求导后的闭式，本式未给出解析结论，故不列出。");
    }

    /// <summary>把分子的「零点」结论借过来（标准形式能力内可得闭式）。</summary>
    private static string? ZerosOf(Expression expr, ParameterExpression param, AngleMode mode)
    {
        if (Strip(expr) is ConstantExpression { Value: double v })
            return Math.Abs(v) < 1e-12 ? "处处为零（除分母为零处）" : "无";

        var form = FunctionClosedForm.Match(Expression.Lambda(expr, param), mode);
        if (form is null) return null;

        foreach (var (label, value) in form.Fields)
            if (label == "零点") return value;

        return null;
    }

    private static string? ParityOf(Expression expr, ParameterExpression param, AngleMode mode)
    {
        var form = FunctionClosedForm.Match(Expression.Lambda(expr, param), mode);
        if (form is null) return null;

        foreach (var (label, value) in form.Fields)
            if (label == "奇偶性") return value;

        return null;
    }

    /// <summary>两个因子的奇偶性合成商（奇/奇=偶，偶/偶=偶，奇/偶=奇，偶/奇=奇）。</summary>
    private static string CombineParity(string? num, string? den)
    {
        var n = num?.StartsWith('奇') == true;
        var nc = num?.StartsWith('偶') == true;
        var d = den?.StartsWith('奇') == true;
        var dc = den?.StartsWith('偶') == true;

        if (n && d) return "偶函数";
        if (nc && dc) return "偶函数";
        if (n && dc) return "奇函数";
        if (nc && d) return "奇函数";
        return "非奇非偶";
    }

    // ────────────────────────── 形状判定 ──────────────────────────

    private static Expression Strip(Expression e)
        => e is UnaryExpression { NodeType: ExpressionType.Convert } u ? Strip(u.Operand) : e;

    /// <summary>把式子拆成 a·x + b；不是一次式返回 null。</summary>
    private static (double A, double B)? LinearIn(Expression e)
    {
        e = Strip(e);

        if (e is ParameterExpression) return (1, 0);
        if (e is ConstantExpression { Value: double c }) return (0, c);

        if (e is UnaryExpression { NodeType: ExpressionType.Negate } neg)
            return LinearIn(neg.Operand) is { } v ? (-v.A, -v.B) : null;

        if (e is BinaryExpression b)
        {
            var l = LinearIn(b.Left);
            var r = LinearIn(b.Right);
            if (l is not { } lv || r is not { } rv) return null;

            return b.NodeType switch
            {
                ExpressionType.Add => (lv.A + rv.A, lv.B + rv.B),
                ExpressionType.Subtract => (lv.A - rv.A, lv.B - rv.B),
                ExpressionType.Multiply when Math.Abs(rv.A) < 1e-12 => (lv.A * rv.B, lv.B * rv.B),
                ExpressionType.Multiply when Math.Abs(lv.A) < 1e-12 => (rv.A * lv.B, rv.B * lv.B),
                ExpressionType.Divide when Math.Abs(rv.A) < 1e-12 && Math.Abs(rv.B) > 1e-12
                    => (lv.A / rv.B, lv.B / rv.B),
                _ => null,
            };
        }

        return null;
    }

    /// <summary>
    /// 整个式子是否**对一切实数有定义**（只认能一眼看穿的运算：
    /// 加减乘、除以非零常数、sin/cos/exp/abs、非负整数次幂）。
    /// 出现对数、开方、变量作除数、tan 一律判 false —— 那些定义域不能只说 "x ∈ ℝ"。
    /// </summary>
    private static bool IsTotal(Expression e, ParameterExpression param)
    {
        e = Strip(e);

        switch (e)
        {
            case ConstantExpression:
            case ParameterExpression:
                return true;

            case UnaryExpression u when u.NodeType == ExpressionType.Negate:
                return IsTotal(u.Operand, param);

            case BinaryExpression b:
                return b.NodeType switch
                {
                    ExpressionType.Add or ExpressionType.Subtract or ExpressionType.Multiply
                        => IsTotal(b.Left, param) && IsTotal(b.Right, param),
                    ExpressionType.Divide
                        => IsTotal(b.Left, param) && Strip(b.Right) is ConstantExpression { Value: double d }
                           && Math.Abs(d) > 1e-12,
                    _ => false,
                };

            case MethodCallExpression c:
                if (c.Object is not null) return false;
                return c.Method.Name switch
                {
                    nameof(Math.Sin) or nameof(Math.Cos) or nameof(Math.Exp) or nameof(Math.Abs)
                        => c.Arguments.All(x => IsTotal(x, param)),

                    // 幂：只有非负整数次才在全实轴有定义（x^0.5 在负数处无定义）
                    nameof(Math.Pow) when c.Arguments.Count == 2
                        => IsTotal(c.Arguments[0], param)
                           && Strip(c.Arguments[1]) is ConstantExpression { Value: double p }
                           && p >= 0 && Math.Abs(p - Math.Round(p)) < 1e-9,
                    _ => false,
                };

            default:
                return false;
        }
    }

    // ────────────────────────── 取值与数字写法 ──────────────────────────

    private static double EvalAt(Expression expr, ParameterExpression param, double x)
    {
        try
        {
            return Expression.Lambda<Func<double, double>>(expr, param).Compile()(x);
        }
        catch
        {
            return double.NaN;
        }
    }

    /// <summary>
    /// 精确数字写法：**先试 π 的整数/分数倍，再试简单分数，最后才退回小数**。
    /// ⛔ 分母为 2、3、4、6 的 π 倍数写成 π/2、2π/3 这种，是教科书写法；
    /// 一律写成 1.5708 会立刻显得"机器算的"（Nick 反复点过这一点）。
    /// </summary>
    private static string ExactNum(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        if (double.IsPositiveInfinity(v)) return "+∞";
        if (double.IsNegativeInfinity(v)) return "−∞";
        if (Math.Abs(v) < 1e-9) return "0";

        var sign = v < 0 ? "−" : "";
        var abs = Math.Abs(v);

        // π 的分数倍
        foreach (var den in new[] { 1, 2, 3, 4, 6 })
        {
            var num = abs / Math.PI * den;
            var rounded = Math.Round(num);
            if (rounded >= 1 && Math.Abs(num - rounded) < 1e-9)
            {
                var n = (int)rounded;
                var head = n == 1 ? "π" : $"{n}π";
                return sign + (den == 1 ? head : $"{head}/{den}");
            }
        }

        // 普通分数
        foreach (var den in new[] { 2, 3, 4, 5, 6, 8 })
        {
            var num = abs * den;
            var rounded = Math.Round(num);
            if (rounded >= 1 && Math.Abs(num - rounded) < 1e-9 && (int)rounded % den != 0)
                return $"{sign}{(int)rounded}/{den}";
        }

        if (Math.Abs(abs - Math.Round(abs)) < 1e-9) return sign + Math.Round(abs).ToString("0");
        if (abs >= 1e5 || abs < 1e-4)
            return sign + abs.ToString("0.###e+0", System.Globalization.CultureInfo.InvariantCulture);

        return sign + Math.Round(abs, 4).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static List<(string Label, string Value)> Prune(List<(string Label, string? Value)> fields)
    {
        var list = new List<(string, string)>();
        foreach (var (label, value) in fields)
            if (value is not null) list.Add((label, value));
        return list;
    }
}
