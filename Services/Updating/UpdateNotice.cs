using System;

namespace ClassSoftwareHub.Desktop.Services.Updating;


















public static class UpdateNotice
{
    private static readonly object Gate = new();
    private static UpdateRelease? _pending;

    
    public static UpdateRelease? Pending
    {
        get
        {
            lock (Gate) return _pending;
        }
    }

    
    
    
    
    public static void Set(UpdateRelease release)
    {
        lock (Gate)
        {
            if (_pending?.Tag == release.Tag) return;
            _pending = release;
        }

        Core.AppLog.Info("update", $"记住可用更新 {release.Tag}（首页会显示「发现新版本」入口）");
        Raise();
    }

    
    
    
    
    public static void Clear()
    {
        lock (Gate)
        {
            if (_pending is null) return;
            _pending = null;
        }

        
        Core.AppLog.Debug("update", "清除「发现新版本」状态（首页那颗入口会收起来）");
        Raise();
    }

    
    
    
    
    public static event Action? Changed;

    private static void Raise()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("update", ex, "广播「发现新版本」状态变化");
        }
    }
}
