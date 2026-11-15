using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>三角函数与反三角函数的角度解释方式。</summary>
public enum AngleMode
{
    /// <summary>弧度制（默认）。</summary>
    Radians = 0,

    /// <summary>角度制。<c>sin(30)</c> 即 0.5。</summary>
    Degrees = 1,
}

/// <summary>
/// 表达式解析失败。<see cref="Position"/> 是出错处在原文里的字符下标（从 0 起）。
/// </summary>
public sealed class ExpressionException : Exception
{
    public int Position { get; }

    public ExpressionException(string message, int position) : base(message) => Position = position;
}

/// <summary>
/// 单变量表达式解析器（自变量固定为 <c>x</c>），把文本编译成 <c>Func&lt;double, double&gt;</c>。
///
/// <para>
/// ⛔⛔ <b>绝不把用户输入当代码执行</b>：没有 <c>eval</c>、没有 <c>DataTable.Compute</c>、
/// 没有 <c>Assembly.Load</c>，全部走 <see cref="Expression"/> 树 —— 能构造出来的只有算术运算
/// 和白名单里的 <see cref="Math"/> 调用，多一个口子都不留。
/// </para>
///
/// <para>
/// <b>语法</b>：<c>+ - * / ^</c>（<c>^</c> 右结合）、括号、逗号、一元正负号；函数见 <see cref="Functions"/>；
/// 常量 <c>pi</c>/<c>π</c>、<c>e</c>、<c>tau</c>/<c>τ</c>；变量 <c>x</c>。
/// 支持<b>隐式乘法</b>（<c>2x</c>、<c>3sin(x)</c>、<c>(x+1)(x-1)</c>）与科学计数法（<c>2e3</c>）。
/// </para>
///
/// <para>
/// ⚠️ <c>e</c> 的歧义按这个规则消解：<c>2e3</c> 是科学计数法的 2000；<c>2e</c> 是 2 乘自然常数 e。
/// 也接受从别处粘进来的全角括号、<c>×</c>、<c>÷</c>、Unicode 减号 <c>−</c>、中文逗号。
/// </para>
/// </summary>
public static class ExpressionParser
{
    /// <summary>表达式长度上限（防粘贴一坨东西进来）。</summary>
    private const int MaxLength = 512;

    /// <summary>递归深度上限（防 <c>((((((…</c> 把栈打穿）。</summary>
    private const int MaxDepth = 64;

    private const string VariableName = "x";

