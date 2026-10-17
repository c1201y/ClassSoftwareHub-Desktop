using System.Linq.Expressions;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 标准函数的**解析结论**（通式）。<para/>
/// Nick 定：sin 这类函数的单调区间要给「(-π/2 + 2kπ, π/2 + 2kπ)」这种数学通式，
/// 而不是把当前视口内的坐标值算出来 —— 前者是数学事实，后者只是采样观察。
/// </summary>
/// <param name="Fields">字段列表（标签, 值）。</param>
/// <param name="Note">给用户的一句话说明（如 "区间中的 k 均为整数"）。</param>
public sealed record ClosedForm(IReadOnlyList<(string Label, string Value)> Fields, string? Note = null);

/// <summary>
/// 把编译后的表达式树**匹配**到一组已知的标准形式，给出通式结论。<para/>
/// ⛔ 匹配范围＝「线性包裹的标准形式」：<c>a·f(x) + c</c>，其中 f 是参数恰好为 x 的标准函数
/// （sin/cos/tan/exp/ln/√/|x|/x²/x³/x）。加减常数、乘除系数都算线性包裹 ——
/// Nick 点名过：sin(x)−6 这么简单的式子必须给通式（单调区间与 sin(x) 完全一致，
/// 值域/极值平移，零点无解）。<para/>
/// ⛔ 仍然不给的：复合（sin(2x)、sin(x²)）、非线性运算（sin(x)+x）、三角的相位平移
/// （sin(x+π/3)，需要重推整个区间通式，等有真实需求再做）。给不出就落回数值观察 ——
/// 宁可少给，不可给错。
/// </summary>
public static class FunctionClosedForm
{
    /// <summary>尝试匹配。命中返回通式结论；没命中返回 null（调用方落回数值观察）。</summary>
    public static ClosedForm? Match(LambdaExpression lambda, AngleMode mode)
    {
        var body = lambda.Body;
        if (body is not Expression e) return null;
        if (!IsVariable(e)) return null;

        var wrap = LinearWrap(e);
        if (wrap is not { } w) return null;

        var (core, a, c) = w;

        // a = 0 ⇒ 整个函数就是常数 y = c
        if (Math.Abs(a) < 1e-12) return ConstantForm(c);

        // 常数函数（core 本身是常数）：y = a·v + c
        if (core is ConstantExpression { Value: double v } && double.IsFinite(v))
            return ConstantForm(a * v + c);

        return core.NodeType switch
        {
            ExpressionType.Parameter => XForm(mode, a, c),
            ExpressionType.Call => CallForm((MethodCallExpression)core, mode, a, c),
            _ => null,
        };
    }

    // ────────────────────────── 匹配辅助 ──────────────────────────

    /// <summary>剥掉隐式数值转换（Expression.Convert 包着 int→double 那类）。</summary>
    private static Expression StripConvert(Expression e)
        => e is UnaryExpression { NodeType: ExpressionType.Convert } u ? StripConvert(u.Operand) : e;

    /// <summary>整个表达式树里只允许出现变量 x（可以出现多次，如 x·x）。</summary>
    private static bool IsVariable(Expression e)
    {
        return e switch
        {
            ParameterExpression p => string.Equals(p.Name, "x", StringComparison.OrdinalIgnoreCase),
            ConstantExpression { Value: double } => true,
            UnaryExpression u => IsVariable(StripConvert(u.Operand)),
            BinaryExpression b => IsVariable(StripConvert(b.Left)) && IsVariable(StripConvert(b.Right)),
            MethodCallExpression c => c.Object is null && c.Arguments.All(a => IsVariable(StripConvert(a))),
            _ => false,
        };
    }

