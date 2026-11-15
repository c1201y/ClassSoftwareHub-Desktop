using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 化学方程式配平。
///
/// <para>
/// 本质是**求线性方程组的零空间**（不是"试数字"）：
/// 每种元素一行，每种物质一列，反应物记正、生成物记负 —— 每一行要求"元素个数守恒"。
/// 解出的零空间向量就是系数；再把它化成最小正整数。
/// </para>
/// <para>
/// ⛔ 必须精确算：`Fe + O2 = Fe2O3` 用 double 消元会得到 4.000000000000001 : 3 : 2
/// 这种脏东西，配平结果是要写在黑板上的，不能四舍五入糊过去。
/// </para>
/// </summary>
public static class ChemistryBalancer
{
    /// <summary>配平结果。失败时 <see cref="Error"/> 非空。</summary>
    public sealed class Result
    {
        public bool Ok => Error is null;
        public string? Error { get; init; }
        /// <summary>配平后的完整方程式（系数已写好，系数 1 省略）。</summary>
        public string Equation { get; init; } = "";
        /// <summary>每一种物质的系数，顺序与输入一致（反应物在前）。</summary>
        public List<(string Formula, int Coeff)> Terms { get; init; } = new();

        /// <summary>每种元素的守恒核对：左边总数与右边总数（应当相等）。</summary>
        public List<(string Element, int Left, int Right)> ElementCheck { get; init; } = new();
        /// <summary>需要提醒用户的情况（例如有多组解、忽略了电荷）。</summary>
        public string? Note { get; init; }
    }

    private static readonly char[] ArrowChars = { '=', '→', '⟶', '⇌' };

    public static Result Balance(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Fail("请输入一个方程式，例如 Fe + O2 = Fe2O3");

        var text = Normalize(input);

        // ── 拆左右两侧 ──
        var arrow = text.IndexOfAny(ArrowChars);
        if (arrow < 0)
            return Fail("缺少等号或箭头。请写成「反应物 = 生成物」，例如 H2 + O2 = H2O");

        // 「->」这种写法先归一成 =
        var left = text[..arrow].Trim();
        var right = text[(arrow + 1)..].Trim();
        if (right.StartsWith(">")) right = right[1..].Trim();

        if (left.Length == 0 || right.Length == 0)
            return Fail("等号两侧都要有物质。");

        var leftTerms = SplitTerms(left, out var splitError);
        if (splitError is not null) return Fail(splitError);

        var rightTerms = SplitTerms(right, out splitError);
        if (splitError is not null) return Fail(splitError);

        if (leftTerms.Count == 0 || rightTerms.Count == 0)
            return Fail("等号两侧都要有物质。");

        // ── 解析每种物质的元素构成 ──
        var species = new List<(string Raw, Dictionary<string, int> Atoms)>();
        var note = "";
        foreach (var raw in leftTerms.Concat(rightTerms))
        {
            var atoms = ParseFormula(raw, out var error, out var chargeIgnored);
            if (atoms is null) return Fail(error ?? $"无法识别「{raw}」");
            if (chargeIgnored) note = "输入中的离子电荷未参与配平（只按元素守恒计算）。";
            species.Add((DisplayFormula(raw), atoms!));
        }

        // ── 建矩阵：元素 × 物质 ──
        var elements = species.SelectMany(s => s.Atoms.Keys).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
        var cols = species.Count;
        var matrix = new Rational[elements.Count][];

        for (var r = 0; r < elements.Count; r++)
        {
            matrix[r] = new Rational[cols];
            for (var c = 0; c < cols; c++)
            {
                species[c].Atoms.TryGetValue(elements[r], out var count);
                // 反应物取正、生成物取负 —— 这就是"守恒"：左边总数 − 右边总数 = 0
                var sign = c < leftTerms.Count ? 1 : -1;
                matrix[r][c] = new Rational(sign * count, BigInteger.One);
            }
        }

        // ── 求零空间 ──
        var basis = LinearSolver.NullSpace(matrix);
        if (basis.Count == 0)
            return Fail("这个方程式无解：等号两侧的元素种类或数量对不上，无法通过调整系数配平。");

        var vector = PickPositiveVector(basis);
        if (vector is null)
            return Fail("找不到全为正的系数：请检查等号两侧的元素是否写全（例如漏了生成物里的某种元素）。");

        var (ints, factor) = ToSmallestIntegers(vector);
        if (ints is null)
            return Fail("系数无法化为整数，请检查输入。");

        // ── 结果 ──
        var terms = new List<(string, int)>();
        for (var i = 0; i < cols; i++)
            terms.Add((species[i].Raw, ints[i]));

        if (ints.Any(v => v > 10000))
            return Fail("算出的系数过大，通常是元素写错了（例如把 O2 写成了 O）。");

        var sb = new StringBuilder();
        for (var i = 0; i < cols; i++)
        {
            if (i == leftTerms.Count) sb.Append(" = ");
            else if (i > 0) sb.Append(" + ");

            if (ints[i] != 1) sb.Append(ints[i]);
            sb.Append(species[i].Raw);
        }

        if (basis.Count > 1)
            note = "该方程式有多组配平系数（存在歧义），以上为最小正整数解。";

        // 元素守恒核对：把系数代回去，左右各数一遍（界面上直接列出来，让人一眼确认）
        var check = new List<(string, int, int)>();
        foreach (var element in elements)
        {
            var leftSum = 0;
            var rightSum = 0;
            for (var c = 0; c < cols; c++)
            {
                species[c].Atoms.TryGetValue(element, out var count);
                if (c < leftTerms.Count) leftSum += count * ints[c];
                else rightSum += count * ints[c];
            }

            check.Add((element, leftSum, rightSum));
        }

        return new Result
        {
            Equation = sb.ToString(),
            Terms = terms,
            ElementCheck = check,
            Note = note.Length > 0 ? note : null,
        };
    }