    /// <summary>支持的常数名。</summary>
    private static readonly Dictionary<string, double> Constants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pi"] = Math.PI,
        ["π"] = Math.PI,
        ["tau"] = Math.Tau,
        ["τ"] = Math.Tau,
        ["e"] = Math.E,
    };

    // ────────────────────────────── 函数白名单 ──────────────────────────────

    private static Expression K(double v) => Expression.Constant(v);

    private static Expression Call1(Expression[] a, string name)
        => Expression.Call(typeof(Math), name, null, a[0]);

    /// <summary>三角函数：角度制时先把入参换算成弧度。</summary>
    private static Expression Trig(Expression[] a, string name, AngleMode m)
        => m == AngleMode.Degrees
            ? Expression.Call(typeof(Math), name, null,
                Expression.Multiply(a[0], K(Math.PI / 180.0)))
            : Call1(a, name);

    /// <summary>反三角函数：角度制时把结果换算成度数。</summary>
    private static Expression Arc(Expression[] a, string name, AngleMode m)
    {
        var call = Call1(a, name);
        return m == AngleMode.Degrees ? Expression.Multiply(call, K(180.0 / Math.PI)) : call;
    }

    /// <summary>二元函数链式折叠（min / max 支持 2 个以上参数）。</summary>
    private static Expression Fold(Expression[] a, string name)
    {
        var acc = a[0];
        for (var i = 1; i < a.Length; i++)
            acc = Expression.Call(typeof(Math), name, null, acc, a[i]);
        return acc;
    }

    private static Expression Recip(Expression[] a, string name, AngleMode m)
        => Expression.Divide(K(1.0), Trig(a, name, m));

    /// <summary>名字 → (最少参数, 最多参数, 造节点)。<c>int.MaxValue</c> 表示不限。</summary>
    private static readonly Dictionary<string, (int Min, int Max, Func<Expression[], AngleMode, Expression> Make)> Functions
        = new(StringComparer.OrdinalIgnoreCase)
    {
        // 三角
        ["sin"] = (1, 1, (a, m) => Trig(a, nameof(Math.Sin), m)),
        ["cos"] = (1, 1, (a, m) => Trig(a, nameof(Math.Cos), m)),
        ["tan"] = (1, 1, (a, m) => Trig(a, nameof(Math.Tan), m)),
        ["sec"] = (1, 1, (a, m) => Recip(a, nameof(Math.Cos), m)),
        ["csc"] = (1, 1, (a, m) => Recip(a, nameof(Math.Sin), m)),
        ["cot"] = (1, 1, (a, m) => Recip(a, nameof(Math.Tan), m)),

        // 反三角（两种写法都收，学生两种都会遇到）
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

        // 双曲
        ["sinh"] = (1, 1, (a, _) => Call1(a, nameof(Math.Sinh))),
        ["cosh"] = (1, 1, (a, _) => Call1(a, nameof(Math.Cosh))),
        ["tanh"] = (1, 1, (a, _) => Call1(a, nameof(Math.Tanh))),
        ["asinh"] = (1, 1, (a, _) => Call1(a, nameof(Math.Asinh))),
        ["acosh"] = (1, 1, (a, _) => Call1(a, nameof(Math.Acosh))),
        ["atanh"] = (1, 1, (a, _) => Call1(a, nameof(Math.Atanh))),

        // 对数与指数
        ["ln"] = (1, 1, (a, _) => Call1(a, nameof(Math.Log))),
        ["lg"] = (1, 1, (a, _) => Call1(a, nameof(Math.Log10))),
        ["log2"] = (1, 1, (a, _) => Call1(a, nameof(Math.Log2))),
        ["lb"] = (1, 1, (a, _) => Call1(a, nameof(Math.Log2))),
        // ⚠️ log 单参按数学惯例＝常用对数(10)；双参是换底 Math.Log(x, base)
        ["log"] = (1, 2, (a, _) => a.Length == 2
            ? Expression.Call(typeof(Math), nameof(Math.Log), null, a[0], a[1])
            : Call1(a, nameof(Math.Log10))),
        ["exp"] = (1, 1, (a, _) => Call1(a, nameof(Math.Exp))),

        // 幂与根
        ["sqrt"] = (1, 1, (a, _) => Call1(a, nameof(Math.Sqrt))),
        ["cbrt"] = (1, 1, (a, _) => Call1(a, nameof(Math.Cbrt))),
        ["pow"] = (2, 2, (a, _) => Expression.Call(typeof(Math), nameof(Math.Pow), null, a[0], a[1])),
        ["hypot"] = (2, 2, (a, _) => Expression.Call(typeof(Math), nameof(Math.Sqrt), null,
            Expression.Add(Expression.Multiply(a[0], a[0]), Expression.Multiply(a[1], a[1])))),

        // 取整与符号
        ["abs"] = (1, 1, (a, _) => Call1(a, nameof(Math.Abs))),
        // ⚠️ Math.Sign(double) 返回的是 **int**，不转回 double 的话整棵树在这里类型对不上
        //    （症状是 "二元运算符 Add 没有为类型 double 和 int 定义"）。
        ["sign"] = (1, 1, (a, _) => Expression.Convert(Call1(a, nameof(Math.Sign)), typeof(double))),
        ["floor"] = (1, 1, (a, _) => Call1(a, nameof(Math.Floor))),
        ["ceil"] = (1, 1, (a, _) => Call1(a, nameof(Math.Ceiling))),
        // ⚠️ 必须显式给 AwayFromZero：Math.Round 的默认是**银行家舍入**（Round half to even），
        //    round(2.5) 会得 2。中文语境下的"四舍五入"是 3，得按用户预期来。
        ["round"] = (1, 1, (a, _) => Expression.Call(typeof(Math), nameof(Math.Round), null,
            a[0], Expression.Constant(MidpointRounding.AwayFromZero))),
        ["trunc"] = (1, 1, (a, _) => Call1(a, nameof(Math.Truncate))),

        // 其他
        ["mod"] = (2, 2, (a, _) => Expression.Modulo(a[0], a[1])),
        ["min"] = (2, int.MaxValue, (a, _) => Fold(a, nameof(Math.Min))),
        ["max"] = (2, int.MaxValue, (a, _) => Fold(a, nameof(Math.Max))),
        ["rad"] = (1, 1, (a, _) => Expression.Multiply(a[0], K(Math.PI / 180.0))),
        ["deg"] = (1, 1, (a, _) => Expression.Multiply(a[0], K(180.0 / Math.PI))),
    };

    /// <summary>给界面用的函数名清单（按字母序，同一名字只出现一次）。</summary>
    public static IReadOnlyList<string> FunctionNames { get; } = BuildFunctionNames();

    private static IReadOnlyList<string> BuildFunctionNames()
    {
        var list = new List<string>(Functions.Keys);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    // ────────────────────────────── 对外入口 ──────────────────────────────

    /// <summary>编译表达式。失败抛 <see cref="ExpressionException"/>。</summary>
    public static Func<double, double> Compile(string text, AngleMode mode = AngleMode.Radians)
    {
        var lambda = ParseTree(text, mode);
        return ((Expression.Lambda<Func<double, double>>(lambda.Body, lambda.Parameters)).Compile());
    }

    /// <summary>
    /// 编译表达式，同时把**表达式树**交出来 —— 「解析通式」匹配标准形式要用树本身，
    /// 光拿编译好的委托看不出来它是 sin 还是别的。失败返回 false 并给出中文错误。
    /// </summary>
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

        // ⚠️ 变量节点必须是**同一个实例**在整棵树里复用 —— 每处都 new 一个
        //    ParameterExpression 会让 Lambda 绑定失败（"变量 x 未定义"）。这里建一次、传下去。
        var x = Expression.Parameter(typeof(double), VariableName);
        var body = new Parser(tokens, mode, x).Parse();

        return Expression.Lambda<Func<double, double>>(body, x);
    }

    /// <summary>编译表达式，失败返回 false 并给出**可直接显示给用户**的中文错误。</summary>
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

    // ────────────────────────────── 词法 ──────────────────────────────

    private enum TokKind
    {
        Number, Ident, Plus, Minus, Star, Slash, Caret, LParen, RParen, Comma, Radical, Cbrt, End,
    }

    /// <summary>
    /// 上标字符 → 对应的普通字符（x²、x⁻¹ 这种数学记号）。
    /// ⛔ 只收数字和正负号 —— <c>ⁿ</c>、<c>ˣ</c> 这类"上标字母"没有唯一含义（n 是什么？），
    ///    收进来就会静默算错，不如明确报错让人写成 <c>^(1/n)</c>。
    /// </summary>
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

            // ── 数字（含小数与科学计数法）──
            if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                var start = i;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                if (i < s.Length && s[i] == '.')
                {
                    i++;
                    while (i < s.Length && char.IsDigit(s[i])) i++;
                }

                // ⚠️ 「2e」后面没跟数字时**必须回退** —— 那种写法是 2 乘自然常数 e，不是半个科学计数法。
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

                // ⚠️ 「1.2.3」这种连续小数点一定是笔误。不拦的话会被切成 1.2 和 .3 两个数字，
                //    再靠隐式乘法算成 0.36 —— 不报错、但结果是错的，这种最坑。
                if (i < s.Length && s[i] == '.')
                    throw new ExpressionException("一个数字里出现了两个小数点", i);

                list.Add(new Tok(TokKind.Number, v, text, start));
                continue;
            }

            // ── 标识符（函数名 / 常量 / 变量）──
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                list.Add(new Tok(TokKind.Ident, 0, s[start..i], start));
                continue;
            }

            // ── 上标（数学记号：x² = x^2、x⁻¹ = x^(-1)、2³ = 8）──
            // 连续读一整串再合成一个数 —— x¹² 是 12 次方，不是 1 次方乘 2 次方。
            if (Superscripts.TryGetValue(c, out var first))
            {
                var start = i;
                var digits = "";
                var signAllowed = true;   // 正负号只能写在最前面（⁻¹ 可以，1⁻² 不行）

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

            // ── 根号（数学记号：√x、√(x+1)、∛8）──
            // 作为一元前缀处理，绑定见 Parser.Primary。
            if (c is '√' or '∛')
            {
                list.Add(new Tok(c == '√' ? TokKind.Radical : TokKind.Cbrt, 0, c.ToString(), i));
                i++;
                continue;
            }

            // ── 运算符（顺手认下从别处粘进来的全角 / 数学符号）──
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

    // ────────────────────────────── 语法 ──────────────────────────────

    /// <summary>
    /// 递归下降。<b>优先级自低到高</b>：
    /// <c>+ -</c> → <c>* / 隐式乘法</c> → 一元 <c>+ -</c> → <c>^</c>（右结合）→ 原子。
    ///
    /// <para>
    /// ⚠️ 一元负号放在 <c>^</c> <b>上面</b>这一层，是为了同时满足两条数学惯例：
    /// <c>-x^2</c> 读作 <c>-(x^2)</c>，而 <c>2^-1</c> 又是一个合法写法。
    /// </para>
    /// </summary>
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
                    else if (CanStartFactor()) left = Expression.Multiply(left, Unary());  // 隐式乘法
                    else return left;
                }
            }
            finally { Leave(); }
        }

        /// <summary>下一个记号能不能当乘数（隐式乘法只在有真实前项时才允许）。</summary>
        private bool CanStartFactor()
            => Cur.Kind is TokKind.Number or TokKind.Ident or TokKind.LParen
               or TokKind.Radical or TokKind.Cbrt;   // 2√x = 2·√x，数学上的常见写法

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
                var e = Unary();   // 右结合，且允许 2^-1
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

                    // 只有「名字在函数表里 + 紧跟左括号」才算调用 ——
                    // 否则 e(x+1) 会被误当函数调用，而它其实是 e 乘 (x+1)。
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
                    // 根号是**一元前缀**，吃紧跟的一个带幂项：√x、√(x+1)、√x²。
                    // ⚠️ 用 Power() 而不是 Primary()：√x² 数学上读作 √(x²)，指数要跟进去；
                    //    而乘除加归外层 —— √2x 是 (√2)·x，√x+1 是 (√x)+1，都是标准读法。
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
            _i++;   // 吃掉左括号

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