    /// <summary>
    /// 把 <paramref name="e"/> 分解成 <c>A·core + C</c>（A≠0）。
    /// core 是"非线性部分"（函数调用、乘方、变量或常数）；纯加减乘除常数的线性外包裹
    /// 全部折进 A 与 C。分不出（如 sin(x)·x）返回 null。
    /// </summary>
    private static (Expression Core, double A, double C)? LinearWrap(Expression e)
    {
        e = StripConvert(e);

        if (e is ParameterExpression or ConstantExpression) return (e, 1, 0);

        if (e is UnaryExpression { NodeType: ExpressionType.Negate } neg)
        {
            var inner = LinearWrap(StripConvert(neg.Operand));
            return inner is { } r ? (r.Core, -r.A, -r.C) : null;
        }

        if (e is BinaryExpression b)
        {
            var l = StripConvert(b.Left);
            var r = StripConvert(b.Right);

            // 常数在右：core 不动，线性系数跟着运算走
            if (r is ConstantExpression { Value: double rc })
            {
                var lw = LinearWrap(l);
                if (lw is null) return null;
                return b.NodeType switch
                {
                    ExpressionType.Add => (lw.Value.Core, lw.Value.A, lw.Value.C + rc),
                    ExpressionType.Subtract => (lw.Value.Core, lw.Value.A, lw.Value.C - rc),
                    ExpressionType.Multiply when Math.Abs(rc) > 1e-12
                        => (lw.Value.Core, lw.Value.A * rc, lw.Value.C * rc),
                    ExpressionType.Divide when Math.Abs(rc) > 1e-12
                        => (lw.Value.Core, lw.Value.A / rc, lw.Value.C / rc),
                    _ => null,
                };
            }

            // 常数在左：core 取反的场合要同步翻 A 与 C
            if (l is ConstantExpression { Value: double lc })
            {
                var rw = LinearWrap(r);
                if (rw is null) return null;
                return b.NodeType switch
                {
                    ExpressionType.Add => (rw.Value.Core, rw.Value.A, rw.Value.C + lc),
                    ExpressionType.Subtract => (rw.Value.Core, -rw.Value.A, lc - rw.Value.C),
                    ExpressionType.Multiply when Math.Abs(lc) > 1e-12
                        => (rw.Value.Core, rw.Value.A * lc, rw.Value.C * lc),
                    _ => null,
                };
            }
        }

        // 非线性部分就是 core 本身（sin(x)、x²、|x| …）
        return (e, 1, 0);
    }

    // ────────────────────────── 分派 ──────────────────────────

    private static ClosedForm CallForm(MethodCallExpression call, AngleMode mode, double a, double c)
    {
        if (call.Object is not null) return null!;
        var name = call.Method.Name;

        // x² / x³：解析器把 x^n 统一编译成 Math.Pow(x, n) —— 树里**没有** Power 节点。
        if (name == nameof(Math.Pow) && call.Arguments.Count == 2
            && StripConvert(call.Arguments[0]) is ParameterExpression
            && StripConvert(call.Arguments[1]) is ConstantExpression { Value: double p })
        {
            if (Math.Abs(p - 2) < 1e-12) return SquareForm(a, c);
            if (Math.Abs(p - 3) < 1e-12) return CubeForm(a, c);
            return null!;
        }

        if (call.Arguments.Count != 1) return null!;
        var arg = StripConvert(call.Arguments[0]);

        // ⛔ **先按函数名分派，再看参数形状** —— 反过来 sin(x) 的纯参数会先落进
        //    "严格参数"分支而那里没有 sin，直接落空（实测全盘翻车）。
        switch (name)
        {
            // 非三角：参数必须**严格是 x**
            case nameof(Math.Exp) when arg is ParameterExpression: return ExpForm(a, c);
            case nameof(Math.Log) when arg is ParameterExpression: return LnForm(a, c);
            case nameof(Math.Sqrt) when arg is ParameterExpression: return SqrtForm(a, c);
            case nameof(Math.Abs) when arg is ParameterExpression: return AbsForm(a, c);

            // 三角：参数是 x 或 c·x（本版只认 c=1，见类型注释）。
            // ⚠️ 角度制下解析器在参数外面包了 Multiply(x, π/180)（度转弧度）——不剥掉
            //    系数判据看到 0.0175 而不是 1，角度制永远匹配不上。
            case nameof(Math.Sin) or nameof(Math.Cos) or nameof(Math.Tan):
                if (mode == AngleMode.Degrees
                    && arg is BinaryExpression { NodeType: ExpressionType.Multiply } wrap
                    && StripConvert(wrap.Left) is ParameterExpression
                    && StripConvert(wrap.Right) is ConstantExpression { Value: double k }
                    && Math.Abs(k - Math.PI / 180.0) < 1e-12)
                {
                    arg = StripConvert(wrap.Left);
                }

                if (CoefficientOf(arg) is { } coef)
                {
                    return name switch
                    {
                        nameof(Math.Sin) when Math.Abs(coef - 1) < 1e-12 => TrigForm("sin", mode, odd: true, a, c),
                        nameof(Math.Cos) when Math.Abs(coef - 1) < 1e-12 => TrigForm("cos", mode, odd: false, a, c),
                        nameof(Math.Tan) when Math.Abs(coef - 1) < 1e-12 => TanForm(mode, a, c),
                        _ => null!,
                    };
                }
                return null!;
        }

        return null!;
    }

