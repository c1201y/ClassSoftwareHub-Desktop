using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>局域网通道的防火墙状态。</summary>
public enum FirewallState
{
    /// <summary>还没查过。调用方按「未知」处理：不显示卡片、不提示。</summary>
    Unknown,

    /// <summary>两条入站规则都在、已启用、覆盖公用网络，且本程序没有入站拦截规则。</summary>
    Ok,

    /// <summary>端口规则缺失 / 被停用 / 不含公用网络。校园网与机房网通常判为「公用」。</summary>
    PortRulesMissing,

    /// <summary>本程序自己有入站 Block 规则 —— Block 永远优先于 Allow，端口规则再对也收不到包。</summary>
    ProgramBlocked,
}

/// <summary>
/// Windows 防火墙的查询与修复。
///
/// <para>
/// **查询只读注册表，不起任何子进程**（2026-10-27 第二次重写）。
/// 上一版用 PowerShell 的 <c>Get-NetFirewallRule</c> 查询，每次应用启动都会拉起一个
/// <c>powershell.exe</c>，安全软件按「程序悄悄调用 PowerShell」的启发式规则直接拦截弹窗 ——
/// 用户反馈「每次启动都要有那个 PowerShell，特别烦」。
/// </para>
///
/// <para>
/// 规则本来就躺在
/// <c>HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules</c>
/// 里，每条是一个明文串，而该键对 <c>BUILTIN\Users</c> 授予了 <c>ReadKey</c> ——
/// 普通权限就能读（实测：非提权进程读出 800 条规则）。一次枚举微秒级完成，
/// 安全软件连影子都看不到。
/// </para>
///
/// <para>
/// ⚠️ 修复动作仍然需要提权，这是绕不过去的。但已从「提权跑 PowerShell 脚本」改成
/// 「提权跑 netsh」：规则名全是 ASCII，命令行不涉及编码转换，安全软件对 <c>netsh advfirewall</c>
/// 的告警等级也远低于 PowerShell。而且它只在用户主动点「修复」时发生。
/// </para>
/// </summary>
public static class FirewallGateway
{
    public const string RuleTcpName = "ClassSoftwareHub-Announce-TCP47821";
    public const string RuleUdpName = "ClassSoftwareHub-Announce-UDP47820";

    /// <summary>Windows 自动为程序生成的规则，名字就是程序文件名。</summary>
    private const string ProgramRuleName = "classsoftwarehub.exe";

    /// <summary>防火墙本地规则库。和「高级安全」界面里看到的同一份数据。</summary>
    private const string RulesKeyPath =
        @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";

    private static readonly object Gate = new();
    private static FirewallState _cached = FirewallState.Unknown;
    private static DateTime _cachedAt = DateTime.MinValue;

    /// <summary>
    /// 缓存时长。读注册表是微秒级，这层只为「一次页面刷新里连调多次」去重，不需要很长。
    /// ⛔ 刻意**不做磁盘缓存**：读一次比读一个文件还快，多一层反而要处理过期与不一致。
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>上一次查到的状态（可能来自缓存）。</summary>
    public static FirewallState LastKnown
    {
        get { lock (Gate) return _cached; }
    }

