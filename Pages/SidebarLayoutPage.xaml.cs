using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Views;
using ClassSoftwareHub.Desktop.Views.Picker;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.Storage;
using Windows.UI;

namespace ClassSoftwareHub.Desktop.Pages;














public sealed partial class SidebarLayoutPage : Page
{
    
    private const double DragSlop = 4;

    
    private const double GapDip = 59;

    
    private const int TouchHoldMs = 400;

    
    private const double TouchSlop = 12;

    
    
    
    
    
    
    
    private const int TouchStillMs = 150;

    
    private const double StillSlop = 3;

    
    
    
    
    private const int TouchHoldMaxMs = 900;

    
    private const int GateTickMs = 60;

    
    private const int MorphMs = 190;

    
    private const int SwapMs = 250;

    
    private const int SlotMs = 180;

    
    private const int FlightMs = 240;

    
    
    
    
    
    private const int SettleMs = 190;

    
    
    
    
    
    private const double DipScale = 0.972;

    
    private const double DipAt = 0.38;

    
    private const double LiftScale = 1.035;

    
    private const double TileW = 96;
    private const double TileH = 56;

    
    private readonly List<string> _ids = new();

    
    
    private readonly List<FrameworkElement> _tiles = new();

    private string? _dragId;                
    private bool _dragFromPreview;          
    private bool _dragging;                 
    private uint _pointerId;
    private Point _pressInRoot;
    private FrameworkElement? _pressSource;
    private Border? _ghost;                 
    private Microsoft.UI.Composition.Visual? _ghostVisual;  
    private Border? _ghostRing;             
    private double _dropDip = DipScale;     
    private Border? _hole;                  
    private Border? _pad;                   
    private FrameworkElement? _hiddenRow;   
    private int _holeSlot = -1;             
    private int _dragSrcIndex = -1;         
    private double _rowsTop;                
    private bool _swapping;                 
    private bool _footerSync;               
    
    
    
    private bool _toolSync = true;
    private string? _recordingAppId;
    private Button? _recordingAppButton;
    private DispatcherQueueTimer? _appHotkeyTimer;

    
    
    
    
    
    private DispatcherQueueTimer? _heightSaveTimer;
    private bool _edgeRebuild;              
    private double _pageScrollOffset;       
    private double _maxMove;                

    
    
    
    
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _settleTimer;

    

    private bool _isTouch;                  
    private bool _armed;                    
    private bool _ending;                   
    private bool _latchScroll;              
    private Pointer? _heldPointer;          
    private DispatcherQueueTimer? _holdTimer;
    private DateTime _downAt;               
    private DateTime _lastMoveAt;           

    

    private double _srcW, _srcH;            
    private Point _grabRatio;               
    private Point _pointerInLayer;          
    private FrameworkElement? _ghostCard;   
    private FrameworkElement? _ghostTile;   
    private bool _ghostTileMode;            
    private bool _flying;                   
    private bool _ghostFrameHooked;         

