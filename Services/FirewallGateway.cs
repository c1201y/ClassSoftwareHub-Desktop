using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>局域网通道的防火墙状态。</summary>
public enum FirewallState
{
    /// <summary>还没查过（页面刚打开、后台查询未回）。</summary>
    Unknown,

    /// <summary>两条入站规则都在、已启用、覆盖公用网络，且本程序没有入站拦截规则。</summary>
    Ok,

    /// <summary>端口规则缺失 / 被停用 / 不含公用网络。校园网与机房网通常判为「公用」。</summary>
    PortRulesMissing,

    /// <summary>本程序自己有入站 Block 规则 —— Block 永远优先于 Allow，端口规则再对也收不到包。</summary>
    ProgramBlocked,
}

/// <summary>
/// Windows 防火墙的查询与修复（2026-10-27 重写）。
///
/// <para>
/// 为什么单独成文件、为什么不用 netsh 解析：旧实现（<c>AnnounceLanService.FirewallRulesPresent</c>）
/// 直接读 netsh 的中文输出再 <c>Contains("已启用")</c>，有两个各自都能让结果恒为 false 的问题，
/// 叠在一起的表现就是「局域网通道永远提示要放行、每次都要弹一次 UAC」：
/// </para>
/// <list type="number">
///   <item>编码：本程序在 app.manifest 里声明了 <c>activeCodePage=UTF-8</c>，子进程控制台按 UTF-8 解码，
///     而 netsh 在中文系统上输出的是 GBK（代码页 936）—— 读完就是乱码，任何中文关键字都匹配不上。</item>
///   <item>判据太窄：只查「名字叫这个的规则存在」，没查它到底覆盖哪些网络配置文件。
///     实测这台机器上留下的旧规则是 <c>配置文件: 域,专用</c>，**不含公用** ——
///     校园网/机房网恰恰被判成「公用」，规则在那种网络下等于没加。</item>
/// </list>
///
/// <para>
/// 现在的做法：用 PowerShell 的 NetSecurity 模块查（脚本经 <c>-EncodedCommand</c> 以 UTF-16 传递，
/// 与系统代码页完全无关；输出强制 UTF-8），判据改成三个必要条件：
/// 规则存在 + 已启用 + <c>Profile</c> 位掩码含 <c>Public</c>；并额外检查本程序有没有入站 Block 规则。
/// </para>
///
/// <para>
/// ⛔ 规则名一律用 ASCII：中文规则名要经 cmd/netsh 命令行传递，任何一层编码没对齐就变成"查不到、也删不掉"。
/// 旧的中文规则会在修复时一并删掉（它们缺公用网络，留着只会误导）。
/// </para>
/// </summary>
public static class FirewallGateway
{
    public const string RuleTcpName = "ClassSoftwareHub-Announce-TCP47821";
    public const string RuleUdpName = "ClassSoftwareHub-Announce-UDP47820";

    /// <summary>规则分组名，方便用户在「高级安全 Windows Defender 防火墙」里一眼找到并整体删除。</summary>
    private const string RuleGroup = "ClassSoftwareHub";

    /// <summary>旧版本用过的中文规则名（缺公用网络，修复时清掉）。</summary>
    private static readonly string[] LegacyRuleNames =
    {
        "ClassSoftwareHub 教室喊话（收件）",
        "ClassSoftwareHub 教室喊话（发现）",
    };

    /// <summary>Windows 自动为程序生成的规则，DisplayName 就是程序文件名。</summary>
    private const string ProgramRuleName = "classsoftwarehub.exe";

    private static readonly object Gate = new();
    private static FirewallState _cached = FirewallState.Unknown;
    private static DateTime _cachedAt = DateTime.MinValue;

    /// <summary>缓存时长：查询要起一个 PowerShell 进程（约 0.5~1.5 秒），不能每次刷界面都跑一遍。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>上一次查到的状态（可能来自缓存）。</summary>
    public static FirewallState LastKnown
    {
        get { lock (Gate) return _cached; }
    }