    private static Result Fail(string message) => new() { Error = message };

    /// <summary>
    /// 校验单个化学式（编辑器实时用）。
    /// ⛔ 直接调 <see cref="ParseFormula"/> —— 与真正配平走**同一套解析**，
    /// 绝不出现"编辑器说可以、配平又报错"的两套判据。
    /// </summary>
    public static bool ValidateFormula(string? raw, out string? error)
    {
        error = null;

        var text = (raw ?? "").Trim();
        if (text.Length == 0)
        {
            error = "还没有输入内容。";
            return false;
        }

        var atoms = ParseFormula(text, out error, out _);
        if (atoms is null) return false;

        if (atoms.Count == 0)
        {
            error = "没有识别出任何元素。";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 展示用的化学式：方括号如果只是把整个式子包了一层（如 [O2]），就去掉 ——
    /// 用户拿它当"整段括起来"用，答案里再带回去显得莫名其妙；真的分组（[Fe(CN)6]）保留。
    /// </summary>
    private static string DisplayFormula(string raw)
    {
        var s = raw.Trim();
        if (s.Length < 3 || s[0] != '[' || s[^1] != ']') return s;

        var inner = s[1..^1];
        if (inner.Contains('[') || inner.Contains('(')) return s;
        return inner;
    }

    /// <summary>
    /// 全角符号、Unicode 下标、各种箭头先归一成 ASCII，
    /// 否则用户从 Word / 网页粘过来的式子会直接解析失败。
    /// </summary>
    private static string Normalize(string input)
    {
        var sb = new StringBuilder(input.Length);

        foreach (var ch in input)
        {
            // ⛔ 全角字母数字必须先转半角：从 Word / 网页粘过来的「Ｈ２Ｏ」如果原样进解析器，
            //    会被当成"无法识别的字符"，而用户完全看不出哪里错了。
            if (ch > '\uFF00' && ch < '\uFF5F') { sb.Append((char)(ch - 0xFEE0)); continue; }
            if (ch == '\u3000') { sb.Append(' '); continue; }

            sb.Append(ch switch
            {
                '（' => '(',
                '）' => ')',
                '＋' => '+',
                '＝' => '=',
                '－' or '—' or '–' => '-',
                '►' or '➔' or '⟹' => '=',
                '₀' => '0', '₁' => '1', '₂' => '2', '₃' => '3', '₄' => '4',
                '₅' => '5', '₆' => '6', '₇' => '7', '₈' => '8', '₉' => '9',
                _ => ch,
            });
        }

        return sb.ToString().Replace("->", "=").Replace("=>", "=");
    }

    private static List<string> SplitTerms(string side, out string? error)
    {
        error = null;
        var list = new List<string>();
        foreach (var part in side.Split('+'))
        {
            var p = part.Trim();
            if (p.Length == 0)
            {
                // 空项说明用户多写了一个 + 或漏了物质，必须报出来 ——
                // 静默跳过会把「H2 + = H2O」当成「H2 = H2O」去算，错误被藏起来
                error = "存在空的物质项，请检查是否多写了「+」或漏了物质。";
                return list;
            }

            // 行首数字是用户抄来的系数：配平由程序算，这里只把它剥掉
            var i = 0;
            while (i < p.Length && char.IsDigit(p[i])) i++;

            var formula = p[i..].Trim();
            if (formula.Length == 0)
            {
                error = "存在空的物质项，请检查是否多写了「+」或漏了物质。";
                return list;
            }

            list.Add(formula);
        }

        return list;
    }

    /// <summary>
    /// 解析一个化学式，返回元素 → 原子个数。
    /// 支持括号嵌套 Ca(OH)2、结晶水 CuSO4·5H2O、方括号 [Fe(CN)6]。
    /// </summary>
    private static Dictionary<string, int>? ParseFormula(string raw, out string? error, out bool chargeIgnored)
    {
        error = null;
        chargeIgnored = false;

        var formula = raw.Trim();
        if (formula.Length == 0) { error = "存在空的物质项。"; return null; }

        // 尾部的电荷（如 SO4^2-、Fe3+ / Fe²⁺）不参与配平，剥掉即可
        var tail = formula[^1];
        if (tail is '+' or '-')
        {
            formula = formula[..^1];
            chargeIgnored = true;
            while (formula.Length > 0 && char.IsDigit(formula[^1])) formula = formula[..^1];
            if (formula.EndsWith('^')) formula = formula[..^1];
        }

        if (formula.Length == 0) { error = $"「{raw}」只有一个电荷符号，缺少化学式。"; return null; }

        var total = new Dictionary<string, int>(StringComparer.Ordinal);

        // 以 · 或 * 分隔的结晶水：CuSO4·5H2O
        foreach (var segment in formula.Split('·', '*'))
        {
            var seg = segment.Trim();
            if (seg.Length == 0) continue;

            var i = 0;
            var multiplier = 1;
            var start = i;
            while (i < seg.Length && char.IsDigit(seg[i])) i++;
            if (i > start && int.TryParse(seg[start..i], out var m)) multiplier = m;

            var counts = ParseGroup(seg, ref i, out error);
            if (counts is null) return null;

            if (i < seg.Length)
            {
                error = $"「{raw}」里有多余字符「{seg[i..]}」，请检查写法。";
                return null;
            }

            foreach (var (el, n) in counts)
                total[el] = total.TryGetValue(el, out var old) ? old + n * multiplier : n * multiplier;
        }

        if (total.Count == 0) { error = $"无法从「{raw}」中识别出元素。"; return null; }
        return total;
    }

    /// <summary>括号内递归解析。</summary>
    private static Dictionary<string, int>? ParseGroup(string s, ref int i, out string? error)
    {
        error = null;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        while (i < s.Length)
        {
            var ch = s[i];

            if (ch == ')' || ch == ']')
                return counts;   // 交给上层吃掉闭括号

            if (ch == '(' || ch == '[')
            {
                var close = ch == '(' ? ')' : ']';
                i++;
                var inner = ParseGroup(s, ref i, out error);
                if (inner is null) return null;
                if (i >= s.Length || s[i] != close) { error = "括号没有闭合。"; return null; }
                i++;

                var mult = ReadNumber(s, ref i, 1);
                foreach (var (el, n) in inner)
                    counts[el] = counts.TryGetValue(el, out var old) ? old + n * mult : n * mult;

                continue;
            }

            if (char.IsUpper(ch))
            {
                var symbol = ch.ToString();
                i++;
                if (i < s.Length && char.IsLower(s[i])) { symbol += s[i]; i++; }

                // ⛔ 必须核对元素符号是否存在：不查的话 "Cx"、"Q" 这类会被当成合法元素
                //    一路算下去，界面给出的配平结果看着对、其实是拿假元素凑的
                if (!PeriodicTable.IsElement(symbol))
                {
                    error = $"「{symbol}」不是元素符号，请检查大小写（如 Fe、Na、Cl）。";
                    return null;
                }

                var n = ReadNumber(s, ref i, 1);
                counts[symbol] = counts.TryGetValue(symbol, out var old) ? old + n : n;
                continue;
            }

            // 其它字符（空格、多余符号）跳过会让错误被藏起来，直接报
            error = $"化学式里出现了无法识别的字符「{ch}」。";
            return null;
        }

        return counts;
    }

    private static int ReadNumber(string s, ref int i, int fallback)
    {
        var start = i;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        if (i == start) return fallback;
        return int.TryParse(s[start..i], out var n) ? n : fallback;
    }

    /// <summary>
    /// 从零空间基里挑一个"全为正（或全为负）"的向量。
    /// 零空间维数大于 1 时（例如 H2 + O2 = H2O + H2O2），基向量本身未必全正，
    /// 这里再试它们的组合，尽量给出一个能用的答案。
    /// </summary>
    private static Rational[]? PickPositiveVector(List<Rational[]> basis)
    {
        foreach (var v in basis)
            if (IsUniform(v)) return v;

        if (basis.Count > 1)
        {
            // 只试小系数的两两组合：再大就不是"课本上的配平"了
            for (var k1 = -3; k1 <= 3; k1++)
            {
                for (var k2 = -3; k2 <= 3; k2++)
                {
                    if (k1 == 0 && k2 == 0) continue;
                    var mixed = new Rational[basis[0].Length];
                    for (var i = 0; i < mixed.Length; i++)
                        mixed[i] = basis[0][i] * new Rational(k1) + basis[1][i] * new Rational(k2);
                    if (IsUniform(mixed)) return mixed;
                }
            }
        }

        return null;
    }

    /// <summary>全为正或全为负，且没有 0（0 表示那种物质不参与反应，不是配平解）。</summary>
    private static bool IsUniform(Rational[] v)
    {
        if (v.Length == 0) return false;
        var sign = 0;
        foreach (var x in v)
        {
            if (x.IsZero) return false;
            if (sign == 0) sign = x.Sign;
            else if (x.Sign != sign) return false;
        }

        return sign != 0;
    }

    /// <summary>化成最小正整数：先乘掉分母，再除以最大公约数。</summary>
    private static (int[]? Values, int Factor) ToSmallestIntegers(Rational[] v)
    {
        var lcm = Rational.LcmOfDenominators(v);
        var scaled = new BigInteger[v.Length];
        for (var i = 0; i < v.Length; i++)
            scaled[i] = v[i].Num * (lcm / v[i].Den);

        var sign = scaled[0].Sign;
        if (sign < 0)
            for (var i = 0; i < scaled.Length; i++) scaled[i] = -scaled[i];

        var gcd = BigInteger.Zero;
        foreach (var x in scaled) gcd = BigInteger.GreatestCommonDivisor(gcd, x);
        if (gcd.IsZero) return (null, 1);

        var result = new int[scaled.Length];
        for (var i = 0; i < scaled.Length; i++)
        {
            var value = scaled[i] / gcd;
            if (value > int.MaxValue || value < int.MinValue) return (null, 1);
            result[i] = (int)value;
        }

        return (result, 1);
    }
}
