using System.Text.Json;

namespace ClassSoftwareHub.Desktop.Services;






public static class SidebarFooterKeys
{
    public const string Fold = "fold";          
    public const string Pin = "pin";            
    public const string Reset = "reset";        
    public const string Hide = "hide";          
    public const string OpenApp = "openapp";    

    public static readonly string[] All = { Fold, Pin, Reset, Hide, OpenApp };
}


public sealed class AppSettings
{
    
    
    
    
    
    
    
    
    public string Backdrop { get; set; } = "mica";         
    public string Theme { get; set; } = "system";              
    
    public bool SplitTheme { get; set; }
    
    public string ExternalTheme { get; set; } = "system";

    public bool AlwaysOnTop { get; set; }
    public bool AutoStart { get; set; }
    
    public bool MinimizeOnStart { get; set; }
    public bool TelemetryEnabled { get; set; }                 
    public bool TelemetryAsked { get; set; }

    
    public bool CloseToTray { get; set; } = true;

    
    
    
    
    
    
    public bool TrayHideHintShown { get; set; }

    

    public const string DefaultHotkeyShow = "Ctrl+Alt+0";
    public const string DefaultHotkeyAnnounce = "Ctrl+Alt+1";
    public const string DefaultHotkeyTimer = "Ctrl+Alt+2";
    public const string DefaultHotkeyPptTouch = "Ctrl+Alt+3";

    
    public bool HotkeysEnabled { get; set; } = true;

    
    public string HotkeyShow { get; set; } = DefaultHotkeyShow;

    
    public string HotkeyAnnounce { get; set; } = DefaultHotkeyAnnounce;

    
    public string HotkeyTimer { get; set; } = DefaultHotkeyTimer;

    
    public string HotkeyPptTouch { get; set; } = DefaultHotkeyPptTouch;

    
    
    
    
    
    
    
    
    
    public string BadgeSinceVersion { get; set; } = "";

    
    public string BadgeSinceUtc { get; set; } = "";

    
    public bool PaletteOnTop { get; set; } = true;

    
    public int PaletteX { get; set; } = -99999;
    public int PaletteY { get; set; } = -99999;

    
    
    
    
    public int PalettePosVersion { get; set; }

    
    public string PaletteTool { get; set; } = "pick-number";

    
    public bool SidebarEnabled { get; set; } = true;

    
    
    
    
    
    
    
    
    public string SidebarEdge { get; set; } = "right";

    
    
    
    
    
    
    
    
    
    
    
    public string SidebarMode { get; set; } = "dock";

    
    
    
    
    
    
    
    
    
    public string SidebarDropAnim { get; set; } = "dip";

    
    
    
    
    
    
    
    
    public double SidebarPosRatio { get; set; } = -1;

    
    
    
    
    public string SidebarFreeEdge { get; set; } = "right";

    
    
    
    
    public bool SidebarPinned { get; set; }

    
    
    
    
    public string[] SidebarModuleIds { get; set; } = { "pick-number", "timer", "stopwatch", "clock" };

    
    
    
    
    
    
    
    
    
    
    public bool SidebarLongLabels { get; set; }

    
    
    
    
    
    public string SubjectToolsLastTool { get; set; } = "function-plot";

    
    
    
    
    
    
    
    
    public string[] SidebarFooterHidden { get; set; } = Array.Empty<string>();

    
    
    
    
    
    
    public bool SidebarFooterEnabled { get; set; } = true;

    
    
    
    
    
    
    
    
    
    
    
    
    
    public string SidebarDensity { get; set; } = "standard";

    
    
    
    
    
    
    
    
    public string SidebarExpandedDensity { get; set; } = "same";

    
    
    
    
    
    
    public int SidebarColumns { get; set; } = 1;

    
    
    
    
    
    
    
    
    public int SidebarCollapsedGap { get; set; } = 6;

    
    
    
    
    
    
    
    
    
    public double SidebarHeightRatio { get; set; } = 0.75;

    
    
    
    
    
    
    
    
