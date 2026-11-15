using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 方程求解：一元方程（数值求根，支持 π / 三角函数 / 对数）与一次方程组（精确分数消元）。
///
/// <para>
/// 判定顺序很重要：**先按一次式解析**，全部成功就走进精确消元 —— 这样 `2x+1=5` 得到的是
/// 精确的 `x = 2` 而不是 `1.9999999997`。只有解析不成一次式（含平方、sin、ln…）才退到数值求根。
/// </para>
/// <para>
/// ⛔ 多元高次方程组（如圆与直线的交点）这一版不做：那是非线性方程组的数值解，
/// 多解、漏解、收敛域都得另做一套，混进来只会给出看着对、其实缺解的结果。
/// </para>
/// </summary>
public static class EquationSolver
{
    public sealed class Result
    {
        public bool Ok => Error is null;
        public string? Error { get; init; }
        /// <summary>「一元方程」/「一次方程组」，给界面当小标题。</summary>
        public string Kind { get; init; } = "";
        /// <summary>每行一条结论，例如 `x = 2`、`y = 3/2`。</summary>
        public List<string> Solutions { get; init; } = new();
        /// <summary>补充说明（近似、无解、无穷多解等）。</summary>
        public string? Note { get; init; }
    }

    /// <summary>
    /// 校验**一条**方程能不能被理解（界面边输边用，决定「确定」能不能点）。
    ///
    /// <para>
    /// 判据和 <see cref="Solve"/> 完全一致：先试一次式（多未知数都行），
    /// 不行再试一元数值式子（只认 x）—— 界面上的"合法"必须等于求解器真的算得出来，
    /// 否则会出现「按钮能点、点了报错」这种最烦人的情况。
    /// </para>
    /// </summary>
    public static bool Validate(string? equation, out string? error)
    {
        var text = (equation ?? "").Trim();

        if (text.Length == 0)
        {
            error = "方程不能为空。";
            return false;
        }

        var sides = text.Split('=');
        if (sides.Length != 2)
        {
            error = sides.Length < 2 ? "还差一个等号，写成「左边 = 右边」。" : "等号只能有一个。";
            return false;
        }

        var left = sides[0].Trim();
        var right = sides[1].Trim();

        if (left.Length == 0 || right.Length == 0)
        {
            error = "等号两侧都要有内容。";
            return false;
        }

        string? linearError = null;
        try
        {
            LinearFormParser.Parse(left);
            LinearFormParser.Parse(right);
            error = null;
            return true;
        }
        catch (LinearParseException ex)
        {
            linearError = ex.Message;
        }

        // 一元方程走数值求根这条路，变量只能是 x
        if (ExpressionParser.TryCompile($"({left}) - ({right})", AngleMode.Radians, out var fn, out var numericError)
            && fn is not null)
        {
            error = null;
            return true;
        }

        error = string.IsNullOrWhiteSpace(numericError)
            ? $"式子看不懂：{linearError}"
            : $"式子看不懂：{numericError}（一元方程的未知数请用 x）";

        return false;
    }

    public static Result Solve(string input)
    {
        var lines = (input ?? "")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

        if (lines.Count == 0) return Fail("请输入方程，每行一个，例如 x^2 - 4 = 0");

        // ── ① 先按一次式解析 ──
        var equations = new List<(LinearForm Left, LinearForm Right)>();
        string? linearError = null;

        foreach (var line in lines)
        {
            var sides = line.Split('=');
            if (sides.Length != 2)
            {
                linearError = "每行要写成「左边 = 右边」的形式。";
                break;
            }

            var left = sides[0].Trim();
            var right = sides[1].Trim();
            if (left.Length == 0 || right.Length == 0)
            {
                linearError = "等号两侧都要有内容。";
                break;
            }

            try
            {
                equations.Add((LinearFormParser.Parse(left), LinearFormParser.Parse(right)));
            }
            catch (LinearParseException ex)
            {
                linearError = ex.Message;
                break;
            }
        }

        if (linearError is null) return SolveLinear(equations);

        // ── ② 一次式解析失败 → 只有当它是**单个未知数的一元方程**才走数值求根 ──
        if (lines.Count == 1) return SolveNumeric(lines[0], linearError);

        return Fail("多元的非一次方程组这一版还不支持。一次方程组可以每行写一个方程，" +
                    "一元方程请用 x 作未知数（例如 x^2 - 5x + 6 = 0）。");
    }

    private static Result Fail(string message) => new() { Error = message };

    // ══════════════════════════════════════════════════════
    //  一次方程组：精确分数高斯消元
    // ══════════════════════════════════════════════════════

