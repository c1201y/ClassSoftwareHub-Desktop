using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;


public enum FirewallState
{
    
    Unknown,

    
    Ok,

    
    PortRulesMissing,

    
    ProgramBlocked,
}

























public static class FirewallGateway
{
    public const string RuleTcpName = "ClassSoftwareHub-Announce-TCP47821";
    public const string RuleUdpName = "ClassSoftwareHub-Announce-UDP47820";

    
    private const string ProgramRuleName = "classsoftwarehub.exe";

    
    private const string RulesKeyPath =
        @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";

    private static readonly object Gate = new();
    private static FirewallState _cached = FirewallState.Unknown;
    private static DateTime _cachedAt = DateTime.MinValue;

    
    
    
    
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    
    public static FirewallState LastKnown
    {
        get { lock (Gate) return _cached; }
    }

    
    public static void Invalidate()
    {
        lock (Gate)
        {
            _cached = FirewallState.Unknown;
            _cachedAt = DateTime.MinValue;
        }
    }

    
    
    
    
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

    
    public static FirewallState Repair()
    {
        try
        {
            
            
            var needsUnblock = Check(force: true) == FirewallState.ProgramBlocked;

            var commands = new List<string>
            {
                
                $"netsh advfirewall firewall delete rule name=\"{RuleTcpName}\" >nul 2>&1",
                $"netsh advfirewall firewall delete rule name=\"{RuleUdpName}\" >nul 2>&1",

                
                $"netsh advfirewall firewall add rule name=\"{RuleTcpName}\" "
                    + $"dir=in action=allow protocol=TCP localport={AnnounceLanService.ReceivePort} profile=any",
                $"netsh advfirewall firewall add rule name=\"{RuleUdpName}\" "
                    + $"dir=in action=allow protocol=UDP localport={AnnounceLanService.DiscoveryPort} profile=any",
            };

            if (needsUnblock)
            {
                
                
                
                
                
                commands.Add($"netsh advfirewall firewall delete rule name=\"{ProgramRuleName}\" dir=in >nul 2>&1");
            }

            var psi = new ProcessStartInfo("cmd.exe", "/c " + string.Join(" & ", commands))
            {
                Verb = "runas",              
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,   
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(30_000);
        }
        catch (Exception ex)
        {
            
            AppLog.Warn("announce-lan", "修复防火墙未完成：" + ex.Message);
        }

        Invalidate();
        var state = QueryCore();

        
        
        AppLog.Info("announce-lan", state == FirewallState.Ok
            ? "防火墙入站规则已就绪（覆盖任何网络，无程序级拦截）"
            : "防火墙修复后仍未达标：" + Describe(state));

        return state;
    }

    

    
    
    
    
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

                var rule = Core.FirewallRule.Parse(raw);   
                if (rule is null) continue;

                sawV2 = true;

                if (!rule.ActiveInbound) continue;

                
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

    
    public static string Describe(FirewallState state) => state switch
    {
        FirewallState.Ok => "防火墙已放行，同网段的电脑可以发进来。",
        FirewallState.PortRulesMissing => "防火墙尚未放行（或规则只覆盖了域/专用网络）。校园网、机房网通常被判为「公用」，在那种网络下收不到消息。",
        FirewallState.ProgramBlocked => "本程序在 Windows 防火墙里有一条入站拦截规则，所有入站连接会被直接丢弃 —— 端口规则正确也无效。",
        _ => "正在检查防火墙状态……",
    };
}
