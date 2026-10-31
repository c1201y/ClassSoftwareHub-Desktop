using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 系统优化项的**纯逻辑**：值的解释、<c>.reg</c> 文本的生成（2026-10-31 抽出）。
///
/// <para>
/// 为什么单独拆一个文件：这两段原来长在 <c>Services/TweakStore.cs</c> 里，而那个类要碰注册表与文件，
/// 于是「类型宽松解析」「.reg 分段合并」「负值写成 8 位补码」这些最容易写错、又最不容易被手测发现的东西
/// 一条都测不了。抽到这里之后它不依赖 <c>Microsoft.Win32</c>、不碰文件，测试工程可以直接链接。
/// </para>
/// <para>
/// ⛔ 这里**不做**任何读写：调用方拿到文本自己去写盘、自己决定用什么编码。
/// </para>
/// </summary>
public static class TweakRules
{
    /// <summary>
    /// 把从注册表读到的原始对象解释成开关值；解释不出来返回 <c>null</c>。
    ///
    /// <para>
    /// ⚠️ 为什么要宽松：DWORD 正常读出来是 <see cref="int"/>，但用 <c>.reg</c> 导入的、或者被旧工具写过的项
    /// 可能留成 <see cref="long"/> 甚至字符串。以前只认 <c>int</c>，结果是"系统明明是深色模式，
    /// 这一项却显示为关"（2026-10-31 Nick 报的问题之一）—— 严格解析在这里只会制造假阴性。
    /// </para>
    /// <para>
    /// ⚠️ 仍然**不认** <c>bool</c> 与 <c>byte[]</c>：前者说明键里存的是别的类型（值名撞车），
    /// 后者是二进制项，按开处理都是猜。认不出就返回 <c>null</c>，由调用方记日志并按关处理。
    /// </para>
    /// </summary>
    public static int? ParseDword(object? raw) => raw switch
    {
        int i => i,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        string s when int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            => parsed,
        _ => null,
    };

    /// <summary>
    /// 这一项在当前系统上是否适用（照 Dism++ 的 <c>&lt;Applicable&gt;&lt;OSVersion&gt;</c> 口径）。
    ///
    /// <para>
    /// ⚠️ <paramref name="build"/> 为 0（读不到系统版本）时**不做门控、全部显示**：
    /// 藏错方向的代价不对等 —— 在真正的 Win11 上误藏两个功能，用户根本不会知道它们存在；
    /// 而在 Win10 上多显示一个拨了没反应的开关，只是难看。所以读不到就宁可不筛。
    /// </para>
    /// </summary>
    public static bool IsApplicable(TweakDef def, int build)
    {
        if (build <= 0) return true;

        if (def.MinBuild > 0 && build < def.MinBuild) return false;
        if (def.MaxBuild > 0 && build > def.MaxBuild) return false;
        return true;
    }

    /// <summary>
    /// 生成 <c>.reg</c> 文本（不含 BOM —— 编码由调用方决定，<c>reg.exe</c> 要求 UTF-16LE 带 BOM）。
    ///
    /// <para>
    /// 同一个键下的多个值合并成一个段：重复的段头虽然合法，但会让"这次到底改了哪几个键"变得难读，
    /// 出问题时人工核对 <c>.reg</c> 是很常见的动作。
    /// </para>
    /// <para>
    /// ⚠️ 负值必须写成 8 位补码（<c>{value:x8}</c>）：<c>-1</c> 直接写进去是 <c>dword:-1</c>，
    /// <c>reg.exe</c> 会当成语法错误的十六进制数。
    /// </para>
    /// </summary>
    public static string BuildRegText(IReadOnlyList<(TweakDef Def, int Value)> changes)
    {
        var text = new StringBuilder();
        text.AppendLine("Windows Registry Editor Version 5.00");
        text.AppendLine();

        foreach (var section in changes.GroupBy(c => (c.Def.RootName, c.Def.KeyPath)))
        {
            text.AppendLine($"[{section.Key.RootName}\\{section.Key.KeyPath}]");

            foreach (var (def, value) in section)
            {
                var name = EscapeValueName(def.ValueName);

                // 关掉时要删值的项：.reg 里的语法是 "值名"=-（等号后面一个减号）
                if (def.DeleteWhenOff && value == def.OffValue)
                {
                    text.AppendLine($"\"{name}\"=-");
                    continue;
                }

                text.AppendLine($"\"{name}\"=dword:{value:x8}");
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>
    /// .reg 里的值名转义。键路径**不转义**（反斜杠在那里是层级分隔符），值名要转。
    /// 目前的项都是写死的常量、不会含这两个字符，但生成器一旦被别处复用就会出事，
    /// 所以这里按格式要求一次性处理干净。
    /// </summary>
    private static string EscapeValueName(string name) =>
        name.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>
    /// 这批改动需要发哪几种外壳通知（去重、去 <see cref="TweakRefresh.None"/>）。
    ///
    /// <para>
    /// 顺序固定为 Tray → Desktop：任务栏先刷，桌面图标后刷，和 Win32 里两者刷新的先后关系一致，
    /// 也避免"桌面先刷完、任务栏后刷又把它顶回去"这类观感问题。
    /// </para>
    /// <para>
    /// ⚠️ 单独抽出来是为了能测：通知本身没法在单测里验证（要真的发消息给外壳），
    /// 但"该发哪几条、会不会重复发"是纯逻辑。
    /// </para>
    /// </summary>
    public static List<TweakRefresh> RefreshesFor(IReadOnlyList<(TweakDef Def, int Value)> changes)
    {
        var result = new List<TweakRefresh>();

        if (changes.Any(c => c.Def.Refresh == TweakRefresh.Tray)) result.Add(TweakRefresh.Tray);
        if (changes.Any(c => c.Def.Refresh == TweakRefresh.Desktop)) result.Add(TweakRefresh.Desktop);

        return result;
    }

    /// <summary>
    /// 「应用更改」之后，待应用队列里**应该剩下谁**：只留没生效的那几项，其余全部出队。
    ///
    /// <para>
    /// ⚠️ 这条规则原来写在页面里、是无条件清空 —— 结果「10 项里 3 项失败」之后，用户既不知道是哪 3 项，
    /// 也没有办法只重试它们（2026-10-31 自检发现）。抽出来单独测，是因为它是这条链路上
    /// **唯一能被自动验证**的一环：注册表真的写没写进去只能真机跑，但"队列该怎么收"是纯逻辑。
    /// </para>
    /// </summary>
    /// <param name="changes">这次提交的改动。</param>
    /// <param name="failed">改完之后重读、仍与目标不一致的项 Id。</param>
    public static Dictionary<string, int> RemainingAfterApply(
        IReadOnlyList<(TweakDef Def, int Value)> changes,
        IReadOnlyList<string> failed)
    {
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (def, value) in changes)
            if (failed.Contains(def.Id))
                remaining[def.Id] = value;   // 保留**原来想改成的那个值**，不是反向

        return remaining;
    }
}