    public bool SidebarHeightAuto { get; set; } = true;

    
    
    
    
    
    
    
    
    
    public string ShutdownMode { get; set; } = "slide";

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    public bool HubAiEnabled { get; set; } = true;

    
    
    
    
    
    public string HubAiProvider { get; set; } = "deepseek";

    
    public string HubAiBaseUrl { get; set; } = "https://api.deepseek.com/v1";

    
    public string HubAiModel { get; set; } = "deepseek-v4-flash";

    
    
    
    
    
    public bool HubAiThinking { get; set; } = true;

    
    public string HubAiEffort { get; set; } = "high";

    
    
    
    
    public string UserDisplayName { get; set; } = "";

    
    
    
    
    
    public System.Collections.Generic.List<HubAi.AiModelEntry> HubAiModels { get; set; } = new();

    
    public string? HubAiActiveModelId { get; set; }

    
    public bool HubAiHistoryCollapsed { get; set; }

    
    
    
    
    public bool HubAiWebEnabled { get; set; }

    
    
    
    
    
    
    public string HubAiLastSessionId { get; set; } = "";

    
    
    
    
    
    
    
    
    
    public bool HubAiToolsEnabled { get; set; }

    
    
    
    
    
    
    
    public int SidebarModulesRevision { get; set; }

    
    
    
    
    
    
    
    
    
    public string[] NavTopLevel { get; set; } = Array.Empty<string>();

    
    public string[] NavSecondLevel { get; set; } = Array.Empty<string>();

    
    
    

    public string WebView2MissingChoice { get; set; } = "";    

    
    public bool ShotAutoSave { get; set; } = true;
    
    public string ShotSaveDir { get; set; } = "";

    
    public string UpdateChannel { get; set; } = Core.ShellConfig.DefaultUpdateChannel;

    
    public bool UpdateChannelSetByUser { get; set; }

    
    public long LastUpdateCheck { get; set; }

    
    public string SkipUpdateVersion { get; set; } = "";

    
    public bool AutoCheckUpdate { get; set; } = true;

    
    
    
    
    
    public int InstallerKeepCount { get; set; } = 3;

    
    
    
    
    
    public int CacheKeepCount { get; set; } = 3;

    
    
    
    
    
    public int LogKeepDays { get; set; } = 7;

    
    
    
    
    public string UpdatePendingPath { get; set; } = "";

    
    public string UpdatePendingTag { get; set; } = "";

    
    
    
    
    
    
    
    
    public bool AutoDeletePackage { get; set; }

    
    
    
    
    public bool AutoOpenExtractFolder { get; set; } = true;

    
    public string AppCardView { get; set; } = "tile";
    public bool WebTransparent { get; set; } = true;
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 820;

    
    public int WindowLeft { get; set; } = int.MinValue;

    
    public int WindowTop { get; set; } = int.MinValue;

    
    public bool WindowMaximized { get; set; }
    public double WebZoom { get; set; } = 1.0;
    public string LastUrl { get; set; } = "";

    
    public string TitleBarTint { get; set; } = "";

    
    
    
    
    public bool NativeCaptionRegions { get; set; } = false;

    
    
    
    
    
    public string TimerAlarmPath { get; set; } = "";

    
    
    
    
    public bool TimerUseClockBackground { get; set; }

    

    
    
    
    
    
    
    
    
    
    
    
    
    public string GithubDownloadRoute { get; set; } = GithubRoutes.Official;

    
    
    
    
    
    
    
    
    
    
    
    
    public string DownloadFolder { get; set; } = "";

    

    
    public bool AnnounceEnabled { get; set; }

    
    public int AnnounceIntervalSec { get; set; } = 30;

    
    public bool AnnounceQuietMode { get; set; }

    
    public bool AnnounceSpeakText { get; set; } = true;

    
    
    
    
    public string AnnounceDefaultView { get; set; } = "receive";

    
    
    
    
    
    public string AnnounceLabel { get; set; } = "";

    
    public bool AnnounceLanEnabled { get; set; }

    
    public System.Collections.Generic.List<AnnounceSavedPeer> AnnounceSavedPeers { get; set; } = new();

    
    
    
    
    public System.Collections.Generic.List<AnnounceBinder> AnnounceBinders { get; set; } = new();

    
    public System.Collections.Generic.List<PendingUnbind> AnnouncePendingUnbinds { get; set; } = new();

    
    
    
    
    public string AnnounceAnimation { get; set; } = "typewriter";

    
    public string AnnounceSoundPath { get; set; } = "";

    
    
    
    
    
    
    
    
    
    