    /// <summary>提取「c·x」里的系数 c；不是这个形状返回 null。</summary>
    private static double? CoefficientOf(Expression e)
    {
        e = StripConvert(e);
        if (e is ParameterExpression) return 1;
        if (e is BinaryExpression b)
        {
            var l = StripConvert(b.Left);
            var r = StripConvert(b.Right);

            if (b.NodeType == ExpressionType.Multiply)
            {
                if (l is ConstantExpression { Value: double c1 } && r is ParameterExpression) return c1;
                if (r is ConstantExpression { Value: double c2 } && l is ParameterExpression) return c2;
            }

            if (b.NodeType == ExpressionType.Divide
                && l is ParameterExpression
                && r is ConstantExpression { Value: double c3 } && c3 != 0)
                return 1 / c3;
        }

        return null;
    }

    // ────────────────────────── 数值格式化 ──────────────────────────

    private static string Num(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        if (double.IsPositiveInfinity(v)) return "+∞";
        if (double.IsNegativeInfinity(v)) return "-∞";

        var abs = Math.Abs(v);
        if (abs < 1e-9) return "0";

        // 简单分数优先：结论里 −1/2 比 −0.5 像数学结论，而且分数会被排版成上下堆叠。
        // ⚠️ 只认分母 2/3/4/6 且分子不太大 —— 再放宽就会把 0.618 这类实数值硬凑成怪分数。
        foreach (var den in new[] { 2, 3, 4, 6 })
        {
            var scaled = v * den;
            var rounded = Math.Round(scaled);
            if (Math.Abs(scaled - rounded) < 1e-9
                && Math.Abs(rounded) < 1000
                && Math.Abs(rounded % den) > 0.5)
            {
                return $"{rounded:0}/{den}".Replace('-', '−');
            }
        }

        if (abs >= 1e5 || abs < 1e-4)
            return v.ToString("0.###e+0", System.Globalization.CultureInfo.InvariantCulture).Replace('-', '−');

        // ⛔ 负号统一用数学减号 −（U+2212）—— 界面上其他字段（值域、区间）都是 −，
        //    同一张卡上混用 ASCII 连字符和数学负号会一眼看出"机器拼的"。
        return Math.Round(v, 4).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
            .Replace('-', '−');
    }

    /// <summary>把 core 的值域 [lo, hi] 做线性变换 y = a·v + c 后重新排序并格式化。</summary>
    private static string ScaledRange(double lo, double hi, double a, double c, string openLo, string openHi)
    {
        var v1 = a * lo + c;
        var v2 = a * hi + c;
        return $"{openLo}{B(Math.Min(v1, v2))}, {B(Math.Max(v1, v2))}{openHi}";
    }

    /// <summary>区间端点：±∞ 直接写符号。</summary>
    private static string B(double v)
        => double.IsNegativeInfinity(v) ? "−∞" : double.IsPositiveInfinity(v) ? "+∞" : Num(v);

    // ────────────────────────── 各类形式 ──────────────────────────
    // ⛔ 每个 form 都接收线性包裹 (a, c)：y = a·f(x) + c。
    //    值域/极值做线性变换；a < 0 时单调区间增减互换、极大极小互换；
    //    奇偶性：c ≠ 0 时奇函数被破坏（sin(x)+6 非奇非偶），偶函数平移后仍是偶函数。