    private static Result SolveLinear(List<(LinearForm Left, LinearForm Right)> equations)
    {
        var variables = equations
            .SelectMany(e => e.Left.Coeffs.Keys.Concat(e.Right.Coeffs.Keys))
            .Distinct()
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        if (variables.Count == 0)
            return Fail("方程里没有未知数。");

        var rows = equations.Count;
        var matrix = new Rational[rows][];
        var rhs = new Rational[rows];

        for (var r = 0; r < rows; r++)
        {
            var (left, right) = equations[r];
            matrix[r] = new Rational[variables.Count];

            for (var c = 0; c < variables.Count; c++)
            {
                var name = variables[c];
                matrix[r][c] = left[name] - right[name];
            }

            // 常数项移到右边：左边常数 − 右边常数，取反
            rhs[r] = right.Constant - left.Constant;
        }

        var solution = LinearSolver.Solve(matrix, rhs);

        if (!solution.Consistent)
            return new Result
            {
                Kind = "一次方程组",
                Note = "把各方程对照一遍：它们互相矛盾（例如同时要求 x = 1 和 x = 2），因此**无解**。",
            };

        if (solution.Unique)
        {
            var lines = new List<string>();
            for (var i = 0; i < variables.Count; i++)
                lines.Add($"{variables[i]} = {Format(solution.Particular[i])}");

            return new Result
            {
                Kind = variables.Count == 1 ? "一元一次方程" : "一次方程组",
                Solutions = lines,
                Note = "结果为精确值（分数形式表示未除尽的有理数）。",
            };
        }

        return DescribeInfinite(variables, solution);
    }

    /// <summary>无穷多解：把自由变量取成参数 t1、t2…，其余变量用它表示。</summary>
    private static Result DescribeInfinite(List<string> variables, SolveResult solution)
    {
        var pivotSet = new HashSet<int>(solution.PivotColumns);
        var free = new List<int>();
        var isPivot = new bool[variables.Count];

        for (var i = 0; i < variables.Count; i++)
        {
            isPivot[i] = pivotSet.Contains(i);
            if (!isPivot[i]) free.Add(i);
        }

        var parameters = free.Select((_, i) => $"t{i + 1}").ToList();
        var lines = new List<string>();

        for (var i = 0; i < variables.Count; i++)
        {
            if (!isPivot[i])
            {
                lines.Add($"{variables[i]} = {parameters[free.IndexOf(i)]}");
                continue;
            }

            var sb = new StringBuilder($"{variables[i]} = {Format(solution.Particular[i])}");

            for (var k = 0; k < free.Count; k++)
            {
                var coeff = solution.NullBasis[k][i];
                if (coeff.IsZero) continue;

                var term = coeff == Rational.One
                    ? parameters[k]
                    : coeff == -Rational.One
                        ? "−" + parameters[k]
                        : Format(coeff) + parameters[k];

                sb.Append(coeff.Sign > 0 ? " + " : " − ");
                sb.Append(coeff.Sign > 0 ? term : term.TrimStart('−'));
            }

            lines.Add(sb.ToString());
        }

        return new Result
        {
            Kind = "一次方程组",
            Solutions = lines,
            Note = $"未知数比独立方程多，有**无穷多组解**。上面是通解，{string.Join("、", parameters)} 可任意取值。",
        };
    }

    // ══════════════════════════════════════════════════════
    //  一元方程：数值求根
    // ══════════════════════════════════════════════════════

    private static Result SolveNumeric(string line, string linearError)
    {
        var sides = line.Split('=');
        if (sides.Length != 2) return Fail("每行要写成「左边 = 右边」的形式。");

        // 与一次式解析共用一条式子，但不能被它挡下来：含 sin / ln 的式子只能数值算
        var text = $"({sides[0].Trim()}) - ({sides[1].Trim()})";

        if (!ExpressionParser.TryCompile(text, AngleMode.Radians, out var fn, out var error) || fn is null)
        {
            return Fail(string.IsNullOrWhiteSpace(error)
                ? linearError
                : $"式子看不懂：{error}（一元方程请用 x 作未知数）");
        }

        var (roots, capped) = FindRoots(fn);

        if (roots.Count == 0)
        {
            return new Result
            {
                Kind = "一元方程",
                Note = "在常用范围内没有找到实根（可能是无解，或根非常稀疏）。",
            };
        }

        // 周期函数有无穷多个解，全列出来没意义：只给靠近原点的那几个，并说明白
        var shown = roots;
        string note;

        if (capped || roots.Count > 12)
        {
            // 先挑**离原点最近**的 6 个，再按大小排 —— 直接取前 6 个会全是负数侧的根
            var near = roots.Where(r => Math.Abs(r) <= 10)
                            .OrderBy(r => Math.Abs(r))
                            .Take(6)
                            .OrderBy(r => r)
                            .ToList();
            shown = near.Count > 0 ? near : roots.OrderBy(r => Math.Abs(r)).Take(6).OrderBy(r => r).ToList();
            note = "该方程在实数范围内有大量解（周期函数通常有无穷多组），上表只列出靠近原点的若干个。" +
                   "通解需按函数的周期写出。";
        }
        else
        {
            note = roots.Count == 1 ? "已按方程本身的性质求根，与窗口大小无关。"
                                    : $"共 {roots.Count} 个实根。";
        }

        var labels = shown.Select(r => $"x = {FormatRoot(r)}").ToList();

        return new Result
        {
            Kind = "一元方程",
            Solutions = labels,
            Note = note,
        };
    }

    private static double Eval(Func<double, double> f, double x)
    {
        try { return f(x); }
        catch { return double.NaN; }
    }

