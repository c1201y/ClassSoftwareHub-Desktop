using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

namespace ClassSoftwareHub.Desktop.Services;














public static class ChemistryBalancer
{
    
    public sealed class Result
    {
        public bool Ok => Error is null;
        public string? Error { get; init; }
        
        public string Equation { get; init; } = "";
        
        public List<(string Formula, int Coeff)> Terms { get; init; } = new();

        
        public List<(string Element, int Left, int Right)> ElementCheck { get; init; } = new();
        
        public string? Note { get; init; }
    }

    private static readonly char[] ArrowChars = { '=', '→', '⟶', '⇌' };

    public static Result Balance(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Fail("请输入一个方程式，例如 Fe + O2 = Fe2O3");

        var text = Normalize(input);

        
        var arrow = text.IndexOfAny(ArrowChars);
        if (arrow < 0)
            return Fail("缺少等号或箭头。请写成「反应物 = 生成物」，例如 H2 + O2 = H2O");

        
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

        
        var species = new List<(string Raw, Dictionary<string, int> Atoms)>();
        var note = "";
        foreach (var raw in leftTerms.Concat(rightTerms))
        {
            var atoms = ParseFormula(raw, out var error, out var chargeIgnored);
            if (atoms is null) return Fail(error ?? $"无法识别「{raw}」");
            if (chargeIgnored) note = "输入中的离子电荷未参与配平（只按元素守恒计算）。";
            species.Add((DisplayFormula(raw), atoms!));
        }

        
        var elements = species.SelectMany(s => s.Atoms.Keys).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
        var cols = species.Count;
        var matrix = new Rational[elements.Count][];

        for (var r = 0; r < elements.Count; r++)
        {
            matrix[r] = new Rational[cols];
            for (var c = 0; c < cols; c++)
            {
                species[c].Atoms.TryGetValue(elements[r], out var count);
                
                var sign = c < leftTerms.Count ? 1 : -1;
                matrix[r][c] = new Rational(sign * count, BigInteger.One);
            }
        }

        
        var basis = LinearSolver.NullSpace(matrix);
        if (basis.Count == 0)
            return Fail("这个方程式无解：等号两侧的元素种类或数量对不上，无法通过调整系数配平。");

        var vector = PickPositiveVector(basis);
        if (vector is null)
            return Fail("找不到全为正的系数：请检查等号两侧的元素是否写全（例如漏了生成物里的某种元素）。");

        var (ints, factor) = ToSmallestIntegers(vector);
        if (ints is null)
            return Fail("系数无法化为整数，请检查输入。");

        
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

    
    
    
    
    private static string DisplayFormula(string raw)
    {
        var s = raw.Trim();
        if (s.Length < 3 || s[0] != '[' || s[^1] != ']') return s;

        var inner = s[1..^1];
        if (inner.Contains('[') || inner.Contains('(')) return s;
        return inner;
    }

    
    
    
    
    private static string Normalize(string input)
    {
        var sb = new StringBuilder(input.Length);

        foreach (var ch in input)
        {
            
            
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
                
                
                error = "存在空的物质项，请检查是否多写了「+」或漏了物质。";
                return list;
            }

            
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

    
    
    
    
    private static Dictionary<string, int>? ParseFormula(string raw, out string? error, out bool chargeIgnored)
    {
        error = null;
        chargeIgnored = false;

        var formula = raw.Trim();
        if (formula.Length == 0) { error = "存在空的物质项。"; return null; }

        
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

    
    private static Dictionary<string, int>? ParseGroup(string s, ref int i, out string? error)
    {
        error = null;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        while (i < s.Length)
        {
            var ch = s[i];

            if (ch == ')' || ch == ']')
                return counts;   

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

                
                
                if (!PeriodicTable.IsElement(symbol))
                {
                    error = $"「{symbol}」不是元素符号，请检查大小写（如 Fe、Na、Cl）。";
                    return null;
                }

                var n = ReadNumber(s, ref i, 1);
                counts[symbol] = counts.TryGetValue(symbol, out var old) ? old + n : n;
                continue;
            }

            
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

    
    
    
    
    
    private static Rational[]? PickPositiveVector(List<Rational[]> basis)
    {
        foreach (var v in basis)
            if (IsUniform(v)) return v;

        if (basis.Count > 1)
        {
            
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