    // ── x ──

    private static ClosedForm XForm(AngleMode mode, double a, double c)
    {
        List<(string Label, string? Value)> fields =
        [
        ("定义域", "x ∈ ℝ"),
        ("值域", "ℝ"),
        ("单调递增", a > 0 ? "处处递增" : null),
        ("单调递减", a < 0 ? "处处递减" : null),
        ("零点", $"x = {Num(-c / a)}"),
        ("奇偶性", Math.Abs(c) < 1e-12 ? "奇函数" : "非奇非偶"),
        ("渐近线", "无"),
        ];

        return new ClosedForm(fields
            .Where(f => f.Value is not null)
            .Select(f => (f.Label, f.Value!))
            .ToList());
    }

    // ── 常数 ──

    private static ClosedForm ConstantForm(double v)
    {
        var s = Num(v);
        return new ClosedForm(
        [
            ("定义域", "x ∈ ℝ"),
            ("值域", Math.Abs(v) < 1e-12 ? "{0}" : $"{{{s}}}"),
            ("零点", Math.Abs(v) < 1e-12 ? "处处为零" : "无"),
            ("单调性", "常数，不增不减"),
            ("奇偶性", Math.Abs(v) < 1e-12 ? "既是奇函数也是偶函数" : "非奇非偶"),
            ("周期性", "常数没有周期"),
        ]);
    }

    // ── 三角 ──

    private static ClosedForm TrigForm(string name, AngleMode mode, bool odd, double a, double c)
    {
        var sin = name == "sin";
        var deg = mode == AngleMode.Degrees;

        // 单调区间用**开区间圆括号** —— 数学惯例（教材上单调区间一律写开区间）。
        // 加减常数、乘除系数都不改变单调性（a<0 时增减互换）。
        (string Inc, string Dec, string? Max, string? Min) r = (sin, deg) switch
        {
            (true, false) => ("(-π/2 + 2kπ, π/2 + 2kπ)", "(π/2 + 2kπ, 3π/2 + 2kπ)",
                              $"y = {Num(a + c)}（x = π/2 + 2kπ）", $"y = {Num(-a + c)}（x = −π/2 + 2kπ）"),
            (true, true) => ("(-90° + k·360°, 90° + k·360°)", "(90° + k·360°, 270° + k·360°)",
                             $"y = {Num(a + c)}（x = 90° + k·360°）", $"y = {Num(-a + c)}（x = −90° + k·360°）"),
            (false, false) => ("(π + 2kπ, 2π + 2kπ)", "(2kπ, π + 2kπ)",
                               $"y = {Num(a + c)}（x = 2kπ）", $"y = {Num(-a + c)}（x = π + 2kπ）"),
            (false, true) => ("(180° + k·360°, 360° + k·360°)", "(k·360°, 180° + k·360°)",
                              $"y = {Num(a + c)}（x = k·360°）", $"y = {Num(-a + c)}（x = 180° + k·360°）"),
        };

        var inc = r.Inc;
        var dec = r.Dec;
        var max = r.Max;
        var min = r.Min;
        if (a < 0)
        {
            // 乘负系数：增减互换、极大极小互换
            (inc, dec) = (dec, inc);
            (max, min) = (min, max);
        }

        var fields = new List<(string, string)>
        {
            ("定义域", "x ∈ ℝ"),
            ("值域", ScaledRange(-1, 1, a, c, "[", "]")),
            ("周期", deg ? "T = 360°" : "T = 2π"),
            ("奇偶性", Math.Abs(c) < 1e-12 ? (odd ? "奇函数" : "偶函数")
                                            : (odd ? "非奇非偶" : "偶函数")),
            ("单调递增", inc),
            ("单调递减", dec),
            ("零点", TrigZeros(sin ? "sin" : "cos", -c / a, mode) ?? "无"),
        };
        fields.Add(("极大值", max!));
        fields.Add(("极小值", min!));
        fields.Add(("渐近线", "无"));

        return new ClosedForm(fields, Note: "区间中的 k 均为整数。");
    }

