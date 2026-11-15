using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>世界坐标下的一个区间。</summary>
public readonly record struct Interval(double From, double To);

/// <summary>极值点。</summary>
public readonly record struct Extremum(double X, double Y, bool IsMaximum);

/// <summary>奇偶性结论。</summary>
public enum ParityKind
{
    /// <summary>数据不足以下结论。</summary>
    Unknown,

    /// <summary>非奇非偶。</summary>
    Neither,

    /// <summary>偶函数 f(-x) = f(x)。</summary>
    Even,

    /// <summary>奇函数 f(-x) = -f(x)。</summary>
    Odd,

    /// <summary>既是奇函数又是偶函数（即恒为 0）。</summary>
    Both,
}

/// <summary>
/// 一条曲线的分析结论（<b>全部为数值结果</b>）。
///
/// <para>
/// ⛔⛔ 这里每一项都是**采样 + 数值微分**得来的近似结论，**不是符号推导**。
/// 所以：措辞一律是"数值上观察到"，不是"数学上等于"；界面上必须配一个讲清精度边界的入口。
/// 承诺不了的一律不做 —— 全局值域（除非由周期性推得）、导数表达式、积分、化简。
/// </para>
/// </summary>
public sealed class CurveAnalysis
{
    public bool Ok { get; set; }

    /// <summary>分析所依据的 x 区间（＝当时的显示范围）。</summary>
    public double XMin { get; set; }
    public double XMax { get; set; }

    // ── 定义域 ──
    /// <summary>在这段范围里是不是处处有定义。</summary>
    public bool DefinedThroughout { get; set; }

    /// <summary>没有定义的段落（采样能分辨出来的）。</summary>
    public List<Interval> UndefinedRanges { get; } = new();

    // ── 零点与截距 ──
    public List<double> Roots { get; } = new();

    /// <summary>零点太多时不列全，这里说明被截断了。</summary>
    public bool RootsTruncated { get; set; }

    public double? YIntercept { get; set; }

    // ── 单调性 ──
    public List<Interval> Increasing { get; } = new();
    public List<Interval> Decreasing { get; } = new();

    // ── 极值 ──
    public List<Extremum> Extrema { get; } = new();

    // ── 奇偶与周期 ──
    public ParityKind Parity { get; set; } = ParityKind.Unknown;

    /// <summary>检测到的周期（数值验证通过的<b>最小</b>正周期）。</summary>
    public double? Period { get; set; }

    /// <summary>是常数函数（此时谈周期没有意义）。</summary>
    public bool IsConstant { get; set; }

    // ── 渐近线 ──
    /// <summary>垂直渐近线的位置。</summary>
    public List<double> VerticalAsymptotes { get; } = new();

    /// <summary>x → +∞ 的极限（有水平渐近线时）。</summary>
    public double? HorizontalRight { get; set; }

    /// <summary>x → -∞ 的极限。</summary>
    public double? HorizontalLeft { get; set; }

    // ── 凹凸 ──
    /// <summary>拐点的位置。</summary>
    public List<double> Inflections { get; } = new();

    // ── 取值范围 ──
    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }

    /// <summary>
    /// 这份取值范围的成色：<c>true</c> ＝ 由周期性推得，**就是值域**；
    /// <c>false</c> ＝ 仅仅是当前显示范围内的取值范围。⛔ 界面上必须据此换措辞。
    /// </summary>
    public bool RangeIsGlobal { get; set; }
}

/// <summary>
/// 单变量函数的数值分析。
///
/// <para>
/// 手段只有两个：**密集采样** + **数值微分**。不化简、不积分、不求导函数表达式。
/// 好处是任何能在图上画出来的函数都能分析；代价是结论都是近似的，
/// 而且极窄的特征（宽度小于采样步长的峰、孤立的定义域空洞）会被跨过去 ——
/// 这一点必须如实地讲给用户，不能让界面看上去像在给数学结论。
/// </para>
/// </summary>
public static class FunctionAnalyzer
{
    /// <summary>采样点数。4000 点在 960 宽的画布上已经远密于像素。</summary>
    private const int Samples = 4000;

