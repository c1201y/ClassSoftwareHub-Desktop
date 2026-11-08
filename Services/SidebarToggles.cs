using System;
using System.Collections.Generic;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 侧边栏里「开关型」模块（<see cref="Data.SidebarModuleKinds.Toggle"/>）的状态读写。
///
/// <para>
/// 侧边栏只认 <see cref="Data.SidebarModule"/> 的 id，**不认得具体功能**；加一个新开关
/// 只要在下表加一行（读状态 + 翻转 + 返回新状态），侧边栏那边一个字都不用改。
/// 这与 <c>Views/ToolSidebarWindow.OpenModuleWindow</c> 用反射打开专用窗口是同一个思路。
/// </para>
/// <para>
/// ⚠️ 表里的读状态必须是**真状态**，不是"上次点的是开还是关"：设置页也有同一个开关，
/// 在那边改过之后回到侧边栏，按钮得显示对。所以一律从配置文件现读。
/// </para>
/// </summary>
internal static class SidebarToggles
{
    /// <summary>一个开关：怎么读当前状态、怎么翻转（返回翻转后的状态）。</summary>
    private sealed record Entry(Func<bool> Read, Func<bool> Flip);

    private static readonly Dictionary<string, Entry> Table = new(StringComparer.Ordinal)
    {
        // PowerPoint 触控（2026-11-08）：写入的是 ppt-touch.json 的 Enabled，与设置页那个
        // 总开关是**同一份**状态 —— 两边随时互相看得到，不会出现"设置里关了、侧边栏还亮着"。
        ["ppttouch"] = new(
            Read: () => PptTouchConfig.Load().Enabled,
            Flip: () =>
            {
                var config = PptTouchConfig.Load();
                config.Enabled = !config.Enabled;
                config.Save();

                // 与 PptTouchPage 的开关同一套调用。Start / Stop 内部会等钩子真的装上 / 卸下
                // （最多各等约 1 秒），所以偶尔会有一次很短的顿挫 —— 换来的是"按完就是真状态"，
                // 不必再猜。⛔ 别改成后台线程：钩子要在自己的线程里起，混淆之后更难排查。
                if (config.Enabled) PptTouch.Start();
                else PptTouch.Stop();

                AppLog.Info("sidebar", $"侧边栏切换 PowerPoint 触控：{(config.Enabled ? "开启" : "关闭")}");
                return config.Enabled;
            }),

        // 以后再加开关：照上面这行抄，Read 读配置、Flip 写配置并落地生效，然后
        // 在 Data/SidebarModule.cs 里登记一条 Kind = SidebarModuleKinds.Toggle 即可。
    };

    /// <summary>这个 id 是不是一个开关（不是的话按普通动作处理）。</summary>
    public static bool IsSupported(string id) => !string.IsNullOrEmpty(id) && Table.ContainsKey(id);

    /// <summary>当前是不是开着。读不到 / 不认识一律当关。</summary>
    public static bool IsOn(string id)
    {
        if (!Table.TryGetValue(id, out var entry)) return false;

        try
        {
            return entry.Read();
        }
        catch (Exception ex)
        {
            AppLog.Warning("sidebar", $"读取开关状态失败（{id}）：{ex.Message}");
            return false;
        }
    }

    /// <summary>翻转并返回新状态。失败时返回原状态，别让按钮显示成翻转成功。</summary>
    public static bool Toggle(string id)
    {
        if (!Table.TryGetValue(id, out var entry)) return false;

        try
        {
            return entry.Flip();
        }
        catch (Exception ex)
        {
            AppLog.Warning("sidebar", $"切换开关失败（{id}）：{ex.Message}");
            return IsOn(id);
        }
    }
}