    private static ClosedForm TanForm(AngleMode mode, double a, double c)
    {
        // ⛔ 弧度和角度**分开写**，别用模板拼 —— 弧度制写 "kπ"（教科书写法），
        //    角度制必须带 ·（"k180°" 不成话）。
        var inc = a > 0
            ? "每个区间内递增"
            : "每个区间内递减";

        return mode == AngleMode.Degrees
            ? new ClosedForm(
            [
                ("定义域", "x ≠ 90° + k·180°"),
                ("值域", "ℝ"),
                ("周期", "T = 180°"),
                ("奇偶性", Math.Abs(c) < 1e-12 ? "奇函数" : "非奇非偶"),
                ("单调递增", inc),
                ("零点", TrigZeros("tan", -c / a, mode) ?? "无"),
                ("极值", "无（函数无界）"),
                ("渐近线", "x = 90° + k·180°"),
            ], Note: "区间中的 k 均为整数。")
            : new ClosedForm(
            [
                ("定义域", "x ≠ π/2 + kπ"),
                ("值域", "ℝ"),
                ("周期", "T = π"),
                ("奇偶性", Math.Abs(c) < 1e-12 ? "奇函数" : "非奇非偶"),
                ("单调递增", inc),
                ("零点", TrigZeros("tan", -c / a, mode) ?? "无"),
                ("极值", "无（函数无界）"),
                ("渐近线", "x = π/2 + kπ"),
            ], Note: "区间中的 k 均为整数。");
    }

    /// <summary>
    /// 解 sin(x)/cos(x)/tan(x) = t 的**精确通式**。特殊值（0、±1/2、±√2/2、±√3/2、±1 等）
    /// 给 π 的分数形式；一般值给 arcsin/arccos/arctan 写法；|t| > 1 给「无」。
    /// ⛔ 返回 null 表示给不出（调用方整体落回数值观察）—— 当前没有这种情况，留作扩展位。
    /// </summary>
    private static string TrigZeros(string fn, double t, AngleMode mode)
    {
        var deg = mode == AngleMode.Degrees;

        if (fn == "tan")
        {
            var per = deg ? "k·180°" : "kπ";
            if (Math.Abs(t) < 1e-12) return $"x = {per}";

            var alpha = (deg, t) switch
            {
                (_, { }) when Math.Abs(t) < 1e-12 => "0",
                (false, { }) when Math.Abs(t - 1) < 1e-12 => "π/4",
                (false, { }) when Math.Abs(t + 1) < 1e-12 => "−π/4",
                (false, { }) when Math.Abs(t - Math.Sqrt(3)) < 1e-12 => "π/3",
                (false, { }) when Math.Abs(t + Math.Sqrt(3)) < 1e-12 => "−π/3",
                (false, { }) when Math.Abs(t - 1 / Math.Sqrt(3)) < 1e-12 => "π/6",
                (false, { }) when Math.Abs(t + 1 / Math.Sqrt(3)) < 1e-12 => "−π/6",
                (true, { }) when Math.Abs(t - 1) < 1e-12 => "45°",
                (true, { }) when Math.Abs(t + 1) < 1e-12 => "−45°",
                (true, { }) when Math.Abs(t - Math.Sqrt(3)) < 1e-12 => "60°",
                (true, { }) when Math.Abs(t + Math.Sqrt(3)) < 1e-12 => "−60°",
                (true, { }) when Math.Abs(t - 1 / Math.Sqrt(3)) < 1e-12 => "30°",
                (true, { }) when Math.Abs(t + 1 / Math.Sqrt(3)) < 1e-12 => "−30°",
                _ => $"arctan {Num(t)}",
            };
            return $"x = {alpha} + {per}";
        }

        // sin / cos：|t| > 1 无解
        if (Math.Abs(t) > 1 + 1e-12) return "无";

        var two = deg ? "k·360°" : "2kπ";
        var isSin = fn == "sin";

        if (Math.Abs(t) < 1e-12)
            return isSin ? (deg ? "x = k·180°" : "x = kπ") : (deg ? "x = 90° + k·180°" : "x = π/2 + kπ");

        if (Math.Abs(Math.Abs(t) - 1) < 1e-12)
        {
            return (isSin, t > 0, deg) switch
            {
                (true, true, false) => "x = π/2 + 2kπ",
                (true, false, false) => "x = −π/2 + 2kπ",
                (true, true, true) => "x = 90° + k·360°",
                (true, false, true) => "x = −90° + k·360°",
                (false, true, false) => "x = 2kπ",
                (false, false, false) => "x = π + 2kπ",
                (false, true, true) => "x = k·360°",
                (false, false, true) => "x = 180° + k·360°",
            };
        }

        // |t| < 1 的特殊值表（sin：两组解 α 与 π−α；cos：一组 α 与 −α）
        (var a1, var a2) = SinCosSpecial(isSin, t, mode);
        if (a1 is not null)
            return isSin
                ? $"x = {a1} + {two}、x = {a2} + {two}"
                : $"x = {a1} + {two}、x = −{a1} + {two}";

        // 一般值：arcsin / arccos 写法（数学上精确）
        var inv = isSin ? $"arcsin {Num(t)}" : $"arccos {Num(t)}";
        var halfTurn = deg ? "180°" : "π";
        return isSin
            ? $"x = {inv} + {two}、x = {halfTurn} − {inv} + {two}"
            : $"x = ±{inv} + {two}";
    }

