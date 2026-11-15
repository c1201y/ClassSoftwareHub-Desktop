using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Services;


public readonly record struct Interval(double From, double To);


public readonly record struct Extremum(double X, double Y, bool IsMaximum);


public enum ParityKind
{
    
    Unknown,

    
    Neither,

    
    Even,

    
    Odd,

    
    Both,
}










public sealed class CurveAnalysis
{
    public bool Ok { get; set; }

    
    public double XMin { get; set; }
    public double XMax { get; set; }

    
    
    public bool DefinedThroughout { get; set; }

    
    public List<Interval> UndefinedRanges { get; } = new();

    
    public List<double> Roots { get; } = new();

    
    public bool RootsTruncated { get; set; }

    public double? YIntercept { get; set; }

    
    public List<Interval> Increasing { get; } = new();
    public List<Interval> Decreasing { get; } = new();

    
    public List<Extremum> Extrema { get; } = new();

    
    public ParityKind Parity { get; set; } = ParityKind.Unknown;

    
    public double? Period { get; set; }

    
    public bool IsConstant { get; set; }

    
    
    public List<double> VerticalAsymptotes { get; } = new();

    
    public double? HorizontalRight { get; set; }

    
    public double? HorizontalLeft { get; set; }

    
    
    public List<double> Inflections { get; } = new();

    
    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }

    
    
    
    
    public bool RangeIsGlobal { get; set; }
}











public static class FunctionAnalyzer
{
    
    private const int Samples = 4000;

    
    private const int MaxListed = 12;

    
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

        
        
        
        
        
        
        var scale = RobustAbsScale(ys);
        var breakIndex = new bool[Samples];
        for (var i = 0; i < Samples - 1; i++)
        {
            if (!double.IsFinite(ys[i]) || !double.IsFinite(ys[i + 1])) continue;
            if ((ys[i] > 0) == (ys[i + 1] > 0)) continue;

            
            if (Math.Abs(ys[i]) > scale * 5 && Math.Abs(ys[i + 1]) > scale * 5)
                breakIndex[i] = true;
        }

        
        
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

        
        if (result.MinValue is { } minValue && result.MaxValue is { } maxValue)
            result.IsConstant = Math.Abs(maxValue - minValue) <= 1e-9 * Math.Max(1, Math.Abs(maxValue));

        
        if (!result.IsConstant)
        {
            result.Period = DetectPeriod(f, result, xMin, xMax);

            
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

            
            
            
            var from = start > 0 ? (xs[start - 1] + xs[start]) / 2 : r.XMin;
            var to = i < Samples ? (xs[i - 1] + xs[i]) / 2 : r.XMax;

            if (r.UndefinedRanges.Count < MaxListed) r.UndefinedRanges.Add(new Interval(from, to));
        }

