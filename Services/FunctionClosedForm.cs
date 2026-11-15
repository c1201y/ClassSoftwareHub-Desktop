using System.Linq.Expressions;

namespace ClassSoftwareHub.Desktop.Services;








public sealed record ClosedForm(IReadOnlyList<(string Label, string Value)> Fields, string? Note = null);











public static class FunctionClosedForm
{
    
    public static ClosedForm? Match(LambdaExpression lambda, AngleMode mode)
    {
        var body = lambda.Body;
        if (body is not Expression e) return null;
        if (!IsVariable(e)) return null;

        var wrap = LinearWrap(e);
        if (wrap is not { } w) return null;

        var (core, a, c) = w;

        
        if (Math.Abs(a) < 1e-12) return ConstantForm(c);

        
        if (core is ConstantExpression { Value: double v } && double.IsFinite(v))
            return ConstantForm(a * v + c);

        return core.NodeType switch
        {
            ExpressionType.Parameter => XForm(mode, a, c),
            ExpressionType.Call => CallForm((MethodCallExpression)core, mode, a, c),
            _ => null,
        };
    }

    

    
    private static Expression StripConvert(Expression e)
        => e is UnaryExpression { NodeType: ExpressionType.Convert } u ? StripConvert(u.Operand) : e;

    
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

        
        return (e, 1, 0);
    }

    

    private static ClosedForm CallForm(MethodCallExpression call, AngleMode mode, double a, double c)
    {
        if (call.Object is not null) return null!;
        var name = call.Method.Name;

        
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

        
        
        switch (name)
        {
            
            case nameof(Math.Exp) when arg is ParameterExpression: return ExpForm(a, c);
            case nameof(Math.Log) when arg is ParameterExpression: return LnForm(a, c);
            case nameof(Math.Sqrt) when arg is ParameterExpression: return SqrtForm(a, c);
            case nameof(Math.Abs) when arg is ParameterExpression: return AbsForm(a, c);

            
            
            
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

    

    private static string Num(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        if (double.IsPositiveInfinity(v)) return "+∞";
        if (double.IsNegativeInfinity(v)) return "-∞";

        var abs = Math.Abs(v);
        if (abs < 1e-9) return "0";

        
        
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

        
        
        return Math.Round(v, 4).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
            .Replace('-', '−');
    }

    
    private static string ScaledRange(double lo, double hi, double a, double c, string openLo, string openHi)
    {
        var v1 = a * lo + c;
        var v2 = a * hi + c;
        return $"{openLo}{B(Math.Min(v1, v2))}, {B(Math.Max(v1, v2))}{openHi}";
    }

    
    private static string B(double v)
        => double.IsNegativeInfinity(v) ? "−∞" : double.IsPositiveInfinity(v) ? "+∞" : Num(v);

    
    
    
    

    

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

    

    private static ClosedForm TrigForm(string name, AngleMode mode, bool odd, double a, double c)
    {
        var sin = name == "sin";
        var deg = mode == AngleMode.Degrees;

        
        
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

        
        (var a1, var a2) = SinCosSpecial(isSin, t, mode);
        if (a1 is not null)
            return isSin
                ? $"x = {a1} + {two}、x = {a2} + {two}"
                : $"x = {a1} + {two}、x = −{a1} + {two}";

        
        var inv = isSin ? $"arcsin {Num(t)}" : $"arccos {Num(t)}";
        var halfTurn = deg ? "180°" : "π";
        return isSin
            ? $"x = {inv} + {two}、x = {halfTurn} − {inv} + {two}"
            : $"x = ±{inv} + {two}";
    }

    
    private static (string?, string?) SinCosSpecial(bool isSin, double t, AngleMode mode)
    {
        var deg = mode == AngleMode.Degrees;
        var d = deg ? "°" : "";

        
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

    

    
    private static string ZeroOfPower(double t)
    {
        if (t < -1e-12) return "无";
        if (Math.Abs(t) < 1e-12) return "x = 0";
        return $"x = ±√{Num(t)}";
    }

    
    private static string ZeroOfAbs(double t)
    {
        if (t < -1e-12) return "无";
        if (Math.Abs(t) < 1e-12) return "x = 0";
        return $"x = ±{Num(t)}";
    }
}
