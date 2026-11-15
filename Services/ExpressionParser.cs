using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;

namespace ClassSoftwareHub.Desktop.Services;


public enum AngleMode
{
    
    Radians = 0,

    
    Degrees = 1,
}




public sealed class ExpressionException : Exception
{
    public int Position { get; }

    public ExpressionException(string message, int position) : base(message) => Position = position;
}





















public static class ExpressionParser
{
    
    private const int MaxLength = 512;

    
    private const int MaxDepth = 64;

    private const string VariableName = "x";

    
    private static readonly Dictionary<string, double> Constants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pi"] = Math.PI,
        ["π"] = Math.PI,
        ["tau"] = Math.Tau,
        ["τ"] = Math.Tau,
        ["e"] = Math.E,
    };

    

    private static Expression K(double v) => Expression.Constant(v);

    private static Expression Call1(Expression[] a, string name)
        => Expression.Call(typeof(Math), name, null, a[0]);

    
    private static Expression Trig(Expression[] a, string name, AngleMode m)
        => m == AngleMode.Degrees
            ? Expression.Call(typeof(Math), name, null,
                Expression.Multiply(a[0], K(Math.PI / 180.0)))
            : Call1(a, name);

    
    private static Expression Arc(Expression[] a, string name, AngleMode m)
    {
        var call = Call1(a, name);
        return m == AngleMode.Degrees ? Expression.Multiply(call, K(180.0 / Math.PI)) : call;
    }

    
    private static Expression Fold(Expression[] a, string name)
    {
        var acc = a[0];
        for (var i = 1; i < a.Length; i++)
            acc = Expression.Call(typeof(Math), name, null, acc, a[i]);
        return acc;
    }

    private static Expression Recip(Expression[] a, string name, AngleMode m)
        => Expression.Divide(K(1.0), Trig(a, name, m));

    
    private static readonly Dictionary<string, (int Min, int Max, Func<Expression[], AngleMode, Expression> Make)> Functions
        = new(StringComparer.OrdinalIgnoreCase)
    {
        
        ["sin"] = (1, 1, (a, m) => Trig(a, nameof(Math.Sin), m)),
        ["cos"] = (1, 1, (a, m) => Trig(a, nameof(Math.Cos), m)),
        ["tan"] = (1, 1, (a, m) => Trig(a, nameof(Math.Tan), m)),
        ["sec"] = (1, 1, (a, m) => Recip(a, nameof(Math.Cos), m)),
        ["csc"] = (1, 1, (a, m) => Recip(a, nameof(Math.Sin), m)),
        ["cot"] = (1, 1, (a, m) => Recip(a, nameof(Math.Tan), m)),

        
        ["asin"] = (1, 1, (a, m) => Arc(a, nameof(Math.Asin), m)),
        ["acos"] = (1, 1, (a, m) => Arc(a, nameof(Math.Acos), m)),
        ["atan"] = (1, 1, (a, m) => Arc(a, nameof(Math.Atan), m)),
        ["arcsin"] = (1, 1, (a, m) => Arc(a, nameof(Math.Asin), m)),
        ["arccos"] = (1, 1, (a, m) => Arc(a, nameof(Math.Acos), m)),
        ["arctan"] = (1, 1, (a, m) => Arc(a, nameof(Math.Atan), m)),
        ["atan2"] = (2, 2, (a, m) => m == AngleMode.Degrees
            ? Expression.Multiply(Expression.Call(typeof(Math), nameof(Math.Atan2), null, a[0], a[1]),
                K(180.0 / Math.PI))
            : Expression.Call(typeof(Math), nameof(Math.Atan2), null, a[0], a[1])),

        
        ["sinh"] = (1, 1, (a, _) => Call1(a, nameof(Math.Sinh))),
        ["cosh"] = (1, 1, (a, _) => Call1(a, nameof(Math.Cosh))),
        ["tanh"] = (1, 1, (a, _) => Call1(a, nameof(Math.Tanh))),
        ["asinh"] = (1, 1, (a, _) => Call1(a, nameof(Math.Asinh))),
        ["acosh"] = (1, 1, (a, _) => Call1(a, nameof(Math.Acosh))),
        ["atanh"] = (1, 1, (a, _) => Call1(a, nameof(Math.Atanh))),

        
        ["ln"] = (1, 1, (a, _) => Call1(a, nameof(Math.Log))),
        ["lg"] = (1, 1, (a, _) => Call1(a, nameof(Math.Log10))),
        ["log2"] = (1, 1, (a, _) => Call1(a, nameof(Math.Log2))),
        ["lb"] = (1, 1, (a, _) => Call1(a, nameof(Math.Log2))),
        
        ["log"] = (1, 2, (a, _) => a.Length == 2
            ? Expression.Call(typeof(Math), nameof(Math.Log), null, a[0], a[1])
            : Call1(a, nameof(Math.Log10))),
        ["exp"] = (1, 1, (a, _) => Call1(a, nameof(Math.Exp))),

        
        ["sqrt"] = (1, 1, (a, _) => Call1(a, nameof(Math.Sqrt))),
        ["cbrt"] = (1, 1, (a, _) => Call1(a, nameof(Math.Cbrt))),
        ["pow"] = (2, 2, (a, _) => Expression.Call(typeof(Math), nameof(Math.Pow), null, a[0], a[1])),
        ["hypot"] = (2, 2, (a, _) => Expression.Call(typeof(Math), nameof(Math.Sqrt), null,
            Expression.Add(Expression.Multiply(a[0], a[0]), Expression.Multiply(a[1], a[1])))),

        
        ["abs"] = (1, 1, (a, _) => Call1(a, nameof(Math.Abs))),
        
        
        ["sign"] = (1, 1, (a, _) => Expression.Convert(Call1(a, nameof(Math.Sign)), typeof(double))),
        ["floor"] = (1, 1, (a, _) => Call1(a, nameof(Math.Floor))),
        ["ceil"] = (1, 1, (a, _) => Call1(a, nameof(Math.Ceiling))),
        
        
        ["round"] = (1, 1, (a, _) => Expression.Call(typeof(Math), nameof(Math.Round), null,
            a[0], Expression.Constant(MidpointRounding.AwayFromZero))),
        ["trunc"] = (1, 1, (a, _) => Call1(a, nameof(Math.Truncate))),

        
        ["mod"] = (2, 2, (a, _) => Expression.Modulo(a[0], a[1])),
        ["min"] = (2, int.MaxValue, (a, _) => Fold(a, nameof(Math.Min))),
        ["max"] = (2, int.MaxValue, (a, _) => Fold(a, nameof(Math.Max))),
        ["rad"] = (1, 1, (a, _) => Expression.Multiply(a[0], K(Math.PI / 180.0))),
        ["deg"] = (1, 1, (a, _) => Expression.Multiply(a[0], K(180.0 / Math.PI))),
    };

    
    public static IReadOnlyList<string> FunctionNames { get; } = BuildFunctionNames();

    private static IReadOnlyList<string> BuildFunctionNames()
    {
        var list = new List<string>(Functions.Keys);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    

    
    public static Func<double, double> Compile(string text, AngleMode mode = AngleMode.Radians)
    {
        var lambda = ParseTree(text, mode);
        return ((Expression.Lambda<Func<double, double>>(lambda.Body, lambda.Parameters)).Compile());
    }

    
    
    
    
    public static bool TryParseTree(string text, AngleMode mode, out LambdaExpression? lambda, out string? error)
    {
        try
        {
            lambda = ParseTree(text, mode);
            error = null;
            return true;
        }
        catch (ExpressionException ex)
        {
            lambda = null;
            error = ex.Position > 0 && ex.Position <= text.Length
                ? $"第 {ex.Position + 1} 个字符处：{ex.Message}"
                : ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("plot", ex, "解析表达式树");
            lambda = null;
            error = "表达式无法解析";
            return false;
        }
    }

    private static LambdaExpression ParseTree(string text, AngleMode mode)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ExpressionException("表达式是空的", 0);

        if (text.Length > MaxLength)
            throw new ExpressionException($"表达式太长了（最多 {MaxLength} 个字符）", MaxLength);

        var tokens = Tokenize(text);

        
        
        var x = Expression.Parameter(typeof(double), VariableName);
        var body = new Parser(tokens, mode, x).Parse();

        return Expression.Lambda<Func<double, double>>(body, x);
    }

    
    public static bool TryCompile(string text, AngleMode mode, out Func<double, double>? fn, out string? error)
    {
        try
        {
            fn = Compile(text, mode);
            error = null;
            return true;
        }
        catch (ExpressionException ex)
        {
            fn = null;
            error = ex.Position > 0 && ex.Position <= text.Length
                ? $"第 {ex.Position + 1} 个字符处：{ex.Message}"
                : ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("plot", ex, "编译表达式");
            fn = null;
            error = "表达式无法解析";
            return false;
        }
    }

    

    private enum TokKind
    {
        Number, Ident, Plus, Minus, Star, Slash, Caret, LParen, RParen, Comma, Radical, Cbrt, End,
    }

    
    
    
    
    
    private static readonly Dictionary<char, char> Superscripts = new()
    {
        ['⁰'] = '0', ['¹'] = '1', ['²'] = '2', ['³'] = '3', ['⁴'] = '4',
        ['⁵'] = '5', ['⁶'] = '6', ['⁷'] = '7', ['⁸'] = '8', ['⁹'] = '9',
        ['⁺'] = '+', ['⁻'] = '-',
    };

    private readonly record struct Tok(TokKind Kind, double Num, string Text, int Pos);

    private static List<Tok> Tokenize(string s)
    {
        var list = new List<Tok>(s.Length);
        var i = 0;

        while (i < s.Length)
        {
            var c = s[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            
            if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                var start = i;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                if (i < s.Length && s[i] == '.')
                {
                    i++;
                    while (i < s.Length && char.IsDigit(s[i])) i++;
                }

                
                if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
                {
                    var save = i;
                    i++;
                    if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                    if (i < s.Length && char.IsDigit(s[i])) { while (i < s.Length && char.IsDigit(s[i])) i++; }
                    else i = save;
                }

                var text = s[start..i];
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    throw new ExpressionException($"数字「{text}」读不出来", start);

                
                
                if (i < s.Length && s[i] == '.')
                    throw new ExpressionException("一个数字里出现了两个小数点", i);

                list.Add(new Tok(TokKind.Number, v, text, start));
                continue;
            }

            
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                list.Add(new Tok(TokKind.Ident, 0, s[start..i], start));
                continue;
            }

            
            
            if (Superscripts.TryGetValue(c, out var first))
            {
                var start = i;
                var digits = "";
                var signAllowed = true;   

                while (i < s.Length && Superscripts.TryGetValue(s[i], out var m))
                {
                    if (m is '+' or '-')
                    {
                        if (!signAllowed)
                            throw new ExpressionException("上标的正负号只能写在最前面", i);
                        if (m == '-') digits += '-';
                        i++;
                        continue;
                    }

                    digits += m;
                    signAllowed = false;
                    i++;
                }

                if (digits.Length == 0 || digits == "-")
                    throw new ExpressionException("上标里只能是数字（如 x²、x⁻¹）；任意次根请写 ^(1/n)", start);

                if (!double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var sup))
                    throw new ExpressionException($"上标「{digits}」读不出来", start);

                list.Add(new Tok(TokKind.Caret, 0, "^", start));
                list.Add(new Tok(TokKind.Number, sup, digits, start));
                continue;
            }

            
            
            if (c is '√' or '∛')
            {
                list.Add(new Tok(c == '√' ? TokKind.Radical : TokKind.Cbrt, 0, c.ToString(), i));
                i++;
                continue;
            }

            
            TokKind kind;
            switch (c)
            {
                case '+': kind = TokKind.Plus; break;
                case '-' or '−' or '–' or 'ー': kind = TokKind.Minus; break;
                case '*' or '×' or '·' or '⋅': kind = TokKind.Star; break;
                case '/' or '÷' or '∕': kind = TokKind.Slash; break;
                case '^': kind = TokKind.Caret; break;
                case '(' or '（' or '[': kind = TokKind.LParen; break;
                case ')' or '）' or ']': kind = TokKind.RParen; break;
                case ',' or '，' or '、': kind = TokKind.Comma; break;
                default: throw new ExpressionException($"不认识符号「{c}」", i);
            }

            list.Add(new Tok(kind, 0, c.ToString(), i));
            i++;
        }

        list.Add(new Tok(TokKind.End, 0, "", s.Length));
        return list;
    }

    

    
    
    
    
    
    
    
    
    
    private sealed class Parser
    {
        private readonly List<Tok> _toks;
        private readonly AngleMode _mode;
        private readonly ParameterExpression _x;
        private int _i;
        private int _depth;

        public Parser(List<Tok> toks, AngleMode mode, ParameterExpression x)
        {
            _toks = toks;
            _mode = mode;
            _x = x;
        }

        private Tok Cur => _toks[_i];

        private ExpressionException Err(string message) => new(message, Cur.Pos);

        private bool Take(TokKind kind)
        {
            if (Cur.Kind != kind) return false;
            _i++;
            return true;
        }

        private void Expect(TokKind kind, string what)
        {
            if (Cur.Kind != kind) throw Err($"缺少{what}");
            _i++;
        }

        public Expression Parse()
        {
            var e = Expr();
            if (Cur.Kind != TokKind.End) throw Err($"这里多出了一个「{Cur.Text}」");
            return e;
        }

        private void Enter()
        {
            if (++_depth > MaxDepth) throw Err($"表达式嵌套太深（最多 {MaxDepth} 层）");
        }

        private void Leave() => _depth--;

        private Expression Expr()
        {
            Enter();
            try
            {
                var left = Term();
                while (true)
                {
                    if (Take(TokKind.Plus)) left = Expression.Add(left, Term());
                    else if (Take(TokKind.Minus)) left = Expression.Subtract(left, Term());
                    else return left;
                }
            }
            finally { Leave(); }
        }

        private Expression Term()
        {
            Enter();
            try
            {
                var left = Unary();
                while (true)
                {
                    if (Take(TokKind.Star)) left = Expression.Multiply(left, Unary());
                    else if (Take(TokKind.Slash)) left = Expression.Divide(left, Unary());
                    else if (CanStartFactor()) left = Expression.Multiply(left, Unary());  
                    else return left;
                }
            }
            finally { Leave(); }
        }

        
        private bool CanStartFactor()
            => Cur.Kind is TokKind.Number or TokKind.Ident or TokKind.LParen
               or TokKind.Radical or TokKind.Cbrt;   

        private Expression Unary()
        {
            Enter();
            try
            {
                if (Take(TokKind.Plus)) return Unary();
                if (Take(TokKind.Minus)) return Expression.Negate(Unary());
                return Power();
            }
            finally { Leave(); }
        }

        private Expression Power()
        {
            Enter();
            try
            {
                var b = Primary();
                if (!Take(TokKind.Caret)) return b;
                var e = Unary();   
                return Expression.Call(typeof(Math), nameof(Math.Pow), null, b, e);
            }
            finally { Leave(); }
        }

        private Expression Primary()
        {
            Enter();
            try
            {
                var t = Cur;

                if (t.Kind == TokKind.Number)
                {
                    _i++;
                    return Expression.Constant(t.Num);
                }

                if (t.Kind == TokKind.Ident)
                {
                    _i++;
                    var name = t.Text;

                    
                    
                    if (Cur.Kind == TokKind.LParen && Functions.TryGetValue(name, out var sig))
                        return CallFunction(name, sig, t.Pos);

                    if (Constants.TryGetValue(name, out var cv)) return Expression.Constant(cv);

                    if (string.Equals(name, VariableName, StringComparison.OrdinalIgnoreCase)) return _x;

                    throw Err($"不认识的名字「{name}」");
                }

                if (t.Kind == TokKind.LParen)
                {
                    _i++;
                    var inner = Expr();
                    Expect(TokKind.RParen, "右括号「)」");
                    return inner;
                }

                if (t.Kind is TokKind.Radical or TokKind.Cbrt)
                {
                    
                    
                    
                    _i++;
                    var operand = Power();
                    return Expression.Call(typeof(Math),
                        t.Kind == TokKind.Radical ? nameof(Math.Sqrt) : nameof(Math.Cbrt),
                        null, operand);
                }

                if (t.Kind == TokKind.End) throw Err("表达式没有写完");

                throw Err($"这里不该出现「{t.Text}」");
            }
            finally { Leave(); }
        }

        private Expression CallFunction(string name, (int Min, int Max, Func<Expression[], AngleMode, Expression> Make) sig, int pos)
        {
            _i++;   

            var args = new List<Expression>(2);
            if (Cur.Kind != TokKind.RParen)
            {
                args.Add(Expr());
                while (Take(TokKind.Comma)) args.Add(Expr());
            }

            if (Cur.Kind != TokKind.RParen)
                throw new ExpressionException($"函数「{name}」的参数没有收尾，缺少右括号「)」", pos);

            _i++;

            if (args.Count < sig.Min || args.Count > sig.Max)
            {
                var want = sig.Max == int.MaxValue ? $"{sig.Min} 个以上"
                         : sig.Min == sig.Max ? $"{sig.Min} 个"
                         : $"{sig.Min} 到 {sig.Max} 个";
                throw new ExpressionException($"函数「{name}」需要 {want}参数，这里给了 {args.Count} 个", pos);
            }

            return sig.Make(args.ToArray(), _mode);
        }
    }
}
