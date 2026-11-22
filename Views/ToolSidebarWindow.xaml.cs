using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Text;
using Windows.Graphics;
using Windows.Storage;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;
























public sealed partial class ToolSidebarWindow : Window
{
    
    
    
    
    
    
    
    
    
    
    
    
    
    private const int CollapsedThicknessDip = 20;        
    private const int CollapsedLengthDip = 110;          
    private const int CollapsedCompactThicknessDip = 12; 
    private const int CollapsedCompactLengthDip = 72;    

    
    
    
    
    
    
    
    
    
    private const int CollapsedEdgeGapDip = 6;

    
    private const int MaxCollapsedGapDip = 24;

    
    
    
    
    private static int CollapsedGapDip
    {
        get
        {
            try
            {
                var v = App.Settings.Current.SidebarCollapsedGap;
                return v < 0 ? 0 : v > MaxCollapsedGapDip ? MaxCollapsedGapDip : v;
            }
            catch
            {
                return CollapsedEdgeGapDip;
            }
        }
    }

    private const int PanelThicknessDip = 92;       
    
    
    
    
    private const int PanelLengthDip = 450;         
    private const int PanelThicknessFlatDip = 112;  
    private const int PanelLengthFlatDip = 470;     

    
    public const double MinHeightRatio = 0.45;
    public const double MaxHeightRatio = 0.95;
    
    public const double DefaultHeightRatio = 0.75;

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private static double SingleColumnMaxHeightRatio
    {
        get
        {
            try
            {
                var v = App.Settings.Current.SidebarHeightRatio;
                
                if (double.IsNaN(v) || v <= 0) return DefaultHeightRatio;
                return Math.Clamp(v, MinHeightRatio, MaxHeightRatio);
            }
            catch
            {
                return DefaultHeightRatio;
            }
        }
    }

    
    
    
    
    
    
    private static bool HeightAuto
    {
        get
        {
            try { return App.Settings.Current.SidebarHeightAuto; }
            catch { return true; }
        }
    }

    
    
    
    
    
    private const double CollapseSlideMs = 190;

    
    
    
    
    
    
    
    private const int FooterBaseDip = 94;

    
    private static int FooterDipFor(int visible) =>
        visible <= 0 ? 0 : (int)Math.Round(FooterBaseDip * visible / 5.0);

    
    
    
    
    
    
    private const int PanelThicknessFlatBareDip = 72;

    
    
    
    private const int FooterFlatButtonWidthDip = 104;   
    private const int FooterFlatSpacingDip = 8;         
    private const int FlatRowPadDip = 24;               

    
    
    
    
    
    
    
    
    private static readonly Dictionary<string, ToolSidebarWindow> _pool = new();

    
    private readonly string _edge;

    private AppWindow? _appWindow;
    private bool _expanded;

    
    
    
    
    
    
    
    private double _liveRatio = -1;

    private int _collapseEpoch;                            
    private bool _shownOnce;
    private bool _visible;

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _idle;

    
    private EventHandler<object>? _slideFrame;

    
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _slideWatchdog;

    
    
    private Windows.Graphics.RectInt32? _restRect;

    
    
    
    
    private int _slideEpoch;

    
    private readonly List<Button> _toolButtons = new();

    
    private Flyout? _confirmFlyout;

    
    private Flyout? _securityFlyout;

    
    private Flyout? _pptFlyout;

    
    private FontIcon? _foldIcon;

    
    private FontIcon? _pinIcon;

    
    
    
    
    
    
    
    public static bool SuppressAutoCollapse
    {
        get => DateTime.Now < _suppressUntil;
        set => _suppressUntil = value ? DateTime.Now.AddSeconds(SuppressMaxSeconds) : DateTime.MinValue;
    }

    private static DateTime _suppressUntil = DateTime.MinValue;

    
    private const int SuppressMaxSeconds = 15;

    
    
    
    
    
    
    
    
    public static Windows.Graphics.RectInt32? CurrentRect => RectOfInstance(AnchorInstance());

    
    
    
    
    public static Windows.Graphics.RectInt32? RectOf(string edge)
    {
        if (_pool.TryGetValue(edge, out var w) && w._visible) return RectOfInstance(w);
        return CurrentRect;
    }

    
    public static bool IsEdgePresent(string edge) => _pool.TryGetValue(edge, out var w) && w._visible;

    private static Windows.Graphics.RectInt32? RectOfInstance(ToolSidebarWindow? w)
    {
        if (w?._appWindow is null) return null;
        try
        {
            
            
            if (w._slideFrame is not null && w._restRect is { } rest) return rest;

            var pos = w._appWindow.Position;
            var size = w._appWindow.Size;
            return new Windows.Graphics.RectInt32(pos.X, pos.Y, size.Width, size.Height);
        }
        catch
        {
            return null;
        }
    }

    
    private static ToolSidebarWindow? AnchorInstance()
    {
        if (_pool.TryGetValue("right", out var r) && r._visible) return r;
        foreach (var w in _pool.Values) if (w._visible) return w;
        return null;
    }

    
    
    
    
    
    
    
    
    
    
    
    
    public static string CurrentEdge
    {
        get
        {
            try
            {
                var inst = AnchorInstance();
                if (inst is not null) return inst._edge;

                
                var want = DesiredEdges();
                return want.Contains("right") ? "right" : want[0];
            }
            catch
            {
                return "right";
            }
        }
    }

    
    private bool _pressed;
    private bool _dragging;
    private POINT _dragStart;
    private PointInt32 _dragOrigin;
    private double _pressInWindowDipX;        
    private double _pressInWindowDipY;
    private bool _useSnapToFinger;            
    private int _pressOffsetPx;               
    private int _pressOffsetPy;
    private bool _loggedSource;               
    private int _lastTargetX;                 
    private int _lastTargetY;
    private int _dragSamples;                 

    private ToolSidebarWindow(string edge)
    {
        _edge = edge;
        InitializeComponent();

        
        _idle = DispatcherQueue.CreateTimer();
        _idle.Interval = TimeSpan.FromSeconds(10);
        _idle.IsRepeating = false;
        _idle.Tick += (_, _) => { if (_expanded && !App.Settings.Current.SidebarPinned && !SuppressAutoCollapse) Collapse(); };

        Configure();

        Core.AppLog.Info("exit", $"侧边栏实例已建 edge={_edge}");
        Closed += (_, _) => Core.AppLog.Info("exit", $"侧边栏 Closed edge={_edge}");
    }

    

    
    public static void ShowSidebar()
    {
        if (!App.Settings.Current.SidebarEnabled)
        {
            App.Settings.Current.SidebarEnabled = true;
            App.Settings.Save();
        }
        foreach (var w in SyncInstances()) w.Present();
    }

    public static void HideSidebar()
    {
        foreach (var w in _pool.Values) w.HideSelf();
    }

    
    
    
    
    
    public static void CloseForExit()
    {
        foreach (var w in _pool.Values.ToList())
        {
            try { w.Close(); } catch { }
        }
        _pool.Clear();
        Core.AppLog.Info("exit", "侧边栏实例已请求 Close");
    }

    
    
    
    
    
    
    
    
    
    
    private static List<ToolSidebarWindow> SyncInstances(string? ratioEdge = null, double ratio = -1)
    {
        var want = DesiredEdges();

        foreach (var kv in _pool)
            if (!want.Contains(kv.Key)) kv.Value.HideSelf();

        var list = new List<ToolSidebarWindow>();
        foreach (var e in want)
        {
            if (!_pool.TryGetValue(e, out var w))
            {
                w = new ToolSidebarWindow(e);
                _pool[e] = w;
            }

            
            if (ratioEdge == e && ratio >= 0)
                w._liveRatio = ratio;

            list.Add(w);
        }

        
        
        
        if (ratioEdge is null && App.Settings.Current.SidebarPosRatio >= 0)
        {
            foreach (var w in list) w._liveRatio = App.Settings.Current.SidebarPosRatio;
        }

        return list;
    }

    
    
    
    
    
    
    private static List<string> DesiredEdges()
    {
        if (IsFreeMode) return new List<string> { FreeEdgeSetting };

        return App.Settings.Current.SidebarEdge switch
        {
            "left" => new List<string> { "left" },
            "both" => new List<string> { "left", "right" },
            
            
            _ => new List<string> { "right" },
        };
    }

    
    private static string FreeEdgeSetting => App.Settings.Current.SidebarFreeEdge switch
    {
        "left" or "top" or "bottom" => App.Settings.Current.SidebarFreeEdge,
        _ => "right",
    };

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    public static void CollapseAfterPopupsClosed()
    {
        foreach (var w in _pool.Values)
        {
            try
            {
                if (!w._visible || !w._expanded) continue;
                if (w._confirmFlyout is not null || w._securityFlyout is not null || w._pptFlyout is not null) continue;  
                if (App.Settings.Current.SidebarPinned) continue;
                w.Collapse();
            }
            catch { }
        }
    }

    
    
    
    
    public static void CollapseForCapture()
    {
        foreach (var w in _pool.Values)
        {
            try
            {
                if (!w._expanded) continue;
                w.Collapse(animate: false);               
            }
            catch { }
        }
    }

    public static bool IsSidebarVisible
    {
        get
        {
            foreach (var w in _pool.Values) if (w._visible) return true;
            return false;
        }
    }

    
    public static void ApplySetting(string? ratioEdge = null, double ratio = -1)
    {
        if (!App.Settings.Current.SidebarEnabled) { HideSidebar(); return; }
        foreach (var w in SyncInstances(ratioEdge, ratio))
        {
            w.Present();
            
            
            w.RebuildModules();
        }
    }

    
    
    
    
    
    
    public static void ApplyCollapsedGap()
    {
        foreach (var w in _pool.Values)
        {
            try { w.SnapToSetting(); }
            catch { }
        }
    }

    
    
    
    
    
    
    
    
    public static void ApplyHeightRatio()
    {
        foreach (var w in _pool.Values)
        {
            try
            {
                if (!w._expanded) continue;
                w.SnapToSetting();
            }
            catch { }
        }
    }

    private void SnapToSetting()
    {
        ApplyEdgeLayout();
        ApplySize();
        MoveToEdge();
    }

    

    
    private string Edge => _edge;

    
    private bool IsFlat => _edge is "top" or "bottom";

    
    
    
    
    
    
    
    
    
    
    

    
    
    
    
    private static bool CompactCollapsed =>
        string.Equals(App.Settings.Current.SidebarDensity, "compact", StringComparison.OrdinalIgnoreCase);

    
    
    
    
    
    