    /// <summary>sin/cos 在 |t| &lt; 1 时的特殊值表。负 t 直接取负 —— 两组解同步取负。</summary>
    private static (string?, string?) SinCosSpecial(bool isSin, double t, AngleMode mode)
    {
        var deg = mode == AngleMode.Degrees;
        var d = deg ? "°" : "";

        // (比值, sin 第一组, sin 第二组, cos)
        (double V, string S1, string S2, string C)[] table =
        {
            (0.5,              $"π/6{d}",  $"5π/6{d}",  deg ? "60°" : "π/3"),
            (Math.Sqrt(2) / 2, $"π/4{d}",  $"3π/4{d}",  deg ? "45°" : "π/4"),
            (Math.Sqrt(3) / 2, $"π/3{d}",  $"2π/3{d}",  deg ? "30°" : "π/6"),
        };

        foreach (var (v, s1, s2, cAlpha) in table)
        {
            if (Math.Abs(Math.Abs(t) - v) > 1e-12) continue;

            return t < 0
                ? (isSin ? $"−{s1}" : $"−{cAlpha}", isSin ? $"−{s2}" : null)
                : (isSin ? s1 : cAlpha, isSin ? s2 : null);
        }

        return (null, null);
    }

    // ── 指数 / 对数 ──

    private static ClosedForm ExpForm(double a, double c)
    {
        List<(string Label, string? Value)> fields =
        [
        ("定义域", "x ∈ ℝ"),
        ("值域", a > 0 ? $"({Num(c)}, +∞)" : $"(−∞, {Num(c)})"),
        ("单调递增", a > 0 ? "处处递增" : null),
        ("单调递减", a < 0 ? "处处递减" : null),
        ("零点", -c / a > 0 ? $"x = {Num(Math.Log(-c / a))}" : "无（恒为正）"),
        ("与 y 轴交点", $"(0, {Num(a + c)})"),
        ("奇偶性", "非奇非偶"),
        ("渐近线", $"y = {Num(c)}"),
        ];

        return new ClosedForm(fields
            .Where(f => f.Value is not null)
            .Select(f => (f.Label, f.Value!))
            .ToList());
    }

    private static ClosedForm LnForm(double a, double c)
    {
        List<(string Label, string? Value)> fields =
        [
        ("定义域", "(0, +∞)"),
        ("值域", "ℝ"),
        ("单调递增", a > 0 ? "定义域内处处递增" : null),
        ("单调递减", a < 0 ? "定义域内处处递减" : null),
        ("零点", $"x = {Num(Math.Exp(-c / a))}"),
        ("奇偶性", "非奇非偶"),
        ("渐近线", "x = 0（沿定义域边界）"),
        ];

        return new ClosedForm(fields
            .Where(f => f.Value is not null)
            .Select(f => (f.Label, f.Value!))
            .ToList());
    }