    /// <summary>最多列几个零点 / 极值 / 渐近线（再多就不是给人看的了）。</summary>
    private const int MaxListed = 12;

    /// <summary>零点的二分精化迭代次数（60 次足够到双精度极限）。</summary>
    private const int BisectIterations = 70;

    public static CurveAnalysis Analyze(Func<double, double> f, double xMin, double xMax)
    {
        var result = new CurveAnalysis { XMin = xMin, XMax = xMax };

        if (!double.IsFinite(xMin) || !double.IsFinite(xMax) || xMax <= xMin) return result;
        result.Ok = true;

        var span = xMax - xMin;
        var step = span / (Samples - 1);

        var xs = new double[Samples];
        var ys = new double[Samples];
        for (var i = 0; i < Samples; i++)
        {
            xs[i] = xMin + i * step;
            ys[i] = Eval(f, xs[i]);
        }

        // ⛔ 跳变点：相邻两点数值相差极大且分居 0 两侧 —— 那是渐近线穿过去的地方。
        //    必须先认出来，否则零点检测会把这种"符号变化"当成根，单调整段也会被它污染。
        //
        // ⛔⛔ 标尺不能用"跨度(max−min)"：1/x 这类函数的跨度**本身**就是被极端值撑大的
        //     （x 越靠近 0，|y| 越大），拿它当标尺，判据永远不成立 —— 实测就是这样漏掉了 1/x 的渐近线。
        //     改用 |y| 的 95 分位数：它对少数极端值不敏感，能反映"典型量级"。
        var scale = RobustAbsScale(ys);
        var breakIndex = new bool[Samples];
        for (var i = 0; i < Samples - 1; i++)
        {
            if (!double.IsFinite(ys[i]) || !double.IsFinite(ys[i + 1])) continue;
            if ((ys[i] > 0) == (ys[i + 1] > 0)) continue;

            // 两侧的量级都远超典型值 ⇒ 中间隔着一条渐近线
            if (Math.Abs(ys[i]) > scale * 5 && Math.Abs(ys[i + 1]) > scale * 5)
                breakIndex[i] = true;
        }

        // ⚠️ 顺序有依赖：单调性要用到无障碍区间与垂直渐近线（切分点），
        //    零点的重根补充要用到极值点，取值兜底也要用极值点。
        FindDomain(result, xs, ys);
        FindInterceptsAndRoots(result, f, xs, ys, breakIndex);
        FindVerticalAsymptotes(result, xs, ys, breakIndex, scale);
        FindMonotonicity(result, f, xs, ys, breakIndex);
        RefineZerosFromExtrema(result, scale);
        RefineRangeWithExtrema(result, ys);
        HandleEdges(result, f, scale);
        FindParity(result, f, xMin, xMax);
        FindHorizontals(result, f);
        FindInflections(result, f, xs, breakIndex);

        // 常数函数：谈周期没意义，单独认出来
        if (result.MinValue is { } minValue && result.MaxValue is { } maxValue)
            result.IsConstant = Math.Abs(maxValue - minValue) <= 1e-9 * Math.Max(1, Math.Abs(maxValue));

        // 周期检测要在零点、极值点都备齐之后做（候选周期会从它们的间距反推）
        if (!result.IsConstant)
        {
            result.Period = DetectPeriod(f, result, xMin, xMax);

            // 认出了周期 ⇒ 一个周期内的取值就是全部取值，这才是**值域**。
            if (result.Period is { } t && t > 0)
            {
                var (pmin, pmax) = RangeOverPeriod(f, xs, t);
                if (pmin is not null && pmax is not null)
                {
                    result.MinValue = pmin;
                    result.MaxValue = pmax;
                    result.RangeIsGlobal = true;
                }
            }
        }

        return result;
    }

    // ────────────────────────────── 求值与小工具 ──────────────────────────────

    private static double Eval(Func<double, double> f, double x)
    {
        try
        {
            var y = f(x);
            return double.IsNaN(y) ? double.NaN : y;
        }
        catch
        {
            return double.NaN;
        }
    }

