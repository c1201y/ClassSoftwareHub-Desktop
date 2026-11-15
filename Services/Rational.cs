using System;
using System.Globalization;
using System.Numerics;

namespace ClassSoftwareHub.Desktop.Services;















public readonly struct Rational : IEquatable<Rational>, IComparable<Rational>
{
    public BigInteger Num { get; }
    
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

    
    public override int GetHashCode() => HashCode.Combine(Num, Den);

    public int CompareTo(Rational other) => (Num * other.Den).CompareTo(other.Num * Den);

    public override string ToString() =>
        Den.IsOne ? Num.ToString(CultureInfo.InvariantCulture)
                  : $"{Num}/{Den}";

    public double ToDouble() => (double)Num / (double)Den;

    
    
    
    public bool TryGetInteger(out BigInteger value)
    {
        value = default;
        if (!Den.IsOne) return false;
        value = Num;
        return true;
    }

    
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