    // ── 幂 / 根 / 绝对值 ──

    private static ClosedForm SquareForm(double a, double c)
    {
        List<(string Label, string? Value)> fields =
        [
        ("定义域", "x ∈ ℝ"),
        ("值域", a > 0 ? $"[{Num(c)}, +∞)" : $"(−∞, {Num(c)}]"),
        ("单调递减", a > 0 ? "(−∞, 0)" : null),
        ("单调递增", a > 0 ? "(0, +∞)" : null),
        ("单调递增", a < 0 ? "(−∞, 0)" : null),
        ("单调递减", a < 0 ? "(0, +∞)" : null),
        (a > 0 ? "极小值" : "极大值", $"y = {Num(c)}（x = 0）"),
        ("零点", ZeroOfPower(-c / a)),
        ("奇偶性", "偶函数"),
        ("渐近线", "无"),
        ];

        return new ClosedForm(fields
            .Where(f => f.Value is not null)
            .Select(f => (f.Label, f.Value!))
            .ToList());
    }

    private static ClosedForm CubeForm(double a, double c)
    {
        List<(string Label, string? Value)> fields =
        [
        ("定义域", "x ∈ ℝ"),
        ("值域", "ℝ"),
        ("单调递增", a > 0 ? "处处递增" : null),
        ("单调递减", a < 0 ? "处处递减" : null),
        ("极值", "无（x = 0 处切线水平但不变号，是拐点）"),
        ("零点", $"x = ∛{Num(-c / a)}"),
        ("奇偶性", Math.Abs(c) < 1e-12 ? "奇函数" : "非奇非偶"),
        ("渐近线", "无"),
        ];

        return new ClosedForm(fields
            .Where(f => f.Value is not null)
            .Select(f => (f.Label, f.Value!))
            .ToList());
    }

    private static ClosedForm SqrtForm(double a, double c)
    {
        List<(string Label, string? Value)> fields =
        [
        ("定义域", "[0, +∞)"),
        ("值域", a > 0 ? $"[{Num(c)}, +∞)" : $"(−∞, {Num(c)}]"),
        ("单调递增", a > 0 ? "定义域内处处递增" : null),
        ("单调递减", a < 0 ? "定义域内处处递减" : null),
        ("零点", ZeroOfPower(-c / a)),
        ("奇偶性", "非奇非偶"),
        ("渐近线", "无"),
        ];

        return new ClosedForm(fields
            .Where(f => f.Value is not null)
            .Select(f => (f.Label, f.Value!))
            .ToList());
    }

    private static ClosedForm AbsForm(double a, double c)
    {
        List<(string Label, string? Value)> fields =
        [
        ("定义域", "x ∈ ℝ"),
        ("值域", a > 0 ? $"[{Num(c)}, +∞)" : $"(−∞, {Num(c)}]"),
        ("单调递减", a > 0 ? "(−∞, 0)" : null),
        ("单调递增", a > 0 ? "(0, +∞)" : null),
        ("单调递增", a < 0 ? "(−∞, 0)" : null),
        ("单调递减", a < 0 ? "(0, +∞)" : null),
        (a > 0 ? "极小值" : "极大值", $"y = {Num(c)}（x = 0）"),
        ("零点", ZeroOfAbs(-c / a)),
        ("奇偶性", "偶函数"),
        ("渐近线", "无"),
        ];

        return new ClosedForm(fields
            .Where(f => f.Value is not null)
            .Select(f => (f.Label, f.Value!))
            .ToList());
    }

    // ────────────────────────── 零点辅助 ──────────────────────────

    /// <summary>解 x² = t 或 √x = t 的零点（t = −c/a）。</summary>
    private static string ZeroOfPower(double t)
    {
        if (t < -1e-12) return "无";
        if (Math.Abs(t) < 1e-12) return "x = 0";
        return $"x = ±√{Num(t)}";
    }

    /// <summary>解 |x| = t 的零点。</summary>
    private static string ZeroOfAbs(double t)
    {
        if (t < -1e-12) return "无";
        if (Math.Abs(t) < 1e-12) return "x = 0";
        return $"x = ±{Num(t)}";
    }
}