    /// <summary>
    /// |y| 的 95 分位数，作为"典型量级"的标尺。
    /// ⛔ 不能用 max−min：渐近线附近的极端值会把跨度自己撑大，判据就永远不成立了。
    /// </summary>
    private static double RobustAbsScale(double[] ys)
    {
        var abs = new List<double>(ys.Length);
        foreach (var y in ys)
        {
            if (double.IsFinite(y)) abs.Add(Math.Abs(y));
        }

        if (abs.Count == 0) return 1;

        abs.Sort();
        var q = abs[(int)(abs.Count * 0.95)];
        return q > 0 ? q : 1;
    }

    private static (double? Min, double? Max) FiniteRange(IEnumerable<double> values)
    {
        double? min = null, max = null;
        foreach (var v in values)
        {
            if (!double.IsFinite(v)) continue;
            if (min is null || v < min) min = v;
            if (max is null || v > max) max = v;
        }
        return (min, max);
    }

    private static void AddCapped<T>(List<T> list, T item, ref bool truncated)
    {
        if (list.Count < MaxListed) list.Add(item);
        else truncated = true;
    }

    // ────────────────────────────── 定义域 ──────────────────────────────

    /// <summary>探测定义域边界时用的步长，从远到近。</summary>
    private static readonly double[] EdgeProbeSteps = { 1e-2, 1e-3, 1e-4, 1e-6, 1e-8, 1e-10 };

    private static void FindDomain(CurveAnalysis r, double[] xs, double[] ys)
    {
        var i = 0;
        var any = false;

        while (i < Samples)
        {
            if (double.IsFinite(ys[i])) { i++; continue; }

            any = true;
            var start = i;
            while (i < Samples && !double.IsFinite(ys[i])) i++;

            // ⚠️ 边界取**相邻两点的中点**：真边界一定夹在"最后一个有定义的点"和"第一个无定义的点"
            //    之间，直接拿其中一个当边界会偏掉整整一个步长 —— 后面要靠它做极限探测，
            //    偏一个步长（实测 0.0025）就够把 sqrt(0) 算成 sqrt(0.0025) 了。
            var from = start > 0 ? (xs[start - 1] + xs[start]) / 2 : r.XMin;
            var to = i < Samples ? (xs[i - 1] + xs[i]) / 2 : r.XMax;

            if (r.UndefinedRanges.Count < MaxListed) r.UndefinedRanges.Add(new Interval(from, to));
        }

        r.DefinedThroughout = !any;
    }

    /// <summary>
    /// 定义域**边界**上的行为 —— 采样点天生看不到这一块：
    /// ① 边界上的零点（<c>sqrt(0)=0</c>，函数在那里有定义但只是在端点取到 0，不变号）；
    /// ② 边界处的极值（<c>sqrt</c> 的最小值是 0，而采样最小值只有 sqrt(半个步长)）。
    /// </summary>
    private static void HandleEdges(CurveAnalysis r, Func<double, double> f, double scale)
    {
        var truncated = r.RootsTruncated;

        foreach (var range in r.UndefinedRanges)
        {
            TryEdge(r, f, range.To, +1, scale, ref truncated);    // 右边界，定义域在它右边
            TryEdge(r, f, range.From, -1, scale, ref truncated);  // 左边界，定义域在它左边
        }

        r.RootsTruncated = truncated;
        Dedupe(r.VerticalAsymptotes, Math.Max(1e-9, (r.XMax - r.XMin) * 1e-3));
    }

    private static void TryEdge(
        CurveAnalysis r, Func<double, double> f, double edge, int inward, double scale, ref bool truncated)
    {
        // 范围端点上没有"边界"可言（那不是函数自己的定义域边缘）
        if (!double.IsFinite(edge) || edge <= r.XMin + 1e-12 || edge >= r.XMax - 1e-12) return;

        var (limit, converging, probed) = ProbeEdge(f, edge, inward);

        // 一个点都探不到 ⇒ 边界估计偏进无定义区了，不下结论
        if (probed < 4) return;

        if (!converging)
        {
            // ⛔⛔ 逼近边界时 |f| 一路上涨 ⇒ 垂直渐近线。
            //    这一条是专门为 ln(x)、1/√x 这类**缓慢发散**的函数准备的：
            //    ln 在 x=0 附近的值只有 −6 上下，和"正常值"一个量级，
            //    拿数值大小去比（"比典型量级大很多倍"）永远判不出来，只能看趋势。
            if (r.VerticalAsymptotes.Count < MaxListed) r.VerticalAsymptotes.Add(edge);
            return;
        }

        if (limit is not { } value) return;

        // 收敛到边界上的那个值，比任何采样点都更接近真实极值
        if (r.MinValue is null || value < r.MinValue) r.MinValue = value;
        if (r.MaxValue is null || value > r.MaxValue) r.MaxValue = value;

        // 趋于 0 ⇒ 边界上有个零点
        if (Math.Abs(value) <= 1e-3 * Math.Max(1.0, scale)) AddCapped(r.Roots, edge, ref truncated);
    }