    /// <summary>
    /// 由近及远多尺度扫描 + 二分细化。
    ///
    /// ⚠️ 顺序必须是"先扫原点附近"：周期函数（sin(x)=0.5）会有无穷多个根，
    /// 从 −1e5 开始扫会把解全找在西边几百个单位外，列表里看不到人真正关心的那几个。
    /// ⚠️ 抓到 64 个就停：再扫下去只是浪费时间，界面也放不下。
    /// </summary>
    private static (List<double> Roots, bool Capped) FindRoots(Func<double, double> f)
    {
        var roots = new List<double>();
        const int cap = 64;
        var capped = false;

        void Push(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return;
            foreach (var existing in roots)
            {
                var tol = 1e-6 * Math.Max(1, Math.Abs(existing));
                if (Math.Abs(existing - v) < tol) return;
            }

            roots.Add(v);
        }

        void Scan(double from, double to, int steps)
        {
            var step = (to - from) / steps;
            var prevX = from;
            var prevY = Eval(f, from);

            for (var i = 1; i <= steps; i++)
            {
                var x = from + step * i;
                var y = Eval(f, x);

                if (double.IsNaN(y)) { prevX = x; prevY = y; continue; }

                if (Math.Abs(y) < 1e-12)
                {
                    Push(x);
                }
                else if (!double.IsNaN(prevY) && prevY != 0 && Math.Sign(y) != Math.Sign(prevY))
                {
                    var root = Bisect(f, prevX, x, prevY);
                    if (root is { } r) Push(r);
                }

                prevX = x;
                prevY = y;

                if (roots.Count > cap) { capped = true; return; }
            }
        }

        Scan(-10, 10, 40_000);            // 原点附近，最细
        if (capped) { roots.Sort(); return (roots, true); }

        Scan(-100, 100, 120_000);
        if (capped) { roots.Sort(); return (roots, true); }

        Scan(-1000, 1000, 120_000);
        if (capped) { roots.Sort(); return (roots, true); }

        Scan(-100_000, 100_000, 120_000);  // 大根（x² = 1e10 这种）
        roots.Sort();
        return (roots, capped);
    }

    /// <summary>二分。⛔ 必须核对"收敛点真的是零点"：tan(x) 的极点也会造成变号，那是假根。</summary>
    private static double? Bisect(Func<double, double> f, double a, double b, double fa)
    {
        for (var i = 0; i < 200; i++)
        {
            var mid = (a + b) / 2;
            var fm = Eval(f, mid);
            if (double.IsNaN(fm)) return null;

            if (Math.Sign(fm) == Math.Sign(fa)) { a = mid; fa = fm; }
            else b = mid;

            if (b - a < 1e-14 * Math.Max(1, Math.Abs(b))) break;
        }

        var root = (a + b) / 2;
        var value = Eval(f, root);

        // 真根：代入后接近 0；假根（极点）：代入是几万、几亿
        if (double.IsNaN(value) || Math.Abs(value) > 1e-6) return null;
        return root;
    }

    // ══════════════════════════════════════════════════════
    //  显示
    // ══════════════════════════════════════════════════════

    /// <summary>精确值显示：整数、简单分数，负数用数学负号。</summary>
    private static string Format(Rational value)
    {
        var text = value.ToString();
        return text.Replace("-", "−");
    }

    /// <summary>
    /// 数值根显示：能认出来的就写成数学式（1/2、√2、π/6），认不出才落到小数。
    /// 这样按课本做题时看到的是 `x = −1/2`，而不是 `−0.5`。
    /// </summary>
    private static string FormatRoot(double v)
    {
        if (Math.Abs(v) < 1e-12) return "0";

        // 整数 / 简单分数
        for (var q = 1; q <= 64; q++)
        {
            var p = v * q;
            var rounded = Math.Round(p);
            if (Math.Abs(p - rounded) > 1e-9) continue;

            var sign = rounded < 0 ? "−" : "";
            var num = Math.Abs(rounded);
            return q == 1 ? sign + num.ToString("0") : $"{sign}{num:0}/{q}";
        }

        // π 的倍数：1/6 π、2π …
        var k = v / Math.PI;
        for (var q = 1; q <= 12; q++)
        {
            var p = k * q;
            var rounded = Math.Round(p);
            if (Math.Abs(p - rounded) > 1e-9 || rounded == 0) continue;

            var sign = rounded < 0 ? "−" : "";
            var num = Math.Abs(rounded);
            var head = num == 1 ? "π" : $"{num:0}π";
            return q == 1 ? sign + head : $"{sign}{head}/{q}";
        }

        // 平方根：√2、−√3
        var square = v * v;
        var n = Math.Round(square);
        if (n > 0 && Math.Abs(square - n) < 1e-9 && Math.Abs(Math.Sqrt(n) - Math.Abs(v)) < 1e-9)
        {
            var sign = v < 0 ? "−" : "";
            return n == 1 ? sign + "1" : $"{sign}√{n:0}";
        }

        return v.ToString("0.############", CultureInfo.InvariantCulture).Replace("-", "−");
    }
}
