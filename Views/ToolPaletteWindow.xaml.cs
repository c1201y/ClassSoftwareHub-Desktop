using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;











public sealed partial class ToolPaletteWindow : Window
{
    private const int PaletteWidthDip = 400;      
    private const int PaletteHeightDip = 360;

    
    private const int PositionVersion = 2;

    private static ToolPaletteWindow? _instance;

    private AppWindow? _appWindow;
    private string _tool = "pick-number";
    private bool _shownOnce;
    private bool _visible;
    private bool _syncingPivot;                         
    private bool _pivotLoaded;                          
    private int _fadeEpoch;                              

    public bool IsVisible => _visible;

    private ToolPaletteWindow()
    {
        InitializeComponent();
        Configure();

        
        
        ToolPivot.Loaded += (_, _) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _pivotLoaded = true;
            SyncPivotToTool();
        });
    }

    
    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        switch (_tool)
        {
            case "timer":
            case "stopwatch":
                HidePalette();
                App.MainWindow?.OpenToolSettings(typeof(Pages.Tools.TimerToolPage));
                break;

            case "clock":
                HidePalette();   
                try { ClockFullscreenWindow.Show(null, Root.ActualTheme == ElementTheme.Dark); } catch { }
                break;

            default:             
                HidePalette();
                App.MainWindow?.OpenToolSettings(typeof(Pages.Tools.PickNumberToolPage));
                break;
        }
    }

    

    
    public static void ShowTool(string? toolId = null)
    {
        var win = _instance ??= new ToolPaletteWindow();
        win.ShowPalette(toolId);
    }

    public static void HidePalette() => _instance?.Collapse();

    public static void TogglePalette()
    {
        if (_instance is { _visible: true }) _instance.Collapse();
        else ShowTool();
    }

    
    
    
    
    
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
        Core.AppLog.Info("exit", "浮窗实例已请求 Close");
    }

    public static bool IsPaletteVisible => _instance is { _visible: true };

    
    public static void ApplyOnTopSetting()
    {
        if (_instance?._appWindow?.Presenter is OverlappedPresenter p)
            p.IsAlwaysOnTop = App.Settings.Current.PaletteOnTop;
    }

    

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "常用工具";
            _appWindow.IsShownInSwitchers = false;          

            if (_appWindow.Presenter is OverlappedPresenter p)
            {
                p.SetBorderAndTitleBar(true, true);         
                p.IsResizable = false;
                p.IsMaximizable = false;                    
                p.IsMinimizable = false;                    
                p.IsAlwaysOnTop = App.Settings.Current.PaletteOnTop;
            }

            UseToolWindowExStyle(hwnd);                     
            ConfigureTitleBar();

            
            var want = App.Settings.Current.Backdrop;
            BackdropHost.Apply(this, Root,
                string.Equals(want, "mica", StringComparison.OrdinalIgnoreCase) ? "acrylic" : want);

            Root.ActualThemeChanged += (_, _) => { UpdateCaptionButtonColors(); };   
            ThemeHost.Apply(Root, external: true);          
            ResizeClientDip(PaletteWidthDip, PaletteHeightDip);
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("palette", ex, "初始化工具浮窗");
        }

        ApplyPosition();

        Core.AppLog.Info("exit", "浮窗实例已建");
        Closed += (_, _) => Core.AppLog.Info("exit", "浮窗 Closed");
        if (_appWindow is not null)
        {
            
            
            
            
            _appWindow.Closing += (_, args) =>
            {
                Core.AppLog.Info("exit", $"浮窗 Closing: IsExiting={App.IsExiting}");
                if (App.IsExiting) return;
                args.Cancel = true;
                Core.AppLog.Info("exit", "浮窗 Closing -> 取消（收起来）");
                HidePalette();
            };
        }
    }

    
    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        if (_appWindow is null) return;
        try
        {
            var bar = _appWindow.TitleBar;
            bar.ExtendsContentIntoTitleBar = true;
            bar.PreferredHeightOption = TitleBarHeightOption.Standard;   
            bar.ButtonBackgroundColor = Colors.Transparent;              
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("palette", "标题栏定制不可用：" + ex.Message);
        }

        UpdateCaptionButtonColors();

        
        
        Core.WindowChrome.SetRounded(WindowNative.GetWindowHandle(this), rounded: true);
    }

    
    private void UpdateCaptionButtonColors()
    {
        if (_appWindow is null) return;
        var dark = Root.ActualTheme == ElementTheme.Dark;
        try
        {
            var bar = _appWindow.TitleBar;
            bar.ButtonForegroundColor = dark ? Colors.White : Windows.UI.Color.FromArgb(255, 30, 30, 30);
            bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
            bar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(26, 255, 255, 255) : Windows.UI.Color.FromArgb(20, 0, 0, 0);
            bar.ButtonPressedForegroundColor = dark ? Windows.UI.Color.FromArgb(255, 200, 200, 200) : Windows.UI.Color.FromArgb(255, 90, 90, 90);
            bar.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(160, 255, 255, 255) : Windows.UI.Color.FromArgb(140, 0, 0, 0);
            bar.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(40, 255, 255, 255) : Windows.UI.Color.FromArgb(30, 0, 0, 0);
        }
        catch { }
    }

    
    private static void UseToolWindowExStyle(IntPtr hwnd)
    {
        try
        {
            var ex = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(ex | WsExToolWindow));
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("palette", "工具窗口样式设置失败（多加一个 WS_EX_TOOLWINDOW 而已，不致命）：" + ex.Message);
        }
    }

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private static void Log(string message)
    {
        try
        {
            Core.AppLog.Info("palette", message);
        }
        catch { }
    }

    private double Scale()
    {
        try
        {
            var dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch { return 1.0; }
    }

    
    
    
    
    private void ResizeClientDip(int dipWidth, int dipHeight)
    {
        if (_appWindow is null) return;
        var scale = Scale();
        var wantW = (int)Math.Round(dipWidth * scale);
        var wantH = (int)Math.Round(dipHeight * scale);

        try
        {
            _appWindow.ResizeClient(new SizeInt32(wantW, wantH));
            return;
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("palette", "ResizeClient 不可用，退回 Resize：" + ex.Message);
        }

        
        try { _appWindow.Resize(new SizeInt32(wantW + (int)Math.Round(16 * scale), wantH + (int)Math.Round(40 * scale))); }
        catch { }
    }

    
    
    
    
    private void ApplyPosition()
    {
        if (_appWindow is null) return;
        try
        {
            var work = DisplayArea.Primary.WorkArea;
            var size = _appWindow.Size;
            var s = App.Settings.Current;

            var hasSaved = s.PalettePosVersion >= PositionVersion && s.PaletteX > -10000 && s.PaletteY > -10000;

            int x, y;
            if (hasSaved)
            {
                x = Math.Clamp(s.PaletteX, work.X, Math.Max(work.X, work.X + work.Width - size.Width));
                y = Math.Clamp(s.PaletteY, work.Y, Math.Max(work.Y, work.Y + work.Height - size.Height));
            }
            else
            {
                x = work.X + (work.Width - size.Width) / 2;
                y = work.Y + (work.Height - size.Height) / 2;
            }

            
            var target = new PointInt32(x, y);
            for (var i = 0; i < 4; i++)
            {
                _appWindow.Move(target);
                var now = _appWindow.Position;
                var dx = x - now.X;
                var dy = y - now.Y;
                if (Math.Abs(dx) <= 2 && Math.Abs(dy) <= 2) break;
                target = new PointInt32(target.X + dx, target.Y + dy);
            }

            var actual = _appWindow.Position;
            Log($"几何：请求 {x},{y} 尺寸 {size.Width}x{size.Height} → 实际 {actual.X},{actual.Y}（scale={Scale():0.##}，用存档={hasSaved}）");
        }
        catch (Exception ex)
        {
            Log("定位失败: " + ex.Message);
        }
    }

    
    private void SavePosition()
    {
        if (_appWindow is not { } app) return;
        try
        {
            var pos = app.Position;
            App.Settings.Current.PaletteX = pos.X;
            App.Settings.Current.PaletteY = pos.Y;
            App.Settings.Current.PalettePosVersion = PositionVersion;
            App.Settings.Save();
        }
        catch { }
    }

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private void ShowPalette(string? toolId)
    {
        if (!string.IsNullOrWhiteSpace(toolId)) _tool = toolId!;
        SelectPane(_tool, reload: true);

        
        
        _fadeEpoch++;                            
        FlyoutFade.Prepare(Root);

        try
        {
            if (_appWindow?.Presenter is OverlappedPresenter p)
                p.IsAlwaysOnTop = App.Settings.Current.PaletteOnTop;

            var hwnd = WindowNative.GetWindowHandle(this);
            if (!_shownOnce)
            {
                _shownOnce = true;
                Activate();
            }
            else
            {
                ShowWindow(hwnd, SW_SHOW);
                SetForegroundWindow(hwnd);
                Activate();
            }
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("palette", ex, "显示工具浮窗");
        }

        _visible = true;
        try { Root.Focus(FocusState.Programmatic); } catch { }     

        FlyoutFade.In(Root, 150);

        
        try { ClockView.Resume(); TimerView.Resume(); StopwatchView.Resume(); } catch { }
    }

    
    
    
    private void Collapse()
    {
        if (!_visible) return;
        _visible = false;
        SavePosition();

        
        try { ClockView.Pause(); TimerView.Pause(); StopwatchView.Pause(); } catch { }

        
        var epoch = ++_fadeEpoch;
        FlyoutFade.Out(Root, 150, () =>
        {
            if (epoch != _fadeEpoch) return;             
            try { ShowWindow(WindowNative.GetWindowHandle(this), SW_HIDE); }
            catch (Exception ex) { Core.AppLog.Warn("palette", "收起工具浮窗失败：" + ex.Message); }

            MemoryTrimmer.Trim();
        });
    }

    

    
    
    
    
    
    private void SelectPane(string tool, bool reload)
    {
        PanePick.Visibility = tool == "pick-number" ? Visibility.Visible : Visibility.Collapsed;
        PaneTimer.Visibility = tool == "timer" ? Visibility.Visible : Visibility.Collapsed;
        PaneStopwatch.Visibility = tool == "stopwatch" ? Visibility.Visible : Visibility.Collapsed;
        PaneClock.Visibility = tool == "clock" ? Visibility.Visible : Visibility.Collapsed;

        
        App.Settings.Current.PaletteTool = tool;

        if (reload && tool == "pick-number") PickView.Reload();

        SyncPivotToTool();
    }

    
    private void SyncPivotToTool()
    {
        var item = _tool switch
        {
            "pick-number" => PivotPick,
            "timer" => PivotTimer,
            "stopwatch" => PivotStopwatch,
            _ => PivotClock,
        };

        
        var current = ToolPivot.SelectedItem as SelectorBarItem;
        if (current != item)
        {
            _syncingPivot = true;
            try { ToolPivot.SelectedItem = item; }
            finally { _syncingPivot = false; }
        }
    }

    private void ToolPivot_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncingPivot || !_pivotLoaded) return;   
        if (ToolPivot.SelectedItem is not SelectorBarItem item) return;
        if (item.Tag is not string tag || tag == _tool) return;

        _tool = tag;
        SelectPane(tag, reload: true);
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape) HidePalette();
    }
}
