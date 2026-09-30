namespace ClassSoftwareHub.Desktop.Data;

/// <summary>
/// 一个键"按下去干什么"。
///
/// ⚠️ 这里刻意**不区分大小写**：字母键只有"哪个虚拟键码"，大小写由上档状态决定 ——
///    跟真键盘一样。理由：中文输入法只认虚拟键码，送 Unicode 字符会绕过输入法
///    （打拼音出不来的那种毛病就是这么来的）。
/// </summary>
public enum KeyRole
{
    /// <summary>字符键：按虚拟键码送键，上档状态决定大小写 / 上档符号。</summary>
    Char,
    /// <summary>功能键（退格 / 回车 / 方向键…）：直接送虚拟键码。</summary>
    Key,
    /// <summary>粘滞修饰键（Ctrl / ⊞ / Alt）：按一下点亮，下一个键走组合。</summary>
    Modifier,
    /// <summary>上档（Shift）：一次性，送完一个键就灭。</summary>
    Shift,
    /// <summary>大写锁定：按一下常亮，只对字母键生效。</summary>
    Caps,
    /// <summary>切层（<see cref="KeyDef.Target"/> 指定目标层）。</summary>
    Layer,
    /// <summary>收起键盘。</summary>
    Hide,
    /// <summary>空占位：只占格子，不画东西。</summary>
    Gap,
}

/// <summary>虚拟键码常量（只列这里用得到的）。</summary>
public static class KeyCode
{
    public const ushort Back = 0x08;
    public const ushort Tab = 0x09;
    public const ushort Enter = 0x0D;
    public const ushort Escape = 0x1B;
    public const ushort Space = 0x20;
    public const ushort Prior = 0x21;
    public const ushort Next = 0x22;
    public const ushort End = 0x23;
    public const ushort Home = 0x24;
    public const ushort Left = 0x25;
    public const ushort Up = 0x26;
    public const ushort Right = 0x27;
    public const ushort Down = 0x28;
    public const ushort Delete = 0x2E;
    public const ushort CapsLock = 0x14;

    /// <summary>修饰键统一送**左**键码，避免和「左右键要分开」的应用打架。</summary>
    public const ushort LControl = 0xA2;
    public const ushort LShift = 0xA0;
    public const ushort LMenu = 0xA4;      // Alt
    public const ushort LWin = 0x5B;

    public const ushort D0 = 0x30, D9 = 0x39;   // 0..9
    public const ushort A = 0x41, Z = 0x5A;     // A..Z

    // OEM 键（美式布局）：标点走这些，交给当前键盘布局 / 输入法决定全角半角。
    public const ushort Oem1 = 0xBA;   // ; :
    public const ushort OemPlus = 0xBB;   // = +
    public const ushort OemComma = 0xBC;   // , <
    public const ushort OemMinus = 0xBD;   // - _
    public const ushort OemPeriod = 0xBE;   // . >
    public const ushort Oem2 = 0xBF;   // / ?
    public const ushort Oem3 = 0xC0;   // ` ~
    public const ushort Oem4 = 0xDB;   // [ {
    public const ushort Oem5 = 0xDC;   // \ |
    public const ushort Oem6 = 0xDD;   // ] }
    public const ushort Oem7 = 0xDE;   // ' "

    /// <summary>方向键 / Del 这些要带 <c>KEYEVENTF_EXTENDEDKEY</c>，否则被当成小键盘上的同键码。</summary>
    public static bool IsExtended(ushort vk) => vk is Left or Up or Right or Down or Delete
        or Prior or Next or End or Home;

    public static bool IsLetter(ushort vk) => vk is >= A and <= Z;
}

/// <summary>一个键帽。</summary>
public sealed record KeyDef
{
    public KeyRole Role { get; init; } = KeyRole.Char;

    /// <summary>常规显示的字。</summary>
    public string Label { get; init; } = "";

    /// <summary>上档显示的字（空 = 这个键没有上档）。</summary>
    public string Upper { get; init; } = "";

