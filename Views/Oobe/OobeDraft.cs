using System;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views.Oobe;














public sealed class OobeDraft
{
    public string Theme { get; set; } = "system";

    
    public string Backdrop { get; set; } = "mica";

    
    public bool SplitTheme { get; set; }

    
    public string ExternalTheme { get; set; } = "system";

    public bool SidebarEnabled { get; set; }

    
    public bool PaletteOnTop { get; set; } = true;

    
    public bool SidebarLongLabels { get; set; }

    
    public string SidebarEdge { get; set; } = "right";

    
    public string SidebarFreeEdge { get; set; } = "right";

    
    public string SidebarMode { get; set; } = "dock";

    
    public bool SidebarPinned { get; set; }

    
    public string SidebarDensity { get; set; } = "standard";

    
    public string SidebarExpandedDensity { get; set; } = "same";

    
    public int SidebarColumns { get; set; } = 1;

    
    public double SidebarHeightRatio { get; set; } = 0.75;

    
    public bool SidebarHeightAuto { get; set; } = true;

    
    public int SidebarCollapsedGap { get; set; } = 6;

    
    public bool ShotAutoSave { get; set; } = true;

    
    public string ShotSaveDir { get; set; } = "";

    public bool AutoStart { get; set; }
    public bool MinimizeOnStart { get; set; }
    public bool CloseToTray { get; set; }

    public string GithubRoute { get; set; } = GithubRoutes.Auto;
    public bool AutoCheckUpdate { get; set; }
    public int CacheKeepCount { get; set; } = 3;

    public bool HubAiEnabled { get; set; }
    public string UserDisplayName { get; set; } = "";

    public static OobeDraft FromSettings()
    {
        var s = App.Settings.Current;
        return new OobeDraft
        {
            Theme = s.Theme,
            Backdrop = s.Backdrop,
            SplitTheme = s.SplitTheme,
            ExternalTheme = s.ExternalTheme,
            SidebarEnabled = s.SidebarEnabled,
            PaletteOnTop = s.PaletteOnTop,
            SidebarLongLabels = s.SidebarLongLabels,
            SidebarEdge = s.SidebarEdge is "left" or "both" ? s.SidebarEdge : "right",
            SidebarFreeEdge = s.SidebarFreeEdge is "left" or "top" or "bottom" ? s.SidebarFreeEdge : "right",
            SidebarMode = s.SidebarMode == "free" ? "free" : "dock",
            SidebarPinned = s.SidebarPinned,
            SidebarDensity = s.SidebarDensity == "compact" ? "compact" : "standard",
            SidebarExpandedDensity = s.SidebarExpandedDensity is "standard" or "compact" ? s.SidebarExpandedDensity : "same",
            SidebarColumns = s.SidebarColumns == 3 ? 3 : 1,
            SidebarHeightRatio = Math.Clamp(s.SidebarHeightRatio, 0.45, 0.95),
            SidebarHeightAuto = s.SidebarHeightAuto,
            SidebarCollapsedGap = Math.Clamp(s.SidebarCollapsedGap, 0, 24),
            ShotAutoSave = s.ShotAutoSave,
            ShotSaveDir = s.ShotSaveDir ?? "",
            AutoStart = s.AutoStart,
            MinimizeOnStart = s.MinimizeOnStart,
            CloseToTray = s.CloseToTray,
            GithubRoute = GithubRoutes.Normalize(s.GithubDownloadRoute),
            AutoCheckUpdate = s.AutoCheckUpdate,
            CacheKeepCount = s.CacheKeepCount,
            HubAiEnabled = s.HubAiEnabled,
            UserDisplayName = s.UserDisplayName,
        };
    }
}
