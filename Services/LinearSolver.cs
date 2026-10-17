using System;
using System.Collections.Generic;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 有理数矩阵的初等行变换 —— 配平（求零空间）与多元一次方程（求解 / 判无解）共用。
///
/// <para>
/// 全部用精确分数，不用浮点：高斯消元里"主元是不是 0"必须**精确**判断。
/// 用 double 时一个 1e-17 的残差会被当成主元，最后除出来是天文数字或 NaN。
/// </para>
/// </summary>
public static class LinearSolver
{
    /// <summary>
    /// 化行最简形（RREF）。
    /// </summary>
    /// <param name="m">原地修改，调用方要给副本。</param>
    /// <returns>主元所在列号（升序）。</returns>
    public static List<int> Reduce(Rational[][] m)
    {
        var pivots = new List<int>();
        if (m.Length == 0) return pivots;

        var rows = m.Length;
        var cols = m[0].Length;
        var row = 0;

        for (var col = 0; col < cols && row < rows; col++)
        {
            // 找这一列第一个非零元素（精确判零，不需要"容差"这一说）
            var pivot = -1;
            for (var r = row; r < rows; r++)
            {
                if (m[r][col].IsZero) continue;
                pivot = r;
                break;
            }

            if (pivot < 0) continue;   // 整列全零 ⇒ 这是自由变量

            (m[row], m[pivot]) = (m[pivot], m[row]);

            // 主元归一
            var head = m[row][col];
            if (head != Rational.One)
            {
                for (var c = col; c < cols; c++)
                    m[row][c] /= head;
            }

            // 其余行消成 0（含上方的行，直接得到最简形）
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

    /// <summary>
    /// 求 A·x = 0 的零空间（一组线性无关的解向量）。
    /// 配平的本质就是这个：每个元素一行、每种物质一列（反应物取正、生成物取负），
    /// 解出来的零空间向量就是配平系数。
    /// </summary>
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

            // 每个主元行的值由自由变量反推：x[pivotCol] = −系数 × x[free]
            for (var r = 0; r < pivots.Count; r++)
                v[pivots[r]] = -a[r][free];

            basis.Add(v);
        }

        return basis;
    }

    /// <summary>
    /// 解 A·x = b。返回是否相容、是否唯一，以及特解与零空间基。
    /// </summary>
    public static SolveResult Solve(Rational[][] a, Rational[] b)
    {
        var rows = a.Length;
        var cols = rows == 0 ? 0 : a[0].Length;

        // 增广矩阵：最后一列是 b
        var aug = new Rational[rows][];
        for (var r = 0; r < rows; r++)
        {
            aug[r] = new Rational[cols + 1];
            for (var c = 0; c < cols; c++) aug[r][c] = a[r][c];
            aug[r][cols] = b[r];
        }

        var pivots = Reduce(aug);

        // 出现 [0 0 … 0 | 非零] ⇒ 无解
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

        // 零空间：把增广列去掉再求一次（等价于解 A·x = 0）
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

/// <summary>一次线性方程组的求解结果。</summary>
public sealed class SolveResult
{
    public bool Consistent { get; init; }
    public bool Unique { get; init; }
    /// <summary>特解（每个未知数一个值）。</summary>
    public Rational[] Particular { get; init; } = Array.Empty<Rational>();
    /// <summary>齐次解空间的基；唯一解时为空。</summary>
    public List<Rational[]> NullBasis { get; init; } = new();
    /// <summary>主元所在的列（= 被方程"定住"的未知数），其余列是自由变量。</summary>
    public List<int> PivotColumns { get; init; } = new();
}
