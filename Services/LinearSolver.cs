using System;
using System.Collections.Generic;

namespace ClassSoftwareHub.Desktop.Services;









public static class LinearSolver
{
    
    
    
    
    
    public static List<int> Reduce(Rational[][] m)
    {
        var pivots = new List<int>();
        if (m.Length == 0) return pivots;

        var rows = m.Length;
        var cols = m[0].Length;
        var row = 0;

        for (var col = 0; col < cols && row < rows; col++)
        {
            
            var pivot = -1;
            for (var r = row; r < rows; r++)
            {
                if (m[r][col].IsZero) continue;
                pivot = r;
                break;
            }

            if (pivot < 0) continue;   

            (m[row], m[pivot]) = (m[pivot], m[row]);

            
            var head = m[row][col];
            if (head != Rational.One)
            {
                for (var c = col; c < cols; c++)
                    m[row][c] /= head;
            }

            
            for (var r = 0; r < rows; r++)
            {
                if (r == row || m[r][col].IsZero) continue;
                var factor = m[r][col];
                for (var c = col; c < cols; c++)
                    m[r][c] -= factor * m[row][c];
            }

            pivots.Add(col);
            row++;
        }

        return pivots;
    }

    
    
    
    
    
    public static List<Rational[]> NullSpace(Rational[][] a)
    {
        var basis = new List<Rational[]>();
        if (a.Length == 0) return basis;

        var cols = a[0].Length;
        var pivots = Reduce(a);
        var pivotSet = new HashSet<int>(pivots);

        for (var free = 0; free < cols; free++)
        {
            if (pivotSet.Contains(free)) continue;

            var v = new Rational[cols];
            for (var i = 0; i < cols; i++) v[i] = Rational.Zero;
            v[free] = Rational.One;

            
            for (var r = 0; r < pivots.Count; r++)
                v[pivots[r]] = -a[r][free];

            basis.Add(v);
        }

        return basis;
    }

    
    
    
    public static SolveResult Solve(Rational[][] a, Rational[] b)
    {
        var rows = a.Length;
        var cols = rows == 0 ? 0 : a[0].Length;

        
        var aug = new Rational[rows][];
        for (var r = 0; r < rows; r++)
        {
            aug[r] = new Rational[cols + 1];
            for (var c = 0; c < cols; c++) aug[r][c] = a[r][c];
            aug[r][cols] = b[r];
        }

        var pivots = Reduce(aug);

        
        foreach (var p in pivots)
        {
            if (p != cols) continue;
            return new SolveResult { Consistent = false };
        }

        var pivotCols = pivots.FindAll(p => p < cols);
        var pivotSet = new HashSet<int>(pivotCols);

        var particular = new Rational[cols];
        for (var i = 0; i < cols; i++) particular[i] = Rational.Zero;
        for (var r = 0; r < pivotCols.Count; r++)
            particular[pivotCols[r]] = aug[r][cols];

        
        var homo = new Rational[rows][];
        for (var r = 0; r < rows; r++)
        {
            homo[r] = new Rational[cols];
            for (var c = 0; c < cols; c++) homo[r][c] = aug[r][c];
        }

        var freeCount = cols - pivotCols.Count;
        var basis = new List<Rational[]>();
        for (var free = 0; free < cols; free++)
        {
            if (pivotSet.Contains(free)) continue;

            var v = new Rational[cols];
            for (var i = 0; i < cols; i++) v[i] = Rational.Zero;
            v[free] = Rational.One;
            for (var r = 0; r < pivotCols.Count; r++)
                v[pivotCols[r]] = -homo[r][free];
            basis.Add(v);
        }

        return new SolveResult
        {
            Consistent = true,
            Unique = freeCount == 0,
            Particular = particular,
            NullBasis = basis,
            PivotColumns = pivotCols,
        };
    }
}


public sealed class SolveResult
{
    public bool Consistent { get; init; }
    public bool Unique { get; init; }
    
    public Rational[] Particular { get; init; } = Array.Empty<Rational>();
    
    public List<Rational[]> NullBasis { get; init; } = new();
    
    public List<int> PivotColumns { get; init; } = new();
}
