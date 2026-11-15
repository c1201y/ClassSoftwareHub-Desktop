using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 系统功能的命令生成与结果核对（2026-10-31）。
///
/// <para>
/// 抽在这里的原因和 <c>TweakRules</c> 一样：这一段**不碰注册表、不碰进程**，
/// 于是「命令拼错一个字」这种最难查的故障能被单元测试钉住 ——
/// 拼错的后果是用户点完 UAC 才发现没生效，现场排查极慢。
/// </para>
/// </summary>
public static class SysFeatureRules
{
    /// <summary><c>reg add</c> 一条 DWORD。路径含空格，引号必须包在键名外层。</summary>
    public static string RegAdd(string path, string name, int value) =>
        $"reg add \"HKLM\\{path}\" /v {name} /t REG_DWORD /d {value} /f";

    /// <summary>
    /// 改电源设置的两条命令。
    /// <para>
    /// ⚠️ <c>setacvalueindex</c> 只把值写进注册表，**新值不会应用到当前方案** ——
    /// 必须再跑一次 <c>setactive</c> 才真正生效。这两条永远成对出现。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> PowerSettingCommands(string subGroup, string setting, int value) => new[]
    {
        $"powercfg /setacvalueindex SCHEME_CURRENT {subGroup} {setting} {value}",
        "powercfg /setactive SCHEME_CURRENT",
    };

    /// <summary>
    /// 把一批改动拼成一次性执行的 cmd 脚本。
    ///
    /// <para>
    /// ⚠️ 内容**必须全是 ASCII**：cmd 的代码页跟系统语言走，脚本里写中文会让 GBK/UTF-8 打架。
    /// 实际的命令只含盘符、GUID、数字与注册表路径（英文），不会踩到 —— 这条约束是给以后加项的人看的。
    /// </para>
    /// <para>
    /// ⛔ 不在脚本里打"成功"字样：成败由调用方**重读注册表**判定，
    /// 黑窗上闪过的 echo 既看不见也不可信。
    /// </para>
    /// </summary>
    public static string BuildScript(IReadOnlyList<(SysFeatureDef Def, int Value)> changes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 437 >nul");

        foreach (var (def, value) in changes)
            foreach (var line in def.Command(value))
                sb.AppendLine(line);

        return sb.ToString();
    }

    /// <summary>
    /// 整批跑完后，哪些项**没**变成目标值。
    /// <para>
    /// 判据是「重读当前值 == 目标值」，不是命令返回值 —— <c>runas</c> 拿不到可靠 ExitCode，
    /// 而且用户点「否」与命令执行失败在代码里长得一样（<c>FirewallGateway</c> 吃过这个亏）。
    /// </para>
    /// </summary>
    public static List<string> FailedAfterApply(IReadOnlyList<(SysFeatureDef Def, int Value)> changes) =>
        changes.Where(c => c.Def.Read() != c.Value).Select(c => c.Def.Id).ToList();
}