    /// <summary>
    /// 从边界往定义域内部一档档逼近，返回最贴近边界的那次取值。
    /// <paramref name="converging"/> 表示 |f| 一路在收敛（而不是发散到 ±∞）——
    /// ⛔ 只有收敛才能把结果当作"极值"用：<c>ln(x)</c> 在 0 的左极限是 −∞，
    /// 探测出来的 −23 只是探得多深的问题，不能当成最小值。
    /// </summary>
    private static (double? Limit, bool Converging, int Probed) ProbeEdge(
        Func<double, double> f, double edge, int inward)
    {
        double? closest = null;
        var converging = true;
        var probed = 0;

        foreach (var h in EdgeProbeSteps)
        {
            var y = Eval(f, edge + inward * h);
            if (!double.IsFinite(y)) break;

            probed++;
            if (closest is { } prev && Math.Abs(y) > Math.Abs(prev)) converging = false;
            closest = y;
        }

        return (closest, converging, probed);
    }

    // ────────────────────────────── 零点与截距 ──────────────────────────────

    private static void FindInterceptsAndRoots(
        CurveAnalysis r, Func<double, double> f, double[] xs, double[] ys, bool[] breakIndex)
    {
        // y 轴交点：要看 x=0 在不在分析范围里
        var y0 = Eval(f, 0);
        if (double.IsFinite(y0) && r.XMin <= 0 && r.XMax >= 0) r.YIntercept = y0;

        var truncated = false;

        for (var i = 0; i < Samples - 1; i++)
        {
            if (breakIndex[i]) continue;   // 渐近线穿过去的地方不是零点
            if (!double.IsFinite(ys[i]) || !double.IsFinite(ys[i + 1])) continue;

            if (ys[i] == 0) { AddCapped(r.Roots, xs[i], ref truncated); continue; }
            if ((ys[i] > 0) == (ys[i + 1] > 0)) continue;

            var root = Bisect(f, xs[i], xs[i + 1]);
            if (double.IsFinite(root)) AddCapped(r.Roots, root, ref truncated);
        }

        r.RootsTruncated = truncated;

        // 采样点正好压在根上会连着记两次，去掉挨得太近的
        Dedupe(r.Roots, Math.Max(1e-9, (r.XMax - r.XMin) * 1e-6));
    }

    private static double Bisect(Func<double, double> f, double a, double b)
    {
        var fa = Eval(f, a);
        if (!double.IsFinite(fa)) return double.NaN;

        for (var i = 0; i < BisectIterations; i++)
        {
            var m = (a + b) / 2;
            if (m <= a || m >= b) break;

            var fm = Eval(f, m);
            if (!double.IsFinite(fm)) break;

            if ((fa > 0) == (fm > 0)) { a = m; fa = fm; }
            else b = m;
        }

        return (a + b) / 2;
    }

