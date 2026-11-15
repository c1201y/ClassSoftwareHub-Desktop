using System.Collections.Generic;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Services;


public sealed record Element(int Number, string Symbol, int Row, int Column)
{
    
    public bool IsLanthanide => Row == 8;
    public bool IsActinide => Row == 9;
}









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

    
    public static bool IsElement(string symbol) => SymbolSet.Contains(symbol);

    
    
    
    
    public static readonly IReadOnlyList<(string Symbol, string Name)> Common = new List<(string, string)>
    {
        ("H", "氢"), ("He", "氦"), ("C", "碳"), ("N", "氮"), ("O", "氧"), ("F", "氟"), ("Ne", "氖"),
        ("Na", "钠"), ("Mg", "镁"), ("Al", "铝"), ("Si", "硅"), ("P", "磷"), ("S", "硫"), ("Cl", "氯"),
        ("Ar", "氩"), ("K", "钾"), ("Ca", "钙"), ("Mn", "锰"), ("Fe", "铁"), ("Cu", "铜"), ("Zn", "锌"),
        ("Br", "溴"), ("Ag", "银"), ("I", "碘"), ("Ba", "钡"), ("Pb", "铅"),
        ("Hg", "汞"), ("Sn", "锡"),
    };

    
    
    
    
    
    
    
    
    
    
    
    
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

    
    public static string ChineseName(int number) =>
        number >= 1 && number <= NameList.Length ? NameList[number - 1] : "";

    
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

        3 or 4 => (2, z - 2),                 
        >= 5 and <= 10 => (2, z + 8),         
        11 or 12 => (3, z - 10),              
        >= 13 and <= 18 => (3, z),            

        >= 19 and <= 36 => (4, z - 18),       
        >= 37 and <= 54 => (5, z - 36),       

        55 => (6, 1),
        56 => (6, 2),
        >= 57 and <= 71 => (8, z - 54),       
        >= 72 and <= 86 => (6, z - 68),       

        87 => (7, 1),
        88 => (7, 2),
        >= 89 and <= 103 => (9, z - 86),      
        >= 104 and <= 118 => (7, z - 100),    

        _ => (0, 0),
    };
}
