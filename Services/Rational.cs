using System;
using System.Globalization;
using System.Numerics;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 精确分数（分子 / 分母，始终约分、分母恒正）。
///
/// <para>
/// ⛔⛔ 这两个功能**不能用 double**：
/// 配平要判断"这个系数到底是不是 0"，消元过程中一次 1e-17 的残差就会把
/// <c>Fe + O2 → Fe2O3</c> 算成 4 : 3 : 2 之外的怪结果；多元方程也一样，
/// 高斯消元的主元判断依赖精确的 0。
/// </para>
/// <para>
/// 用 <see cref="BigInteger"/> 而不是 long：消元中间结果的分母会连乘，
/// 四个五元方程就能把 long 顶爆（真实踩过）。
/// </para>
/// </summary>
public readonly struct Rational : IEquatable<Rational>, IComparable<Rational>
{
    public BigInteger Num { get; }
    /// <summary>恒为正。零表示为 0/1。</summary>
    public BigInteger Den { get; }

    public static readonly Rational Zero = new(BigInteger.Zero, BigInteger.One);
    public static readonly Rational One = new(BigInteger.One, BigInteger.One);

    public Rational(BigInteger num, BigInteger den)
    {
        if (den.IsZero) throw new DivideByZeroException("分母不能为 0");

        if (den.Sign < 0) { num = -num; den = -den; }

        var g = BigInteger.GreatestCommonDivisor(BigInteger.Abs(num), den);
        if (!g.IsZero && !g.IsOne) { num /= g; den /= g; }

        Num = num;
        Den = den;
    }

    public Rational(BigInteger value) : this(value, BigInteger.One) { }

    /// <summary>从整数构造（配平里系数、元素个数都是整数）。</summary>
    public static implicit operator Rational(int value) => new(value, BigInteger.One);

    public bool IsZero => Num.IsZero;
    public int Sign => Num.Sign;

    public static Rational operator +(Rational a, Rational b) =>
        new(a.Num * b.Den + b.Num * a.Den, a.Den * b.Den);

    public static Rational operator -(Rational a, Rational b) =>
        new(a.Num * b.Den - b.Num * a.Den, a.Den * b.Den);

    public static Rational operator -(Rational a) => new(-a.Num, a.Den);

    public static Rational operator *(Rational a, Rational b) =>
        new(a.Num * b.Num, a.Den * b.Den);

    public static Rational operator /(Rational a, Rational b) =>
        new(a.Num * b.Den, a.Den * b.Num);

    public static bool operator ==(Rational a, Rational b) => a.Equals(b);
    public static bool operator !=(Rational a, Rational b) => !a.Equals(b);

    public bool Equals(Rational other) => Num == other.Num && Den == other.Den;

    public override bool Equals(object? obj) => obj is Rational r && Equals(r);

    /// <summary>⚠️ 哈希必须跟着约分后的规范形走，否则同一个值会有不同哈希。</summary>
    public override int GetHashCode() => HashCode.Combine(Num, Den);

    public int CompareTo(Rational other) => (Num * other.Den).CompareTo(other.Num * Den);

    public override string ToString() =>
        Den.IsOne ? Num.ToString(CultureInfo.InvariantCulture)
                  : $"{Num}/{Den}";

    public double ToDouble() => (double)Num / (double)Den;

    /// <summary>
    /// 想当整数看时用：确实是整数就返回（配平结果必须全是正整数）。
    /// </summary>
    public bool TryGetInteger(out BigInteger value)
    {
        value = default;
        if (!Den.IsOne) return false;
        value = Num;
        return true;
    }

    /// <summary>一组分数的分母最小公倍数 —— 把一组分数同时乘成整数用。</summary>
    public static BigInteger LcmOfDenominators(System.Collections.Generic.IEnumerable<Rational> values)
    {
        var lcm = BigInteger.One;
        foreach (var v in values)
            lcm = Lcm(lcm, v.Den);
        return lcm;
    }

    public static BigInteger Lcm(BigInteger a, BigInteger b)
    {
        if (a.IsZero || b.IsZero) return BigInteger.Zero;
        return BigInteger.Abs(a / BigInteger.GreatestCommonDivisor(a, b) * b);
    }
}