    private static void Dedupe(List<double> list, double tol)
    {
        if (list.Count < 2) return;

        var sorted = list.OrderBy(v => v).ToList();
        list.Clear();
        list.Add(sorted[0]);

        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] - list[^1] > tol) list.Add(sorted[i]);
        }
    }

    // ────────────────────────────── 垂直渐近线 ──────────────────────────────

    private static void FindVerticalAsymptotes(
        CurveAnalysis r, double[] xs, double[] ys, bool[] breakIndex, double scale)
    {
        for (var i = 0; i < Samples - 1; i++)
        {
            if (breakIndex[i])
            {
                if (r.VerticalAsymptotes.Count < MaxListed)
                    r.VerticalAsymptotes.Add((xs[i] + xs[i + 1]) / 2);
                continue;
            }

            // 另一种形态：一边有定义另一边是空洞，且有限的那一侧量级远超典型值
            var a = ys[i];
            var b = ys[i + 1];
            if (double.IsFinite(a) && !double.IsFinite(b) && Math.Abs(a) > scale * 10
                && r.VerticalAsymptotes.Count < MaxListed)
            {
                r.VerticalAsymptotes.Add((xs[i] + xs[i + 1]) / 2);
            }
            else if (!double.IsFinite(a) && double.IsFinite(b) && Math.Abs(b) > scale * 10
                && r.VerticalAsymptotes.Count < MaxListed)
            {
                r.VerticalAsymptotes.Add((xs[i] + xs[i + 1]) / 2);
            }
        }

        Dedupe(r.VerticalAsymptotes, Math.Max(1e-9, (r.XMax - r.XMin) * 1e-3));
    }

    // ────────────────────────────── 单调性与极值 ──────────────────────────────

    /// <summary>中心差分求导。h 按 x 的尺度取，兼顾截断误差与浮点噪声。</summary>
    private static double Derivative(Func<double, double> f, double x)
    {
        var h = Math.Max(Math.Abs(x), 1.0) * 1e-6;
        var a = Eval(f, x - h);
        var b = Eval(f, x + h);
        if (!double.IsFinite(a) || !double.IsFinite(b)) return double.NaN;
        return (b - a) / (2 * h);
    }

    /// <summary>二阶中心差分。⚠️ h 必须比一阶大得多 —— 否则 h² 会把浮点噪声放大到无法用。</summary>
    private static double SecondDerivative(Func<double, double> f, double x)
    {
        var h = Math.Max(Math.Abs(x), 1.0) * 1e-3;
        var a = Eval(f, x - h);
        var b = Eval(f, x);
        var c = Eval(f, x + h);
        if (!double.IsFinite(a) || !double.IsFinite(b) || !double.IsFinite(c)) return double.NaN;
        return (a - 2 * b + c) / (h * h);
    }

    /// <summary>
    /// 单调性与极值。
    ///
    /// <para>
    /// 三步走：① 逐点判导数符号；② 变号处用三分法**精化出极值点的精确位置**；
    /// ③ 用「极值点 + 无定义区边界 + 垂直渐近线 + 范围端点」把定义域切段，每段按采样点符号投票定方向。
    /// </para>
    ///
    /// <para>
    /// ⛔⛔ 区间边界必须是**精化后的极值点**，不能拿采样点凑 —— 采样步长 0.005 会让区间端点
    /// 整体偏移一小格（实测 sin 的递增区间端点偏了 0.0045），列在界面上很难看。
    /// </para>
    /// </summary>
    private static void FindMonotonicity(
        CurveAnalysis r, Func<double, double> f, double[] xs, double[] ys, bool[] breakIndex)
    {
        // ① 逐点判导数符号
        var signs = new int[Samples];
        for (var i = 0; i < Samples; i++)
        {
            if (breakIndex[Math.Min(i, Samples - 2)]) continue;   // 跳变处不作数

            var d = Derivative(f, xs[i]);
            if (!double.IsFinite(d)) continue;

            // 抑制浮点噪声：中心差分的噪声量级约 eps·|f|/h，取比它大几档的容差
            var tol = 1e-9 * Math.Max(1.0, Math.Abs(ys[i]));
            signs[i] = d > tol ? 1 : d < -tol ? -1 : 0;
        }

        // ② 变号处即极值点，精化出位置
        for (var i = 1; i < Samples; i++)
        {
            if (signs[i] == 0 || signs[i - 1] == 0 || signs[i] == signs[i - 1]) continue;

            var lo = xs[Math.Max(0, i - 3)];
            var hi = xs[Math.Min(Samples - 1, i + 2)];

            var x = Ternary(f, lo, hi, max: signs[i - 1] > 0);
            if (!double.IsFinite(x)) continue;

            var y = Eval(f, x);
            if (double.IsFinite(y) && r.Extrema.Count < MaxListed)
                r.Extrema.Add(new Extremum(x, y, signs[i - 1] > 0));
        }

        DedupeExtrema(r);

        // ③ 切分点
        var cuts = new List<double>(r.Extrema.Select(e => e.X));
        foreach (var u in r.UndefinedRanges) { cuts.Add(u.From); cuts.Add(u.To); }
        cuts.AddRange(r.VerticalAsymptotes);

        var bounds = cuts
            .Where(c => double.IsFinite(c) && c > r.XMin && c < r.XMax)
            .Distinct()
            .OrderBy(c => c)
            .ToList();
        bounds.Insert(0, r.XMin);
        bounds.Add(r.XMax);

        // ④ 每段内按采样点投票定方向
        for (var k = 0; k + 1 < bounds.Count; k++)
        {
            var lo = bounds[k];
            var hi = bounds[k + 1];
            if (hi - lo < 1e-9) continue;

            var up = 0;
            var down = 0;
            for (var i = 0; i < Samples; i++)
            {
                if (xs[i] <= lo || xs[i] >= hi) continue;
                if (signs[i] > 0) up++;
                else if (signs[i] < 0) down++;
            }

            if (up == 0 && down == 0) continue;

            var list = up > down ? r.Increasing : r.Decreasing;
            if (list.Count < MaxListed) list.Add(new Interval(lo, hi));
        }
    }

    /// <summary>
    /// 极值恰好落在 0 上（重根，例如 x²）时曲线是**擦着** x 轴过的、符号不变，
    /// 靠符号变化永远找不到 —— 只能在精化出的极值点上认。
    /// </summary>
    private static void RefineZerosFromExtrema(CurveAnalysis r, double scale)
    {
        var tol = 1e-9 * Math.Max(1.0, scale);
        var truncated = r.RootsTruncated;

        foreach (var e in r.Extrema)
        {
            if (Math.Abs(e.Y) <= tol) AddCapped(r.Roots, e.X, ref truncated);
        }

        r.RootsTruncated = truncated;
        Dedupe(r.Roots, Math.Max(1e-9, (r.XMax - r.XMin) * 1e-6));
        r.Roots.Sort();
    }

    /// <summary>
    /// 取值上下界要用**精化后的极值**兜底：采样点不一定踩在极值上
    /// （实测 x² 的采样最小值是 6.25e-6 而不是 0）。
    /// </summary>
    private static void RefineRangeWithExtrema(CurveAnalysis r, double[] ys)
    {
        var (min, max) = FiniteRange(ys);

        foreach (var e in r.Extrema)
        {
            if (min is null || e.Y < min) min = e.Y;
            if (max is null || e.Y > max) max = e.Y;
        }

        r.MinValue = min;
        r.MaxValue = max;
    }

    /// <summary>三分搜索单峰极值。</summary>
    private static double Ternary(Func<double, double> f, double a, double b, bool max)
    {
        for (var i = 0; i < 200; i++)
        {
            var m1 = a + (b - a) / 3;
            var m2 = b - (b - a) / 3;
            if (m2 <= m1) break;

            var f1 = Eval(f, m1);
            var f2 = Eval(f, m2);
            if (!double.IsFinite(f1) || !double.IsFinite(f2)) break;

            if (max ? f1 < f2 : f1 > f2) a = m1;
            else b = m2;

            if (b - a <= 1e-12 * Math.Max(1, Math.Abs(a))) break;
        }

        return (a + b) / 2;
    }

    private static void DedupeExtrema(CurveAnalysis r)
    {
        if (r.Extrema.Count < 2) return;

        var sorted = r.Extrema.OrderBy(e => e.X).ToList();
        r.Extrema.Clear();

        foreach (var e in sorted)
        {
            if (r.Extrema.Count > 0 && Math.Abs(e.X - r.Extrema[^1].X) < 1e-6 * Math.Max(1, Math.Abs(e.X)))
                continue;
            r.Extrema.Add(e);
        }
    }

    // ────────────────────────────── 奇偶性 ──────────────────────────────

    private static void FindParity(CurveAnalysis r, Func<double, double> f, double xMin, double xMax)
    {
        // 只有关于原点对称的范围才谈得上奇偶
        var reach = Math.Min(Math.Abs(xMin), Math.Abs(xMax));
        if (!(reach > 0)) return;

        var evenOk = true;
        var oddOk = true;
        var tested = 0;

        for (var k = 1; k <= 50; k++)
        {
            var x = reach * k / 50.0;
            var a = Eval(f, x);
            var b = Eval(f, -x);

            // 两边只要有一边没有定义，这个点就不能用来判奇偶 —— 跳过而不是判失败
            if (!double.IsFinite(a) || !double.IsFinite(b)) continue;

            tested++;
            var tol = 1e-8 * Math.Max(1.0, Math.Abs(a));

            if (Math.Abs(a - b) > tol) evenOk = false;
            if (Math.Abs(a + b) > tol) oddOk = false;

            if (!evenOk && !oddOk) { r.Parity = ParityKind.Neither; return; }
        }

        if (tested < 20) { r.Parity = ParityKind.Unknown; return; }

        r.Parity = (evenOk, oddOk) switch
        {
            (true, true) => ParityKind.Both,
            (true, false) => ParityKind.Even,
            (false, true) => ParityKind.Odd,
            _ => ParityKind.Neither,
        };
    }

    // ────────────────────────────── 水平渐近线 ──────────────────────────────

    private static void FindHorizontals(CurveAnalysis r, Func<double, double> f)
    {
        r.HorizontalRight = LimitAtInfinity(f, 1);
        r.HorizontalLeft = LimitAtInfinity(f, -1);
    }

    /// <summary>
    /// 在几个越来越大的 x 上取样，最后三个几乎相等才认为收敛。
    /// ⛔ 只比最后两个点会被"碰巧撞上"骗到（比如 sin 取两个恰好接近的值），所以要看三点。
    /// </summary>
    private static double? LimitAtInfinity(Func<double, double> f, int sign)
    {
        var probes = new double[5];
        for (var i = 0; i < probes.Length; i++)
        {
            var magnitude = Math.Pow(10, 2 + i);
            var y = Eval(f, sign * magnitude);
            if (!double.IsFinite(y)) return null;
            probes[i] = y;
        }

        var scale = Math.Max(1.0, Math.Abs(probes[^1]));
        var tol = 1e-3 * scale;

        for (var i = 2; i < probes.Length; i++)
        {
            if (Math.Abs(probes[i] - probes[i - 1]) > tol) return null;
        }

        return probes[^1];
    }

    // ────────────────────────────── 凹凸与拐点 ──────────────────────────────

    private static void FindInflections(CurveAnalysis r, Func<double, double> f, double[] xs, bool[] breakIndex)
    {
        // 二阶差分对噪声敏感得多，所以隔几个采样点取一个，步子大一点更稳
        var stride = 4;
        var sign = 0;
        var prevX = double.NaN;

        for (var i = stride; i < Samples - stride; i += stride)
        {
            if (breakIndex[Math.Min(i, Samples - 2)]) { sign = 0; continue; }

            var d2 = SecondDerivative(f, xs[i]);
            if (!double.IsFinite(d2)) { sign = 0; continue; }

            var s = d2 > 1e-6 ? 1 : d2 < -1e-6 ? -1 : 0;
            if (s == 0) continue;

            if (sign != 0 && s != sign && double.IsFinite(prevX))
            {
                var x = BisectSecond(f, prevX, xs[i]);
                if (double.IsFinite(x) && r.Inflections.Count < MaxListed) r.Inflections.Add(x);
            }

            sign = s;
            prevX = xs[i];
        }

        Dedupe(r.Inflections, Math.Max(1e-9, (r.XMax - r.XMin) * 1e-3));
    }

    private static double BisectSecond(Func<double, double> f, double a, double b)
    {
        var fa = SecondDerivative(f, a);
        if (!double.IsFinite(fa)) return double.NaN;

        for (var i = 0; i < 60; i++)
        {
            var m = (a + b) / 2;
            if (m <= a || m >= b) break;

            var fm = SecondDerivative(f, m);
            if (!double.IsFinite(fm)) break;
            if (fm == 0) return m;

            if ((fa > 0) == (fm > 0)) { a = m; fa = fm; }
            else b = m;
        }

        return (a + b) / 2;
    }

    // ────────────────────────────── 周期性 ──────────────────────────────

    /// <summary>
    /// 周期检测：先生成一批候选周期，再逐个在多个点上验证 f(x+T) ≈ f(x)。
    ///
    /// <para>
    /// 候选来自两处：① 常见常数（2π、π、π/2…2、1、0.5）；② **从零点间距与极值点间距反推**
    /// （sin 的零点间距是 π、而周期是 2π，所以间距和间距的两倍都要试）。
    /// 验证要求足够多的点全部吻合，只要有两点不符就否掉 —— 宁可漏报，不可误报。
    /// </para>
    /// </summary>
    private static double? DetectPeriod(Func<double, double> f, CurveAnalysis r, double xMin, double xMax)
    {
        var span = xMax - xMin;
        if (!(span > 0)) return null;

        var candidates = new List<double>
        {
            Math.Tau, Math.PI, Math.PI / 2, Math.PI / 3, Math.PI / 4,
            Math.Tau / 3, Math.Tau * 2, Math.PI * 3,
            1, 2, 3, 4, 5, 6, 8, 10, 0.5, 0.25, 12, 24,
        };

        // 从零点间距推（间距本身，以及它的两倍）
        for (var i = 0; i + 1 < r.Roots.Count; i++)
        {
            var d = r.Roots[i + 1] - r.Roots[i];
            if (d > 0) { candidates.Add(d); candidates.Add(2 * d); }
        }

        // 从极值点间距推
        for (var i = 0; i + 1 < r.Extrema.Count; i++)
        {
            var d = Math.Abs(r.Extrema[i + 1].X - r.Extrema[i].X);
            if (d > 0) { candidates.Add(d); candidates.Add(2 * d); }
        }

        double? best = null;

        foreach (var t in candidates.Distinct().Where(t => double.IsFinite(t) && t > 0))
        {
            if (t < span * 1e-3 || t > span * 20) continue;   // 太小的没意义，太大的验证不了
            if (best is not null && t >= best.Value) continue;

            if (VerifyPeriod(f, t, xMin, xMax)) best = t;
        }

        return best;
    }

    private static bool VerifyPeriod(Func<double, double> f, double period, double xMin, double xMax)
    {
        // 验证窗口向外扩一些，别只在视口里自证
        var lo = xMin - (xMax - xMin) * 0.2;
        var hi = xMax + (xMax - xMin) * 0.2;
        if (hi - lo <= period) return false;

        const int probes = 60;
        var checkedCount = 0;
        var mismatches = 0;

        for (var i = 0; i < probes; i++)
        {
            var x = lo + (hi - lo - period) * i / (probes - 1);

            var a = Eval(f, x);
            var b = Eval(f, x + period);

            if (!double.IsFinite(a) && !double.IsFinite(b))
            {
                // 两边都没定义 —— 一致
                checkedCount++;
                continue;
            }

            if (!double.IsFinite(a) || !double.IsFinite(b)) return false;

            checkedCount++;
            var tol = 1e-7 * Math.Max(1.0, Math.Abs(a));
            if (Math.Abs(a - b) > tol && ++mismatches > 1) return false;
        }

        return checkedCount >= 20;
    }

    /// <summary>取一个周期内的取值上下界（用来给出真正的值域）。</summary>
    private static (double? Min, double? Max) RangeOverPeriod(Func<double, double> f, double[] xs, double period)
    {
        var yMin = double.NaN;
        var yMax = double.NaN;

        const int probes = 4000;
        for (var i = 0; i < probes; i++)
        {
            var x = period * i / (double)probes;
            var y = Eval(f, x);
            if (!double.IsFinite(y)) continue;

            if (double.IsNaN(yMin) || y < yMin) yMin = y;
            if (double.IsNaN(yMax) || y > yMax) yMax = y;
        }

        if (double.IsNaN(yMin) || double.IsNaN(yMax)) return (null, null);
        return (yMin, yMax);
    }
}