    /// <summary>清掉缓存，下一次查询一定重新跑。</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _cached = FirewallState.Unknown;
            _cachedAt = DateTime.MinValue;
        }
    }

    /// <summary>
    /// 查询当前状态（带缓存）。永远不抛异常：查不出来就返回 <see cref="FirewallState.Unknown"/>，
    /// 调用方按「未知」处理，不提示、不打扰。
    /// </summary>
    public static async Task<FirewallState> CheckAsync(bool force = false)
    {
        lock (Gate)
        {
            if (!force && _cached != FirewallState.Unknown && DateTime.UtcNow - _cachedAt < CacheTtl)
                return _cached;
        }

        var state = await Task.Run(QueryCore);

        lock (Gate)
        {
            _cached = state;
            _cachedAt = DateTime.UtcNow;
        }

        return state;
    }

    private static FirewallState QueryCore()
    {
        var output = RunPowerShell(QueryScript(), elevate: false);
        if (string.IsNullOrWhiteSpace(output)) return FirewallState.Unknown;

        // 脚本输出形如 tcp=1;udp=1;block=0
        var tcp = false;
        var udp = false;
        var block = false;
        var sawAny = false;

        foreach (var part in output.Trim().Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0) continue;

            var key = part[..idx].Trim();
            var value = part[(idx + 1)..].Trim() == "1";
            sawAny = true;

            switch (key)
            {
                case "tcp": tcp = value; break;
                case "udp": udp = value; break;
                case "block": block = value; break;
            }
        }

        if (!sawAny) return FirewallState.Unknown;   // 脚本没跑起来 / 被策略拦了

        // Block 优先报：它比端口规则更致命，提示语也不一样
        if (block) return FirewallState.ProgramBlocked;
        if (!tcp || !udp) return FirewallState.PortRulesMissing;
        return FirewallState.Ok;
    }

    /// <summary>
    /// 补规则 / 删拦截规则。会弹一次 UAC（这是唯一需要管理员的动作）。
    /// 返回修复后的实际状态：只有 <see cref="FirewallState.Ok"/> 才算成功。
    /// </summary>
    public static FirewallState Repair()
    {
        try
        {
            RunPowerShell(RepairScript(), elevate: true);
        }
        catch (Exception ex)
        {
            // 用户点了「否」也走这里（Win32Exception：操作已被用户取消）
            AppLog.Warn("announce-lan", "修复防火墙未完成：" + ex.Message);
        }

        Invalidate();
        return QueryCore();
    }

    // ════════════════════════ PowerShell ════════════════════════

    /// <summary>
    /// 起一个 PowerShell 执行脚本。
    /// <para>
    /// ⚠️ 脚本一律走 <c>-EncodedCommand</c>（Base64 的 UTF-16LE）：直接塞进命令行会被系统代码页
    /// 重新解释一次，中文规则名、中文用户名路径都会变形，而这正是旧实现栽的地方。
    /// </para>
    /// </summary>
    private static string? RunPowerShell(string script, bool elevate)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + encoded)
        {
            CreateNoWindow = true,
            UseShellExecute = elevate,        // runas 必须走 shell
            RedirectStandardOutput = !elevate,
            RedirectStandardError = !elevate,
            StandardOutputEncoding = elevate ? null : Encoding.UTF8,
            StandardErrorEncoding = elevate ? null : Encoding.UTF8,
        };

        if (elevate) psi.Verb = "runas";

        using var proc = Process.Start(psi);
        if (proc is null) return null;

        if (elevate)
        {
            proc.WaitForExit(45_000);   // 用户在 UAC 框上犹豫是常态，别太急
            return null;
        }

        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(15_000);
        return output;
    }

    /// <summary>
    /// 查询脚本。判据写死在脚本里，返回三态：tcp / udp / block。
    /// <para>
    /// ⚠️ <c>-PolicyStore ActiveStore</c>：只看当前**生效**的规则集，本机策略与组策略合并后的结果。
    /// 默认的 PersistentStore 在域控下发的规则上看不到，会误判成"没有规则"。
    /// </para>
    /// </summary>
    private static string QueryScript()
    {
        var exe = (Environment.ProcessPath ?? "").Replace("'", "''");

        return $@"
$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

function Test-PortRule([string]$name) {{
  $rules = @(Get-NetFirewallRule -DisplayName $name -PolicyStore ActiveStore)
  foreach ($r in $rules) {{
    if ($r.Direction -ne 'Inbound') {{ continue }}
    if ($r.Action -ne 'Allow') {{ continue }}
    if ($r.Enabled -ne 'True') {{ continue }}
    # Profile 是位掩码：Domain=1, Private=2, Public=4
    if (($r.Profile -band 4) -ne 0) {{ return 1 }}
  }}
  return 0
}}

$tcp = Test-PortRule '{RuleTcpName}'
$udp = Test-PortRule '{RuleUdpName}'

# 程序级拦截：Block 规则永远优先于 Allow，端口规则再对也收不到包
$block = 0
$exe = '{exe}'
foreach ($r in @(Get-NetFirewallRule -DisplayName '{ProgramRuleName}' -PolicyStore ActiveStore)) {{
  if ($r.Direction -ne 'Inbound') {{ continue }}
  if ($r.Action -ne 'Block') {{ continue }}
  if ($r.Enabled -ne 'True') {{ continue }}
  $app = $r | Get-NetFirewallApplicationFilter
  if ($app -and $app.Program -ieq $exe) {{ $block = 1; break }}
}}

Write-Output ('tcp=' + $tcp + ';udp=' + $udp + ';block=' + $block)
";
    }

    /// <summary>
    /// 修复脚本（提权执行）：
    /// 删掉新旧端口规则 → 重建两条覆盖「任何网络」的入站规则 → 删掉本程序的入站 Block 规则。
    /// ⚠️ 只动名字是本程序的那几条，不碰任何其它规则。
    /// </summary>
    private static string RepairScript()
    {
        var exe = (Environment.ProcessPath ?? "").Replace("'", "''");
        var legacy = string.Join(", ", Array.ConvertAll(LegacyRuleNames, n => "'" + n.Replace("'", "''") + "'"));

        return $@"
$ErrorActionPreference = 'SilentlyContinue'

# 1) 清掉旧的端口规则（含早期版本留下的中文名规则：它们只覆盖 域/专用，留着会一直误导判断）
Remove-NetFirewallRule -DisplayName '{RuleTcpName}' -PolicyStore ActiveStore
Remove-NetFirewallRule -DisplayName '{RuleUdpName}' -PolicyStore ActiveStore
foreach ($n in @({legacy})) {{ Remove-NetFirewallRule -DisplayName $n -PolicyStore ActiveStore }}

# 2) 重建：Profile Any = 域/专用/公用全覆盖，校园网与机房网才能收到
New-NetFirewallRule -DisplayName '{RuleTcpName}' -Group '{RuleGroup}' -Direction Inbound -Action Allow `
  -Protocol TCP -LocalPort {AnnounceLanService.ReceivePort} -Profile Any -PolicyStore ActiveStore | Out-Null
New-NetFirewallRule -DisplayName '{RuleUdpName}' -Group '{RuleGroup}' -Direction Inbound -Action Allow `
  -Protocol UDP -LocalPort {AnnounceLanService.DiscoveryPort} -Profile Any -PolicyStore ActiveStore | Out-Null

# 3) 删掉本程序的入站拦截规则（通常是 Windows 第一次弹「允许访问」时点了取消留下的）
$exe = '{exe}'
foreach ($r in @(Get-NetFirewallRule -DisplayName '{ProgramRuleName}' -PolicyStore ActiveStore)) {{
  if ($r.Direction -ne 'Inbound') {{ continue }}
  if ($r.Action -ne 'Block') {{ continue }}
  $app = $r | Get-NetFirewallApplicationFilter
  if ($app -and $app.Program -ieq $exe) {{ Remove-NetFirewallRule -Name $r.Name -PolicyStore ActiveStore }}
}}
";
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
