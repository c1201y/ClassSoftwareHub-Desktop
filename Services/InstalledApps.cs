using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>本机装的一个软件（从注册表 Uninstall 键读出来的）。</summary>
public sealed record InstalledApp(string Name, string Version, string Publisher, string Location);

/// <summary>
/// 枚举本机已安装的桌面软件。
///
/// 只读注册表，不需要管理员，不联网，不落盘。
/// 覆盖三个位置（少一个就会漏一半软件）：
///   · HKLM 64 位视图  —— 绝大多数 64 位程序
///   · HKLM 32 位视图  —— 32 位程序（在 64 位系统上被重定向到 WOW6432Node）
///   · HKCU            —— 只给当前用户装的程序（很多国产软件走这条路）
///
/// ⚠️ 已知边界（界面上要如实说明，别让用户以为这里是权威清单）：
///   · Microsoft Store / MSIX 应用**不写**这些键 —— 这里查不到，不是没装
///   · 免安装（绿色版）软件也不写 —— 同样查不到
/// </summary>
public static class InstalledApps
{
    /// <summary>抓一份本机已装软件列表（按名称去重）。</summary>
    public static List<InstalledApp> Enumerate()
    {
        var found = new List<InstalledApp>();

        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Registry64),
                     (RegistryHive.CurrentUser, RegistryView.Registry32),
                 })
        {
            ReadHive(hive, view, found);
        }

        // 同一款软件可能在 64/32 两个视图里各出现一次（甚至多个版本并存）→ 按「名称+版本」去重
        return found
            .GroupBy(a => (a.Name.ToLowerInvariant(), a.Version.ToLowerInvariant()))
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private static void ReadHive(RegistryHive hive, RegistryView view, List<InstalledApp> sink)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) return;

            foreach (var subName in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var sub = uninstall.OpenSubKey(subName);
                    var app = ReadEntry(sub);
                    if (app is not null) sink.Add(app);
                }
                catch
                {
                    // 单个键读不动（权限/损坏）不该拖垮整轮枚举
                }
            }
        }
        catch
        {
            // 整个视图读不到就算了，继续下一个
        }
    }

    private static InstalledApp? ReadEntry(RegistryKey? k)
    {
        if (k is null) return null;

        var name = (k.GetValue("DisplayName") as string)?.Trim() ?? "";
        if (name.Length == 0) return null;

        // ── 排除「不是软件」的条目 ──
        // SystemComponent=1：系统组件（VC 运行库、驱动等），列出来只会淹没真正的软件
        if (k.GetValue("SystemComponent") is int sc && sc == 1) return null;
        // ParentKeyName：补丁/更新，挂在主程序下面
        if (!string.IsNullOrWhiteSpace(k.GetValue("ParentKeyName") as string)) return null;
        // ReleaseType：热修复 / 更新汇总 / Service Pack
        var releaseType = (k.GetValue("ReleaseType") as string)?.Trim() ?? "";
        if (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
            releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase) ||
            releaseType.Contains("ServicePack", StringComparison.OrdinalIgnoreCase))
            return null;
        // 明确声明"不显示在程序和功能里"的也跳过
        if (k.GetValue("NoDisplay") is int nd && nd == 1) return null;

        var version = (k.GetValue("DisplayVersion") as string)?.Trim() ?? "";
        var publisher = (k.GetValue("Publisher") as string)?.Trim() ?? "";
        var location = (k.GetValue("InstallLocation") as string)?.Trim() ?? "";

        return new InstalledApp(name, version, publisher, location);
    }

    /// <summary>
    /// 名称归一化，用来做模糊比对：去掉空格、标点、位数后缀和常见版本尾巴。
    /// 例：「Google Chrome (x64)」→「googlechrome」；「360 系统急救箱 5.1」→「360系统急救箱」。
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        // 先剥掉括号里的补充说明。清单里的名字经常带注解
        //（「ToDesk 远程控制 5.x（以安装时官网版本为准）」「UU 远程（网易）」），
        // 那些字不是产品名的一部分，留着会把重合度拉低到认不出来。
        var cleaned = StripParenthetical(raw);

        var sb = new StringBuilder(cleaned.Length);
        foreach (var ch in cleaned)
        {
            // 只留中日韩文字、字母、数字
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }

        var s = sb.ToString();

        // 去掉常见的位数/架构/渠道/版本类型后缀（归一化后没有空格，所以直接去掉子串）。
        // ⚠️ 版本类型后缀很关键：清单里叫「微信」，注册表里写的是「微信（测试版）」——
        //    不去掉「测试版」的话两者差 3 个字，重合度只有 0.4，会被判成"没装"。
        foreach (var noise in new[]
                 {
                     // 位数 / 架构
                     "64bit", "32bit", "64位", "32位", "x64", "x86", "amd64", "arm64",
                     // 语言 / 平台
                     "forwindows", "windows版", "中文版",
                     // 版本类型（厂商自己加的，不是产品区分点）
                     "测试版", "正式版", "极速版", "免费版", "精简版", "便携版",
                     "绿色版", "安装版", "企业版", "专业版", "个人版", "完整版",
                     "增强版", "经典版", "网络版", "抢先版", "体验版", "最新版",
                 })
        {
            s = s.Replace(noise, "");
        }

        // 去掉结尾的版本串（如 chrome-setup → 我们更希望「chrome」能命中「chrome」）
        var cut = s.Length;
        while (cut > 0)
        {
            var c = s[cut - 1];
            if (char.IsDigit(c) || c == '.' || c == 'v') cut--;
            else break;
        }
        // 只有"版本尾巴"足够短（不超过整串 1/3）才砍，避免把「360安全卫士」这种纯数字品牌砍没了
        if (cut > 0 && cut < s.Length && (s.Length - cut) <= Math.Max(4, s.Length / 3))
            s = s[..cut];

        return s;
    }

    /// <summary>去掉括号（中英文都算）及其中的内容，按层嵌套匹配。</summary>
    private static string StripParenthetical(string s)
    {
        var sb = new StringBuilder(s.Length);
        var depth = 0;

        foreach (var ch in s)
        {
            if (ch is '（' or '(')
            {
                depth++;
                continue;
            }

            if (ch is '）' or ')')
            {
                if (depth > 0) depth--;
                continue;
            }

            if (depth == 0) sb.Append(ch);
        }

        return sb.ToString();
    }
}