    /// <summary>清掉缓存，下一次查询一定重新读。</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _cached = FirewallState.Unknown;
            _cachedAt = DateTime.MinValue;
        }
    }

    /// <summary>
    /// 查当前状态（带 30 秒缓存）。**同步、进程内、永不抛异常**：
    /// 读不到或格式不认识就返回 <see cref="FirewallState.Unknown"/>，调用方按「未知」处理。
    /// </summary>
    public static FirewallState Check(bool force = false)
    {
        lock (Gate)
        {
            if (!force && _cached != FirewallState.Unknown && DateTime.UtcNow - _cachedAt < CacheTtl)
                return _cached;
        }

        var state = QueryCore();

        lock (Gate)
        {
            _cached = state;
            _cachedAt = DateTime.UtcNow;
        }

        return state;
    }

    /// <summary>补规则 / 删拦截规则。会弹一次 UAC（唯一需要管理员的动作）。返回修复后的实际状态。</summary>
    public static FirewallState Repair()
    {
        try
        {
            // 只有真的被程序级规则拦着才去删 —— 那条命令是按名字删「所有同名入站规则」，
            // 会把 Windows 自动生成的 Allow 条目一并带走。没被拦就不必付这个代价。
            var needsUnblock = Check(force: true) == FirewallState.ProgramBlocked;

            var commands = new List<string>
            {
                // 1) 清掉同名的旧端口规则（早期版本建的可能只覆盖域/专用，叠加会留下一条无效规则）
                $"netsh advfirewall firewall delete rule name=\"{RuleTcpName}\" >nul 2>&1",
                $"netsh advfirewall firewall delete rule name=\"{RuleUdpName}\" >nul 2>&1",

                // 2) 重建：profile=any 才是「任何网络」—— 校园网、机房网判为「公用」时才放行
                $"netsh advfirewall firewall add rule name=\"{RuleTcpName}\" "
                    + $"dir=in action=allow protocol=TCP localport={AnnounceLanService.ReceivePort} profile=any",
                $"netsh advfirewall firewall add rule name=\"{RuleUdpName}\" "
                    + $"dir=in action=allow protocol=UDP localport={AnnounceLanService.DiscoveryPort} profile=any",
            };

            if (needsUnblock)
            {
                // 3) 删掉本程序的入站拦截规则（Windows 第一次弹「允许访问」时点取消留下的）。
                //    ⚠️ netsh 的 delete **没有 action= 参数**（2026-10-27 用 `netsh advfirewall firewall
                //       delete rule ?` 确认过）。所以只能按「名字 + 方向」删，会连带删掉同名的
                //       Allow 规则 —— 无害：端口规则已经覆盖 47820/47821，不依赖程序级规则。
                //    ⚠️ 也不带 program=：路径可能含中文用户名，命令行编码会出问题；规则名只有本程序用。
                commands.Add($"netsh advfirewall firewall delete rule name=\"{ProgramRuleName}\" dir=in >nul 2>&1");
            }

            var psi = new ProcessStartInfo("cmd.exe", "/c " + string.Join(" & ", commands))
            {
                Verb = "runas",              // 唯一一次提权
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,   // 提权后别再闪一个黑框
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(30_000);

            AppLog.Info("announce-lan", "已通过 netsh 重写防火墙入站规则（覆盖任何网络）");
        }
        catch (Exception ex)
        {
            // 用户点了「否」也走这里（Win32Exception：操作已被用户取消）
            AppLog.Warn("announce-lan", "修复防火墙未完成：" + ex.Message);
        }

        Invalidate();
        return QueryCore();
    }

    // ════════════════════════ 注册表解析 ════════════════════════

    /// <summary>
    /// 读一遍规则库，判断三件事：TCP 规则在不在、UDP 规则在不在、本程序有没有被入站拦截。
    /// 任何读不到 / 格式不对的情况都返回 <see cref="FirewallState.Unknown"/>。
    /// </summary>
    private static FirewallState QueryCore()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RulesKeyPath, writable: false);
            if (key is null) return FirewallState.Unknown;

            var names = key.GetValueNames();
            if (names.Length == 0) return FirewallState.Unknown;

            var exe = NormalizePath(Environment.ProcessPath);

            var tcpOk = false;
            var udpOk = false;
            var blocked = false;
            var sawV2 = false;

            foreach (var valueName in names)
            {
                if (key.GetValue(valueName) is not string raw) continue;

                var rule = Core.FirewallRule.Parse(raw);   // 非 v2 值（PolicyVersion 等）在这里被挡掉
                if (rule is null) continue;

                sawV2 = true;

                if (!rule.ActiveInbound) continue;

                // 端口规则按我们自己的规则名认；程序级拦截按程序路径认
                if (string.Equals(rule.Action, "Allow", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(rule.Name, RuleTcpName, StringComparison.OrdinalIgnoreCase) && rule.CoversPublic)
                        tcpOk = true;
                    else if (string.Equals(rule.Name, RuleUdpName, StringComparison.OrdinalIgnoreCase) && rule.CoversPublic)
                        udpOk = true;
                }
                else if (string.Equals(rule.Action, "Block", StringComparison.OrdinalIgnoreCase))
                {
                    var app = NormalizePath(rule.App);
                    if (app is not null && exe is not null && app.Equals(exe, StringComparison.OrdinalIgnoreCase))
                        blocked = true;
                }
            }

            // 一条 v2 规则都没解析出来 = 格式变了或环境特殊，别硬给结论
            if (!sawV2) return FirewallState.Unknown;

            if (blocked) return FirewallState.ProgramBlocked;
            if (!tcpOk || !udpOk) return FirewallState.PortRulesMissing;
            return FirewallState.Ok;
        }
        catch (Exception ex)
        {
            AppLog.Warn("announce-lan", "读取防火墙规则失败：" + ex.Message);
            return FirewallState.Unknown;
        }
    }

    /// <summary>
    /// 路径规范化后再比。注册表里的路径可能大小写不一、也可能带尾部反斜杠。
    /// 取不到（null/空）就原样返回 null，让调用方跳过比较。
    /// </summary>
    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            return System.IO.Path.GetFullPath(path.Trim().TrimEnd('\\', '/'));
        }
        catch
        {
            return path.Trim();
        }
    }

    /// <summary>给人看的说明（页面用）。</summary>
    public static string Describe(FirewallState state) => state switch
    {
        FirewallState.Ok => "防火墙已放行，同网段的电脑可以发进来。",
        FirewallState.PortRulesMissing => "防火墙尚未放行（或规则只覆盖了域/专用网络）。校园网、机房网通常被判为「公用」，在那种网络下收不到消息。",
        FirewallState.ProgramBlocked => "本程序在 Windows 防火墙里有一条入站拦截规则，所有入站连接会被直接丢弃 —— 端口规则正确也无效。",
        _ => "正在检查防火墙状态……",
    };
}
