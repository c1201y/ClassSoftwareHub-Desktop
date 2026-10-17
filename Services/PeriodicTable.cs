using System.Collections.Generic;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>一个元素：原子序数、符号、在周期表里的行列（1 起）。</summary>
public sealed record Element(int Number, string Symbol, int Row, int Column)
{
    /// <summary>镧系 / 锕系单独排两行（第 8、9 行），表里用它们占住下方空位。</summary>
    public bool IsLanthanide => Row == 8;
    public bool IsActinide => Row == 9;
}

/// <summary>
/// 元素周期表（1~118 号）。
///
/// <para>
/// 位置不用手摆：按原子序数就能算出族和周期，只有镧系 / 锕系要单独归到第 8、9 行。
/// 手摆 118 个格子既容易错位，也难看懂为什么错。
/// </para>
/// </summary>
public static class PeriodicTable
{
    private const string Symbols =
        "H He Li Be B C N O F Ne Na Mg Al Si P S Cl Ar K Ca Sc Ti V Cr Mn Fe Co Ni Cu Zn " +
        "Ga Ge As Se Br Kr Rb Sr Y Zr Nb Mo Tc Ru Rh Pd Ag Cd In Sn Sb Te I Xe Cs Ba La Ce Pr Nd Pm Sm Eu Gd Tb Dy Ho Er Tm Yb Lu " +
        "Hf Ta W Re Os Ir Pt Au Hg Tl Pb Bi Po At Rn Fr Ra Ac Th Pa U Np Pu Am Cm Bk Cf Es Fm Md No Lr " +
        "Rf Db Sg Bh Hs Mt Ds Rg Cn Nh Fl Mc Lv Ts Og";

    public static IReadOnlyList<Element> All { get; } = Build();

    private static readonly HashSet<string> SymbolSet =
        new(All.Select(e => e.Symbol), System.StringComparer.Ordinal);

    /// <summary>这个符号是不是真实元素（"Cx" 不是）。编辑器与配平共用它判合法性。</summary>
    public static bool IsElement(string symbol) => SymbolSet.Contains(symbol);

    /// <summary>
    /// 课堂上真正会用到的那三十来个元素（符号 + 中文名）。
    /// ⛔ 118 个全摆出来对中学生是噪音：他们配平就那几个元素来回用。
    /// </summary>
    public static readonly IReadOnlyList<(string Symbol, string Name)> Common = new List<(string, string)>
    {
        ("H", "氢"), ("He", "氦"), ("C", "碳"), ("N", "氮"), ("O", "氧"), ("F", "氟"), ("Ne", "氖"),
        ("Na", "钠"), ("Mg", "镁"), ("Al", "铝"), ("Si", "硅"), ("P", "磷"), ("S", "硫"), ("Cl", "氯"),
        ("Ar", "氩"), ("K", "钾"), ("Ca", "钙"), ("Mn", "锰"), ("Fe", "铁"), ("Cu", "铜"), ("Zn", "锌"),
        ("Br", "溴"), ("Ag", "银"), ("I", "碘"), ("Ba", "钡"), ("Pb", "铅"),
        ("Hg", "汞"), ("Sn", "锡"),
    };

    /// <summary>
    /// 1~103 号元素的中文名，下标 = 原子序数 - 1。
    ///
    /// <para>
    /// ⛔ 完整周期表上只写符号是不行的（Nick 2026-10-17）：<c>Sn</c>、<c>Sb</c>、<c>Y</c>
    /// 这些符号连化学老师都要想一想，学生在表里找一个元素却只看到符号，等于没给线索。
    /// </para>
    /// <para>
    /// 104 号之后（钅卢、钅杜…）的汉字在 CJK 扩展区，系统字体大多没有字形，
    /// 写上去就是豆腐块，所以留空——课堂上也不会用到。
    /// </para>
    /// </summary>
    private const string Names =
        "氢 氦 锂 铍 硼 碳 氮 氧 氟 氖 钠 镁 铝 硅 磷 硫 氯 氩 钾 钙 " +
        "钪 钛 钒 铬 锰 铁 钴 镍 铜 锌 镓 锗 砷 硒 溴 氪 铷 锶 钇 锆 " +
        "铌 钼 锝 钌 铑 钯 银 镉 铟 锡 锑 碲 碘 氙 铯 钡 镧 铈 镨 钕 " +
        "钷 钐 铕 钆 铽 镝 钬 铒 铥 镱 镥 铪 钽 钨 铼 锇 铱 铂 金 汞 " +
        "铊 铅 铋 钋 砹 氡 钫 镭 锕 钍 镤 铀 镎 钚 镅 锔 锫 锎 锿 镄 " +
        "钔 锘 铹";

    private static readonly string[] NameList = Names.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);

    private static readonly Dictionary<string, string> NameBySymbol = BuildNameMap();

    private static Dictionary<string, string> BuildNameMap()
    {
        var map = new Dictionary<string, string>(System.StringComparer.Ordinal);
        foreach (var element in All)
        {
            var name = ChineseName(element.Number);
            if (name.Length > 0) map[element.Symbol] = name;
        }
        return map;
    }

    /// <summary>按原子序数取中文名（超出已知范围返回空串）。</summary>
    public static string ChineseName(int number) =>
        number >= 1 && number <= NameList.Length ? NameList[number - 1] : "";

    /// <summary>取中文名（没有就返回空串）。</summary>
    public static string ChineseName(string symbol) =>
        NameBySymbol.TryGetValue(symbol, out var name) ? name : "";

    private static List<Element> Build()
    {
        var symbols = Symbols.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        var list = new List<Element>(symbols.Length);

        for (var i = 0; i < symbols.Length; i++)
        {
            var number = i + 1;
            var (row, column) = Position(number);
            list.Add(new Element(number, symbols[i], row, column));
        }

        return list;
    }

    private static (int Row, int Column) Position(int z) => z switch
    {
        1 => (1, 1),
        2 => (1, 18),

        3 or 4 => (2, z - 2),                 // Li Be
        >= 5 and <= 10 => (2, z + 8),         // B … Ne（第 13~18 族）
        11 or 12 => (3, z - 10),              // Na Mg
        >= 13 and <= 18 => (3, z),            // Al … Ar

        >= 19 and <= 36 => (4, z - 18),       // K … Kr，整段 18 个
        >= 37 and <= 54 => (5, z - 36),       // Rb … Xe

        55 => (6, 1),
        56 => (6, 2),
        >= 57 and <= 71 => (8, z - 54),       // 镧系 La…Lu → 第 8 行第 3~17 列
        >= 72 and <= 86 => (6, z - 68),       // Hf … Rn

        87 => (7, 1),
        88 => (7, 2),
        >= 89 and <= 103 => (9, z - 86),      // 锕系 Ac…Lr
        >= 104 and <= 118 => (7, z - 100),    // Rf … Og

        _ => (0, 0),
    };
}