    /// <summary>键帽上方的小字（精简模式把数字印在字母键上）。</summary>
    public string Top { get; init; } = "";

    /// <summary>相对宽度（**整数**，和布局表声明的列数对齐）。</summary>
    public int Width { get; init; } = 1;

    /// <summary>虚拟键码。</summary>
    public ushort Vk { get; init; }

    /// <summary>这个键**本身**就是个上档符号（<c>!</c> <c>@</c> <c>{</c> …），送键时要一直按住 Shift。</summary>
    public bool Shifted { get; init; }

    /// <summary><see cref="KeyRole.Layer"/> 的目标层 id。</summary>
    public string Target { get; init; } = "";

    /// <summary>无障碍名 / 提示（空则用 <see cref="Label"/>）。</summary>
    public string Name { get; init; } = "";

    /// <summary>画在键帽上的字 —— 上档状态优先。</summary>
    public string Caption(bool shifted) =>
        shifted && Upper.Length > 0 ? Upper
        : Label.Length > 0 ? Label
        : Target;

    /// <summary>无障碍名。</summary>
    public string Accessible =>
        Name.Length > 0 ? Name
        : Label.Length > 0 ? Label
        : Target.Length > 0 ? Target
        : "空";

    // ── 工厂 ────────────────────────────────────────────────

    /// <summary>字符键。<paramref name="upper"/> 只在"按 Shift 会变字"时填。</summary>
    public static KeyDef Ch(string label, ushort vk, string upper = "", string top = "", bool shifted = false, int width = 1) =>
        new() { Role = KeyRole.Char, Label = label, Upper = upper, Top = top, Vk = vk, Shifted = shifted, Width = width };

    /// <summary>数字键（顺带把上档符号写进 <paramref name="upper"/>）。</summary>
    public static KeyDef Digit(int d, string upper, int width = 1) =>
        Ch(d.ToString(), (ushort)(KeyCode.D0 + d), upper, "", false, width);

    /// <summary>字母键。</summary>
    public static KeyDef Letter(char c, string top = "", int width = 1) =>
        Ch(c.ToString(), (ushort)(KeyCode.A + (char.ToUpperInvariant(c) - 'A')), char.ToUpperInvariant(c).ToString(), top, false, width);

    /// <summary>功能键。</summary>
    public static KeyDef K(string label, ushort vk, int width = 1, string name = "") =>
        new() { Role = KeyRole.Key, Label = label, Vk = vk, Width = width, Name = name };

    /// <summary>粘滞修饰键。</summary>
    public static KeyDef Mod(string label, ushort vk, int width = 1, string name = "") =>
        new() { Role = KeyRole.Modifier, Label = label, Vk = vk, Width = width, Name = name };

    public static KeyDef ShiftKey(int width = 2) =>
        new() { Role = KeyRole.Shift, Label = "\u21E7", Width = width, Name = "上档 Shift" };

    public static KeyDef CapsKey(int width = 2) =>
        new() { Role = KeyRole.Caps, Label = "Caps", Width = width, Name = "大写锁定" };

    /// <summary>切层键。</summary>
    public static KeyDef Go(string label, string target, int width = 2, string name = "") =>
        new() { Role = KeyRole.Layer, Label = label, Target = target, Width = width, Name = name };

    public static KeyDef Hide(int width = 1) =>
        new() { Role = KeyRole.Hide, Label = "\u25BE", Width = width, Name = "收起键盘" };

    public static KeyDef Gap(int width = 1) => new() { Role = KeyRole.Gap, Width = width };
}

/// <summary>一套布局：若干行，每行若干键。**每行 <see cref="KeyDef.Width"/> 合计必须等于 <see cref="Columns"/>。**</summary>
public sealed record KeyboardLayout(string Id, string Name, int Columns, IReadOnlyList<KeyDef[]> Rows)
{
    /// <summary>行数（键区高度按它算）。</summary>
    public int RowCount => Rows.Count;
}