    public SidebarLayoutPage()
    {
        InitializeComponent();

        
        
        Core.WheelRelay.Attach(this, PageScroll);

        LoadFromSettings();

        
        
        
        WorkspaceRoot.AddHandler(PointerMovedEvent, new PointerEventHandler(OnRootPointerMoved), true);

        
        
        WorkspaceRoot.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnRootPointerReleased), true);
        WorkspaceRoot.AddHandler(PointerPressedEvent, new PointerEventHandler(OnRootPointerPressed), true);
        WorkspaceRoot.AddHandler(KeyDownEvent, new KeyEventHandler(OnAppHotkeyKeyDown), true);

        
        
        
        Loaded += (_, _) =>
        {
            Services.ThemeBrush.Probe(this, "SidebarLayoutPage.Loaded");
            InitToolSettings();
            Refresh();
            SettleScrollToTop();
        };
        ActualThemeChanged += (_, _) => Refresh();

        
        Unloaded += (_, _) =>
        {
            _settleTimer?.Stop();
            _settleTimer = null;
            _tweens.Clear();            
            UnhookTweenFrame();
            StopTweenWatch();
        };
    }

    

    private void LoadFromSettings()
    {
        _ids.Clear();
        foreach (var id in App.Settings.Current.SidebarModuleIds ?? Array.Empty<string>())
        {
            
            if (SidebarModules.Find(id) is not null && !_ids.Contains(id)) _ids.Add(id);
        }
    }

    private void Save()
    {
        App.Settings.Current.SidebarModuleIds = _ids.ToArray();
        App.Settings.Save();
        ToolSidebarWindow.ApplyModules();      
    }

    private void Refresh()
    {
        BuildLibrary();
        BuildPreview();
        RefreshPinnedSettings();

        if (SidebarButton is not null)
            SidebarButton.Content = ToolSidebarWindow.IsSidebarVisible ? "隐藏侧边栏" : "显示侧边栏";

        SyncFooterToggles();
        SyncToolSettings();
    }

    
    
    
    
    
    private void InitToolSettings()
    {
        var s = App.Settings.Current;

        
        
        if (s.SidebarEdge is not ("left" or "both" or "right"))
        {
            s.SidebarEdge = "right";
            App.Settings.Save();
        }

        
        if (s.SidebarFreeEdge is not ("left" or "right" or "top" or "bottom"))
        {
            s.SidebarFreeEdge = "right";
            App.Settings.Save();
        }

        RebuildEdgeCombo();
        UpdateSidebarHints();
    }

    
    
    
    
    
    
    
    private void SyncToolSettings()
    {
        var s = App.Settings.Current;

        _toolSync = true;
        try
        {
            if (PaletteTopSwitch is not null) PaletteTopSwitch.IsOn = s.PaletteOnTop;
            if (SidebarSwitch is not null) SidebarSwitch.IsOn = s.SidebarEnabled;
            if (LongLabelSwitch is not null) LongLabelSwitch.IsOn = s.SidebarLongLabels;
            if (SidebarModeCombo is not null)
            {
                var index = s.SidebarMode == "free" ? 1 : 0;
                if (SidebarModeCombo.SelectedIndex != index) SidebarModeCombo.SelectedIndex = index;
            }

            
            
            if (SidebarDensityCombo is not null)
            {
                var index = s.SidebarDensity == "compact" ? 1 : 0;
                if (SidebarDensityCombo.SelectedIndex != index) SidebarDensityCombo.SelectedIndex = index;
            }
            if (SidebarExpandedDensityCombo is not null)
            {
                
                var index = s.SidebarExpandedDensity switch
                {
                    "standard" => 1,
                    "compact" => 2,
                    _ => 0,
                };
                if (SidebarExpandedDensityCombo.SelectedIndex != index) SidebarExpandedDensityCombo.SelectedIndex = index;
            }
            if (SidebarColumnsCombo is not null)
            {
                
                
                
                var index = s.SidebarColumns == 3 ? 0 : 1;
                if (SidebarColumnsCombo.SelectedIndex != index) SidebarColumnsCombo.SelectedIndex = index;
            }
            if (SidebarPinnedPositionCombo is not null)
            {
                var index = string.Equals(s.SidebarPinnedPosition, "top", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                if (SidebarPinnedPositionCombo.SelectedIndex != index) SidebarPinnedPositionCombo.SelectedIndex = index;
            }
            if (SidebarGapCombo is not null)
            {
                var index = GapIndex(s.SidebarCollapsedGap);
                if (SidebarGapCombo.SelectedIndex != index) SidebarGapCombo.SelectedIndex = index;
            }

            
            
            if (HeightRatioSlider is not null)
            {
                var raw = s.SidebarHeightRatio;
                if (double.IsNaN(raw) || raw <= 0) raw = ToolSidebarWindow.DefaultHeightRatio;
                var percent = Math.Clamp((int)Math.Round(raw * 100), 45, 95);
                if (Math.Abs(HeightRatioSlider.Value - percent) > 0.01) HeightRatioSlider.Value = percent;
                if (HeightRatioText is not null) HeightRatioText.Text = percent + "%";
            }

            
            if (HeightAutoSwitch is not null) HeightAutoSwitch.IsOn = s.SidebarHeightAuto;

            
            _dropDip = DropDipScale(s.SidebarDropAnim);
            if (DropAnimCombo is not null)
            {
                var index = _dropDip < 0.999 ? 1 : 0;
                if (DropAnimCombo.SelectedIndex != index) DropAnimCombo.SelectedIndex = index;
            }

            
            if (ShotAutoSaveSwitch is not null) ShotAutoSaveSwitch.IsOn = s.ShotAutoSave;
            if (ShotDirText is not null) RefreshShotDir();

            
            if (ShutdownModeCombo is not null)
            {
                var index = App.Settings.Current.ShutdownMode == "shutdown" ? 1 : 0;
                if (ShutdownModeCombo.SelectedIndex != index) ShutdownModeCombo.SelectedIndex = index;
            }
        }
        finally { _toolSync = false; }
    }

    
    
    
    
    
    private void SyncFooterToggles()
    {
        var hidden = App.Settings.Current.SidebarFooterHidden ?? Array.Empty<string>();

        _footerSync = true;
        try
        {
            
            if (FoldToggle is not null) FoldToggle.IsOn = !hidden.Contains(SidebarFooterKeys.Fold);
            if (PinToggle is not null) PinToggle.IsOn = !hidden.Contains(SidebarFooterKeys.Pin);
            if (ResetToggle is not null) ResetToggle.IsOn = !hidden.Contains(SidebarFooterKeys.Reset);
            if (HideToggle is not null) HideToggle.IsOn = !hidden.Contains(SidebarFooterKeys.Hide);
            if (OpenAppToggle is not null) OpenAppToggle.IsOn = !hidden.Contains(SidebarFooterKeys.OpenApp);
        }
        finally { _footerSync = false; }
    }

    
    
    
    
    
    
    
    private void SettleScrollToTop()
    {
        _settleTimer ??= DispatcherQueue.CreateTimer();
        _settleTimer.Interval = TimeSpan.FromMilliseconds(220);
        _settleTimer.IsRepeating = false;
        _settleTimer.Tick -= OnSettleTick;
        _settleTimer.Tick += OnSettleTick;
        _settleTimer.Start();
    }

    private void OnSettleTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        PageScroll.ChangeView(null, 0, null, true);
    }

    private void AddToEnd(string id)
    {
        if (_ids.Contains(id)) return;
        _ids.Add(id);
        Save();
        Refresh();
    }

    

    
    
    
    
    
    
    
    
    
    private Brush Br(string shortName)
    {
        var key = shortName switch
        {
            "ModCardBg" => "CardBackgroundFillColorDefaultBrush",
            "ModCardStroke" => "CardStrokeColorDefaultBrush",
            "ModHoverBg" => "SubtleFillColorSecondaryBrush",
            "ModAccent" => "AccentFillColorDefaultBrush",
            "ModTextDim" => "TextFillColorSecondaryBrush",
            _ => "TextFillColorPrimaryBrush",
        };

        return Services.ThemeBrush.Get(this, key);
    }

    
    private static IEnumerable<ResourceDictionary> ResourceDictionaries()
    {
        var root = Application.Current.Resources;
        yield return root;

        foreach (var md in root.MergedDictionaries)
        {
            yield return md;
            foreach (var nested in md.MergedDictionaries) yield return nested;
        }
    }

    private bool Dark => ActualTheme == ElementTheme.Dark
        || (ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);

    private static Color AccentColor() =>
        new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);

    private void RefreshPinnedSettings()
    {
        if (PinnedAppsPanel is null) return;

        PinnedAppLauncher.EnsureIds();
        var apps = App.Settings.Current.PinnedApps ?? new List<PinnedApp>();
        PinnedAppsPanel.Children.Clear();
        AppHotkeyPanel.Children.Clear();

        if (apps.Count == 0)
        {
            PinnedAppsPanel.Children.Add(new TextBlock
            {
                Text = "还没有快捷应用。",
                FontSize = 12,
                Opacity = 0.6,
            });
            AppHotkeyPanel.Children.Add(new TextBlock
            {
                Text = "添加快捷应用后即可为它录制组合键。",
                FontSize = 12,
                Opacity = 0.6,
            });
            PinnedStatusText.Text = "";
            AppHotkeyStatusText.Text = "";
            return;
        }

        foreach (var app in apps)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = PinnedAppLauncher.DisplayName(app),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            ToolTipService.SetToolTip(name, app.Path);
            row.Children.Add(name);

            var edit = new Button
            {
                Content = "编辑",
                Tag = app.Id,
                MinWidth = 0,
                FontSize = 12,
            };
            edit.Click += EditPinned_Click;
            Grid.SetColumn(edit, 1);
            row.Children.Add(edit);

            var remove = new Button
            {
                Content = "移除",
                Tag = app.Id,
                MinWidth = 0,
                FontSize = 12,
            };
            remove.Click += RemovePinned_Click;
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);

            PinnedAppsPanel.Children.Add(row);

            var hotkeyRow = new Grid { ColumnSpacing = 8 };
            hotkeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hotkeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hotkeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hotkeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var hotkeyName = new TextBlock
            {
                Text = PinnedAppLauncher.DisplayName(app),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            hotkeyRow.Children.Add(hotkeyName);

            var status = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
            };
            var hotkey = AppHotkeyText(app.Id);
            var error = App.MainWindow?.HotkeyTargetError(app.Id);
            if (!App.Settings.Current.HotkeysEnabled)
            {
                status.Text = "总开关关闭";
                status.Foreground = new SolidColorBrush(Color.FromArgb(255, 190, 153, 92));
            }
            else if (error is not null)
            {
                status.Text = "未生效";
                status.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 124, 124));
                ToolTipService.SetToolTip(status, error);
            }
            else if (hotkey == "设置快捷键")
            {
                status.Text = "未设置";
                status.Foreground = new SolidColorBrush(Color.FromArgb(255, 142, 160, 188));
            }
            else
            {
                status.Text = "已生效";
                status.Foreground = new SolidColorBrush(Color.FromArgb(255, 110, 201, 151));
            }
            Grid.SetColumn(status, 1);
            hotkeyRow.Children.Add(status);

            var record = new Button
            {
                Content = hotkey,
                Tag = app.Id,
                MinWidth = 96,
                FontSize = 12,
            };
            record.Click += AppHotkey_Click;
            record.LostFocus += AppHotkeyButton_LostFocus;
            Grid.SetColumn(record, 2);
            hotkeyRow.Children.Add(record);

            var clear = new Button
            {
                Content = "清除",
                Tag = app.Id,
                MinWidth = 0,
                FontSize = 12,
            };
            clear.Click += ClearAppHotkey_Click;
            Grid.SetColumn(clear, 3);
            hotkeyRow.Children.Add(clear);

            AppHotkeyPanel.Children.Add(hotkeyRow);
        }

        var conflicts = App.MainWindow?.HotkeyConflicts;
        AppHotkeyStatusText.Text = conflicts is { Count: > 0 }
            ? "未生效：" + string.Join("；", conflicts)
            : "组合键已写入，录制后立即注册。";
    }

    private string AppHotkeyText(string appId)
        => App.Settings.Current.SidebarAppHotkeys?.TryGetValue(appId, out var value) == true && value.Length > 0
            ? value
            : "设置快捷键";

    private async void AddPinnedApp_Click(object sender, RoutedEventArgs e) => await ShowAppPickerAsync();

    private async void AddPinnedShortcut_Click(object sender, RoutedEventArgs e) => await PickAndAddAsync(".lnk", "选择快捷方式");

    private async void AddPinnedFile_Click(object sender, RoutedEventArgs e) => await PickAndAddAsync("*", "选择文件");

    private async Task PickAndAddAsync(string filter, string title)
    {
        try
        {
            var path = await PickFileAsync(filter, title);
            if (path is null) return;

            if (!AddPinnedPath(path, null))
            {
                PinnedStatusText.Text = "这个程序已经在快捷应用里了。";
                return;
            }

            AfterPinnedChanged();
            PinnedStatusText.Text = "已添加：" + Path.GetFileName(path);
        }
        catch (Exception ex)
        {
            PinnedStatusText.Text = "添加失败：" + ex.Message;
        }
    }

    private async Task<string?> PickFileAsync(string filter, string title)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            CommitButtonText = title,
        };
        picker.FileTypeFilter.Add(filter);
        Core.WindowHandle.BindPicker(picker, this);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>加入一个快捷应用；已经在列表里则返回 false。</summary>
    private bool AddPinnedPath(string path, string? name)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var apps = App.Settings.Current.PinnedApps;
        if (apps.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase))) return false;

        apps.Add(new PinnedApp
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name!,
            Path = path,
        });
        return true;
    }

    /// <summary>保存并把改动同步到侧边栏与快捷键。</summary>
    private void AfterPinnedChanged()
    {
        App.Settings.Save();

        if (!_ids.Contains("pinned-apps"))
        {
            _ids.Insert(0, "pinned-apps");
            Save();
        }
        else
        {
            ToolSidebarWindow.ApplyModules();
        }

        App.MainWindow?.RefreshHotkeys();
        Refresh();
    }

    /// <summary>
    /// 「添加应用」：先列出开始菜单与桌面上的应用供多选，也可以改成自己挑文件。
    /// 教室里装的东西五花八门，让用户自己找 exe 路径太费劲（2026-11-22）。
    /// </summary>
    private async Task ShowAppPickerAsync()
    {
        List<ScannedApp> scanned;
        try
        {
            PinnedStatusText.Text = "正在扫描本机应用…";
            scanned = await Task.Run(InstalledAppScanner.Scan);
        }
        catch (Exception ex)
        {
            PinnedStatusText.Text = "扫描失败：" + ex.Message;
            return;
        }

        var dialog = new AppPickerDialog(scanned) { XamlRoot = XamlRoot };
        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Secondary)
        {
            var path = await PickFileAsync("*", "选择应用");
            if (path is null) return;

            if (!AddPinnedPath(path, null))
            {
                PinnedStatusText.Text = "这个程序已经在快捷应用里了。";
                return;
            }

            AfterPinnedChanged();
            PinnedStatusText.Text = "已添加：" + Path.GetFileName(path);
            return;
        }

        if (result != ContentDialogResult.Primary) return;

        var chosen = dialog.Selected;
        if (chosen.Count == 0) return;

        var added = chosen.Count(a => AddPinnedPath(a.Path, a.Name));
        if (added == 0)
        {
            PinnedStatusText.Text = "选中的应用都已经在快捷应用里了。";
            return;
        }

        AfterPinnedChanged();
        PinnedStatusText.Text = added == 1
            ? "已添加：" + chosen[0].Name
            : $"已添加 {added} 个应用。";
    }

    private async void EditPinned_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id) return;
        var app = App.Settings.Current.PinnedApps.FirstOrDefault(a => a.Id == id);
        if (app is null) return;

        var nameBox = new TextBox
        {
            Text = PinnedAppLauncher.DisplayName(app),
            Header = "显示名称",
            MaxLength = 60,
        };
        var argsBox = new TextBox
        {
            Text = app.Arguments ?? "",
            Header = "启动参数（可选）",
            PlaceholderText = "例如 /min 或 --profile work",
            MaxLength = 400,
        };
        var pathText = new TextBlock
        {
            Text = app.Path,
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(nameBox);
        content.Children.Add(argsBox);
        content.Children.Add(pathText);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "编辑快捷应用",
            Content = content,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        catch (Exception ex)
        {
            PinnedStatusText.Text = "打开编辑窗口失败：" + ex.Message;
            return;
        }

        var name = nameBox.Text.Trim();
        app.Name = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileNameWithoutExtension(app.Path)
            : name;
        app.Arguments = argsBox.Text.Trim();
        App.Settings.Save();
        ToolSidebarWindow.ApplyModules();
        RefreshPinnedSettings();
        PinnedStatusText.Text = "已保存：" + PinnedAppLauncher.DisplayName(app);
    }

    private void AppHotkeyButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_recordingAppButton != sender) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_recordingAppButton != sender) return;
            CancelAppHotkeyRecord();
            RefreshPinnedSettings();
            AppHotkeyStatusText.Text = "焦点离开录制按钮，已取消。";
        });
    }

    private void RemovePinned_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id) return;
        if (!PinnedAppLauncher.RemoveById(id)) return;

        App.MainWindow?.RefreshHotkeys();
        ToolSidebarWindow.ApplyModules();
        Refresh();
        PinnedStatusText.Text = "已移除。";
    }

    private void ClearAppHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id) return;
        App.Settings.Current.SidebarAppHotkeys?.Remove(id);
        App.Settings.Save();
        App.MainWindow?.RefreshHotkeys();
        RefreshPinnedSettings();
        AppHotkeyStatusText.Text = "已清除该应用的组合键。";
    }

    private void AppHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id) return;

        CancelAppHotkeyRecord();

        if (!App.Settings.Current.HotkeysEnabled)
        {
            App.Settings.Current.HotkeysEnabled = true;
            App.Settings.Save();
            App.MainWindow?.RefreshHotkeys();
        }

        _recordingAppId = id;
        _recordingAppButton = button;
        button.Content = "按下组合键…";
        button.Focus(FocusState.Programmatic);
        App.MainWindow?.SuspendHotkeys();
        AppHotkeyStatusText.Text = "正在录制：按下想用的组合键，Esc 取消。";

        _appHotkeyTimer ??= DispatcherQueue.CreateTimer();
        _appHotkeyTimer.Interval = TimeSpan.FromSeconds(5);
        _appHotkeyTimer.IsRepeating = false;
        _appHotkeyTimer.Tick -= OnAppHotkeyTimeout;
        _appHotkeyTimer.Tick += OnAppHotkeyTimeout;
        _appHotkeyTimer.Start();
    }

    private void OnAppHotkeyTimeout(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_recordingAppId is null) return;
        CancelAppHotkeyRecord();
        RefreshPinnedSettings();
        AppHotkeyStatusText.Text = "录制超时，未设置新组合键。";
    }

    private void CancelAppHotkeyRecord()
    {
        _appHotkeyTimer?.Stop();

        var wasRecording = _recordingAppId is not null;
        if (_recordingAppButton is not null && _recordingAppId is not null)
            _recordingAppButton.Content = AppHotkeyText(_recordingAppId);

        _recordingAppId = null;
        _recordingAppButton = null;

        if (wasRecording) App.MainWindow?.RefreshHotkeys();
    }

    private void OnAppHotkeyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_recordingAppId is null) return;

        e.Handled = true;

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CancelAppHotkeyRecord();
            RefreshPinnedSettings();
            AppHotkeyStatusText.Text = "已取消。";
            return;
        }

        var vk = (uint)e.Key;
        if (HotkeyCombo.IsModifierKey(vk)) return;

        var modifiers = CurrentHotkeyModifiers();
        if (modifiers == 0) return;

        if (!HotkeyCombo.TryBuild(modifiers, vk, out var text, out var error))
        {
            AppHotkeyStatusText.Text = error ?? "这个键不能用。";
            return;
        }

        var id = _recordingAppId;
        App.Settings.Current.SidebarAppHotkeys ??= new Dictionary<string, string>();
        App.Settings.Current.SidebarAppHotkeys[id] = text;
        App.Settings.Save();
        CancelAppHotkeyRecord();
        App.MainWindow?.RefreshHotkeys();
        RefreshPinnedSettings();
        AppHotkeyStatusText.Text = "已设置组合键：" + text;
    }

    private static uint CurrentHotkeyModifiers()
    {
        uint mods = 0;
        try
        {
            if (IsHotkeyDown(Windows.System.VirtualKey.Control)) mods |= HotkeyCombo.ModControl;
            if (IsHotkeyDown(Windows.System.VirtualKey.Menu)) mods |= HotkeyCombo.ModAlt;
            if (IsHotkeyDown(Windows.System.VirtualKey.Shift)) mods |= HotkeyCombo.ModShift;
            if (IsHotkeyDown(Windows.System.VirtualKey.LeftWindows)
                || IsHotkeyDown(Windows.System.VirtualKey.RightWindows)) mods |= HotkeyCombo.ModWin;
        }
        catch { }
        return mods;
    }

    private static bool IsHotkeyDown(Windows.System.VirtualKey key)
        => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down)
           == Windows.UI.Core.CoreVirtualKeyStates.Down;

    

    private void BuildLibrary()
    {
        LibraryPanel.Children.Clear();

        Grid? row = null;
        var col = 0;
        foreach (var m in SidebarModules.All)
        {
            if (col == 0)
            {
                row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                LibraryPanel.Children.Add(row);
            }

            var card = BuildLibraryCard(m, _ids.Contains(m.Id));
            Grid.SetColumn(card, col);
            row!.Children.Add(card);
            col = col == 0 ? 1 : 0;
        }
    }

    
    private Border BuildLibraryCard(SidebarModule m, bool used)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = m.Name,
            FontSize = 14.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Br("ModText")
        });
        text.Children.Add(new TextBlock
        {
            Text = used ? "已在侧边栏中" : m.Hint,
            FontSize = 11.5,
            Opacity = 0.65,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Br("ModTextDim")
        });

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new FontIcon
        {
            Glyph = m.Glyph,
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var badge = new FontIcon
        {
            Glyph = used ? "\uE73E" : "\uE710",        
            FontSize = 13,
            Opacity = used ? 0.55 : 0.8,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(badge, 2);
        grid.Children.Add(badge);

        var card = new Border
        {
            Height = 72,
            Padding = new Thickness(14, 0, 14, 0),
            Background = Br("ModCardBg"),
            BorderBrush = Br("ModCardStroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Tag = m.Id,
            Opacity = used ? 0.5 : 1,
            Child = grid
        };
        ToolTipService.SetToolTip(card, used ? $"{m.Name}（已在侧边栏中）" : $"{m.Name}：单击加入侧边栏，亦可拖入右侧");

        if (used) return card;                          

        card.PointerEntered += (_, _) => card.BorderBrush = Br("ModAccent");
        card.PointerExited += (_, _) => card.BorderBrush = Br("ModCardStroke");
        card.PointerPressed += (s, e) => BeginPress(s, e, m.Id, false);
        card.PointerMoved += OnPointerMoved;
        card.PointerReleased += OnPointerReleased;
        card.PointerCaptureLost += (_, _) => CancelDrag();
        
        
        card.PointerCanceled += (_, _) => CancelDrag();
        return card;
    }

    

    private void BuildPreview()
    {
        PreviewPanel.Children.Clear();
        HoleLayer.Children.Clear();
        _tiles.Clear();
        _hole = null;
        _tweens.Clear();        
        UnhookTweenFrame();     
        StopTweenWatch();

        
        PreviewPanel.Children.Add(new TextBlock
        {
            Text = "常用工具",
            FontSize = 10,
            Opacity = 0.55,
            Width = 96,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 2)
        });

        foreach (var id in _ids)
        {
            if (SidebarModules.Find(id) is { } m) PreviewPanel.Children.Add(BuildRow(m));
        }

        if (_ids.Count == 0)
        {
            PreviewPanel.Children.Add(new TextBlock
            {
                Text = "将左侧\n卡片拖入此处",
                FontSize = 10.5,
                Opacity = 0.5,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Width = 96,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(4, 10, 4, 10)
            });
        }

        
        
        _pad = new Border
        {
            Width = TileW,
            Height = 0,
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        PreviewPanel.Children.Add(_pad);

        
        _holeSlot = -1;
        _hiddenRow = null;
        _dragSrcIndex = -1;

        
    }

    
    
    
    
    
    
    
    
    private FrameworkElement BuildRow(SidebarModule m)
    {
        var index = _ids.IndexOf(m.Id);
        var tile = BuildTile(m);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        actions.Children.Add(ActionButton("\uE70E", "上移一位", m.Id, MoveUp_Click, index > 0));
        actions.Children.Add(ActionButton("\uE70D", "下移一位", m.Id, MoveDown_Click, index < _ids.Count - 1));
        actions.Children.Add(ActionButton("\uE711", "从侧边栏移除", m.Id, Remove_Click, true));

        var row = new Grid { Height = TileH, ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(tile);
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);

        _tiles.Add(row);
        return row;
    }

    
    private Button ActionButton(string glyph, string tip, string id, RoutedEventHandler onClick, bool enabled)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 15 },
            Width = 38,
            Height = 38,
            MinWidth = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Tag = id,
            IsEnabled = enabled,
            Opacity = enabled ? 0.85 : 0.28
        };
        ToolTipService.SetToolTip(b, tip);
        b.PointerEntered += (_, _) => { if (enabled) b.Background = Br("ModHoverBg"); };
        b.PointerExited += (_, _) => b.Background = new SolidColorBrush(Colors.Transparent);
        b.Click += onClick;
        return b;
    }

    
    private Border BuildTile(SidebarModule m)
    {
        var content = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new FontIcon
        {
            Glyph = m.Glyph,
            FontSize = 19,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = m.ShortName,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Br("ModText")
        });

        var tile = new Border
        {
            Width = TileW,
            Height = TileH,
            CornerRadius = new CornerRadius(6),
            Background = Br("ModCardBg"),
            BorderBrush = Br("ModCardStroke"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 0, 4, 0),
            Tag = m.Id,
            Child = content
        };
        ToolTipService.SetToolTip(tile, $"{m.Name}：拖动可调整顺序，拖至别处松手即移除");

        tile.PointerEntered += (_, _) =>
        {
            tile.Background = Br("ModHoverBg");
            tile.BorderBrush = Br("ModAccent");
        };
        tile.PointerExited += (_, _) =>
        {
            tile.Background = Br("ModCardBg");
            tile.BorderBrush = Br("ModCardStroke");
        };
        tile.PointerPressed += (s, e) => BeginPress(s, e, m.Id, true);
        tile.PointerMoved += OnPointerMoved;
        tile.PointerReleased += OnPointerReleased;
        tile.PointerCaptureLost += (_, _) => CancelDrag();
        tile.PointerCanceled += (_, _) => CancelDrag();

        return tile;
    }


    
    private Border BuildFixedTile(string glyph, string label)
    {
        var content = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 16,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Br("ModTextDim")
        });

        var tile = new Border
        {
            Width = 96,
            Height = 46,
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = content
        };
        ToolTipService.SetToolTip(tile, "侧边栏自带按钮，不参与拼接");
        return tile;
    }

    

    private void BeginPress(object sender, PointerRoutedEventArgs e, string id, bool fromPreview)
    {
        if (_flying) return;                            
        if (InsideButton(e.OriginalSource)) return;     
        if (sender is not FrameworkElement src) return;
        if (_swapping) return;                          

        Log($"按下 {id} 来源={(fromPreview ? "预览" : "模块库")} 行数={_tiles.Count} 设备={e.Pointer.PointerDeviceType}");

        _dragId = id;
        _dragFromPreview = fromPreview;
        _dragging = false;
        _armed = false;
        _holeSlot = -1;
        _hiddenRow = null;
        _dragSrcIndex = fromPreview ? _ids.IndexOf(id) : -1;
        _pressSource = src;
        _pointerId = e.Pointer.PointerId;
        _heldPointer = e.Pointer;
        _pressInRoot = e.GetCurrentPoint(WorkspaceRoot).Position;
        _maxMove = 0;

        
        
        
        _pageScrollOffset = PageScroll.VerticalOffset;

        var inSrc = e.GetCurrentPoint(src).Position;

        
        _srcW = src.ActualWidth;
        _srcH = src.ActualHeight;
        _grabRatio = new Point(
            _srcW > 1 ? Math.Clamp(inSrc.X / _srcW, 0, 1) : 0.5,
            _srcH > 1 ? Math.Clamp(inSrc.Y / _srcH, 0, 1) : 0.5);

        MeasureRowsTop();

        _isTouch = e.Pointer.PointerDeviceType == PointerDeviceType.Touch;

        if (_isTouch)
        {
            
            
            
            HintPress(true);
            _downAt = DateTime.UtcNow;
            _lastMoveAt = _downAt;
            ArmHoldTimer();
            return;
        }

        
        _armed = true;
        LockScroll();
        src.CapturePointer(e.Pointer);
        e.Handled = true;                              
    }

    
    
    
    
    
    
    private void OnHoldTick(DispatcherQueueTimer sender, object args)
    {
        if (_dragId is null || _pressSource is null || _armed || _heldPointer is null)
        {
            sender.Stop();
            return;
        }

        if (_maxMove > TouchSlop)
        {
            Log($"触摸闸门：手指已移动 {_maxMove:F0}px -> 判定为滚动，放弃拖 {_dragId}");
            sender.Stop();
            CancelPress();
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - _downAt).TotalMilliseconds < TouchHoldMs) return;              

        var still = (now - _lastMoveAt).TotalMilliseconds >= TouchStillMs;
        if (!still && (now - _downAt).TotalMilliseconds < TouchHoldMaxMs) return; 

        sender.Stop();
        _armed = true;
        LockScroll();

        
        
        
        
        
        
        
        try { _pressSource.CancelDirectManipulations(); } catch { }
        try { _pressSource.CapturePointer(_heldPointer); } catch { }
        Log($"触摸闸门到点 -> 接管 {_dragId}");
    }

    
    
    
    
    
    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragId is null || e.Pointer.PointerId != _pointerId) return;
        if (_armed) return;                             

        var p = e.GetCurrentPoint(WorkspaceRoot).Position;
        var d = Math.Abs(p.X - _pressInRoot.X) + Math.Abs(p.Y - _pressInRoot.Y);
        if (d > _maxMove + StillSlop)                   
        {
            _maxMove = d;
            _lastMoveAt = DateTime.UtcNow;
        }

        if (_isTouch && _maxMove > TouchSlop) CancelPress();   
    }

    
    private void CancelPress()
    {
        _holdTimer?.Stop();
        HintPress(false);
        _dragId = null;
        _dragging = false;
        _armed = false;
        _pressSource = null;
        _heldPointer = null;
        _holeSlot = -1;
        _dragSrcIndex = -1;
        _maxMove = 0;
    }

    
    private void HintPress(bool on)
    {
        if (_pressSource is null) return;
        _pressSource.Opacity = on ? 0.75 : 1;
    }

    private void ArmHoldTimer()
    {
        _holdTimer ??= DispatcherQueue.CreateTimer();
        _holdTimer.Interval = TimeSpan.FromMilliseconds(GateTickMs);
        _holdTimer.IsRepeating = true;                 
        _holdTimer.Tick -= OnHoldTick;                 
        _holdTimer.Tick += OnHoldTick;
        _holdTimer.Start();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragId is null || e.Pointer.PointerId != _pointerId) return;
        if (!_armed) return;                            

        var inRoot = e.GetCurrentPoint(WorkspaceRoot).Position;
        if (!_dragging)
        {
            if (Math.Abs(inRoot.X - _pressInRoot.X) + Math.Abs(inRoot.Y - _pressInRoot.Y) < DragSlop) return;

            _dragging = true;
            if (_dragFromPreview) HideSourceRow();       
            else if (_pressSource is not null) _pressSource.Opacity = 0.45;   
            ShowGhost();
        }

        e.Handled = true;
        _pointerInLayer = e.GetCurrentPoint(DragLayer).Position;
        PlaceGhost();                                   

        var inStrip = e.GetCurrentPoint(PreviewStrip).Position;
        var inside = inStrip.X >= 0 && inStrip.Y >= 0
                     && inStrip.X <= PreviewStrip.ActualWidth && inStrip.Y <= PreviewStrip.ActualHeight;

        UpdateHole(inside ? SlotFromPointer(e.GetCurrentPoint(PreviewPanel).Position.Y) : -1);

        
        if (!_dragFromPreview) MorphGhost(inside);

        
        
        
        if (!inside && _dragFromPreview)
        {
            var overLib = IsOverLibrary(e.GetCurrentPoint(WorkspaceRoot).Position);
            RemoveHintText.Text = overLib ? "松手即从侧边栏移除" : "松手取消，卡片回到原位";
            RemoveHintIcon.Glyph = overLib ? "\uE74D" : "\uE711";     
            RemoveHint.Visibility = Visibility.Visible;
        }
        else
        {
            RemoveHint.Visibility = Visibility.Collapsed;
        }
    }

    
    
    
    
    
    
    
    
    private void HideSourceRow()
    {
        if (_dragSrcIndex < 0 || _dragSrcIndex >= _tiles.Count) return;

        _hiddenRow = _tiles[_dragSrcIndex];
        _hiddenRow.Opacity = 0;
        _hiddenRow.Height = 0;

        
        _holeSlot = _dragSrcIndex;
        ApplyRowOffsets(animate: false);
        AnimatePad();
        ShowHole();
    }

    
    private void RestoreHiddenRow()
    {
        if (_hiddenRow is not null)
        {
            _hiddenRow.Height = TileH;
            _hiddenRow.Opacity = 1;
            _hiddenRow = null;
        }
        foreach (var t in _tiles)
        {
            CancelTween(t);
            SetRowShift(t, 0);
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragId is null || e.Pointer.PointerId != _pointerId) return;

        if (!_armed)
        {
            
            var tapId = _dragId;
            var tapFromPreview = _dragFromPreview;
            CancelPress();
            if (!tapFromPreview) AddToEnd(tapId);
            return;
        }

        var id = _dragId;
        var fromPreview = _dragFromPreview;
        var dragged = _dragging;

        
        var src = _pressSource;                     
        var homeRow = _hiddenRow;                   
        var homeIndex = _dragSrcIndex;

        var inStrip = e.GetCurrentPoint(PreviewStrip).Position;
        var inside = inStrip.X >= 0 && inStrip.Y >= 0
                     && inStrip.X <= PreviewStrip.ActualWidth && inStrip.Y <= PreviewStrip.ActualHeight;

        
        var index = inside
            ? InsertIndexFromSlot(SlotFromPointer(e.GetCurrentPoint(PreviewPanel).Position.Y))
            : -1;

        
        var overLibrary = !inside && IsOverLibrary(e.GetCurrentPoint(WorkspaceRoot).Position);

        
        
        
        _ending = true;
        (_pressSource as UIElement)?.ReleasePointerCapture(e.Pointer);
        _ending = false;

        if (!dragged)
        {
            EndDrag();
            if (!fromPreview) AddToEnd(id);             
            return;
        }

        
        
        
        if (inside && index >= 0)
        {
            var slotPt = _hole is not null ? LayerPoint(_hole) : LayerPoint(PreviewStrip);
            EndDrag(keepGhost: true);
            FlyGhost(slotPt, TileW, TileH, () =>
            {
                
                HideHole();
                MoveTo(id, index);
            });
            return;
        }

        
        
        if (fromPreview && overLibrary)
        {
            var cardEl = LibraryCardEl(id);
            var to = cardEl is not null ? LayerPoint(cardEl) : LayerPoint(LibraryPanel);
            var (cw, ch) = CardSize();

            EndDrag(keepGhost: true);
            FlyGhost(to, cw, ch, () =>
            {
                
                
                HideHole();
                RestoreHiddenRow();
                _ids.Remove(id);
                Save();
                Refresh();
            });
            return;
        }

        
        
        var homeEl = fromPreview ? (homeRow as FrameworkElement) : LibraryCardEl(id);
        var back = homeEl is not null ? LayerPoint(homeEl) : LayerPoint(WorkspaceRoot);
        var (bw, bh) = fromPreview ? (TileW, TileH) : CardSize();

        
        
        
        
        
        
        if (fromPreview && homeIndex >= 0)
        {
            UpdateHole(VisibleIndex(homeIndex));
            if (_hole is not null) back = LayerPoint(_hole);
        }

        EndDrag(keepGhost: true);
        FlyGhost(back, bw, bh, () =>
        {
            
            HideHole();
            if (fromPreview && homeIndex >= 0) RestoreHiddenRow();
            if (src is not null) src.Opacity = 1;
            Refresh();
        });
    }

    
    private void CancelDrag()
    {
        if (_dragId is null || _ending) return;
        Log($"拖动被打断（{_dragId}）—— 滚动锁扣到手指抬起");
        var midDrag = _dragging;
        EndDrag(keepScrollLocked: midDrag);
        if (midDrag) _latchScroll = true;
        Refresh();
    }

    
    
    
    
    
    private void EndDrag(bool keepScrollLocked = false, bool keepGhost = false)
    {
        _holdTimer?.Stop();
        HintPress(false);
        if (!keepGhost)
        {
            HideGhost();
            RestoreHiddenRow();
            HideHole();
            if (_pressSource is not null) _pressSource.Opacity = 1;
        }
        RemoveHint.Visibility = Visibility.Collapsed;

        if (_armed)
        {
            if (keepScrollLocked)
            {
                
                
                
                
                PageScroll.ChangeView(null, _pageScrollOffset, null, true);
            }
            else
            {
                UnlockScroll();
            }
        }

        _dragId = null;
        _dragging = false;
        _armed = false;
        _pressSource = null;
        _heldPointer = null;
        _dragSrcIndex = -1;
        _maxMove = 0;
        
        
    }

    
    private void HideHole()
    {
        _holeSlot = -1;
        if (_hole is not null) FadeElement(_hole, 0, 90);
        AnimatePad();
    }

    
    private void LockScroll()
    {
        PageScroll.VerticalScrollMode = ScrollMode.Disabled;
        PageScroll.HorizontalScrollMode = ScrollMode.Disabled;
    }

    private void UnlockScroll()
    {
        _latchScroll = false;
        PageScroll.VerticalScrollMode = ScrollMode.Auto;
        PageScroll.HorizontalScrollMode = ScrollMode.Auto;
        PageScroll.ChangeView(null, _pageScrollOffset, null, true);
    }

    private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e) => ReleaseScrollLatch();

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e) => ReleaseScrollLatch();

    
    private void ReleaseScrollLatch()
    {
        if (!_latchScroll) return;
        Log("手指抬起 -> 放开滚动锁");
        UnlockScroll();
    }

    
    
    
    
    
    

    private void ShowGhost()
    {
        if (_dragId is null || SidebarModules.Find(_dragId) is not { } m) return;

        
        _ghostTileMode = _dragFromPreview;
        var w = _ghostTileMode ? TileW : (_srcW > 1 ? _srcW : TileW);
        var h = _ghostTileMode ? TileH : (_srcH > 1 ? _srcH : TileH);

        _ghostCard = GhostCardContent(m);
        _ghostTile = GhostTileContent(m);
        _ghostCard.Opacity = _ghostTileMode ? 0 : 1;
        _ghostTile.Opacity = _ghostTileMode ? 1 : 0;

        var layers = new Grid();
        layers.Children.Add(_ghostCard);
        layers.Children.Add(_ghostTile);

        
        
        
        
        var edge = new Border
        {
            BorderBrush = Br("ModCardStroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            IsHitTestVisible = false
        };
        _ghostRing = new Border
        {
            BorderBrush = Br("ModAccent"),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(8),
            IsHitTestVisible = false
        };
        layers.Children.Add(edge);
        layers.Children.Add(_ghostRing);

        var ghost = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(8),
            Background = Br("ModCardBg"),
            BorderThickness = new Thickness(0),     
            Opacity = 0.95,
            Child = layers,
            Shadow = new ThemeShadow(),
            Translation = new Vector3(0, 0, 52)     
        };

        _ghost = ghost;
        DragLayer.Children.Add(ghost);

        
        
        
        
        try
        {
            _ghostVisual = ElementCompositionPreview.GetElementVisual(ghost);
            _ghostVisual.CenterPoint = new Vector3((float)(w / 2), (float)(h / 2), 0);
        }
        catch { _ghostVisual = null; }

        HookGhostFrame();
        PlaceGhost();
        PopGhost(up: true);
    }

    
    
    
    
    
    
    private void PopGhost(bool up)
    {
        if (_ghostVisual is null) return;
        try
        {
            var comp = _ghostVisual.Compositor;
            var ease = comp.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

            var anim = comp.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(1f, up ? (float)LiftScale : 1f, ease);
            anim.Duration = TimeSpan.FromMilliseconds(up ? MorphMs : FlightMs);

            _ghostVisual.StartAnimation("Scale.X", anim);
            _ghostVisual.StartAnimation("Scale.Y", anim);
        }
        catch { }
    }

    
    private FrameworkElement GhostCardContent(SidebarModule m)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = m.Name,
            FontSize = 14.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Br("ModText")
        });
        text.Children.Add(new TextBlock
        {
            Text = m.Hint,
            FontSize = 11.5,
            Opacity = 0.65,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Br("ModTextDim")
        });

        
        
        
        
        var grid = new Grid { ColumnSpacing = 12, Margin = new Thickness(15, 0, 15, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new FontIcon
        {
            Glyph = m.Glyph,
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        
        
        
        var badge = new FontIcon
        {
            Glyph = "\uE710",
            FontSize = 13,
            Opacity = 0.8,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(badge, 2);
        grid.Children.Add(badge);
        return grid;
    }

    
    private FrameworkElement GhostTileContent(SidebarModule m)
    {
        var content = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        content.Children.Add(new FontIcon
        {
            Glyph = m.Glyph,
            FontSize = 19,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = m.ShortName,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Br("ModText")
        });
        return content;
    }

    
    private void MorphGhost(bool toTile)
    {
        if (_ghost is null || _ghostTileMode == toTile) return;
        _ghostTileMode = toTile;

        var toW = toTile ? TileW : (_srcW > 1 ? _srcW : TileW);
        var toH = toTile ? TileH : (_srcH > 1 ? _srcH : TileH);

        
        
        if (_ghostCard is not null) _ghostCard.Opacity = toTile ? 0 : 1;
        if (_ghostTile is not null) _ghostTile.Opacity = toTile ? 1 : 0;

        MorphSizeTo(toW, toH, MorphMs);
    }

    
    
    
    
    
    
    
    
    
    private void MorphSizeTo(double toW, double toH, int ms)
    {
        if (_ghost is not { } g) return;

        var fromW = g.ActualWidth > 0.5 ? g.ActualWidth : g.Width;
        var fromH = g.ActualHeight > 0.5 ? g.ActualHeight : g.Height;
        if (double.IsNaN(fromW) || fromW <= 0.5) fromW = toW;
        if (double.IsNaN(fromH) || fromH <= 0.5) fromH = toH;

        StartTween(g, t =>
        {
            g.Width = fromW + (toW - fromW) * t;
            g.Height = fromH + (toH - fromH) * t;
        }, 0, 1, ms, done: null);
    }

    private void HookGhostFrame()
    {
        if (_ghostFrameHooked) return;
        CompositionTarget.Rendering += OnGhostFrame;
        _ghostFrameHooked = true;
    }

    private void UnhookGhostFrame()
    {
        if (!_ghostFrameHooked) return;
        CompositionTarget.Rendering -= OnGhostFrame;
        _ghostFrameHooked = false;
    }

    
    
    
    
    
    private void OnGhostFrame(object? sender, object e) => PlaceGhost();

    private void PlaceGhost()
    {
        if (_ghost is null) return;
        var w = _ghost.ActualWidth > 0.5 ? _ghost.ActualWidth : _ghost.Width;
        var h = _ghost.ActualHeight > 0.5 ? _ghost.ActualHeight : _ghost.Height;
        
        if (_ghostVisual is not null)
            _ghostVisual.CenterPoint = new Vector3((float)(w / 2), (float)(h / 2), 0);
        Canvas.SetLeft(_ghost, _pointerInLayer.X - _grabRatio.X * w);
        Canvas.SetTop(_ghost, _pointerInLayer.Y - _grabRatio.Y * h);
    }

    private void HideGhost()
    {
        UnhookGhostFrame();
        
        
        
        _ghostVisual = null;
        if (_ghost is null) return;
        CancelTween(_ghost);                    
        DragLayer.Children.Remove(_ghost);
        _ghost = null;
        _ghostCard = null;
        _ghostTile = null;
        _ghostRing = null;
    }

    
    
    
    
    
    

    
    
    
    
    
    
    
    
    
    
    private void FlyGhost(Point to, double toW, double toH, Action onDone)
    {
        if (_ghost is null) { onDone(); return; }

        UnhookGhostFrame();                     
        var g = _ghost;
        _flying = true;                         

        
        var toTile = toW < 150;

        var fromX = Canvas.GetLeft(g); if (double.IsNaN(fromX)) fromX = 0;
        var fromY = Canvas.GetTop(g); if (double.IsNaN(fromY)) fromY = 0;
        var fromW = g.ActualWidth > 0.5 ? g.ActualWidth : toW;
        var fromH = g.ActualHeight > 0.5 ? g.ActualHeight : toH;

        
        
        
        
        
        
        
        
        
        
        var switched = _ghostTileMode == toTile;
        const double SizeFirst = 0.97;

        
        void Finish()
        {
            HideGhost();
            _flying = false;                
        }

        void Land()
        {
            
            try { onDone(); }
            
            finally { SettleGhost(Finish); }
        }

        StartTween(g, t =>
        {
            var sizeT = Math.Min(1, t / SizeFirst);         
            g.Width = fromW + (toW - fromW) * sizeT;
            g.Height = fromH + (toH - fromH) * sizeT;
            Canvas.SetLeft(g, fromX + (to.X - fromX) * t);
            Canvas.SetTop(g, fromY + (to.Y - fromY) * t);

            if (!switched && sizeT >= 1)
            {
                switched = true;
                _ghostTileMode = toTile;
                if (_ghostCard is not null) _ghostCard.Opacity = toTile ? 0 : 1;
                if (_ghostTile is not null) _ghostTile.Opacity = toTile ? 1 : 0;
            }
        }, 0, 1, FlightMs, done: Land);

        PopGhost(up: false);                    
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private void SettleGhost(Action done)
    {
        if (_ghost is not { } g) { done(); return; }

        var ring = _ghostRing;
        var fromOpacity = g.Opacity;
        var fromZ = g.Translation.Z;

        
        
        
        
        
        try
        {
            if (_dropDip < 0.999 && _ghostVisual is { } vis)
            {
                var comp = vis.Compositor;
                var breathe = comp.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

                var dip = comp.CreateScalarKeyFrameAnimation();
                dip.InsertKeyFrame(0f, 1f);
                dip.InsertKeyFrame((float)DipAt, (float)_dropDip);              
                dip.InsertKeyFrame(1f, 1f, breathe);                            
                dip.Duration = TimeSpan.FromMilliseconds(SettleMs);

                vis.StartAnimation("Scale.X", dip);
                vis.StartAnimation("Scale.Y", dip);
            }
        }
        catch {  }

        
        StartTween(g, t =>
        {
            try
            {
                g.Opacity = fromOpacity + (1.0 - fromOpacity) * t;
                if (ring is not null) ring.Opacity = 1 - t;
                if (fromZ > 0.5) g.Translation = new Vector3(0, 0, (float)(fromZ * (1 - t)));
            }
            catch {  }
        }, 0, 1, SettleMs, done: done);
    }

    
    private Point LayerPoint(FrameworkElement el)
    {
        try { return el.TransformToVisual(DragLayer).TransformPoint(new Point(0, 0)); }
        catch { return new Point(0, 0); }
    }

    
    private FrameworkElement? LibraryCardEl(string id)
    {
        foreach (var row in LibraryPanel.Children)
        {
            if (row is not Grid g) continue;
            foreach (var c in g.Children)
            {
                if (c is FrameworkElement fe && (fe.Tag as string) == id) return fe;
            }
        }
        return null;
    }

    
    
    
    
    private (double W, double H) CardSize()
    {
        foreach (var row in LibraryPanel.Children)
        {
            if (row is not Grid g) continue;
            foreach (var c in g.Children)
            {
                if (c is FrameworkElement fe && fe.ActualWidth > 1 && fe.ActualHeight > 1)
                    return (fe.ActualWidth, fe.ActualHeight);
            }
        }
        return (_srcW > 1 ? _srcW : 300, 72);
    }

    
    private bool IsOverLibrary(Point inRoot)
    {
        try
        {
            if (LibraryPanel.ActualWidth < 1 || LibraryPanel.ActualHeight < 1) return false;
            var p = LibraryPanel.TransformToVisual(WorkspaceRoot).TransformPoint(new Point(0, 0));
            return new Rect(p.X, p.Y, LibraryPanel.ActualWidth, LibraryPanel.ActualHeight).Contains(inRoot);
        }
        catch { return false; }
    }

    
    
    
    
    
    
    
    
    

    
    private int VisibleCount => _tiles.Count - (_dragSrcIndex >= 0 ? 1 : 0);

    
    private int VisibleIndex(int k) => _dragSrcIndex >= 0 && k > _dragSrcIndex ? k - 1 : k;

    
    
    
    
    
    private double RowOffset(int k) =>
        _holeSlot >= 0 && VisibleIndex(k) >= _holeSlot ? GapDip : 0;

    
    private void ApplyRowOffsets(bool animate)
    {
        for (var k = 0; k < _tiles.Count; k++)
        {
            if (k == _dragSrcIndex) continue;           
            SlideRow(_tiles[k], RowOffset(k), animate);
        }
    }

    
    
    
    
    
    private int SlotFromPointer(double yInPanel)
    {
        for (var v = 0; v < VisibleCount; v++)
        {
            if (yInPanel < _rowsTop + v * GapDip + TileH / 2) return v;
        }
        return VisibleCount;
    }

    
    private int InsertIndexFromSlot(int slot) =>
        slot + (_dragSrcIndex >= 0 && slot >= _dragSrcIndex ? 1 : 0);

    
    private void MeasureRowsTop()
    {
        _rowsTop = 0;
        if (PreviewPanel.Children.Count <= 1) return;
        if (PreviewPanel.Children[1] is not FrameworkElement first) return;
        _rowsTop = first.TransformToVisual(PreviewPanel).TransformPoint(new Point(0, 0)).Y;
    }

    
    
    
    
    private void UpdateHole(int slot)
    {
        if (slot == _holeSlot) return;
        _holeSlot = slot;
        Log($"空档 -> {slot}（可见 {VisibleCount} 行 / 整体 {_tiles.Count} 行）");

        ApplyRowOffsets(animate: true);
        AnimatePad();
        ShowHole();
    }

    
    private void ShowHole()
    {
        if (_holeSlot < 0)
        {
            if (_hole is not null) FadeElement(_hole, 0, 110);
            return;
        }

        if (_hole is null)
        {
            _hole = new Border
            {
                Width = TileW,
                Height = TileH,
                CornerRadius = new CornerRadius(6),
                Background = Br("ModHoverBg"),
                BorderBrush = Br("ModAccent"),
                BorderThickness = new Thickness(1),
                Opacity = 0
            };
            HoleLayer.Children.Add(_hole);
        }

        
        
        Canvas.SetLeft(_hole, 0);
        Canvas.SetTop(_hole, _rowsTop + _holeSlot * GapDip);
        FadeElement(_hole, 1, 110);
    }

    
    private void AnimatePad()
    {
        if (_pad is not { } pad) return;
        AnimateHeight(pad, _holeSlot >= 0 ? GapDip : 0, SlotMs);
    }

    
    
    
    
    
    
    
    
    
    

    private sealed class SlideTween
    {
        public FrameworkElement Owner = null!;
        public Action<double> Apply = _ => { };
        public double From;
        public double To;
        public int Ms;
        public long StartTs;                    
        public Action? Done;
    }

    private readonly List<SlideTween> _tweens = new();
    private bool _tweenFrameHooked;
    private long _lastTweenStepTs;      
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _tweenWatch;

    
    
    
    
    
    
    
    
    
    
    
    private static double RowShift(FrameworkElement row) => row.Margin.Top;

    private static void SetRowShift(FrameworkElement row, double v)
    {
        row.Margin = new Thickness(0, v, 0, -v);
    }

    
    private void SlideRow(FrameworkElement row, double to, bool animate)
    {
        if (!animate)
        {
            CancelTween(row);
            SetRowShift(row, to);
            return;
        }

        var from = RowShift(row);
        if (Math.Abs(from - to) < 0.5) return;

        StartTween(row, v => SetRowShift(row, v), from, to, SlotMs, done: null);
    }

    
    
    
    
    
    
    
    
    
    
    
    private void StartTween(FrameworkElement owner, Action<double> apply, double from, double to, int ms,
                            Action? done)
    {
        CancelTween(owner);
        _tweens.Add(new SlideTween
        {
            Owner = owner,
            Apply = apply,
            From = from,
            To = to,
            Ms = ms,
            StartTs = Stopwatch.GetTimestamp(),
            Done = done
        });
        HookTweenFrame();
        EnsureTweenWatch();
    }

    private void CancelTween(FrameworkElement owner)
    {
        for (var i = _tweens.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_tweens[i].Owner, owner)) _tweens.RemoveAt(i);
        }
    }

    private void HookTweenFrame()
    {
        if (_tweenFrameHooked) return;
        CompositionTarget.Rendering += OnTweenFrame;
        _tweenFrameHooked = true;
    }

    private void UnhookTweenFrame()
    {
        if (!_tweenFrameHooked) return;
        CompositionTarget.Rendering -= OnTweenFrame;
        _tweenFrameHooked = false;
    }

    private void OnTweenFrame(object? sender, object e) => StepTweens();

    
    
    
    
    
    
    
    
    private void EnsureTweenWatch()
    {
        _tweenWatch ??= DispatcherQueue.CreateTimer();
        _tweenWatch.Interval = TimeSpan.FromMilliseconds(250);
        _tweenWatch.IsRepeating = true;
        _tweenWatch.Tick -= OnTweenWatch;
        _tweenWatch.Tick += OnTweenWatch;
        if (!_tweenWatch.IsRunning) _tweenWatch.Start();
    }

    private void StopTweenWatch()
    {
        _tweenWatch?.Stop();
    }

    private void OnTweenWatch(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_tweens.Count == 0) { StopTweenWatch(); return; }

        var idleMs = (Stopwatch.GetTimestamp() - _lastTweenStepTs) / (double)Stopwatch.Frequency * 1000.0;
        if (idleMs > 200) StepTweens();
    }

    private void StepTweens()
    {
        _lastTweenStepTs = Stopwatch.GetTimestamp();

        
        
        List<SlideTween>? finished = null;

        for (var i = _tweens.Count - 1; i >= 0; i--)
        {
            var tw = _tweens[i];
            var p = tw.Ms <= 0
                ? 1
                : Math.Min(1, (Stopwatch.GetTimestamp() - tw.StartTs)
                              / (double)Stopwatch.Frequency * 1000.0 / tw.Ms);

            tw.Apply(tw.From + (tw.To - tw.From) * FluidEase(p));

            if (p >= 1)
            {
                _tweens.RemoveAt(i);
                (finished ??= new List<SlideTween>()).Add(tw);
            }
        }

        if (finished is not null)
        {
            foreach (var tw in finished) tw.Done?.Invoke();
        }

        if (_tweens.Count == 0)
        {
            UnhookTweenFrame();
            StopTweenWatch();
        }
    }

    
    
    
    
    
    
    
    
    private const double FluidX1 = 0.1, FluidY1 = 0.9, FluidX2 = 0.2, FluidY2 = 1.0;

    
    private static double BezierAxis(double a, double b, double t)
    {
        var m = 1 - t;
        return 3 * a * t * m * m + 3 * b * t * t * m + t * t * t;
    }

    
    
    
    
    private static double FluidEase(double p)
    {
        if (p <= 0) return 0;
        if (p >= 1) return 1;

        var lo = 0.0;
        var hi = 1.0;
        for (var i = 0; i < 18; i++)
        {
            var mid = (lo + hi) * 0.5;
            if (BezierAxis(FluidX1, FluidX2, mid) < p) lo = mid; else hi = mid;
        }
        return BezierAxis(FluidY1, FluidY2, (lo + hi) * 0.5);
    }

    
    
    
    
    
    
    
    private void AnimateHeight(FrameworkElement el, double to, int ms)
    {
        var from = el.Height;
        if (double.IsNaN(from)) from = 0;
        if (Math.Abs(from - to) < 0.5) return;

        StartTween(el, v => el.Height = v, from, to, ms, done: null);
    }

    
    
    
    
    
    private static void FadeElement(FrameworkElement el, double to, int ms)
    {
        if (Math.Abs(el.Opacity - to) < 0.01) return;

        var anim = new DoubleAnimation
        {
            From = el.Opacity,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        el.Opacity = to;                                
        Storyboard.SetTarget(anim, el);
        Storyboard.SetTargetProperty(anim, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    
    private static bool InsideButton(object? src)
    {
        var d = src as DependencyObject;
        while (d is not null)
        {
            if (d is ButtonBase) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    
    private void Log(string text)
    {
        try
        {
            Core.AppLog.Info("layout", text);
        }
        catch
        {
            
        }
    }

    private void MoveTo(string id, int index)
    {
        var from = _ids.IndexOf(id);
        if (from >= 0)
        {
            if (from < index) index--;                  
            _ids.RemoveAt(from);
        }

        index = Math.Clamp(index, 0, _ids.Count);
        _ids.Insert(index, id);
        Save();
        Refresh();
    }

    

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string id) return;
        if (_ids.Remove(id)) { Save(); Refresh(); }
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(sender, -1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(sender, +1);

    private void Move(object sender, int delta)
    {
        if (sender is not Button b || b.Tag is not string id) return;
        var i = _ids.IndexOf(id);
        if (i < 0) return;
        var j = i + delta;
        if (j < 0 || j >= _ids.Count) return;

        AnimateSwap(i, j);
    }

    
    
    
    
    
    
    
    
    
    private void AnimateSwap(int i, int j)
    {
        if (_swapping) return;
        if (i == j || i < 0 || j < 0 || i >= _tiles.Count || j >= _tiles.Count) return;
        if (_dragId is not null || _holeSlot >= 0) return;      

        var mover = _tiles[i];          
        var other = _tiles[j];
        var dy = (j - i) * GapDip;      

        SlideRow(mover, 0, animate: false);     
        SlideRow(other, 0, animate: false);
        Canvas.SetZIndex(mover, 1);
        _swapping = true;

        var left = 2;
        void Finish()
        {
            if (--left > 0) return;
            (_ids[i], _ids[j]) = (_ids[j], _ids[i]);
            Refresh();          
            Canvas.SetZIndex(mover, 0);
            _swapping = false;
            Save();
        }

        StartTween(mover, v => SetRowShift(mover, v), 0, dy, SwapMs, done: Finish);
        StartTween(other, v => SetRowShift(other, v), 0, -dy, SwapMs, done: Finish);
    }

    private void ResetDefault_Click(object sender, RoutedEventArgs e)
    {
        _ids.Clear();
        _ids.AddRange(SidebarModules.DefaultIds);
        Save();
        Refresh();
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        if (ToolSidebarWindow.IsSidebarVisible) ToolSidebarWindow.HideSidebar();
        else ToolSidebarWindow.ShowSidebar();
        Refresh();
    }

    
    
    
    
    
    private void FooterItem_Toggled(object sender, RoutedEventArgs e)
    {
        if (_footerSync) return;                                     
        if (sender is not ToggleSwitch t || t.Tag is not string key) return;

        var hidden = (App.Settings.Current.SidebarFooterHidden ?? Array.Empty<string>()).ToList();
        if (t.IsOn) hidden.Remove(key);
        else if (!hidden.Contains(key)) hidden.Add(key);

        App.Settings.Current.SidebarFooterHidden = hidden.ToArray();
        App.Settings.Save();
        ToolSidebarWindow.ApplyFooterSetting();
    }

    
    
    
    

    private void PaletteTopSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_toolSync) return;
        App.MainWindow?.SetPaletteOnTop(PaletteTopSwitch.IsOn);
    }

    
    
    
    
    
    
    
    private void LongLabelSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_toolSync) return;

        App.Settings.Current.SidebarLongLabels = LongLabelSwitch.IsOn;
        App.Settings.Save();
        ToolSidebarWindow.ApplyModules();
    }

    private void SidebarSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_toolSync) return;
        App.Settings.Current.SidebarEnabled = SidebarSwitch.IsOn;
        App.Settings.Save();
        ToolSidebarWindow.ApplySetting();

        
        if (SidebarButton is not null)
            SidebarButton.Content = ToolSidebarWindow.IsSidebarVisible ? "隐藏侧边栏" : "显示侧边栏";
    }

    
    
    

    
    
    
    
    
    
    private void RebuildEdgeCombo()
    {
        var s = App.Settings.Current;
        var free = s.SidebarMode == "free";

        _edgeRebuild = true;
        try
        {
            SidebarEdgeCombo.Items.Clear();
            if (free)
            {
                AddEdgeItem("左边", "left");
                AddEdgeItem("右边", "right");
                AddEdgeItem("上边", "top");
                AddEdgeItem("下边", "bottom");
                SelectEdge(s.SidebarFreeEdge);
            }
            else
            {
                AddEdgeItem("左边", "left");
                AddEdgeItem("左右两边", "both");
                AddEdgeItem("右边", "right");
                SelectEdge(s.SidebarEdge);
            }
        }
        finally
        {
            _edgeRebuild = false;
        }
    }

    private void AddEdgeItem(string text, string tag) =>
        SidebarEdgeCombo.Items.Add(new ComboBoxItem { Content = text, Tag = tag });

    private void SelectEdge(string tag)
    {
        foreach (var item in SidebarEdgeCombo.Items)
        {
            if (item is ComboBoxItem it && it.Tag as string == tag)
            {
                SidebarEdgeCombo.SelectedItem = it;
                return;
            }
        }
        if (SidebarEdgeCombo.Items.Count > 0) SidebarEdgeCombo.SelectedIndex = 0;
    }

    
    private void UpdateSidebarHints()
    {
        var free = App.Settings.Current.SidebarMode == "free";

        ModeHint.Text = free
            ? "侧边栏可吸附屏幕任意一条边；贴上边或下边时呈横条。"
            : "侧边栏只吸附屏幕左右两条边，可选左右同时显示。";

        EdgeHint.Text = free
            ? "四条边均可吸附；贴上边或下边时侧边栏为横条。拖动收起状态的抓手也可改边。"
            : "选「左右两边」时两侧同时显示，上下位置保持一致，拖动其中一条另一条同步移动。拖动收起状态的抓手也可改边。";
    }

    private void SidebarMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_toolSync) return;
        if (SidebarModeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string mode) return;

        App.Settings.Current.SidebarMode = mode;
        App.Settings.Save();

        
        RebuildEdgeCombo();
        UpdateSidebarHints();

        if (App.Settings.Current.SidebarEnabled) ToolSidebarWindow.ApplySetting();
    }

    private void SidebarEdge_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_toolSync || _edgeRebuild) return;
        if (SidebarEdgeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string edge) return;

        var s = App.Settings.Current;
        if (s.SidebarMode == "free") s.SidebarFreeEdge = edge;
        else s.SidebarEdge = edge;
        App.Settings.Save();

        if (s.SidebarEnabled) ToolSidebarWindow.ApplySetting();
    }

    
    
    
    
    
    
    private void SidebarDensity_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_toolSync) return;
        if (SidebarDensityCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string style) return;

        App.Settings.Current.SidebarDensity = style;
        App.Settings.Save();

        if (App.Settings.Current.SidebarEnabled) ToolSidebarWindow.ApplySetting();
    }

    
    
    
    
    
    private void SidebarExpandedDensity_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_toolSync) return;
        if (SidebarExpandedDensityCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string style) return;

        App.Settings.Current.SidebarExpandedDensity = style;
        App.Settings.Save();

        if (App.Settings.Current.SidebarEnabled) ToolSidebarWindow.ApplySetting();
    }

    
    
    
    
    
    
    private void SidebarColumns_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_toolSync) return;
        if (SidebarColumnsCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;

        App.Settings.Current.SidebarColumns = tag == "3" ? 3 : 1;
        App.Settings.Save();

        if (App.Settings.Current.SidebarEnabled) ToolSidebarWindow.ApplySetting();
    }

    
    
    
    
    
    
    
    
    private void SidebarPinnedPosition_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_toolSync) return;
        if (SidebarPinnedPositionCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;

        App.Settings.Current.SidebarPinnedPosition = tag == "top" ? "top" : "left";
        App.Settings.Save();

        if (App.Settings.Current.SidebarEnabled) ToolSidebarWindow.ApplySetting();
    }

    private void SidebarGap_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_toolSync) return;
        if (SidebarGapCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;

        App.Settings.Current.SidebarCollapsedGap = int.TryParse(tag, out var v) ? v : 6;
        App.Settings.Save();

        if (App.Settings.Current.SidebarEnabled) ToolSidebarWindow.ApplyCollapsedGap();
    }

    
    private static int GapIndex(int gap) => gap switch
    {
        0 => 0,
        2 => 1,
        4 => 2,
        6 => 3,
        10 => 4,
        16 => 5,
        _ => 3,
    };

    
    
    
    
    
    
    
    
    
    
    private void HeightRatio_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_toolSync) return;

        
        
        var percent = Math.Clamp(
            (int)Math.Round(e.NewValue / 5.0, MidpointRounding.AwayFromZero) * 5, 45, 95);

        App.Settings.Current.SidebarHeightRatio = percent / 100.0;
        if (HeightRatioText is not null) HeightRatioText.Text = percent + "%";

        if (App.Settings.Current.SidebarEnabled) ToolSidebarWindow.ApplyHeightRatio();

        _heightSaveTimer ??= DispatcherQueue.CreateTimer();
        _heightSaveTimer.Interval = TimeSpan.FromMilliseconds(350);
        _heightSaveTimer.IsRepeating = false;
        _heightSaveTimer.Tick -= HeightSaveTimer_Tick;
        _heightSaveTimer.Tick += HeightSaveTimer_Tick;
        _heightSaveTimer.Start();
    }

    private void HeightSaveTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        try { App.Settings.Save(); } catch { }
    }

    
    
    
    
    
    
    
    private void HeightAuto_Toggled(object sender, RoutedEventArgs e)
    {
        if (_toolSync) return;

        App.Settings.Current.SidebarHeightAuto = HeightAutoSwitch.IsOn;
        App.Settings.Save();

        if (App.Settings.Current.SidebarEnabled) ToolSidebarWindow.ApplyHeightRatio();
    }

    

    
    private static double DropDipScale(string? style) => style == "plain" ? 1.0 : DipScale;

    
    
    
    
    private void DropAnim_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_toolSync) return;
        if (DropAnimCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string style) return;

        App.Settings.Current.SidebarDropAnim = style;
        App.Settings.Save();

        _dropDip = DropDipScale(style);
    }

    

    
    private void ShutdownMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_toolSync) return;
        if (ShutdownModeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string mode) return;

        App.Settings.Current.ShutdownMode = mode;
        App.Settings.Save();
    }

    
    

    private void ShotAutoSave_Toggled(object sender, RoutedEventArgs e)
    {
        if (_toolSync) return;
        App.Settings.Current.ShotAutoSave = ShotAutoSaveSwitch.IsOn;
        App.Settings.Save();
        RefreshShotDir();
    }

    
    private void RefreshShotDir()
    {
        var dir = Services.ShotSaver.DirSetting();
        var custom = !string.IsNullOrWhiteSpace(App.Settings.Current.ShotSaveDir);
        ShotDirText.Text = custom ? dir : $"{dir}（默认：桌面，没改过）";
        ShotDirText.Opacity = ShotAutoSaveSwitch.IsOn ? 0.7 : 0.4;
    }

    private async void ShotDir_Change_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop,
            };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            App.Settings.Current.ShotSaveDir = folder.Path;
            App.Settings.Save();
            RefreshShotDir();
        }
        catch (Exception ex)
        {
            Services.ScreenCapture.Log("选截图目录失败: " + ex.Message);
        }
    }

    private void ShotDir_Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Services.ShotSaver.Dir();
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Services.ScreenCapture.Log("打开截图目录失败: " + ex.Message);
        }
    }

    private void ShotDir_Reset_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.ShotSaveDir = "";                
        App.Settings.Save();
        RefreshShotDir();
    }
}
