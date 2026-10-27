using System;
using System.Collections.Generic;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 防火墙规则库里的一条规则。
///
/// <para>
/// 注册表 <c>HKLM\SYSTEM\…\FirewallPolicy\FirewallRules</c> 下每条规则是一个明文串，形如：
/// <c>v2.33|Action=Allow|Active=TRUE|Dir=In|Protocol=6|Profile=Domain|Profile=Private|LPort=47821|Name=…|</c>
/// </para>
///
/// <para>
/// ⚠️ **同一个键可以出现多次**（上例的 <c>Profile</c> 写了三遍，表示规则同时作用于两个网络类型），
/// 所以解析时值要收集成列表，不能"后者覆盖前者" —— 那就只剩最后一种网络，判「是否覆盖公用」会误判成否。
/// </para>
///
/// <para>
/// 这个类**只做字符串解析、不碰注册表**，所以能脱离 Windows 单独测
/// （测试工程链接的是这一份，见 <c>E:/Classsoftwarehub测试</c>）。
/// </para>
/// </summary>
public sealed class FirewallRule
{
    /// <summary>Allow / Block。</summary>
    public string? Action { get; private init; }

    /// <summary>规则显示名（用户在「高级安全 Windows 防火墙」里看到的那一列）。</summary>
    public string? Name { get; private init; }

    /// <summary>程序级规则的程序路径；端口级规则没有这一项。</summary>
    public string? App { get; private init; }

    /// <summary>这条规则是不是「已启用 + 入站」—— 不成立的话其余判断都没意义。</summary>
    public bool ActiveInbound { get; private init; }

    /// <summary>
    /// 是否覆盖「公用」网络。
    /// 校园网、机房网、没设过家庭网络的机器，Windows 多半判成「公用」；
    /// 只写域/专用的规则在那种网络下等于没加（包被静默丢弃，表现成超时而不是拒绝连接）。
    /// </summary>
    public bool CoversPublic { get; private init; }

    /// <summary>
    /// 解析一条规则。不是 v2 格式、或解析不出任何键值对时返回 null（调用方跳过即可）。
    /// </summary>
    public static FirewallRule? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!text.StartsWith("v2.", StringComparison.Ordinal)) return null;   // 同键下还有 PolicyVersion 之类的非规则值

        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in text.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = segment.IndexOf('=');
            if (idx <= 0) continue;

            var key = segment[..idx].Trim();
            var value = segment[(idx + 1)..].Trim();
            if (key.Length == 0) continue;

            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<string>();
            list.Add(value);
        }

        if (map.Count == 0) return null;

        return new FirewallRule
        {
            Action = First(map, "Action"),
            Name = First(map, "Name"),
            App = First(map, "App"),
            ActiveInbound = HasValue(map, "Active", "TRUE") && HasValue(map, "Dir", "In"),
            CoversPublic = ProfileCoversPublic(map),
        };
    }

    /// <summary>这些值里有没有「任何网络」的表示法。Windows 在不同版本里写法不一，全都认。</summary>
    private static bool ProfileCoversPublic(Dictionary<string, List<string>> map)
    {
        if (!map.TryGetValue("Profile", out var profiles)) return false;

        foreach (var profile in profiles)
        {
            if (profile.Equals("Any", StringComparison.OrdinalIgnoreCase)) return true;
            if (profile.Equals("All", StringComparison.OrdinalIgnoreCase)) return true;
            if (profile.Equals("Public", StringComparison.OrdinalIgnoreCase)) return true;
            if (profile.Equals("0x7fffffff", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static string? First(Dictionary<string, List<string>> map, string key) =>
        map.TryGetValue(key, out var list) && list.Count > 0 ? list[0] : null;

    private static bool HasValue(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var list)) return false;

        foreach (var item in list)
            if (item.Equals(value, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }
}