/// <summary>
/// 内置布局表（纯静态数据）。
///
/// **两套模式**（对应设置里的「键盘布局」）：
///   · <c>full</c>    完整 —— 5 行 × 15 格，功能键齐全（Esc / Tab / Caps / 双 Shift / Ctrl / ⊞ / Alt / 方向键）
///   · <c>compact</c> 精简 —— 4 行 × 12 格，数字并到字母键上当键帽上方小字
/// ⚠️ **默认必须是完整**（Nick 2026-09-30 明确：「我不是教你使用系统的布局吗」）。
///
/// **三个层**（对应键盘上的切换键）：<c>letters</c> / <c>symbols</c> / <c>numpad</c>。
/// </summary>
public static class KeyboardLayouts
{
    public const string ModeFull = "full";
    public const string ModeCompact = "compact";

    public const string Letters = "letters";
    public const string Symbols = "symbols";
    public const string Numpad = "numpad";

    /// <summary>「键盘布局」下拉里的项（**完整在前**）。</summary>
    public static readonly IReadOnlyList<string> Modes = [ModeFull, ModeCompact];

    public static string ModeName(string? id) => id == ModeCompact ? "精简" : "完整";

    /// <summary>认不出的模式一律落回**完整** —— 默认给精简是踩过的坑。</summary>
    public static string NormalizeMode(string? id) => id == ModeCompact ? ModeCompact : ModeFull;

    public static string LayerName(string? id) => id switch
    {
        Symbols => "符号",
        Numpad => "数字",
        _ => "字母",
    };

    /// <summary>取一套布局。层认不出就当字母层。</summary>
    public static KeyboardLayout Get(string? mode, string? layer)
    {
        var compact = NormalizeMode(mode) == ModeCompact;

        return layer switch
        {
            Symbols => compact ? SymbolsCompact() : SymbolsFull(),
            Numpad => compact ? NumpadCompact() : NumpadFull(),
            _ => compact ? LettersCompact() : LettersFull(),
        };
    }

    /// <summary>
    /// **自检：每行宽度合计必须等于 Columns。** 空列表 = 全部对。
    ///
    /// 为什么要它：网格是按 <see cref="KeyboardLayout.Columns"/> 建固定列数的，
    /// 某一行合计少一格 → 末尾**静默**留空一条；多一格 → 最后一个键**静默**挤到下一列，
    /// 整行错位。以前只能靠人工把每行加一遍才知道对不对（改布局时踩过）。
    /// </summary>
    public static IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        foreach (var mode in Modes)
        {
            foreach (var layer in new[] { Letters, Symbols, Numpad })
            {
                var layout = Get(mode, layer);
                var name = $"{ModeName(mode)}·{LayerName(layer)}";

                for (var i = 0; i < layout.Rows.Count; i++)
                {
                    var sum = 0;
                    foreach (var key in layout.Rows[i]) sum += key.Width;

                    if (sum != layout.Columns)
                    {
                        problems.Add($"{name} 第 {i + 1} 行宽度合计 {sum} ≠ {layout.Columns}");
                    }
                }
            }
        }

