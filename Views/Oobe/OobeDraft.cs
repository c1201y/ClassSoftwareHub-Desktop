using System;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views.Oobe;

/// <summary>
/// 向导编辑中的设置副本。
///
/// <para>
/// 照 ClassIsland 的做法：每一步只改这份草稿，窗口收尾时（完成 / 关窗）一次写进设置。
/// 这样向导期间用户已经动过的选项不会被中途写盘，关窗也不会白配一场。
/// </para>
///
/// <para>
/// ⛔ 没有「跳过」这条路径了（2026-10-31 Nick 定：初次配置必选）——
///    别再加一个"全部不写"的分支，那正是用户选了浅色却被丢掉的原因之一。
/// </para>
/// </summary>
public sealed class OobeDraft
{
    public string Theme { get; set; } = "system";

    /// <summary>窗口背景材质：mica | acrylic | solid。</summary>
    public string Backdrop { get; set; } = "mica";

    /// <summary>外部组件（侧边栏 / 常用工具浮窗）单独设置外观。</summary>
    public bool SplitTheme { get; set; }

    /// <summary>外部组件的外观：system | light | dark（仅 <see cref="SplitTheme"/> 打开时有效）。</summary>
    public string ExternalTheme { get; set; } = "system";

    public bool SidebarEnabled { get; set; }

    /// <summary>常用工具浮窗始终置顶。</summary>
    public bool PaletteOnTop { get; set; } = true;

    /// <summary>侧边栏按钮上写长名（最多四个字）。</summary>
    public bool SidebarLongLabels { get; set; }

    /// <summary>贴靠模式贴哪条边：left | right | both（见 SettingsStore.SidebarEdge）。</summary>
    public string SidebarEdge { get; set; } = "right";

    /// <summary>自由模式贴哪条边：left | right | top | bottom（见 SettingsStore.SidebarFreeEdge）。</summary>
    public string SidebarFreeEdge { get; set; } = "right";

    /// <summary>侧边栏放置模式：dock（贴靠）| free（自由）。</summary>
    public string SidebarMode { get; set; } = "dock";

    /// <summary>展开后常驻，不自动收起。</summary>
    public bool SidebarPinned { get; set; }

    /// <summary>收起条大小样式：standard | compact。</summary>
    public string SidebarDensity { get; set; } = "standard";

    /// <summary>展开面板大小样式：same | standard | compact。</summary>
    public string SidebarExpandedDensity { get; set; } = "same";

    /// <summary>工具排列：1（紧凑，单列）| 3（舒展，每行三个）。</summary>
    public int SidebarColumns { get; set; } = 1;

    /// <summary>展开面板高度上限（0.45~0.95）。</summary>
    public double SidebarHeightRatio { get; set; } = 0.75;

    /// <summary>展开面板高度跟随内容。</summary>
    public bool SidebarHeightAuto { get; set; } = true;

    /// <summary>收起条离屏幕边缘的 dip（0~24）。</summary>
    public int SidebarCollapsedGap { get; set; } = 6;

    /// <summary>截图后自动保存一份原图。</summary>
    public bool ShotAutoSave { get; set; } = true;

    /// <summary>截图保存目录；空 = 桌面。</summary>
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
