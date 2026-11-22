using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Services;






public static class MathSolver
{
    public static bool TryCompile(string expression, out Func<double, double>? fn, out string? error)
        => ExpressionParser.TryCompile(expression, AngleMode.Radians, out fn, out error);

    
    public static double Derivative(Func<double, double> fn, double x)
    {
        var h = Math.Max(1e-5, Math.Abs(x) * 1e-6);
        return (fn(x + h) - fn(x - h)) / (2 * h);
    }

    
    
    
    
    public static double Integral(Func<double, double> fn, double a, double b, int steps = 2000)
    {
        if (b < a) return -Integral(fn, b, a, steps);
        steps = Math.Clamp(steps, 10, 20000);

        var h = (b - a) / steps;
        var sum = fn(a) + fn(b);
        if (!double.IsFinite(sum)) return double.NaN;

        for (var i = 1; i < steps; i++)
        {
            var y = fn(a + i * h);
            if (!double.IsFinite(y)) return double.NaN;
            sum += i % 2 == 0 ? 2 * y : 4 * y;
        }

        var result = sum * h / 3.0;
        return double.IsFinite(result) ? result : double.NaN;
    }

    
    
    
    
    public static List<double> Solve(Func<double, double> fn, double lo, double hi, int samples = 400)
    {
        var roots = new List<double>();
        if (hi <= lo) return roots;

        var dx = (hi - lo) / samples;
        var prevX = lo;
        var prevY = fn(prevX);

        for (var i = 1; i <= samples; i++)
        {
            var x = lo + i * dx;
            var y = fn(x);
            if (double.IsFinite(prevY) && double.IsFinite(y) && prevY * y < 0)
            {
                var root = Bisect(fn, prevX, x);
                if (double.IsFinite(root) && !HasNear(roots, root)) roots.Add(root);
            }
            prevX = x;
            prevY = y;
        }

        roots.Sort();
        return roots;
    }

    private static double Bisect(Func<double, double> fn, double a, double b)
    {
        var fa = fn(a);
        for (var i = 0; i < 60; i++)
        {
            var mid = (a + b) / 2;
            var fm = fn(mid);
            if (Math.Abs(fm) < 1e-9 || b - a < 1e-10) return mid;

            if (fa * fm < 0)
            {
                b = mid;
            }
            else
            {
                a = mid;
                fa = fm;
            }
        }
        return (a + b) / 2;
    }

    private static bool HasNear(List<double> roots, double root)
        => roots.Any(r => Math.Abs(r - root) < 1e-4);

    
    public static string Format(double value)
        => double.IsFinite(value) ? value.ToString("0.######", CultureInfo.InvariantCulture) : "—";
}