    public int OobeState { get; set; } = OobeStates.Unknown;

    
    
    
    
    
    
    
    
    
    public string DictionarySource { get; set; } = "offline-first";


    
    public System.Collections.Generic.List<PinnedApp> PinnedApps { get; set; } = new();

    public System.Collections.Generic.Dictionary<string, string> SidebarAppHotkeys { get; set; } = new();

    
    public string HubAiTemplate { get; set; } = "default";

    
    public List<string> DictionaryHistory { get; set; } = new();
}


public sealed class PinnedApp
{
    public string Id { get; set; } = "";

    
    public string Name { get; set; } = "";

    
    public string Path { get; set; } = "";

    
    public string Arguments { get; set; } = "";
}


public static class OobeStates
{
    
    public const int Unknown = 0;

    
    public const int Pending = 1;

    
    public const int Done = 2;
}


public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassSoftwareHub");

    public string FilePath { get; } = Path.Combine(Dir, "settings.json");

    public AppSettings Current { get; private set; } = new();

    
    public void Replace(AppSettings settings)
    {
        Current = settings;
        EnsureNewSidebarModules();
        MigrateSidebarFooter();
        Save();
    }

    
    
    
    
    
    
    
    private bool _loadFailed;

    public void Load()
    {
        try
        {
            var json = ReadTolerant();
            if (json is not null)
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            _loadFailed = false;
        }
        catch (JsonException)
        {
            
            
            KeepCorruptCopy();
            Current = new AppSettings();
            _loadFailed = false;
        }
        catch (Exception ex)
        {
            
            _loadFailed = true;
            Log($"读取设置失败，本次不覆盖原文件: {ex.Message}");
        }

        
        
        if (!Current.UpdateChannelSetByUser)
            Current.UpdateChannel = Core.ShellConfig.DefaultUpdateChannel;

        
        if (!_loadFailed)
        {
            EnsureNewSidebarModules();
            MigrateSidebarFooter();
        }
    }

    
    
    
    
    
    
    
    private void MigrateSidebarFooter()
    {
        if (Current.SidebarFooterEnabled) return;      

        var hidden = (Current.SidebarFooterHidden ?? Array.Empty<string>()).ToList();
        foreach (var key in SidebarFooterKeys.All)
            if (!hidden.Contains(key)) hidden.Add(key);

        Current.SidebarFooterHidden = hidden.ToArray();
        Current.SidebarFooterEnabled = true;           
        Save();
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private void EnsureNewSidebarModules()
    {
        var changed = false;

        if (Current.SidebarModulesRevision < 1)
        {
            try
            {
                var list = (Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();

                if (!list.Contains("volume"))
                {
                    var at = list.FindIndex(id => Data.SidebarModules.Find(id)?.Kind == Data.SidebarModuleKinds.Action);
                    if (at < 0) list.Add("volume");
                    else list.Insert(at, "volume");
                    Current.SidebarModuleIds = list.ToArray();
                }
            }
            catch (Exception ex)
            {
                Log($"补侧边栏新模块失败: {ex.Message}");
            }

            Current.SidebarModulesRevision = 1;
            changed = true;
        }

        if (Current.SidebarModulesRevision < 2)
        {
            try
            {
                var list = (Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();

                if (!list.Contains("brightness"))
                {
                    var at = list.IndexOf("volume");                       
                    if (at >= 0) list.Insert(at + 1, "brightness");
                    else
                    {
                        at = list.FindIndex(id => Data.SidebarModules.Find(id)?.Kind == Data.SidebarModuleKinds.Action);
                        if (at < 0) list.Add("brightness");
                        else list.Insert(at, "brightness");
                    }
                    Current.SidebarModuleIds = list.ToArray();
                }
            }
            catch (Exception ex)
            {
                Log($"补侧边栏新模块失败: {ex.Message}");
            }

            Current.SidebarModulesRevision = 2;
            changed = true;
        }

        
        
        
        if (Current.SidebarModulesRevision < 4)
        {
            
            
            
            
            try
            {
                var list = (Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();

                var removed = list.RemoveAll(id => id is "keyboard" or "mirror-download");
                if (removed > 0)
                {
                    Current.SidebarModuleIds = list.ToArray();
                    Log($"侧边栏清单已剔除 {removed} 个下线模块（虚拟键盘 / 系统镜像下载）");
                }
            }
            catch (Exception ex)
            {
                Log($"清理下线模块失败: {ex.Message}");
            }

            Current.SidebarModulesRevision = 4;
            changed = true;
        }

        if (Current.SidebarModulesRevision < 5)
        {
            
            
            
            try
            {
                var list = (Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();
                var at = list.FindIndex(id => Data.SidebarModules.Find(id)?.Kind == Data.SidebarModuleKinds.Action);

                foreach (var id in new[] { "taskmgr", "security", "clipboard" })
                {
                    if (list.Contains(id)) continue;            
                    if (at < 0) { list.Add(id); at = list.Count; }
                    else { list.Insert(at, id); at++; }
                }

                Current.SidebarModuleIds = list.ToArray();
            }
            catch (Exception ex)
            {
                Log($"补侧边栏新模块失败: {ex.Message}");
            }

            Current.SidebarModulesRevision = 5;
            changed = true;
        }

        if (Current.SidebarModulesRevision < 6)
        {
            
            
            
            try
            {
                var list = (Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();

                if (!list.Contains("killseewo"))
                {
                    var last = list.FindLastIndex(id => Data.SidebarModules.Find(id)?.Kind == Data.SidebarModuleKinds.Action);
                    if (last < 0) list.Add("killseewo");
                    else list.Insert(last + 1, "killseewo");

                    Current.SidebarModuleIds = list.ToArray();
                }
            }
            catch (Exception ex)
            {
                Log($"补侧边栏新模块失败: {ex.Message}");
            }

            Current.SidebarModulesRevision = 6;
            changed = true;
        }

        if (Current.SidebarModulesRevision < 7)
        {
            
            
            
            try
            {
                var list = (Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();

                if (!list.Contains("subject-tools"))
                {
                    var at = list.FindIndex(id =>
                    {
                        var kind = Data.SidebarModules.Find(id)?.Kind;
                        return kind is not (Data.SidebarModuleKinds.Palette
                            or Data.SidebarModuleKinds.Page
                            or Data.SidebarModuleKinds.Window);
                    });

                    if (at < 0) list.Add("subject-tools");
                    else list.Insert(at, "subject-tools");

                    Current.SidebarModuleIds = list.ToArray();
                }
            }
            catch (Exception ex)
            {
                Log($"补侧边栏新模块失败: {ex.Message}");
            }

            Current.SidebarModulesRevision = 7;
            changed = true;
        }

        if (Current.SidebarModulesRevision < 8)
        {
            
            
            
            
            try
            {
                var list = (Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();
                string[] wanted = { "subject-function", "subject-equation", "subject-chemistry" };

                var legacy = list.IndexOf("subject-tools");
                if (legacy >= 0)
                {
                    list.RemoveAt(legacy);
                    list.InsertRange(legacy, wanted.Where(id => !list.Contains(id)));
                }

                foreach (var id in wanted)
                {
                    if (list.Contains(id)) continue;

                    var at = list.FindIndex(x =>
                    {
                        var kind = Data.SidebarModules.Find(x)?.Kind;
                        return kind is not (Data.SidebarModuleKinds.Palette
                            or Data.SidebarModuleKinds.Page
                            or Data.SidebarModuleKinds.Window);
                    });

                    if (at < 0) list.Add(id);
                    else list.Insert(at, id);
                }

                Current.SidebarModuleIds = list.ToArray();
            }
            catch (Exception ex)
            {
                Log($"补侧边栏新模块失败: {ex.Message}");
            }

            Current.SidebarModulesRevision = 8;
            changed = true;
        }

        if (Current.SidebarModulesRevision < 9)
        {
            
            
            
            try
            {
                var list = (Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();

                if (!list.Contains("shutdown"))
                {
                    var at = list.IndexOf("cancelshutdown");
                    if (at < 0) list.Add("shutdown");
                    else list.Insert(at, "shutdown");

                    Current.SidebarModuleIds = list.ToArray();
                }
            }
            catch (Exception ex)
            {
                Log($"补侧边栏新模块失败: {ex.Message}");
            }

            Current.SidebarModulesRevision = 9;
            changed = true;
        }

        if (Current.SidebarModulesRevision < 10)
        {
            
            
            
            
            
            Current.SidebarModulesRevision = 10;
            changed = true;
        }

        if (Current.SidebarModulesRevision < 11)
        {
            
            
            
            
            
            
            
            try
            {
                var list = (Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();
                var kept = list.Where(id => Data.SidebarModules.Find(id) is not null).ToList();

                if (kept.Count != list.Count)
                {
                    Log($"清理侧边栏存档里已删除的模块：{string.Join(", ", list.Except(kept))}");
                    Current.SidebarModuleIds = kept.ToArray();
                }
            }
            catch (Exception ex)
            {
                Log($"清理侧边栏模块失败: {ex.Message}");
            }

            Current.SidebarModulesRevision = 11;
            changed = true;
        }

        if (Current.SidebarModulesRevision < 12)
        {
            
            
            
            
            
            try
            {
                var list = (Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();

                var at = list.FindIndex(id => Data.SidebarModules.Find(id)?.Kind == Data.SidebarModuleKinds.Action);
                foreach (var id in new[] { "explorer", "run", "osk", "lock" })
                {
                    if (list.Contains(id)) continue;            
                    if (at < 0) { list.Add(id); at = list.Count; }
                    else { list.Insert(at, id); at++; }
                }

                if (!list.Contains("ppttouch")) list.Add("ppttouch");

                Current.SidebarModuleIds = list.ToArray();
            }
            catch (Exception ex)
            {
                Log($"补侧边栏新模块失败: {ex.Message}");
            }

            Current.SidebarModulesRevision = 12;
            changed = true;
        }

        if (Current.SidebarModulesRevision < 13)
        {
            
            
            
            
            
            
            try
            {
                var list = (Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();
                var at = list.IndexOf("quickmenu");

                if (at >= 0)
                {
                    list.RemoveAt(at);

                    var slot = at;
                    foreach (var id in new[] { "explorer", "run", "osk", "lock" })
                    {
                        if (list.Contains(id)) continue;
                        list.Insert(slot, id);
                        slot++;
                    }

                    if (!list.Contains("ppttouch")) list.Add("ppttouch");

                    Current.SidebarModuleIds = list.ToArray();
                    Log("侧边栏清单已从「快捷入口」拆回四颗入口 + PPT 触控");
                }
            }
            catch (Exception ex)
            {
                Log($"收拾侧边栏「快捷入口」失败: {ex.Message}");
            }

            Current.SidebarModulesRevision = 13;
            changed = true;
        }

        if (Current.SidebarModulesRevision < 14)
        {
            try
            {
                var list = (Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();
                if (Current.PinnedApps is { Count: > 0 } && !list.Contains("pinned-apps"))
                {
                    list.Insert(0, "pinned-apps");
                    Current.SidebarModuleIds = list.ToArray();
                }
            }
            catch (Exception ex)
            {
                Log($"补侧边栏快捷应用模块失败: {ex.Message}");
            }

            Current.SidebarModulesRevision = 14;
            changed = true;
        }

        
        if (changed) Save();
    }

    
    
    
    
    
    private string? ReadTolerant()
    {
        if (!File.Exists(FilePath)) return null;

        using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    public void Save()
    {
        try
        {
            
            
            if (_loadFailed)
            {
                try
                {
                    var existing = ReadTolerant();
                    if (existing is not null)
                    {
                        Current = JsonSerializer.Deserialize<AppSettings>(existing, JsonOpts) ?? Current;
                        _loadFailed = false;
                    }
                }
                catch
                {
                    Log("设置文件仍然读不到，跳过本次保存");
                    return;
                }
            }

            Directory.CreateDirectory(Dir);

            
            
            
            var json = JsonSerializer.Serialize(Current, JsonOpts);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);

            if (File.Exists(FilePath))
            {
                
                File.Replace(tmp, FilePath, FilePath + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tmp, FilePath);
            }
        }
        catch (Exception ex)
        {
            
            Log("保存设置失败: " + ex.Message);
        }
    }

    private void KeepCorruptCopy()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Copy(FilePath, FilePath + ".bad", overwrite: true);
        }
        catch
        {
            
        }
    }

    private static void Log(string message)
    {
        try
        {
            Core.AppLog.Info("settings", message);
        }
        catch
        {
            
        }
    }
}
