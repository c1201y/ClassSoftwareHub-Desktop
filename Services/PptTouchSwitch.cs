using System;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 侧边栏「快捷入口」二级菜单里那两个 PowerPoint 触控开关的读写。
///
/// <para>
/// 读写的就是 <see cref="PptTouchConfig"/> 本身（<c>ppt-touch.json</c>），与「PowerPoint 触控优化」
/// 页上的两个开关是**同一份**状态 —— 任何一边改过，另一边下次读到的都是真状态，
/// 不会出现"设置里关了、菜单里还亮着"。
/// </para>
/// <para>
/// ⚠️ 一律**现读配置**，不缓存"上次拨的方向"：页面那边随时可能改。
/// </para>
/// </summary>
internal static class PptTouchSwitch
{
    /// <summary>触控手势（总开关）当前开着吗。读不到一律当关。</summary>
    public static bool GestureOn()
    {
        try
        {
            return PptTouchConfig.Load().Enabled;
        }
        catch (Exception ex)
        {
            AppLog.Warning("sidebar", "读取触控手势开关失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>长按弹出菜单当前开着吗。</summary>
    public static bool MenuOn()
    {
        try
        {
            return PptTouchConfig.Load().CustomMenu;
        }
        catch (Exception ex)
        {
            AppLog.Warning("sidebar", "读取长按菜单开关失败：" + ex.Message);
            return true;                     // 与 PptTouchConfig.CustomMenu 的默认值一致
        }
    }

    /// <summary>
    /// 拨动触控手势总开关，返回**实际生效后**的状态。
    /// <para>
    /// ⚠️ <see cref="PptTouch.Start"/> / <see cref="PptTouch.Stop"/> 内部会等钩子真的装上 / 卸下
    /// （最多各等约 1 秒），所以偶尔会有一次很短的顿挫 —— 换来的是"拨完就是真状态"，不必再猜。
    /// ⛔ 别挪到后台线程：钩子要在自己的线程里起，混淆之后更难排查。
    /// </para>
    /// </summary>
    public static bool SetGesture(bool on)
    {
        try
        {
            var config = PptTouchConfig.Load();
            config.Enabled = on;
            config.Save();

            if (on) PptTouch.Start();
            else PptTouch.Stop();

            AppLog.Info("sidebar", $"快捷菜单切换触控手势：{(on ? "开启" : "关闭")}");
            return on;
        }
        catch (Exception ex)
        {
            AppLog.Warning("sidebar", "切换触控手势失败：" + ex.Message);
            return GestureOn();              // 失败就把真状态还回去，别让界面显示成拨成功了
        }
    }

    /// <summary>拨动「长按弹出菜单」，返回实际生效后的状态。</summary>
    public static bool SetMenu(bool on)
    {
        try
        {
            var config = PptTouchConfig.Load();
            config.CustomMenu = on;
            config.Save();

            // 钩子回调里不读配置文件（一次磁盘 IO 就够拖出钩子超时），要走这一步把缓存刷掉。
            PptTouch.ApplyConfig();

            AppLog.Info("sidebar", $"快捷菜单切换长按菜单：{(on ? "开启" : "关闭")}");
            return on;
        }
        catch (Exception ex)
        {
            AppLog.Warning("sidebar", "切换长按菜单失败：" + ex.Message);
            return MenuOn();
        }
    }
}