        r.DefinedThroughout = !any;
    }

    
    
    
    
    
    private static void HandleEdges(CurveAnalysis r, Func<double, double> f, double scale)
    {
        var truncated = r.RootsTruncated;

        foreach (var range in r.UndefinedRanges)
        {
            TryEdge(r, f, range.To, +1, scale, ref truncated);    
            TryEdge(r, f, range.From, -1, scale, ref truncated);  
        }

        r.RootsTruncated = truncated;
        Dedupe(r.VerticalAsymptotes, Math.Max(1e-9, (r.XMax - r.XMin) * 1e-3));
    }

    private static void TryEdge(
        CurveAnalysis r, Func<double, double> f, double edge, int inward, double scale, ref bool truncated)
    {
        
        if (!double.IsFinite(edge) || edge <= r.XMin + 1e-12 || edge >= r.XMax - 1e-12) return;

        var (limit, converging, probed) = ProbeEdge(f, edge, inward);

        
        if (probed < 4) return;

        if (!converging)
        {
            
            
            
            
            if (r.VerticalAsymptotes.Count < MaxListed) r.VerticalAsymptotes.Add(edge);
            return;
        }

        if (limit is not { } value) return;

        
        if (r.MinValue is null || value < r.MinValue) r.MinValue = value;
        if (r.MaxValue is null || value > r.MaxValue) r.MaxValue = value;

        
        if (Math.Abs(value) <= 1e-3 * Math.Max(1.0, scale)) AddCapped(r.Roots, edge, ref truncated);
    }

    
    
    
    
    
    
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

    

    private static void FindInterceptsAndRoots(
        CurveAnalysis r, Func<double, double> f, double[] xs, double[] ys, bool[] breakIndex)
    {
        
        var y0 = Eval(f, 0);
        if (double.IsFinite(y0) && r.XMin <= 0 && r.XMax >= 0) r.YIntercept = y0;

        var truncated = false;

        for (var i = 0; i < Samples - 1; i++)
        {
            if (breakIndex[i]) continue;   
            if (!double.IsFinite(ys[i]) || !double.IsFinite(ys[i + 1])) continue;

            if (ys[i] == 0) { AddCapped(r.Roots, xs[i], ref truncated); continue; }
            if ((ys[i] > 0) == (ys[i + 1] > 0)) continue;

            var root = Bisect(f, xs[i], xs[i + 1]);
            if (double.IsFinite(root)) AddCapped(r.Roots, root, ref truncated);
        }

        r.RootsTruncated = truncated;

        
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

    

    
    private static double Derivative(Func<double, double> f, double x)
    {
        var h = Math.Max(Math.Abs(x), 1.0) * 1e-6;
        var a = Eval(f, x - h);
        var b = Eval(f, x + h);
        if (!double.IsFinite(a) || !double.IsFinite(b)) return double.NaN;
        return (b - a) / (2 * h);
    }

    
    private static double SecondDerivative(Func<double, double> f, double x)
    {
        var h = Math.Max(Math.Abs(x), 1.0) * 1e-3;
        var a = Eval(f, x - h);
        var b = Eval(f, x);
        var c = Eval(f, x + h);
        if (!double.IsFinite(a) || !double.IsFinite(b) || !double.IsFinite(c)) return double.NaN;
        return (a - 2 * b + c) / (h * h);
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    private static void FindMonotonicity(
        CurveAnalysis r, Func<double, double> f, double[] xs, double[] ys, bool[] breakIndex)
    {
        
        var signs = new int[Samples];
        for (var i = 0; i < Samples; i++)
        {
            if (breakIndex[Math.Min(i, Samples - 2)]) continue;   

            var d = Derivative(f, xs[i]);
            if (!double.IsFinite(d)) continue;

            
            var tol = 1e-9 * Math.Max(1.0, Math.Abs(ys[i]));
            signs[i] = d > tol ? 1 : d < -tol ? -1 : 0;
        }

        
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

    

    private static void FindParity(CurveAnalysis r, Func<double, double> f, double xMin, double xMax)
    {
        
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

    

    private static void FindHorizontals(CurveAnalysis r, Func<double, double> f)
    {
        r.HorizontalRight = LimitAtInfinity(f, 1);
        r.HorizontalLeft = LimitAtInfinity(f, -1);
    }

    
    
    
    
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

    

    private static void FindInflections(CurveAnalysis r, Func<double, double> f, double[] xs, bool[] breakIndex)
    {
        
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

        
        for (var i = 0; i + 1 < r.Roots.Count; i++)
        {
            var d = r.Roots[i + 1] - r.Roots[i];
            if (d > 0) { candidates.Add(d); candidates.Add(2 * d); }
        }

        
        for (var i = 0; i + 1 < r.Extrema.Count; i++)
        {
            var d = Math.Abs(r.Extrema[i + 1].X - r.Extrema[i].X);
            if (d > 0) { candidates.Add(d); candidates.Add(2 * d); }
        }

        double? best = null;

        foreach (var t in candidates.Distinct().Where(t => double.IsFinite(t) && t > 0))
        {
            if (t < span * 1e-3 || t > span * 20) continue;   
            if (best is not null && t >= best.Value) continue;

            if (VerifyPeriod(f, t, xMin, xMax)) best = t;
        }

        return best;
    }

    private static bool VerifyPeriod(Func<double, double> f, double period, double xMin, double xMax)
    {
        
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