    private static bool CompactBody
    {
        get
        {
            var v = App.Settings.Current.SidebarExpandedDensity;
            if (string.Equals(v, "compact", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(v, "standard", StringComparison.OrdinalIgnoreCase)) return false;
            return CompactCollapsed;
        }
    }

    
    
    
    
    private int Columns => IsFlat ? 1 : (App.Settings.Current.SidebarColumns == 3 ? 3 : 1);

    
    private static int BarWidth => CompactBody ? 58 : PanelThicknessDip;

    /// <summary>
    /// 快捷应用栏摆在工具上方（贴左右边时才有效）。对应「侧边布局」里的
    /// 「快捷应用位置：工具左侧 / 工具上方」。
    /// </summary>
    private bool PinnedAbove =>
        !IsFlat && string.Equals(App.Settings.Current.SidebarPinnedPosition, "top", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否真的有一栏快捷应用要显示（模块还在列表里、且至少有一个应用）。</summary>
    private static bool HasPinnedApps
    {
        get
        {
            var settings = App.Settings.Current;
            if ((settings.PinnedApps?.Count ?? 0) == 0) return false;

            return (settings.SidebarModuleIds ?? Array.Empty<string>())
                .Any(id => SidebarModules.Find(id)?.Kind == SidebarModuleKinds.Pinned);
        }
    }

    /// <summary>
    /// 软件栏额外占的宽度：只有「贴左右边 + 软件栏在左」时才另起一列。
    /// 摆在上方或横条时都跟工具同宽，不再加宽面板。
    /// </summary>
    private int SoftwareBarWidth
    {
        get
        {
            if (!HasPinnedApps) return 0;
            if (IsFlat || PinnedAbove) return 0;

            return CellWidth + 1;   // +1 是右侧分隔线
        }
    }

    
    private static int CellWidth => BarWidth - 12;

    private static int ToolButtonH => CompactBody ? 40 : 52;
    private static double ToolIcon => CompactBody ? 16 : 20;
    private static double ToolLabel => CompactBody ? 9.5 : 11;
    private static int ToolGap => CompactBody ? 2 : 3;

    private static int FooterButtonH => CompactBody ? 36 : 44;
    private static int FlatMinLength => CompactBody ? 380 : PanelLengthFlatDip;

    /// <summary>横条模式的高度上限。实际高度由内容量出来，这个只是封顶。</summary>
    private const int FlatMaxThicknessDip = 240;

    
    private static bool IsFreeMode => App.Settings.Current.SidebarMode == "free";

    
    private static bool IsDual => !IsFreeMode && App.Settings.Current.SidebarEdge == "both";

    
    private ToolSidebarWindow? Partner()
    {
        if (!IsDual) return null;
        var other = _edge == "left" ? "right" : "left";
        return _pool.TryGetValue(other, out var w) ? w : null;
    }

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "工具侧边栏";
            _appWindow.Closing += (_, _) => Core.AppLog.Info("exit", $"侧边栏 Closing edge={_edge}");

            _appWindow.IsShownInSwitchers = false;          

            if (_appWindow.Presenter is OverlappedPresenter p)
            {
                p.SetBorderAndTitleBar(false, false);       
                p.IsResizable = false;
                p.IsMaximizable = false;
                p.IsMinimizable = false;
                p.IsAlwaysOnTop = true;                    
            }

            var ex = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(ex | WsExToolWindow));

            
            
            
            SystemBackdrop = new DesktopAcrylicBackdrop();
            ApplyPanelBrush();
            ThemeHost.Apply(Root, external: true);         
            BuildToolButtons();                            
            Root.PointerMoved += (_, _) => Touch();
            Root.PointerPressed += (_, _) => Touch();

            Activated += (_, args) =>
            {
                
                if (args.WindowActivationState == WindowActivationState.Deactivated
                    && _expanded && !_pressed && !App.Settings.Current.SidebarPinned && !SuppressAutoCollapse)
                    Collapse();
            };
            Root.ActualThemeChanged += (_, _) =>
            {
                ApplyPanelBrush();
                UpdatePinVisual();                         
                
                try
                {
                    WindowChrome.RemoveBorder(WindowNative.GetWindowHandle(this), rounded: _expanded,
                                              dark: Root.ActualTheme == ElementTheme.Dark);
                    RefreshWindowShape();                  
                }
                catch { }
            };

            ApplyEdgeLayout();
        }
        catch (Exception ex)
        {
            Log("初始化失败: " + ex.Message);
        }
    }

    private void Present()
    {
        try
        {
            ApplyEdgeLayout();
            ApplySize();

            var hwnd = WindowNative.GetWindowHandle(this);

            
            HoldOpaqueForTransition();

            if (!_shownOnce)
            {
                _shownOnce = true;
                Activate();
            }
            else
            {
                
                ShowWindow(hwnd, SW_SHOW);
                SetForegroundWindow(hwnd);
            }

            _visible = true;
            Collapse(animate: false);                      

            if (_appWindow?.Presenter is OverlappedPresenter p) p.IsAlwaysOnTop = true;

            MoveToEdge();
            
            
            
            WindowChrome.RemoveBorder(hwnd, rounded: _expanded, dark: Root.ActualTheme == ElementTheme.Dark);
            RefreshWindowShape();                          
        }
        catch (Exception ex)
        {
            Log("显示失败: " + ex.Message);
        }
    }

    private void HideSelf()
    {
        if (!_visible) return;
        StopSlide();
        _visible = false;

        
        VolumeFlyoutGroup.CloseAll();

        try { ShowWindow(WindowNative.GetWindowHandle(this), SW_HIDE); } catch { }
    }

    
    
    
    
    
    
    
    
    private void ApplyPanelBrush(bool opaque = false)
    {
        var solid = opaque;
        var dark = Root.ActualTheme == ElementTheme.Dark;
        Panel.Background = new SolidColorBrush(solid
            ? (dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
                    : Windows.UI.Color.FromArgb(255, 243, 243, 243))
            : (dark ? Windows.UI.Color.FromArgb(95, 20, 20, 20)
                    : Windows.UI.Color.FromArgb(110, 255, 255, 255)));
    }

    
    
    
    
    
    
    
    private void HoldOpaqueForTransition(int frames = 8)
    {
        ApplyPanelBrush(opaque: true);
        try
        {
            var left = frames;
            EventHandler<object>? handler = null;
            handler = (_, _) =>
            {
                if (left-- > 0) return;
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= handler!;
                ApplyPanelBrush();
            };
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += handler;
        }
        catch
        {
            ApplyPanelBrush();       
        }
    }

    

    private void Expand()
    {
        StopSlide();                                      
        _collapseEpoch++;                                 
        _expanded = true;
        ResetPanelOpacity();

        
        
        HoldOpaqueForTransition();

        CollapsedView.Visibility = Visibility.Collapsed;
        ExpandedView.Visibility = Visibility.Visible;
        
        Panel.BorderThickness = new Thickness(1);
        ApplyScrollLimit();                               

        
        
        
        
        SlideInFromEdge();
        Touch();
    }

    private void Collapse(bool animate = true)
    {
        StopSlide();                                      
        _idle.Stop();

        
        
        if (!_expanded || ExpandedView.Visibility != Visibility.Visible || !animate)
        {
            FinishCollapse();
            return;
        }

        _expanded = false;                                
        var epoch = ++_collapseEpoch;
        
        if (SlideOutToEdge(() => { if (epoch == _collapseEpoch) FinishCollapse(fadeIn: true); })) return;
        FinishCollapse();                                 
    }


    
    
    
    
    
    
    
    private void FinishCollapse(bool fadeIn = false)
    {
        if (_expanded) return;

        
        if (fadeIn) SetPanelOpacity(0f);

        
        
        
        Panel.BorderThickness = new Thickness(0);

        ExpandedView.Visibility = Visibility.Collapsed;
        CollapsedView.Visibility = Visibility.Visible;

        
        
        
        
        
        
        
        
        PointInt32? slideFrom = null, slideTo = null;
        try
        {
            if (_appWindow is not null)
            {
                var size = CollapsedSize();
                var pos = EdgePosition(size.Width, size.Height);
                var startPos = fadeIn
                    ? OutwardOffset(pos, (IsFlat ? size.Height : size.Width) + 2)   
                    : pos;

                _appWindow.MoveAndResize(new RectInt32(startPos.X, startPos.Y, size.Width, size.Height));
                ApplyRoundRegion(size);                    

                if (fadeIn)
                {
                    slideFrom = startPos;
                    slideTo = pos;
                    _restRect = new Windows.Graphics.RectInt32(pos.X, pos.Y, size.Width, size.Height);  
                }
            }
        }
        catch (Exception ex)
        {
            Log("收尾落位失败: " + ex.Message);
        }

        if (fadeIn)
        {
            FadePanelToOpaque(CollapseSlideMs);
            if (slideFrom is { } f && slideTo is { } t) TweenWindow(f, t, CollapseSlideMs);
        }
        else ResetPanelOpacity();

        ReassertCollapsed();
    }

    private void ResetPanelOpacity() => SetPanelOpacity(1f);

    
    private void SetPanelOpacity(float value)
    {
        try
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(Panel);
            visual.StopAnimation("Opacity");
            visual.Opacity = value;
        }
        catch { }
    }

    
    private void FadePanelToOpaque(double ms)
    {
        try
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(Panel);
            var compositor = visual.Compositor;
            visual.StopAnimation("Opacity");

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.2f, 0f), new Vector2(0f, 1f)));      
            fade.Duration = TimeSpan.FromMilliseconds(ms);
            visual.StartAnimation("Opacity", fade);
        }
        catch (Exception ex)
        {
            Log("抓手淡入失败: " + ex.Message);
            ResetPanelOpacity();                            
        }
    }

    
    
    
    
    
    private void ReassertCollapsed()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_expanded || _appWindow is null) return;

                
                
                
                if (_slideFrame is not null) return;

                
                
                var size = CollapsedSize();
                var pos = EdgePosition(size.Width, size.Height);
                var now = _appWindow.Size;
                var at = _appWindow.Position;
                if (now.Width == size.Width && now.Height == size.Height
                    && Math.Abs(at.X - pos.X) <= 2 && Math.Abs(at.Y - pos.Y) <= 2) return;

                Log("兜底落位：尺寸/位置没对上，纠一次");
                _appWindow.MoveAndResize(new RectInt32(pos.X, pos.Y, size.Width, size.Height));
                ApplyRoundRegion(size);
            }
            catch (Exception ex)
            {
                Log("兜底落位失败: " + ex.Message);
            }
        });
    }

    private void Touch()
    {
        if (!_expanded || _pressed) return;
        if (App.Settings.Current.SidebarPinned) return;    
        _idle.Stop();
        _idle.Start();
    }

    private void Collapse_Click(object sender, RoutedEventArgs e) => Collapse();

    
    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        var pinned = !App.Settings.Current.SidebarPinned;
        App.Settings.Current.SidebarPinned = pinned;
        App.Settings.Save();

        _idle.Stop();                       
        UpdatePinVisual();
        Touch();                            
        Log(pinned ? "常驻：开" : "常驻：关");
    }

    
    private void UpdatePinVisual()
    {
        try
        {
            var pinned = App.Settings.Current.SidebarPinned;
            if (_pinIcon is not null)
            {
                _pinIcon.Glyph = pinned ? "\uE840" : "\uE718";      

                if (pinned)
                {
                    _pinIcon.Foreground = new SolidColorBrush(AccentColor());
                }
                else
                {
                    
                    
                    
                    
                    
                    _pinIcon.ClearValue(FontIcon.ForegroundProperty);
                }
            }

            if (PinButton is not null)
                ToolTipService.SetToolTip(PinButton, pinned
                    ? "常驻中：展开后不会自动收起（再点一下松开）"
                    : "常驻：展开后不自动收起（手动点「收起」还是能收）");
        }
        catch (Exception ex)
        {
            Log("常驻外观更新失败: " + ex.Message);
        }
    }

    private static Windows.UI.Color AccentColor() =>
        new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);

    
    
    
    
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (!IsDual)
        {
            if (IsFreeMode) App.Settings.Current.SidebarFreeEdge = "right";
            else App.Settings.Current.SidebarEdge = "right";
        }

        App.Settings.Current.SidebarPosRatio = -1;
        App.Settings.Save();

        
        ApplySetting();
        Partner()?.SnapToSetting();
        Touch();
    }

    
    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.SidebarEnabled = false;
        App.Settings.Save();
        HideSidebar();                                    
    }

    
    
    
    
    private void OpenApp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            App.MainWindow?.ShowFromTray();
        }
        catch (Exception ex)
        {
            Log("打开主界面失败: " + ex.Message);
        }
    }

    
    public static void ApplyModules()
    {
        if (!App.Settings.Current.SidebarEnabled) return;
        foreach (var w in _pool.Values) w.RebuildModules();
    }

    
    
    
    
    
    public static void ApplyFooterSetting() => ApplyModules();

    private void RebuildModules()
    {
        try
        {
            BuildToolButtons();
            ApplyEdgeLayout();
            ApplySize();
            MoveToEdge();
        }
        catch (Exception ex)
        {
            Log("重建模块失败: " + ex.Message);
        }
    }

    
    
    
    
    private void BuildToolButtons()
    {
        ToolStack.Children.Clear();
        ToolStack.RowDefinitions.Clear();
        ToolStack.ColumnDefinitions.Clear();
        _toolButtons.Clear();

        var settings = App.Settings.Current;
        var modules = new List<SidebarModule>();
        foreach (var id in settings.SidebarModuleIds ?? Array.Empty<string>())
        {
            if (SidebarModules.Find(id) is { } found) modules.Add(found);
        }

        var pinnedModule = modules.FirstOrDefault(m => m.Kind == SidebarModuleKinds.Pinned);
        var pinnedApps = new List<PinnedApp>();
        if (pinnedModule is not null)
        {
            PinnedAppLauncher.EnsureIds();
            pinnedApps = settings.PinnedApps ?? new List<PinnedApp>();
        }

        // 快捷应用一律独立成栏：竖排是左边一列，横条是左边一段（2026-11-22）。
        if (pinnedModule is not null) modules.Remove(pinnedModule);

        var cells = modules.Count;
        var cols = IsFlat
            ? Math.Max(1, cells)
            : Math.Max(1, Math.Min(Columns, Math.Max(1, cells)));
        var rows = Math.Max(1, (int)Math.Ceiling(cells / (double)cols));

        for (var c = 0; c < cols; c++)
        {
            ToolStack.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = IsFlat ? GridLength.Auto : new GridLength(1, GridUnitType.Star),
            });
        }
        for (var r = 0; r < rows; r++)
            ToolStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var i = 0;
        foreach (var m in modules)
        {
            AddModuleButton(m, i, cols, 0);
            i++;
        }

        if (cells == 0)
        {
            var tip = new TextBlock
            {
                Text = "还没选模块\n去「侧边布局」挑几个",
                FontSize = 11,
                Opacity = 0.7,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(2, 6, 2, 6)
            };
            Grid.SetRow(tip, 0);
            Grid.SetColumn(tip, 0);
            ToolStack.Children.Add(tip);
        }

        BuildSoftwareButtons(pinnedModule, pinnedApps);
    }

    /// <summary>
    /// 软件栏三种排法：贴左右边·左侧是一整列，贴左右边·上方是一整行，
    /// 贴上下边是夹在工具与操作按钮之间的一整行（2026-11-22 Nick 定）。
    /// </summary>
    private void BuildSoftwareButtons(SidebarModule? pinnedModule, List<PinnedApp> pinnedApps)
    {
        SoftwareStack.Children.Clear();
        SoftwareStack.RowDefinitions.Clear();
        SoftwareStack.ColumnDefinitions.Clear();

        if (pinnedModule is null || pinnedApps.Count == 0)
        {
            SoftwarePane.Visibility = Visibility.Collapsed;
            SoftwarePane.Width = double.NaN;
            SoftwarePane.HorizontalAlignment = HorizontalAlignment.Stretch;
            return;
        }

        SoftwarePane.Visibility = Visibility.Visible;

        if (IsFlat)
        {
            BuildFlatSoftwareBar(pinnedApps);
            return;
        }

        if (PinnedAbove)
        {
            BuildTopSoftwareBar(pinnedApps);
            return;
        }

        BuildSideSoftwareBar(pinnedApps);
    }

    /// <summary>贴左右边·软件栏在左：一列，与工具列同宽，高度跟着工具列走。</summary>
    private void BuildSideSoftwareBar(List<PinnedApp> apps)
    {
        SoftwarePane.Width = CellWidth + 1;
        SoftwarePane.HorizontalAlignment = HorizontalAlignment.Stretch;
        SoftwareScroll.HorizontalScrollMode = ScrollMode.Disabled;
        SoftwareScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SoftwareScroll.VerticalScrollMode = ScrollMode.Auto;
        SoftwareScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        SoftwareScroll.MaxWidth = double.PositiveInfinity;
        SoftwareScroll.MaxHeight = MeasureToolColumnHeight();
        SoftwareStack.HorizontalAlignment = HorizontalAlignment.Stretch;

        SoftwareStack.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });

        for (var i = 0; i < apps.Count; i++)
        {
            SoftwareStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var button = BuildSoftwareTile(apps[i]);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetRow(button, i);
            Grid.SetColumn(button, 0);
            SoftwareStack.Children.Add(button);
        }
    }

    /// <summary>
    /// 贴左右边·软件栏在上：按面板列数横排，最多两行，再多在栏内纵向滚动。
    /// </summary>
    private void BuildTopSoftwareBar(List<PinnedApp> apps)
    {
        var cols = Math.Max(1, Columns);

        SoftwarePane.Width = double.NaN;
        SoftwarePane.HorizontalAlignment = HorizontalAlignment.Stretch;
        SoftwareScroll.HorizontalScrollMode = ScrollMode.Disabled;
        SoftwareScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SoftwareScroll.VerticalScrollMode = ScrollMode.Auto;
        SoftwareScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        SoftwareScroll.MaxWidth = double.PositiveInfinity;
        SoftwareScroll.MaxHeight = 2 * (ToolButtonH + ToolGap);
        SoftwareStack.HorizontalAlignment = HorizontalAlignment.Stretch;

        for (var c = 0; c < cols; c++)
            SoftwareStack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var rows = Math.Max(1, (int)Math.Ceiling(apps.Count / (double)cols));
        for (var r = 0; r < rows; r++)
            SoftwareStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < apps.Count; i++)
        {
            var button = BuildSoftwareTile(apps[i]);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetRow(button, i / cols);
            Grid.SetColumn(button, i % cols);
            SoftwareStack.Children.Add(button);
        }
    }

    /// <summary>
    /// 量一下工具列当前有多高。软件栏拿它当最大高度 ——
    /// 面板高度只由工具决定，软件再多也只是栏内滚动，不会在右侧留出空白（2026-11-22）。
    /// </summary>
    private double MeasureToolColumnHeight()
    {
        try
        {
            var cols = Math.Max(1, Columns);
            var width = Math.Max(CellWidth, cols * CellWidth + (cols - 1) * ToolGap);

            ToolStack.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));

            var height = ToolStack.DesiredSize.Height;
            if (height > 20) return height;
        }
        catch (Exception ex)
        {
            Log("量工具列高度失败: " + ex.Message);
        }

        var fallbackCols = Math.Max(1, Columns);
        var rows = Math.Max(1, (int)Math.Ceiling(Math.Max(1, _toolButtons.Count) / (double)fallbackCols));
        return rows * (ToolButtonH + ToolGap);
    }

    /// <summary>横条模式：软件栏独占一行，夹在工具与操作按钮之间，横向铺满。</summary>
    private void BuildFlatSoftwareBar(List<PinnedApp> apps)
    {
        SoftwarePane.Width = double.NaN;
        SoftwarePane.HorizontalAlignment = HorizontalAlignment.Stretch;
        SoftwareScroll.MaxHeight = double.PositiveInfinity;
        SoftwareScroll.MinHeight = 0;
        SoftwareScroll.MaxWidth = double.PositiveInfinity;
        SoftwareScroll.HorizontalScrollMode = ScrollMode.Auto;
        SoftwareScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        SoftwareScroll.VerticalScrollMode = ScrollMode.Disabled;
        SoftwareScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;

        // 整条横条里工具是居中的，快捷应用也一样居中，不留左边一坨右边空着。
        SoftwareStack.HorizontalAlignment = HorizontalAlignment.Center;

        SoftwareStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < apps.Count; i++)
        {
            SoftwareStack.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var button = BuildSoftwareTile(apps[i], compact: true);
            Grid.SetRow(button, 0);
            Grid.SetColumn(button, i);
            SoftwareStack.Children.Add(button);
        }
    }

    /// <summary>软件栏里的一格：竖排时带名称（与模块按钮同款），横条时只留图标。</summary>
    private Button BuildSoftwareTile(PinnedApp app, bool compact = false)
    {
        var iconHost = new Grid
        {
            Width = ToolIcon + 6,
            Height = ToolIcon + 6,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var glyph = new FontIcon
        {
            Glyph = "\uE731",
            FontSize = ToolIcon,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var image = new Image
        {
            Width = ToolIcon,
            Height = ToolIcon,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        iconHost.Children.Add(glyph);
        iconHost.Children.Add(image);
        LoadPinnedIcon(image, glyph, app.Path);

        var content = new StackPanel { Spacing = compact ? 0 : 3 };
        content.Children.Add(iconHost);

        if (!compact)
        {
            content.Children.Add(new TextBlock
            {
                Text = PinnedAppLauncher.DisplayName(app),
                FontSize = ToolLabel,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = Math.Max(40, CellWidth - 10),
            });
        }

        var button = new Button
        {
            Tag = $"pinapp:{app.Id}",
            Width = compact ? 44 : double.NaN,
            Height = compact ? 40 : double.NaN,
            MinWidth = 0,
            MinHeight = compact ? 0 : ToolButtonH,
            Padding = compact ? new Thickness(0) : new Thickness(0, CompactBody ? 4 : 8, 0, CompactBody ? 4 : 8),
            Margin = compact ? new Thickness(2, 0, 2, 0) : new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = content,
        };

        ToolTipService.SetToolTip(button, PinnedAppLauncher.DisplayName(app) + "\n" + app.Path);
        button.Click += PinnedApp_Click;

        var flyout = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "打开", Tag = $"pinapp:{app.Id}" };
        open.Click += PinnedApp_Click;
        var remove = new MenuFlyoutItem { Text = "从侧边栏移除", Tag = $"pinapp:{app.Id}" };
        remove.Click += PinnedApp_Remove_Click;
        flyout.Items.Add(open);
        flyout.Items.Add(remove);
        button.ContextFlyout = flyout;

        return button;
    }

    private void AddModuleButton(SidebarModule m, int index, int cols, int rowOffset)
    {
        var content = new StackPanel { Spacing = 3 };
        content.Children.Add(new FontIcon
        {
            Glyph = m.Glyph,
            FontSize = ToolIcon,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = LabelOf(m),
            FontSize = ToolLabel,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 84
        });

        var btn = new Button
        {
            Tag = m.Id,
            MinWidth = 0,
            MinHeight = ToolButtonH,
            Padding = new Thickness(0, CompactBody ? 4 : 8, 0, CompactBody ? 4 : 8),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = content
        };
        ToolTipService.SetToolTip(btn, m.Note ?? m.Name);

        if (m.Kind == SidebarModuleKinds.Action && TeachingActions.IsHold(m.Id))
        {
            var holdId = m.Id;
            btn.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
            {
                SuppressAutoCollapse = true;
                TeachingActions.Begin(holdId);
            }), true);
            btn.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => EndHold(holdId)), true);
            btn.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndHold(holdId)), true);
        }
        else
        {
            btn.Click += Tool_Click;
        }

        Grid.SetRow(btn, rowOffset + index / cols);
        Grid.SetColumn(btn, index % cols);
        ToolStack.Children.Add(btn);
        _toolButtons.Add(btn);
    }

    private void AddPinnedButton(PinnedApp app, int index, int cols, int rowOffset)
    {
        var content = new StackPanel { Spacing = 3 };
        var iconHost = new Grid
        {
            Width = ToolIcon + 4,
            Height = ToolIcon + 4,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var glyph = new FontIcon
        {
            Glyph = "\uE731",
            FontSize = ToolIcon,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var image = new Image
        {
            Width = ToolIcon,
            Height = ToolIcon,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        iconHost.Children.Add(glyph);
        iconHost.Children.Add(image);
        content.Children.Add(iconHost);
        LoadPinnedIcon(image, glyph, app.Path);
        content.Children.Add(new TextBlock
        {
            Text = PinnedAppLauncher.DisplayName(app),
            FontSize = ToolLabel,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 84,
        });

        var button = new Button
        {
            Tag = $"pinapp:{app.Id}",
            MinWidth = 0,
            MinHeight = ToolButtonH,
            Padding = new Thickness(0, CompactBody ? 4 : 8, 0, CompactBody ? 4 : 8),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = content,
        };
        ToolTipService.SetToolTip(button, "快捷应用：" + app.Path);
        button.Click += PinnedApp_Click;

        var flyout = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "打开", Tag = $"pinapp:{app.Id}" };
        open.Click += PinnedApp_Click;
        var remove = new MenuFlyoutItem { Text = "从侧边栏移除", Tag = $"pinapp:{app.Id}" };
        remove.Click += PinnedApp_Remove_Click;
        flyout.Items.Add(open);
        flyout.Items.Add(remove);
        button.ContextFlyout = flyout;

        Grid.SetRow(button, rowOffset + index / cols);
        Grid.SetColumn(button, index % cols);
        ToolStack.Children.Add(button);
        _toolButtons.Add(button);
    }

    /// <summary>
    /// 快捷应用的图标。走 shell 取图标而不是文件缩略图 ——
    /// 开始菜单里的 .lnk 用缩略图接口多半取不到，只剩一个通用字形（2026-11-22）。
    /// </summary>
    private void LoadPinnedIcon(Image image, FontIcon fallback, string path)
    {
        try
        {
            var source = Services.AppIconService.Get(path);
            if (source is null) return;

            image.Source = source;
            image.Visibility = Visibility.Visible;
            fallback.Visibility = Visibility.Collapsed;
        }
        catch
        {
        }
    }

    private void AddPinnedPlaceholder(int index, int cols, int rowOffset)
    {
        var content = new StackPanel { Spacing = 3 };
        content.Children.Add(new FontIcon
        {
            Glyph = "\uE710",
            FontSize = ToolIcon,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = "添加应用",
            FontSize = ToolLabel,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var button = new Button
        {
            MinWidth = 0,
            MinHeight = ToolButtonH,
            Padding = new Thickness(0, CompactBody ? 4 : 8, 0, CompactBody ? 4 : 8),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = content,
        };
        ToolTipService.SetToolTip(button, "去「侧边布局」添加快捷应用");
        button.Click += (_, _) =>
        {
            App.MainWindow?.ShowFromTray();
            App.MainWindow?.Shell.NavigateTo("sidebar");
        };

        Grid.SetRow(button, rowOffset + index / cols);
        Grid.SetColumn(button, index % cols);
        ToolStack.Children.Add(button);
        _toolButtons.Add(button);
    }

    private void PinnedApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string tag) return;
        if (!tag.StartsWith("pinapp:", StringComparison.Ordinal)) return;

        var app = PinnedAppLauncher.FindById(tag[7..]);
        if (app is null) return;

        if (PinnedAppLauncher.Launch(app) is { } error)
        {
            ShowPinnedError(element as Button, error);
            return;
        }

        Collapse();
    }

    private void PinnedApp_Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string tag) return;
        if (!tag.StartsWith("pinapp:", StringComparison.Ordinal)) return;
        if (!PinnedAppLauncher.RemoveById(tag[7..])) return;

        ApplyModules();
        App.MainWindow?.RefreshHotkeys();
    }

    private void ShowPinnedError(Button? anchor, string message)
    {
        SuppressAutoCollapse = true;
        ShowConfirmFlyout(anchor, "快捷应用", message, "知道了", () => { },
            onClosed: () => { SuppressAutoCollapse = false; }, dangerStyle: false);
    }

    
    
    
    
    
    
    
    private static string LabelOf(SidebarModule m)
    {
        if (!App.Settings.Current.SidebarLongLabels) return m.ShortName;
        return m.LongName.Length > 0 ? m.LongName : m.ShortName;
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string id) return;
        try
        {
            var m = SidebarModules.Find(id);
            if (m is null) return;

            
            if (m.Kind == SidebarModuleKinds.Action)
            {
                
                
                
                
                
                
                if (m.Id == "security")
                {
                    ShowSecurityFlyout(b);
                    return;
                }

                if (m.Id == "ppttouch")
                {
                    ShowPptTouchFlyout(b);
                    return;
                }

                
                
                
                var asks = TeachingActions.AsksFirst(m.Id);
                var hint = TeachingActions.Run(m.Id);

                if (hint is { Length: > 0 })
                {
                    
                    
                    SuppressAutoCollapse = true;
                    var lines = hint.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    var title = lines.Length > 0 ? lines[0].Trim() : hint;
                    var body = lines.Length > 1 ? lines[1].Trim() : null;

                    var confirmed = false;

                    
                    
                    ShowConfirmFlyout(b, title, body, asks ? "确定关闭" : "知道了", () =>
                    {
                        if (!asks) return;                 
                        confirmed = true;
                        SuppressAutoCollapse = false;
                        TeachingActions.ConfirmPending(m.Id);
                        Touch();
                    },
                    onClosed: () =>
                    {
                        
                        
                        if (!confirmed) TeachingActions.RefocusPrevious();
                    },
                    dangerStyle: asks);

                    
                    
                }
                else
                {
                    HideConfirmFlyout();
                    Collapse();
                }
                return;
            }

            
            if (m.Kind == SidebarModuleKinds.Panel)
            {
                if (m.Id == "brightness") ToggleBrightness();
                else ToggleVolume();
                return;
            }

            Collapse();                                        
            if (m.Kind == SidebarModuleKinds.Page && m.Page is not null)
                App.MainWindow?.OpenToolSettings(m.Page);      
            else if (m.Kind == SidebarModuleKinds.Window)
                OpenModuleWindow(m);                           
            else
                ToolPaletteWindow.ShowTool(m.Id);              
        }
        catch (Exception ex)
        {
            Log("打开工具失败: " + ex.Message);
        }
    }


    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private static void OpenModuleWindow(SidebarModule m)
    {
        if (m.WindowType is { } type)
        {
            try
            {
                var show = type.GetMethod(
                    "Show",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    binder: null, types: Type.EmptyTypes, modifiers: null);

                if (show is not null)
                {
                    show.Invoke(null, null);
                    return;
                }

                
                Log($"模块 {m.Id} 的窗口没有 public static Show()，已退回学科工具窗");
            }
            catch (Exception ex)
            {
                Log($"打开模块 {m.Id} 的窗口失败: {ex.Message}");
                return;
            }
        }

        SubjectToolsWindow.ShowTool(m.WindowTool);
    }

    

    
    
    
    
    
    
    
    
    
    private void ToggleVolume()
    {
        SuppressAutoCollapse = true;
        VolumeWindow.Toggle(VolumeWindow.Target.Volume, _edge);
    }

    
    
    
    
    private void ToggleBrightness()
    {
        SuppressAutoCollapse = true;
        VolumeWindow.Toggle(VolumeWindow.Target.Brightness, _edge);
    }

    private void EndHold(string id)
    {
        try { TeachingActions.End(id); }
        finally { SuppressAutoCollapse = false; }
    }

    
    
    
    
    
    
    
    
    

    
    
    
    
    private void ShowConfirmFlyout(Button? anchor, string title, string? body, string okText, Action onConfirm,
                                   Action? onClosed = null, bool dangerStyle = true)
    {
        try
        {
            HideConfirmFlyout();

            var panel = new Grid { Width = 300, RowSpacing = 12 };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var text = new StackPanel { Spacing = 4 };
            text.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            if (!string.IsNullOrWhiteSpace(body))
            {
                text.Children.Add(new TextBlock
                {
                    Text = body,
                    FontSize = 12,
                    Opacity = 0.78,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            Grid.SetRow(text, 0);
            panel.Children.Add(text);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            
            if (dangerStyle)
            {
                var cancel = new Button { Content = "取消", FontSize = 13, MinWidth = 76, Padding = new Thickness(0, 6, 0, 6) };
                cancel.Click += (_, _) => HideConfirmFlyout();
                buttons.Children.Add(cancel);
            }

            var danger = new Button { Content = okText, FontSize = 13, MinWidth = 88, Padding = new Thickness(0, 6, 0, 6) };
            if (dangerStyle)
            {
                
                var red = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xC4, 0x2B, 0x1C));
                var redHover = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xD1, 0x43, 0x35));
                var redPressed = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xA8, 0x22, 0x16));
                var white = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
                var clear = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                danger.Resources["ButtonBackground"] = red;
                danger.Resources["ButtonBackgroundPointerOver"] = redHover;
                danger.Resources["ButtonBackgroundPressed"] = redPressed;
                danger.Resources["ButtonBackgroundDisabled"] = redPressed;
                danger.Resources["ButtonForeground"] = white;
                danger.Resources["ButtonForegroundPointerOver"] = white;
                danger.Resources["ButtonForegroundPressed"] = white;
                danger.Resources["ButtonBorderBrush"] = clear;
                danger.Resources["ButtonBorderBrushPointerOver"] = clear;
                danger.Resources["ButtonBorderBrushPressed"] = clear;
            }
            buttons.Children.Add(danger);

            Grid.SetRow(buttons, 1);
            panel.Children.Add(buttons);

            var flyout = new Flyout
            {
                ShouldConstrainToRootBounds = false,     
                Placement = ConfirmPlacement(),
                Content = panel,
            };
            flyout.Closed += (_, _) =>
            {
                if (ReferenceEquals(_confirmFlyout, flyout)) _confirmFlyout = null;
                SuppressAutoCollapse = false;            
                try { onClosed?.Invoke(); } catch (Exception ex) { Log("确认面板收尾失败: " + ex.Message); }

                
                
                CollapseAfterPopupsClosed();
            };
            danger.Click += (_, _) =>
            {
                
                
                try { onConfirm(); } catch (Exception ex) { Log("确认动作失败: " + ex.Message); }
                flyout.Hide();
            };

            _confirmFlyout = flyout;

            var target = (FrameworkElement?)anchor ?? Root;
            flyout.ShowAt(target, new FlyoutShowOptions { Placement = flyout.Placement });
            Log($"确认面板：原生 Flyout 已弹出（标题={title} 锚点={(target as FrameworkElement)?.Name} 方位={flyout.Placement}）");

            
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _ = System.Threading.Tasks.Task.Delay(20000).ContinueWith(_ =>
            {
                try
                {
                    queue?.TryEnqueue(() =>
                    {
                        if (ReferenceEquals(_confirmFlyout, flyout)) HideConfirmFlyout();
                    });
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            Log("确认面板弹出失败: " + ex.Message);
            SuppressAutoCollapse = false;            
        }
    }

    private void HideConfirmFlyout()
    {
        try
        {
            var f = _confirmFlyout;
            _confirmFlyout = null;
            f?.Hide();
        }
        catch { }
    }

    
    
    
    
    
    
    
    
    

    private void ShowPptTouchFlyout(Button anchor)
    {
        try
        {
            HidePptTouchFlyout();
            HideSecurityFlyout();
            HideConfirmFlyout();

            var panel = new StackPanel { Width = 248, Spacing = 1 };

            void Header(string text) => panel.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 12,
                Opacity = 0.7,
                Margin = new Thickness(12, 8, 12, 6),
            });

            
            
            void SwitchRow(string title, string note, bool initial, Func<bool, bool> apply)
            {
                var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = title, FontSize = 14 });
                text.Children.Add(new TextBlock
                {
                    Text = note,
                    FontSize = 11,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                });

                var toggle = new ToggleSwitch
                {
                    IsOn = initial,
                    MinWidth = 0,
                    OnContent = "",
                    OffContent = "",
                    VerticalAlignment = VerticalAlignment.Center,
                };

                
                
                var ready = false;
                toggle.Toggled += (_, _) =>
                {
                    if (!ready) return;

                    var actual = apply(toggle.IsOn);
                    if (actual == toggle.IsOn) return;

                    ready = false;
                    toggle.IsOn = actual;
                    ready = true;
                };
                ready = true;

                var row = new Grid { Margin = new Thickness(12, 4, 12, 6), ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(text, 0);
                Grid.SetColumn(toggle, 1);
                row.Children.Add(text);
                row.Children.Add(toggle);
                panel.Children.Add(row);
            }

            Header("PowerPoint 触控");
            SwitchRow("触控手势", "放映时轻点翻页、上下滑换页", PptTouchSwitch.GestureOn(), PptTouchSwitch.SetGesture);
            SwitchRow("长按菜单", "长按弹出本程序菜单，替代系统右键菜单", PptTouchSwitch.MenuOn(), PptTouchSwitch.SetMenu);

            var flyout = new Flyout
            {
                ShouldConstrainToRootBounds = false,     
                Placement = ConfirmPlacement(),
                Content = panel,
            };
            flyout.Closed += (_, _) =>
            {
                if (ReferenceEquals(_pptFlyout, flyout)) _pptFlyout = null;

                
                
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_confirmFlyout is not null) return;     
                    SuppressAutoCollapse = false;
                    CollapseAfterPopupsClosed();
                });
            };

            SuppressAutoCollapse = true;                
            _pptFlyout = flyout;
            flyout.ShowAt(anchor, new FlyoutShowOptions { Placement = flyout.Placement });
            Log("PPT 触控：菜单已弹出");
        }
        catch (Exception ex)
        {
            Log("PPT 触控菜单弹出失败: " + ex.Message);
            SuppressAutoCollapse = false;
        }
    }

    private void HidePptTouchFlyout()
    {
        try
        {
            var f = _pptFlyout;
            _pptFlyout = null;
            f?.Hide();
        }
        catch { }
    }

    
    
    
    
    
    

    private void ShowSecurityFlyout(Button anchor)
    {
        try
        {
            HideSecurityFlyout();
            HidePptTouchFlyout();
            HideConfirmFlyout();

            var panel = new StackPanel { Width = 216, Spacing = 1 };
            panel.Children.Add(new TextBlock
            {
                Text = "安全选项",
                FontSize = 12,
                Opacity = 0.7,
                Margin = new Thickness(12, 8, 12, 6),
            });

            void Item(string glyph, string text, Action act)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                row.Children.Add(new FontIcon
                {
                    Glyph = glyph,
                    FontSize = 16,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                row.Children.Add(new TextBlock
                {
                    Text = text,
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                });

                
                var item = new Button
                {
                    Content = row,
                    MinHeight = 40,
                    Padding = new Thickness(12, 6, 12, 6),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                item.Click += (_, _) =>
                {
                    HideSecurityFlyout();
                    try { act(); }
                    catch (Exception ex) { Log("安全选项执行失败: " + ex.Message); }
                };
                panel.Children.Add(item);
            }

            
            Item("\uE72E", "锁定电脑", () => TeachingActions.Begin("lock"));
            Item("\uE748", "切换用户", () => TeachingActions.Begin("switchuser"));
            
            Item("\uE7E8", "注销…", () => BeginLogout(anchor));

            var flyout = new Flyout
            {
                ShouldConstrainToRootBounds = false,     
                Placement = ConfirmPlacement(),
                Content = panel,
            };
            flyout.Closed += (_, _) =>
            {
                if (ReferenceEquals(_securityFlyout, flyout)) _securityFlyout = null;

                
                
                
                
                
                
                
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_confirmFlyout is not null) return;     
                    SuppressAutoCollapse = false;
                    CollapseAfterPopupsClosed();
                });
            };

            SuppressAutoCollapse = true;                
            _securityFlyout = flyout;
            flyout.ShowAt(anchor, new FlyoutShowOptions { Placement = flyout.Placement });
            Log("安全选项：菜单已弹出");
        }
        catch (Exception ex)
        {
            Log("安全选项菜单弹出失败: " + ex.Message);
            SuppressAutoCollapse = false;
        }
    }

    private void HideSecurityFlyout()
    {
        try
        {
            var f = _securityFlyout;
            _securityFlyout = null;
            f?.Hide();
        }
        catch { }
    }

    
    
    
    
    private void BeginLogout(Button anchor)
    {
        var hint = TeachingActions.Begin("logout");
        if (string.IsNullOrEmpty(hint)) return;

        SuppressAutoCollapse = true;
        var lines = hint.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var title = lines.Length > 0 ? lines[0].Trim() : hint;
        var body = lines.Length > 1 ? lines[1].Trim() : null;

        ShowConfirmFlyout(anchor, title, body, "确定注销",
            () => TeachingActions.ConfirmPending("logout"), dangerStyle: true);
    }

    
    
    
    
    
    
    private static FlyoutPlacementMode ConfirmPlacement()
    {
        try
        {
            if (CurrentRect is { } r)
            {
                var work = DisplayArea.Primary.WorkArea;
                var tol = 8 + (AnchorInstance() is { } w
                    ? (int)Math.Round(CollapsedGapDip * w.Scale())
                    : CollapsedGapDip);
                if (r.X <= work.X + tol) return FlyoutPlacementMode.Right;
                if (r.X + r.Width >= work.X + work.Width - tol) return FlyoutPlacementMode.Left;
                if (r.Y <= work.Y + tol) return FlyoutPlacementMode.Bottom;
                if (r.Y + r.Height >= work.Y + work.Height - tol) return FlyoutPlacementMode.Top;
            }
        }
        catch { }
        return FlyoutPlacementMode.Right;
    }

    

    private void Strip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_appWindow is null) return;
        StopSlide();                                     
        _dragOrigin = _appWindow.Position;
        _dragging = false;

        
        
        
        
        
        _useSnapToFinger = !IsMousePointer(e);
        if (_useSnapToFinger)
        {
            var press = e.GetCurrentPoint(Root).Position;
            _pressInWindowDipX = press.X;
            _pressInWindowDipY = press.Y;

            
            var pressScale = DpiScaleOf();
            _pressOffsetPx = (int)Math.Round(press.X * pressScale);
            _pressOffsetPy = (int)Math.Round(press.Y * pressScale);
        }
        else
        {
            PointerScreenPoint(e, out _dragStart);       
        }

        _pressed = true;
        _loggedSource = false;
        _lastTargetX = _dragOrigin.X;
        _lastTargetY = _dragOrigin.Y;
        _dragSamples = 0;
        _idle.Stop();
        Log($"按下: device={e.Pointer.PointerDeviceType} pointerId={e.Pointer.PointerId} 算法={(_useSnapToFinger ? "跟随手指" : "绝对位移")} 屏幕点=({_dragStart.X},{_dragStart.Y}) 窗口=({_dragOrigin.X},{_dragOrigin.Y})");
        if (sender is UIElement u) u.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Strip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed || _appWindow is null) return;

        int targetX;
        int targetY;

        if (_useSnapToFinger)
        {
            
            
            if (TryGetPointerScreenPoint(e, out var abs))
            {
                LogSource("系统指针坐标(绝对)");
                targetX = abs.X - _pressOffsetPx;
                targetY = abs.Y - _pressOffsetPy;
            }
            else
            {
                
                
                
                
                
                
                
                
                LogSource("窗口相对·绝对推算");
                var scale = DpiScaleOf();
                var cur = e.GetCurrentPoint(Root).Position;
                var real = WindowRectNow();

                targetX = real.X + (int)Math.Round(cur.X * scale) - _pressOffsetPx;
                targetY = real.Y + (int)Math.Round(cur.Y * scale) - _pressOffsetPy;
                LogDrag(cur.X, cur.Y, targetX - real.X, targetY - real.Y, targetX, targetY);
            }
        }
        else
        {
            if (!PointerScreenPoint(e, out var pt)) return;
            targetX = _dragOrigin.X + (pt.X - _dragStart.X);
            targetY = _dragOrigin.Y + (pt.Y - _dragStart.Y);
        }

        if (!_dragging && Math.Abs(targetX - _dragOrigin.X) + Math.Abs(targetY - _dragOrigin.Y) < 8)
        {
            return;                                      
        }

        _dragging = true;

        
        
        if (IsDual) targetX = _dragOrigin.X;

        if (targetX == _lastTargetX && targetY == _lastTargetY) { e.Handled = true; return; }
        _lastTargetX = targetX;
        _lastTargetY = targetY;

        _appWindow.Move(new PointInt32(targetX, targetY));

        
        
        
        
        
        {
            var wa = DisplayArea.Primary.WorkArea;
            var flatEdge = _edge is "top" or "bottom";
            var along = flatEdge ? wa.Width : wa.Height;
            var mine = flatEdge ? _appWindow.Size.Width : _appWindow.Size.Height;
            var span = Math.Max(0, along - mine);
            var alongPos = flatEdge ? targetX - wa.X : targetY - wa.Y;

            _liveRatio = span <= 0 ? 0.5 : Math.Clamp(alongPos / (double)span, 0, 1);

            
            
            Core.AppLog.Info("sidebar",
                $"拖动 edge={_edge} flat={flatEdge} along={alongPos} span={span} ratio={_liveRatio:F3} " +
                $"target=({targetX},{targetY})");
        }

        
        if (IsDual) FollowPartner(targetY, _appWindow.Size.Height);

        e.Handled = true;
    }

    
    private static bool PointerScreenPoint(PointerRoutedEventArgs e, out POINT pt)
    {
        pt = default;
        try
        {
            if (GetCursorPos(out var cursor))
            {
                pt = cursor;
                return true;
            }
        }
        catch { }

        return false;
    }

    
    
    
    
    
    private static bool TryGetPointerScreenPoint(PointerRoutedEventArgs e, out POINT pt)
    {
        pt = default;
        try
        {
            var id = e.Pointer.PointerId;
            if (id == 0) return false;
            if (!GetPointerInfo(id, out var info)) return false;
            if (info.pointerId != id) return false;                     
            if (info.pointerType != 2 && info.pointerType != 3) return false;   
            if (info.ptPixelLocation.X == 0 && info.ptPixelLocation.Y == 0) return false;

            pt = new POINT(info.ptPixelLocation.X, info.ptPixelLocation.Y);
            return true;
        }
        catch { return false; }
    }

    
    private void LogSource(string src)
    {
        if (_loggedSource) return;
        _loggedSource = true;
        Log($"拖动坐标源: {src}");
    }

    
    
    
    
    private void LogDrag(double curDipX, double curDipY, double dxF, double dyF, int tx, int ty)
    {
        if (++_dragSamples > 40) return;
        try
        {
            var real = WindowRectNow();
            var said = _appWindow?.Position ?? new PointInt32(-1, -1);
            Log($"样本{_dragSamples}: 读数=({curDipX:0.0},{curDipY:0.0}) 手偏=({dxF:0.0},{dyF:0.0}) 目标=({tx},{ty}) 实读=({real.X},{real.Y}) 自称=({said.X},{said.Y})");
        }
        catch { }
    }

    
    private PointInt32 WindowRectNow()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var r))
                return new PointInt32(r.Left, r.Top);
        }
        catch { }

        return _appWindow?.Position ?? new PointInt32(0, 0);
    }

    
    private double DpiScaleOf()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd != IntPtr.Zero)
            {
                var dpi = GetDpiForWindow(hwnd);
                if (dpi > 0) return dpi / 96.0;
            }
        }
        catch { }

        return 1.0;
    }

    
    
    
    
    private static bool IsMousePointer(PointerRoutedEventArgs e)
    {
        try
        {
            return string.Equals(e.Pointer.PointerDeviceType.ToString(), "Mouse", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private void Strip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        if (sender is UIElement u) u.ReleasePointerCapture(e.Pointer);

        if (_dragging)
        {
            _dragging = false;
            DockToNearestEdge();
        }
        else
        {
            Expand();                                      
        }

        e.Handled = true;
    }

    
    
    
    
    
    
    private void DockToNearestEdge()
    {
        if (_appWindow is null) return;
        try
        {
            var work = DisplayArea.Primary.WorkArea;
            var size = _appWindow.Size;
            var pos = _appWindow.Position;

            double cx = pos.X + size.Width / 2.0;
            double cy = pos.Y + size.Height / 2.0;

            var dl = Math.Abs(cx - work.X);
            var dr = Math.Abs(work.X + work.Width - cx);

            string edge;
            if (IsDual)
            {
                edge = _edge;                            
            }
            else
            {
                var dt = Math.Abs(cy - work.Y);
                var db = Math.Abs(work.Y + work.Height - cy);
                var min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
                edge = min == dl ? "left" : min == dr ? "right" : min == dt ? "top" : "bottom";

                if (IsFreeMode)
                {
                    App.Settings.Current.SidebarFreeEdge = edge;    
                }
                else
                {
                    
                    if (edge is "top" or "bottom") edge = dl <= dr ? "left" : "right";
                    App.Settings.Current.SidebarEdge = edge;
                }
            }

            
            
            
            
            App.Settings.Current.SidebarPosRatio = _liveRatio;
            App.Settings.Save();

            
            
            
            
            if (edge != _edge)
            {
                Log("换边: " + _edge + " → " + edge);

                
                
                
                
                
                var flatNew = edge is "top" or "bottom";
                var alongNow = flatNew ? cx - work.X : cy - work.Y;
                var mineNow = flatNew ? Math.Max(1, _appWindow?.Size.Width ?? 1) : Math.Max(1, _appWindow?.Size.Height ?? 1);
                var spanNow = Math.Max(0, (flatNew ? work.Width : work.Height) - mineNow);
                var newRatio = spanNow <= 0 ? 0.5 : Math.Clamp(alongNow / (double)spanNow, 0, 1);

                
                
                ApplySetting(edge, newRatio);
                return;
            }

            SnapToSetting();

            
            Partner()?.SnapToSetting();
        }
        catch (Exception ex)
        {
            Log("换边失败: " + ex.Message);
        }
    }

    
    
    
    
    
    
    
    private void FollowPartner(int myY, int myHeight)
    {
        var other = Partner();
        if (other is null) return;
        try
        {
            var work = DisplayArea.Primary.WorkArea;
            var free = Math.Max(0, work.Height - myHeight);
            _liveRatio = free <= 0 ? 0.5 : Math.Clamp((myY - work.Y) / (double)free, 0, 1);

            
            
            
            
            other._liveRatio = _liveRatio;
            other.MoveToEdge(verify: false);
        }
        catch (Exception ex)
        {
            Log("联动对面失败: " + ex.Message);
        }
    }

    

    
    
    
    
    
    
    
    private void SlideInFromEdge()
    {
        if (_appWindow is null) return;
        StopSlide();                                     

        var size = PlannedSize();                        
        var finalPos = EdgePosition(size.Width, size.Height);
        _restRect = new Windows.Graphics.RectInt32(finalPos.X, finalPos.Y, size.Width, size.Height);   

        
        
        var start = OutwardOffset(finalPos, (int)((IsFlat ? size.Height : size.Width) / 2.0));

        
        
        try { _appWindow.MoveAndResize(new RectInt32(start.X, start.Y, size.Width, size.Height)); }
        catch (Exception ex) { Log("滑入落位失败: " + ex.Message); }
        ApplyRoundRegion(size);                           

        TweenWindow(start, finalPos, 220);
    }

    
    
    
    
    
    
    
    
    private void TweenWindow(PointInt32 from, PointInt32 to, double ms, Action? done = null)
    {
        if (_appWindow is null) return;
        StopSlide();

        var epoch = ++_slideEpoch;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hwnd = WindowNative.GetWindowHandle(this);

        EventHandler<object> onFrame = (_, _) =>
        {
            if (epoch != _slideEpoch) return;                

            var t = Math.Clamp(sw.Elapsed.TotalMilliseconds / ms, 0, 1);
            var e = 1 - Math.Pow(1 - t, 3);              
            var x = (int)Math.Round(from.X + (to.X - from.X) * e);
            var y = (int)Math.Round(from.Y + (to.Y - from.Y) * e);
            _ = SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSizeFlag | SwpNoZOrderFlag | SwpNoActivateFlag);

            if (t < 1) return;
            StopSlide();
            _ = SetWindowPos(hwnd, IntPtr.Zero, to.X, to.Y, 0, 0, SwpNoSizeFlag | SwpNoZOrderFlag | SwpNoActivateFlag);
            try { _restRect = new Windows.Graphics.RectInt32(to.X, to.Y, _appWindow.Size.Width, _appWindow.Size.Height); } catch { }
            done?.Invoke();                                  
        };
        _slideFrame = onFrame;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += onFrame;

        
        var watchdog = DispatcherQueue.CreateTimer();
        watchdog.Interval = TimeSpan.FromMilliseconds(ms + 400);
        watchdog.IsRepeating = false;
        watchdog.Tick += (_, _) =>
        {
            if (epoch != _slideEpoch) return;
            StopSlide();
            _ = SetWindowPos(hwnd, IntPtr.Zero, to.X, to.Y, 0, 0, SwpNoSizeFlag | SwpNoZOrderFlag | SwpNoActivateFlag);
            try { _restRect = new Windows.Graphics.RectInt32(to.X, to.Y, _appWindow.Size.Width, _appWindow.Size.Height); } catch { }
            done?.Invoke();
        };
        _slideWatchdog = watchdog;
        watchdog.Start();
    }

    
    private bool SlideOutToEdge(Action done)
    {
        if (_appWindow is null) return false;
        StopSlide();

        var from = _appWindow.Position;
        var size = _appWindow.Size;

        
        
        
        
        
        var thickness = IsFlat ? size.Height : size.Width;
        var to = OutwardOffset(from, thickness + 2);
        TweenWindow(from, to, 240, done);
        return true;
    }

    private void StopSlide()
    {
        _slideEpoch++;                                    
        if (_slideFrame is not null)
        {
            try { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= _slideFrame; } catch { }
            _slideFrame = null;
        }
        _slideWatchdog?.Stop();
        _slideWatchdog = null;
    }

    
    private void PlaySlideIn()
    {
        try
        {
            var v = ElementCompositionPreview.GetElementVisual(Panel);
            var compositor = v.Compositor;

            var from = IsFlat
                ? new Vector3(0, Edge == "top" ? -56f : 56f, 0)
                : new Vector3(Edge == "left" ? -56f : 56f, 0, 0);

            v.Offset = from;
            v.Opacity = 0.4f;

            var slide = compositor.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(1f, Vector3.Zero,
                compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
            slide.Duration = TimeSpan.FromMilliseconds(190);
            v.StartAnimation("Offset", slide);

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1f, 1f);
            fade.Duration = TimeSpan.FromMilliseconds(190);
            v.StartAnimation("Opacity", fade);
        }
        catch (Exception ex)
        {
            Log("滑入动画失败: " + ex.Message);
        }
    }

    

    
    /// <summary>
    /// 切换 BodyArea 的行列归属。四种形态共用一块 Grid，只改坐标与行列数，
    /// 不搬运控件，避免滚动位置和焦点丢失（2026-11-22）。
    /// </summary>
    private void ApplyBodyLayout()
    {
        var flat = IsFlat;
        var above = !flat && PinnedAbove && HasPinnedApps;

        // 竖排时工具那行吃掉剩余高度；横条时整条高度由内容决定。
        ToolsRow.Height = flat ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        BodyArea.RowSpacing = flat || above ? 3 : 0;
        BodyArea.ColumnSpacing = 0;

        if (flat)
        {
            Place(ToolsScroll, row: 0, col: 0, rowSpan: 1, colSpan: 2);
            Place(SoftwarePane, row: 1, col: 0, rowSpan: 1, colSpan: 2);
            Place(Sep, row: 2, col: 0, rowSpan: 1, colSpan: 2);
            Place(FooterStack, row: 3, col: 0, rowSpan: 1, colSpan: 2);
            SetPaneDivider(new Thickness(0));   // 下面紧跟分隔线，不再多画一条
            return;
        }

        if (above)
        {
            Place(SoftwarePane, row: 0, col: 0, rowSpan: 1, colSpan: 2);
            Place(ToolsScroll, row: 1, col: 0, rowSpan: 1, colSpan: 2);
            Place(Sep, row: 2, col: 0, rowSpan: 1, colSpan: 2);
            Place(FooterStack, row: 3, col: 0, rowSpan: 1, colSpan: 2);
            SetPaneDivider(new Thickness(0, 0, 0, 1));
            return;
        }

        // 竖排·软件栏在左：左列从工具上方一直贯穿到操作按钮下方。
        Place(SoftwarePane, row: 1, col: 0, rowSpan: 3, colSpan: 1);
        Place(ToolsScroll, row: 1, col: 1, rowSpan: 1, colSpan: 1);
        Place(Sep, row: 2, col: 1, rowSpan: 1, colSpan: 1);
        Place(FooterStack, row: 3, col: 1, rowSpan: 1, colSpan: 1);
        SetPaneDivider(new Thickness(0, 0, 1, 0));

        static void Place(FrameworkElement el, int row, int col, int rowSpan, int colSpan)
        {
            Grid.SetRow(el, row);
            Grid.SetColumn(el, col);
            Grid.SetRowSpan(el, rowSpan);
            Grid.SetColumnSpan(el, colSpan);
        }
    }

    private void SetPaneDivider(Thickness thickness) => SoftwarePane.BorderThickness = thickness;

    private void ApplyEdgeLayout()
    {
        try
        {
            var edge = Edge;
            var flat = edge is "top" or "bottom";

            
            
            
            GripStack.Orientation = flat ? Orientation.Horizontal : Orientation.Vertical;
            var gripLong = CompactCollapsed ? 34 : 48;
            var gripStub = CompactCollapsed ? 4 : 5;
            Grip.Width = flat ? gripLong : gripStub;
            Grip.Height = flat ? gripStub : gripLong;

            
            TitleRow.Visibility = flat || CompactBody ? Visibility.Collapsed : Visibility.Visible;

            // 横条时整条高度是固定的，让内容自然排开；竖排时留出可滚动的剩余空间。
            BodyRow.Height = flat ? GridLength.Auto : new GridLength(1, GridUnitType.Star);

            ApplyBodyLayout();

            
            ToolStack.ColumnSpacing = flat ? 8 : ToolGap;
            ToolStack.RowSpacing = flat ? 8 : ToolGap;
            ToolStack.HorizontalAlignment = flat ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;

            Sep.Margin = flat ? new Thickness(10, 4, 10, 4) : new Thickness(2, 3, 2, 3);

            
            
            var footerGap = flat ? FooterFlatSpacingDip : 2;
            FooterStack.ColumnSpacing = footerGap;
            FooterStack.RowSpacing = footerGap;
            FooterStack.HorizontalAlignment = flat ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;

            
            
            
            
            
            var hiddenFooter = App.Settings.Current.SidebarFooterHidden ?? Array.Empty<string>();
            foreach (var (key, btn) in FooterItems())
                btn.Visibility = hiddenFooter.Contains(key) ? Visibility.Collapsed : Visibility.Visible;

            var footerCount = VisibleFooterCount();
            FooterStack.Visibility = footerCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            Sep.Visibility = footerCount > 0 ? Visibility.Visible : Visibility.Collapsed;

            
            LayFooter(flat);

            
            foreach (var b in _toolButtons)
            {
                b.MinWidth = flat ? (CompactBody ? 56 : 64) : 0;
                b.MinHeight = flat ? (CompactBody ? 42 : 48) : ToolButtonH;
                b.Padding = flat
                    ? new Thickness(4, 6, 4, 6)
                    : new Thickness(0, CompactBody ? 4 : 8, 0, CompactBody ? 4 : 8);
            }
            foreach (var (_, b) in FooterItems())
            {
                b.MinWidth = flat ? (CompactBody ? 88 : FooterFlatButtonWidthDip) : 0;
                
                b.MinHeight = flat ? (CompactBody ? 28 : 32) : FooterButtonH;
                b.Padding = flat ? new Thickness(6, 2, 6, 2) : new Thickness(8, 4, 8, 4);
                b.HorizontalContentAlignment = flat ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            }

            ApplyFooterContent(flat);

            
            ToolsScroll.VerticalScrollMode = flat ? ScrollMode.Disabled : ScrollMode.Auto;
            ToolsScroll.VerticalScrollBarVisibility = flat ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            ToolsScroll.HorizontalScrollMode = flat ? ScrollMode.Auto : ScrollMode.Disabled;
            ToolsScroll.HorizontalScrollBarVisibility = flat ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            ApplyScrollLimit();
            if (_foldIcon is not null)
            {
                
                var dir = edge;
                _foldIcon.Glyph = dir switch
                {
                    "left" => "\uE76C",      
                    "top" => "\uE70D",       
                    "bottom" => "\uE70E",    
                    _ => "\uE76B",           
                };
            }
        }
        catch (Exception ex)
        {
            Log("排版失败: " + ex.Message);
        }
    }

    
    
    
    
    
    
    
    
    
    private void LayFooter(bool flat)
    {
        FooterStack.RowDefinitions.Clear();
        FooterStack.ColumnDefinitions.Clear();
        FooterStack.Children.Clear();

        var visible = FooterItems().Where(x => x.Btn.Visibility == Visibility.Visible).ToArray();
        var n = visible.Length;
        if (n == 0) return;

        
        var cols = flat ? n : Math.Max(1, Math.Min(Columns, n));
        var rows = (int)Math.Ceiling(n / (double)cols);

        for (var c = 0; c < cols; c++)
            FooterStack.ColumnDefinitions.Add(new ColumnDefinition
            {
                
                Width = flat ? GridLength.Auto : new GridLength(1, GridUnitType.Star),
            });
        for (var r = 0; r < rows; r++)
            FooterStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var i = 0;
        foreach (var (_, btn) in FooterItems())
        {
            if (btn.Visibility != Visibility.Visible) continue;      
            Grid.SetRow(btn, i / cols);
            Grid.SetColumn(btn, i % cols);
            FooterStack.Children.Add(btn);
            i++;
        }
    }

    
    
    
    
    
    
    private void ApplyFooterContent(bool flat)
    {
        SetFooterButton(FoldButton, "\uE76C", "收起", flat, out _foldIcon);
        SetFooterButton(PinButton, "\uE718", "常驻", flat, out _pinIcon);
        SetFooterButton(ResetButton, "\uE777", "位置复原", flat, out _);
        
        
        
        
        SetFooterButton(HideButton, "\uE89F", "隐藏", flat, out _);
        SetFooterImageButton(OpenAppButton, "打开应用", flat);
        UpdatePinVisual();
    }

    
    
    
    
    private void SetFooterImageButton(Button b, string label, bool flat)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = flat ? 11 : (CompactBody ? 9.5 : 10.5),
            VerticalAlignment = VerticalAlignment.Center
        };
        var img = new Image
        {
            Source = AppIconImage(),
            Width = flat ? 14 : (CompactBody ? 14 : 17),
            Height = flat ? 14 : (CompactBody ? 14 : 17),
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        if (flat)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            row.Children.Add(img);
            row.Children.Add(text);
            b.Content = row;
        }
        else
        {
            text.HorizontalAlignment = HorizontalAlignment.Center;
            var col = new StackPanel { Spacing = 2 };
            col.Children.Add(img);
            col.Children.Add(text);
            b.Content = col;
        }
    }

    private static ImageSource? _appIconImage;

    
    private static ImageSource? AppIconImage()
    {
        if (_appIconImage is not null) return _appIconImage;
        try
        {
            var path = EmbeddedAssets.ExtractToCache("AppIcon-512.png", "app-icon.png");
            if (string.IsNullOrEmpty(path)) return null;
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            bmp.DecodePixelWidth = 64;          
            bmp.UriSource = new Uri(path);
            _appIconImage = bmp;
        }
        catch (Exception ex)
        {
            Log("解应用图标失败: " + ex.Message);
        }
        return _appIconImage;
    }

    private void SetFooterButton(Button b, string glyph, string label, bool flat, out FontIcon icon)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = flat ? 11 : (CompactBody ? 9.5 : 10.5),
            VerticalAlignment = VerticalAlignment.Center
        };
        icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = flat ? 13 : (CompactBody ? 13 : 15),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        if (flat)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            row.Children.Add(icon);
            row.Children.Add(text);
            b.Content = row;
        }
        else
        {
            text.HorizontalAlignment = HorizontalAlignment.Center;
            var col = new StackPanel { Spacing = 2 };
            col.Children.Add(icon);
            col.Children.Add(text);
            b.Content = col;
        }
    }

    
    
    
    
    private (string Key, Button Btn)[] FooterItems() => new (string, Button)[]
    {
        (SidebarFooterKeys.Fold, FoldButton),
        (SidebarFooterKeys.Pin, PinButton),
        (SidebarFooterKeys.Reset, ResetButton),
        (SidebarFooterKeys.Hide, HideButton),
        (SidebarFooterKeys.OpenApp, OpenAppButton),
    };

    
    
    
    
    private static int VisibleFooterCount()
    {
        var hidden = App.Settings.Current.SidebarFooterHidden ?? Array.Empty<string>();
        return SidebarFooterKeys.All.Count(k => !hidden.Contains(k));
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

    
    private SizeInt32 PlannedSize()
    {
        var scale = Scale();
        if (!_expanded) return CollapsedSize();

        var footerCount = VisibleFooterCount();
        var lim = ExpandedLimits();
        int dipW, dipH;

        if (IsFlat)
        {
            
            
            
            var count = Math.Max(1, _toolButtons.Count);
            var cell = CompactBody ? 56 : 64;
            var toolsRow = count * cell + (count - 1) * 8 + FlatRowPadDip;
            var footerCell = CompactBody ? 88 : FooterFlatButtonWidthDip;
            var footerRow = footerCount > 0
                ? footerCount * footerCell + (footerCount - 1) * FooterFlatSpacingDip + FlatRowPadDip
                : 0;

            dipW = (int)Math.Min(Math.Max(Math.Max(FlatMinLength, toolsRow), footerRow), lim.W);
            dipH = (int)Math.Round(FlatPlannedHeight(dipW, lim.H));
        }
        else
        {
            
            var cols = Columns;
            dipW = (cols > 1 ? cols * CellWidth + (cols - 1) * ToolGap + 12 : BarWidth) + SoftwareBarWidth;
            dipH = ExpandedHeight(dipW, lim.H);
        }

        return new SizeInt32((int)Math.Round(dipW * scale), (int)Math.Round(dipH * scale));
    }

    
    
    
    
    
    
    
    
    
    
    /// <summary>
    /// 横条模式的高度：工具行 + 快捷应用行 + 分隔线 + 操作按钮行，量出来为准。
    /// 从前是按固定厚度给的，软件栏挪成独立一行后就量不准了（2026-11-22）。
    /// </summary>
    private double FlatPlannedHeight(int dipW, double limitH)
    {
        try
        {
            ExpandedView.Measure(new Windows.Foundation.Size(dipW, double.PositiveInfinity));
            var want = ExpandedView.DesiredSize.Height;
            if (want > 1) return Math.Min(Math.Ceiling(want) + 2, limitH);
        }
        catch (Exception ex)
        {
            Log("量横条高度失败: " + ex.Message);
        }

        var h = 11 + (CompactBody ? 42 : 48);
        if (HasPinnedApps) h += 3 + (CompactBody ? 40 : 44);
        if (VisibleFooterCount() > 0) h += 9 + (CompactBody ? 28 : 32);
        return Math.Min(h, limitH);
    }

    private int ExpandedHeight(int dipW, double limitH)
    {
        
        
        if (!HeightAuto && Columns <= 1) return (int)Math.Round(limitH);

        try
        {
            ExpandedView.Measure(new Windows.Foundation.Size(dipW, double.PositiveInfinity));
            var want = ExpandedView.DesiredSize.Height;
            if (want > 1) return (int)Math.Min(Math.Ceiling(want) + 2, limitH);   
        }
        catch (Exception ex)
        {
            Log("量展开面板高度失败: " + ex.Message);
        }

        
        var cols = Math.Max(1, Columns);
        var rows = Math.Max(1, (int)Math.Ceiling(Math.Max(1, _toolButtons.Count) / (double)cols));
        var fn = VisibleFooterCount();
        
        var fcols = Math.Max(1, Math.Min(cols, fn));
        var frows = fn > 0 ? (int)Math.Ceiling(fn / (double)fcols) : 0;
        var above = !IsFlat && PinnedAbove && HasPinnedApps ? 2 * (ToolButtonH + ToolGap) + 3 : 0;
        var body = 34 + above + rows * (ToolButtonH + ToolGap) + (frows > 0 ? 10 + frows * (FooterButtonH + 2) : 0) + 14;
        return (int)Math.Min(body, limitH);
    }

    
    
    
    private (double W, double H) ExpandedLimits()
    {
        var scale = Scale();
        var work = DisplayArea.Primary.WorkArea;
        var workW = work.Width / scale - 32;          
        var workH = work.Height / scale - 32;

        if (IsFlat)
        {
            // 长度仍是整屏的九成；高度不再写死，按内容量（工具 + 快捷应用 + 操作按钮）。
            var wantLen = Math.Max(FlatMinLength, workW * 0.92);
            return (Math.Min(workW, wantLen), Math.Min(workH, FlatMaxThicknessDip));
        }

        
        
        
        var cols = Columns;
        var wantW = (cols > 1 ? cols * CellWidth + (cols - 1) * ToolGap + 12 : BarWidth) + SoftwareBarWidth;
        var maxH = cols > 1 ? workH : workH * SingleColumnMaxHeightRatio;
        return (Math.Min(workW, wantW), maxH);
    }

    
    private void ApplyScrollLimit()
    {
        try
        {
            if (!_expanded) return;
            var scale = Scale();
            var work = DisplayArea.Primary.WorkArea;

            if (IsFlat)
            {
                var chrome = 12 + 24;                                    
                ToolsScroll.MaxWidth = Math.Max(120, work.Width / scale - 32 - chrome);
                ToolsScroll.MaxHeight = double.PositiveInfinity;
                ToolsScroll.MinHeight = 0;                               
            }
            else
            {
                var title = TitleRow.Visibility == Visibility.Visible ? 26 : 0;
                
                
                var fn = VisibleFooterCount();
                var fcols = Math.Max(1, Math.Min(Columns, fn));
                var frows = fn > 0 ? (int)Math.Ceiling(fn / (double)fcols) : 0;
                var footer = frows > 0 ? frows * FooterButtonH + (frows - 1) * 2 : 0;

                // 快捷应用摆在工具上方时，上面那条要单独留出高度（最多两行）。
                var above = !IsFlat && PinnedAbove && HasPinnedApps
                    ? 2 * (ToolButtonH + ToolGap) + 3
                    : 0;

                var chrome = title + 7 + footer + 17 + 9 + above;
                var max = Math.Max(120, ExpandedLimits().H - chrome);
                ToolsScroll.MaxHeight = max;
                
                
                
                ToolsScroll.MinHeight = !HeightAuto && Columns <= 1 ? max : 0;
                ToolsScroll.MaxWidth = double.PositiveInfinity;
            }
        }
        catch (Exception ex)
        {
            Log("算滚动范围失败: " + ex.Message);
        }
    }


    private void ApplySize()
    {
        if (_appWindow is null) return;
        try
        {
            ApplyScrollLimit();
            var size = PlannedSize();
            _appWindow.Resize(size);
            ApplyRoundRegion(size);                        
        }
        catch (Exception ex)
        {
            Log("改尺寸失败: " + ex.Message);
        }
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private void ApplyRoundRegion(SizeInt32 size)
    {
        if (_appWindow is null) return;
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var collapsed = !_expanded;

            if (Core.SystemInfo.IsWin11 && !collapsed)
            {
                
                Core.WindowChrome.ClearRoundRegion(hwnd);
                Core.WindowChrome.SetRounded(hwnd, rounded: true);
                return;
            }

            
            if (Core.SystemInfo.IsWin11) Core.WindowChrome.SetRounded(hwnd, rounded: false);

            var radius = (int)Math.Round(8 * Scale());     
            Core.WindowChrome.SetRoundRegion(hwnd, size.Width, size.Height, radius);
        }
        catch (Exception ex)
        {
            Log("圆角区域失败: " + ex.Message);
        }
    }

    
    
    
    
    
    
    
    
    private void RefreshWindowShape()
    {
        try { ApplyRoundRegion(_expanded ? PlannedSize() : CollapsedSize()); }
        catch (Exception ex) { Log("刷新窗口形状失败: " + ex.Message); }
    }

    
    
    
    
    
    
    private SizeInt32 CollapsedSize()
    {
        var scale = Scale();
        var thick = CompactCollapsed ? CollapsedCompactThicknessDip : CollapsedThicknessDip;
        var len = CompactCollapsed ? CollapsedCompactLengthDip : CollapsedLengthDip;
        var dipW = IsFlat ? len : thick;
        var dipH = IsFlat ? thick : len;
        return new SizeInt32((int)Math.Round(dipW * scale), (int)Math.Round(dipH * scale));
    }

    
    private PointInt32 EdgePosition(int width, int height)
    {
        var work = DisplayArea.Primary.WorkArea;

        var edge = Edge;
        var flat = edge is "top" or "bottom";

        var alongLen = flat ? work.Width : work.Height;
        var myLen = flat ? width : height;
        var free = Math.Max(0, alongLen - myLen);

        
        
        
        var offset = _liveRatio < 0 ? free / 2.0 : Math.Clamp(_liveRatio * free, 0, free);

        
        
        
        var gap = _expanded ? 0 : (int)Math.Round(CollapsedGapDip * Scale());

        return edge switch
        {
            "left" => new PointInt32(work.X + gap, (int)Math.Round(work.Y + offset)),
            "top" => new PointInt32((int)Math.Round(work.X + offset), work.Y + gap),
            "bottom" => new PointInt32((int)Math.Round(work.X + offset), work.Y + work.Height - height - gap),
            _ => new PointInt32(work.X + work.Width - width - gap, (int)Math.Round(work.Y + offset))
        };
    }

    
    
    
    
    private PointInt32 OutwardOffset(PointInt32 p, int dist)
    {
        if (dist < 1) dist = 1;

        return Edge switch
        {
            "left" => new PointInt32(p.X - dist, p.Y),
            "top" => new PointInt32(p.X, p.Y - dist),
            "bottom" => new PointInt32(p.X, p.Y + dist),
            _ => new PointInt32(p.X + dist, p.Y)
        };
    }

    
    
    
    
    
    private void MoveToEdge(bool verify = true)
    {
        if (_appWindow is null) return;
        try
        {
            
            var size = PlannedSize();
            var pos = EdgePosition(size.Width, size.Height);
            var x = pos.X;
            var y = pos.Y;

            if (!verify)
            {
                _appWindow.Move(pos);
                return;
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
        }
        catch (Exception ex)
        {
            Log("贴边失败: " + ex.Message);
        }
    }

    

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    
    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [DllImport("user32.dll")]
    private static extern bool GetPointerInfo(uint id, out POINTER_INFO pointerInfo);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    
    private const uint SwpNoSizeFlag = 0x0001;
    private const uint SwpNoMoveFlag = 0x0002;
    private const uint SwpNoZOrderFlag = 0x0004;
    private const uint SwpNoActivateFlag = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static void Log(string message)
    {
        try
        {
            Core.AppLog.Info("sidebar", message);
        }
        catch { }
    }
}
