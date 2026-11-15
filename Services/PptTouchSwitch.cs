using System;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;













internal static class PptTouchSwitch
{
    
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

    
    public static bool MenuOn()
    {
        try
        {
            return PptTouchConfig.Load().CustomMenu;
        }
        catch (Exception ex)
        {
            AppLog.Warning("sidebar", "读取长按菜单开关失败：" + ex.Message);
            return true;                     
        }
    }

    
    
    
    
    
    
    
    
    public static bool SetGesture(bool on)
    {
        try
        {
            var config = PptTouchConfig.Load();
            config.Enabled = on;
            config.Save();

            if (on) PptTouch.Start();
            else PptTouch.Stop();

            AppLog.Info("sidebar", $"侧边栏切换触控手势：{(on ? "开启" : "关闭")}");
            return on;
        }
        catch (Exception ex)
        {
            AppLog.Warning("sidebar", "切换触控手势失败：" + ex.Message);
            return GestureOn();              
        }
    }

    
    public static bool SetMenu(bool on)
    {
        try
        {
            var config = PptTouchConfig.Load();
            config.CustomMenu = on;
            config.Save();

            
            PptTouch.ApplyConfig();

            AppLog.Info("sidebar", $"侧边栏切换长按菜单：{(on ? "开启" : "关闭")}");
            return on;
        }
        catch (Exception ex)
        {
            AppLog.Warning("sidebar", "切换长按菜单失败：" + ex.Message);
            return MenuOn();
        }
    }
}