        return problems;
    }

    // ── 底部功能行（两个模式各一条，切层时复用）──────────────

    // ⚠️ 方向键**四颗都要有**：上一版漏了「上」，光标只能往下挪不能往上挪 ——
    //    这是"完整"布局的硬缺口，不是可选装饰。空格让出 1 格（6→5）给它。
    private static KeyDef[] BottomFull() =>
    [
        KeyDef.Go("&123", Symbols, 2),
        KeyDef.Mod("Ctrl", KeyCode.LControl),
        KeyDef.Mod("\u229E", KeyCode.LWin, 1, "Win"),
        KeyDef.Mod("Alt", KeyCode.LMenu),
        KeyDef.K("空格", KeyCode.Space, 5, "空格"),
        KeyDef.K("\u2190", KeyCode.Left, 1, "左"),
        KeyDef.K("\u2191", KeyCode.Up, 1, "上"),
        KeyDef.K("\u2193", KeyCode.Down, 1, "下"),
        KeyDef.K("\u2192", KeyCode.Right, 1, "右"),
        KeyDef.Hide(),
    ];

    private static KeyDef[] BottomCompact() =>
    [
        KeyDef.Go("&123", Symbols, 2),
        KeyDef.K("空格", KeyCode.Space, 5, "空格"),
        KeyDef.K("\u2190", KeyCode.Left, 1, "左"),
        KeyDef.K("\u2191", KeyCode.Up, 1, "上"),
        KeyDef.K("\u2193", KeyCode.Down, 1, "下"),
        KeyDef.K("\u2192", KeyCode.Right, 1, "右"),
        KeyDef.Hide(),
    ];

    // ── 字母层 ──────────────────────────────────────────────

    private static KeyboardLayout LettersFull() => new(Letters, "字母", 15,
    [
        // 1.2 改成整数 1 —— 网格是整数的，半格会错位
        [ KeyDef.K("Esc", KeyCode.Escape, 1, "Esc"),
          KeyDef.Digit(1, "!"), KeyDef.Digit(2, "@"), KeyDef.Digit(3, "#"), KeyDef.Digit(4, "$"),
          KeyDef.Digit(5, "%"), KeyDef.Digit(6, "^"), KeyDef.Digit(7, "&"), KeyDef.Digit(8, "*"),
          KeyDef.Digit(9, "("), KeyDef.Digit(0, ")"),
          KeyDef.Ch("-", KeyCode.OemMinus, "_"), KeyDef.Ch("=", KeyCode.OemPlus, "+"),
          KeyDef.K("\u232B", KeyCode.Back, 2, "退格") ],

        [ KeyDef.K("Tab", KeyCode.Tab, 2, "Tab"),
          KeyDef.Letter('q'), KeyDef.Letter('w'), KeyDef.Letter('e'), KeyDef.Letter('r'), KeyDef.Letter('t'),
          KeyDef.Letter('y'), KeyDef.Letter('u'), KeyDef.Letter('i'), KeyDef.Letter('o'), KeyDef.Letter('p'),
          KeyDef.Ch("[", KeyCode.Oem4, "{"), KeyDef.Ch("]", KeyCode.Oem6, "}"), KeyDef.Ch("\\", KeyCode.Oem5, "|") ],

        [ KeyDef.CapsKey(2),
          KeyDef.Letter('a'), KeyDef.Letter('s'), KeyDef.Letter('d'), KeyDef.Letter('f'), KeyDef.Letter('g'),
          KeyDef.Letter('h'), KeyDef.Letter('j'), KeyDef.Letter('k'), KeyDef.Letter('l'),
          KeyDef.Ch(";", KeyCode.Oem1, ":"), KeyDef.Ch("'", KeyCode.Oem7, "\""),
          KeyDef.K("\u21B5", KeyCode.Enter, 2, "回车") ],

        [ KeyDef.ShiftKey(2),
          KeyDef.Letter('z'), KeyDef.Letter('x'), KeyDef.Letter('c'), KeyDef.Letter('v'), KeyDef.Letter('b'),
          KeyDef.Letter('n'), KeyDef.Letter('m'),
          KeyDef.Ch(",", KeyCode.OemComma, "<"), KeyDef.Ch(".", KeyCode.OemPeriod, ">"), KeyDef.Ch("/", KeyCode.Oem2, "?"),
          KeyDef.ShiftKey(3) ],

        BottomFull(),
    ]);

    private static KeyboardLayout LettersCompact() => new(Letters, "字母", 12,
    [
        [ KeyDef.Letter('q', "1"), KeyDef.Letter('w', "2"), KeyDef.Letter('e', "3"), KeyDef.Letter('r', "4"),
          KeyDef.Letter('t', "5"), KeyDef.Letter('y', "6"), KeyDef.Letter('u', "7"), KeyDef.Letter('i', "8"),
          KeyDef.Letter('o', "9"), KeyDef.Letter('p', "0"),
          KeyDef.K("\u232B", KeyCode.Back, 2, "退格") ],

        [ KeyDef.K("Tab", KeyCode.Tab, 2, "Tab"),
          KeyDef.Letter('a'), KeyDef.Letter('s'), KeyDef.Letter('d'), KeyDef.Letter('f'), KeyDef.Letter('g'),
          KeyDef.Letter('h'), KeyDef.Letter('j'), KeyDef.Letter('k'), KeyDef.Letter('l'),
          KeyDef.K("\u21B5", KeyCode.Enter, 1, "回车") ],

        [ KeyDef.ShiftKey(2),
          KeyDef.Letter('z'), KeyDef.Letter('x'), KeyDef.Letter('c'), KeyDef.Letter('v'), KeyDef.Letter('b'),
          KeyDef.Letter('n'), KeyDef.Letter('m'),
          KeyDef.Ch(",", KeyCode.OemComma, "<"), KeyDef.Ch(".", KeyCode.OemPeriod, ">"),
          KeyDef.ShiftKey(1) ],

        BottomCompact(),
    ]);

    // ── 符号层 ──────────────────────────────────────────────
    //
    // ⚠️ 这一层曾经**第 2/3/4 行各多一颗键**（合计 16 ≠ 15），网格是固定 15 列的，
    //    多出来的那颗会静默挤到下一列、整行错位 —— 肉眼在 15 列里根本看不出来，
    //    是 `KeyboardLayouts.Validate()` 的自检日志把它揪出来的（2026-09-30）。
    //    ⛔ 以后往这里加键，先去掉等量的重复键；符号在相邻行重复出现是刻意的，
    //       挑"别处已经有"的那颗删最安全。

    private static KeyboardLayout SymbolsFull() => new(Symbols, "符号", 15,
    [
        [ KeyDef.K("Esc", KeyCode.Escape, 1, "Esc"),
          KeyDef.Ch("!", KeyCode.D0 + 1, shifted: true), KeyDef.Ch("@", KeyCode.D0 + 2, shifted: true),
          KeyDef.Ch("#", KeyCode.D0 + 3, shifted: true), KeyDef.Ch("$", KeyCode.D0 + 4, shifted: true),
          KeyDef.Ch("%", KeyCode.D0 + 5, shifted: true), KeyDef.Ch("^", KeyCode.D0 + 6, shifted: true),
          KeyDef.Ch("&", KeyCode.D0 + 7, shifted: true), KeyDef.Ch("*", KeyCode.D0 + 8, shifted: true),
          KeyDef.Ch("(", KeyCode.D0 + 9, shifted: true), KeyDef.Ch(")", KeyCode.D0 + 0, shifted: true),
          KeyDef.Ch("-", KeyCode.OemMinus), KeyDef.Ch("+", KeyCode.OemPlus, shifted: true),
          KeyDef.K("\u232B", KeyCode.Back, 2, "退格") ],

        [ KeyDef.K("Tab", KeyCode.Tab, 2, "Tab"),
          KeyDef.Ch("[", KeyCode.Oem4), KeyDef.Ch("]", KeyCode.Oem6),
          KeyDef.Ch("{", KeyCode.Oem4, shifted: true), KeyDef.Ch("}", KeyCode.Oem6, shifted: true),
          KeyDef.Ch("<", KeyCode.OemComma, shifted: true), KeyDef.Ch(">", KeyCode.OemPeriod, shifted: true),
          KeyDef.Ch("/", KeyCode.Oem2), KeyDef.Ch("\\", KeyCode.Oem5), KeyDef.Ch("|", KeyCode.Oem5, shifted: true),
          KeyDef.Ch("~", KeyCode.Oem3, shifted: true), KeyDef.Ch("`", KeyCode.Oem3),
          KeyDef.Ch("=", KeyCode.OemPlus),
          KeyDef.K("\u21B5", KeyCode.Enter, 1, "回车") ],

        [ KeyDef.Go("ABC", Letters, 2),
          KeyDef.Ch(";", KeyCode.Oem1), KeyDef.Ch(":", KeyCode.Oem1, shifted: true),
          KeyDef.Ch("'", KeyCode.Oem7), KeyDef.Ch("\"", KeyCode.Oem7, shifted: true),
          KeyDef.Ch(",", KeyCode.OemComma), KeyDef.Ch(".", KeyCode.OemPeriod),
          KeyDef.Ch("?", KeyCode.Oem2, shifted: true),
          KeyDef.Ch("_", KeyCode.OemMinus, shifted: true), KeyDef.Ch("&", KeyCode.D0 + 7, shifted: true),
          KeyDef.Ch("*", KeyCode.D0 + 8, shifted: true), KeyDef.Ch("%", KeyCode.D0 + 5, shifted: true),
          KeyDef.Ch("$", KeyCode.D0 + 4, shifted: true), KeyDef.Ch("#", KeyCode.D0 + 3, shifted: true) ],

        [ KeyDef.Go("123", Numpad, 2),
          KeyDef.Ch("^", KeyCode.D0 + 6, shifted: true), KeyDef.Ch("(", KeyCode.D0 + 9, shifted: true),
          KeyDef.Ch(")", KeyCode.D0 + 0, shifted: true), KeyDef.Ch("{", KeyCode.Oem4, shifted: true),
          KeyDef.Ch("}", KeyCode.Oem6, shifted: true), KeyDef.Ch("<", KeyCode.OemComma, shifted: true),
          KeyDef.Ch(">", KeyCode.OemPeriod, shifted: true), KeyDef.Ch("[", KeyCode.Oem4),
          KeyDef.Ch("]", KeyCode.Oem6), KeyDef.Ch("=", KeyCode.OemPlus),
          KeyDef.Ch("+", KeyCode.OemPlus, shifted: true),
          KeyDef.Ch("|", KeyCode.Oem5, shifted: true), KeyDef.Ch("\\", KeyCode.Oem5) ],

        BottomFull(),
    ]);

    private static KeyboardLayout SymbolsCompact() => new(Symbols, "符号", 12,
    [
        [ KeyDef.Ch("!", KeyCode.D0 + 1, shifted: true), KeyDef.Ch("@", KeyCode.D0 + 2, shifted: true),
          KeyDef.Ch("#", KeyCode.D0 + 3, shifted: true), KeyDef.Ch("$", KeyCode.D0 + 4, shifted: true),
          KeyDef.Ch("%", KeyCode.D0 + 5, shifted: true), KeyDef.Ch("^", KeyCode.D0 + 6, shifted: true),
          KeyDef.Ch("&", KeyCode.D0 + 7, shifted: true), KeyDef.Ch("*", KeyCode.D0 + 8, shifted: true),
          KeyDef.Ch("(", KeyCode.D0 + 9, shifted: true), KeyDef.Ch(")", KeyCode.D0 + 0, shifted: true),
          KeyDef.K("\u232B", KeyCode.Back, 2, "退格") ],

        [ KeyDef.Ch("[", KeyCode.Oem4), KeyDef.Ch("]", KeyCode.Oem6),
          KeyDef.Ch("{", KeyCode.Oem4, shifted: true), KeyDef.Ch("}", KeyCode.Oem6, shifted: true),
          KeyDef.Ch("<", KeyCode.OemComma, shifted: true), KeyDef.Ch(">", KeyCode.OemPeriod, shifted: true),
          KeyDef.Ch("/", KeyCode.Oem2), KeyDef.Ch("\\", KeyCode.Oem5), KeyDef.Ch("|", KeyCode.Oem5, shifted: true),
          KeyDef.Ch("~", KeyCode.Oem3, shifted: true),
          KeyDef.K("\u21B5", KeyCode.Enter, 2, "回车") ],

        [ KeyDef.Go("ABC", Letters, 2),
          KeyDef.Ch(";", KeyCode.Oem1), KeyDef.Ch(":", KeyCode.Oem1, shifted: true),
          KeyDef.Ch("'", KeyCode.Oem7), KeyDef.Ch("\"", KeyCode.Oem7, shifted: true),
          KeyDef.Ch(",", KeyCode.OemComma), KeyDef.Ch(".", KeyCode.OemPeriod),
          KeyDef.Ch("?", KeyCode.Oem2, shifted: true), KeyDef.Ch("_", KeyCode.OemMinus, shifted: true),
          KeyDef.K("空格", KeyCode.Space, 2, "空格") ],

        [ KeyDef.Go("123", Numpad, 1),
          KeyDef.Mod("Ctrl", KeyCode.LControl), KeyDef.Mod("\u229E", KeyCode.LWin, 1, "Win"), KeyDef.Mod("Alt", KeyCode.LMenu),
          KeyDef.K("空格", KeyCode.Space, 3, "空格"),
          KeyDef.K("\u2190", KeyCode.Left, 1, "左"), KeyDef.K("\u2191", KeyCode.Up, 1, "上"),
          KeyDef.K("\u2193", KeyCode.Down, 1, "下"),
          KeyDef.K("\u2192", KeyCode.Right, 1, "右"), KeyDef.Hide() ],
    ]);

    // ── 数字层 ──────────────────────────────────────────────

    private static KeyboardLayout NumpadFull() => new(Numpad, "数字", 15,
    [
        [ KeyDef.Digit(7, "&", 3), KeyDef.Digit(8, "*", 3), KeyDef.Digit(9, "(", 3),
          KeyDef.Ch("/", KeyCode.Oem2, width: 3), KeyDef.K("\u232B", KeyCode.Back, 3, "退格") ],

        [ KeyDef.Digit(4, "$", 3), KeyDef.Digit(5, "%", 3), KeyDef.Digit(6, "^", 3),
          KeyDef.Ch("*", KeyCode.D0 + 8, shifted: true, width: 3), KeyDef.K("Esc", KeyCode.Escape, 3, "Esc") ],

        [ KeyDef.Digit(1, "!", 3), KeyDef.Digit(2, "@", 3), KeyDef.Digit(3, "#", 3),
          KeyDef.Ch("-", KeyCode.OemMinus, width: 3), KeyDef.K("Tab", KeyCode.Tab, 3, "Tab") ],

        [ KeyDef.Digit(0, ")", 6), KeyDef.Ch(".", KeyCode.OemPeriod, width: 3),
          KeyDef.Ch("+", KeyCode.OemPlus, shifted: true, width: 3), KeyDef.K("\u21B5", KeyCode.Enter, 3, "回车") ],

        [ KeyDef.Go("ABC", Letters, 4), KeyDef.Mod("Ctrl", KeyCode.LControl, 2), KeyDef.Mod("Alt", KeyCode.LMenu, 2),
          KeyDef.K("空格", KeyCode.Space, 4, "空格"), KeyDef.Hide(3) ],
    ]);

    private static KeyboardLayout NumpadCompact() => new(Numpad, "数字", 12,
    [
        [ KeyDef.Digit(7, "&", 3), KeyDef.Digit(8, "*", 3), KeyDef.Digit(9, "(", 3),
          KeyDef.K("\u232B", KeyCode.Back, 3, "退格") ],

        [ KeyDef.Digit(4, "$", 3), KeyDef.Digit(5, "%", 3), KeyDef.Digit(6, "^", 3),
          KeyDef.Ch("/", KeyCode.Oem2, width: 3) ],

        [ KeyDef.Digit(1, "!", 3), KeyDef.Digit(2, "@", 3), KeyDef.Digit(3, "#", 3),
          KeyDef.Ch("*", KeyCode.D0 + 8, shifted: true, width: 3) ],

        [ KeyDef.Go("ABC", Letters, 2), KeyDef.Digit(0, ")", 2),
          KeyDef.Ch(".", KeyCode.OemPeriod, width: 2), KeyDef.Ch("-", KeyCode.OemMinus, width: 2),
          KeyDef.Ch("+", KeyCode.OemPlus, shifted: true, width: 2), KeyDef.K("\u21B5", KeyCode.Enter, 2, "回车") ],
    ]);
}
