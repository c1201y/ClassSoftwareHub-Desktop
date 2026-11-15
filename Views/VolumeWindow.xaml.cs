using System;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Services.Audio;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace ClassSoftwareHub.Desktop.Views;





















public sealed partial class VolumeWindow : Window
{
    
    public enum Target
    {
        
        Volume,
        
        Brightness,
    }

    
    private const int TallWidthDip = 72;
    private const int TallHeightDip = 300;
    private const int FlatWidthDip = 330;
    private const int FlatHeightDip = 64;

    
    private const int MinPanelWidthDip = 68;
    private const int MinPanelHeightDip = 120;

    private static VolumeWindow? _instance;

    private readonly FlyoutChrome _chrome;
    private readonly FlyoutSlider _slide = new();

    private DispatcherQueueTimer? _poll;
    private bool _visible;
    private bool _started;
    private bool _syncing;
    private bool _dragging;             
    private bool _flat;                 
    private Target _target = Target.Volume;

    
    private bool? _adaptiveOn;              
    private int _brightnessPercent = -1;    
    private bool _brightnessReadable = true;
    private bool _brightnessReading;        

    private VolumeWindow()
    {
        InitializeComponent();
        _chrome = new FlyoutChrome(this, Root, Panel, "音量");
        Configure();
    }

    
    
    
    
    
    
    
    
    public static void Toggle(Target target, string? fromEdge = null)
    {
        try
        {
            _instance ??= new VolumeWindow();
            var w = _instance;

            var edge = fromEdge ?? ToolSidebarWindow.CurrentEdge;
            var moved = w._originEdge != edge;      

            
            
            if (w._visible && w._target == target && !moved)
            {
                w.HideSelf();
                return;
            }

            
            
            
            if (moved && VolumeMixerWindow.IsVisible) VolumeMixerWindow.CloseIfOpen();

            w._originEdge = edge;

            var switched = w._target != target;
            w._target = target;
            if (switched) w.ApplyTarget();

            if (w._visible)
            {
                
                if (moved) w.MoveToEdge();
                else w.Reposition();
            }
            else w.ShowSelf();
        }
        catch (Exception ex)
        {
            
            
            ScreenCapture.Log("浮窗开关失败: " + ex.Message);
        }
    }

    public static void CloseIfOpen()
    {
        if (_instance?._visible == true) _instance.HideSelf();
    }

    
    public static bool IsVisible => _instance?._visible == true;

    
    
    
    
    
    
    
    
    
    
    
    public static string Edge => _instance?.MyEdge ?? ToolSidebarWindow.CurrentEdge;

    
    private string MyEdge =>
        _originEdge.Length > 0 && ToolSidebarWindow.IsEdgePresent(_originEdge)
            ? _originEdge
            : ToolSidebarWindow.CurrentEdge;

    
    private string _originEdge = "";

    
    public static RectInt32? CurrentRect => _instance?._chrome.CurrentRect;

    
    
    
    public static RectInt32? AnchorRect => _instance?._finalRect ?? CurrentRect;

    
    private RectInt32? _finalRect;

    private void Configure()
    {
        try
        {
            
            
            
            ThemeHost.Apply(Root, external: true);
            VolumeFlyoutGroup.MainHwnd = _chrome.Hwnd;

            
            
            
            MasterSlider.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler((_, _) => _dragging = true), true);
            MasterSlider.AddHandler(UIElement.PointerReleasedEvent,
                new PointerEventHandler((_, _) => EndDrag()), true);
            MasterSlider.AddHandler(UIElement.PointerCaptureLostEvent,
                new PointerEventHandler((_, _) => EndDrag()), true);

            Activated += (_, args) =>
            {
                if (args.WindowActivationState == WindowActivationState.Deactivated)
                    VolumeFlyoutGroup.OnAnyDeactivated();
            };

            Root.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Escape) VolumeFlyoutGroup.CloseAll();
            };
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("音量浮窗初始化失败: " + ex.Message);
        }
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        Refresh();
    }

    

    
    
    
    
    
    private void ApplyOrientation(bool flat)
    {
        _flat = flat;

        ContentHost.Orientation = flat ? Orientation.Horizontal : Orientation.Vertical;
        ContentHost.Spacing = 10;
        ContentHost.Padding = flat ? new Thickness(12, 10, 12, 10) : new Thickness(8, 12, 8, 8);

        
        
        ButtonHost.Orientation = flat ? Orientation.Horizontal : Orientation.Vertical;
        ButtonHost.Spacing = flat ? 6 : 4;
        ButtonHost.HorizontalAlignment = flat ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;

        foreach (var b in new[] { ActionToggle, ExpandToggle })
        {
            b.Width = flat ? 40 : double.NaN;                 
            b.Height = 36;
        }

        
        if (flat)
        {
            MasterSlider.Orientation = Orientation.Horizontal;
            MasterSlider.Width = 140;
            MasterSlider.Height = 36;
        }
        else
        {
            MasterSlider.Orientation = Orientation.Vertical;
            MasterSlider.Width = 44;
            MasterSlider.Height = 140;
        }
    }

    
    
    
    
    
    
    private Windows.Foundation.Point SizeDip()
    {
        var fallback = _flat
            ? new Windows.Foundation.Point(FlatWidthDip, FlatHeightDip)
            : new Windows.Foundation.Point(TallWidthDip, TallHeightDip);

        var saved = MasterPercentText.Text;
        try
        {
            MasterPercentText.Text = "100%";
            ContentHost.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

            var desired = ContentHost.DesiredSize;
            var w = Math.Max(desired.Width, MinPanelWidthDip);
            var h = Math.Max(desired.Height, MinPanelHeightDip);

            return new Windows.Foundation.Point(Math.Ceiling(w) + 2, Math.Ceiling(h) + 2);   
        }
        catch
        {
            return fallback;
        }
        finally
        {
            MasterPercentText.Text = saved;
        }
    }

    

    private void ShowSelf()
    {
        try
        {
            var edge = MyEdge;                          
            ApplyTarget();                              
            ApplyOrientation(EdgeGeometry.IsFlat(edge));

            var scale = _chrome.Scale;
            var work = DisplayArea.Primary.WorkArea;

            Start();                                    
            var dip = SizeDip();
            var w = (int)Math.Round(dip.X * scale);
            var h = (int)Math.Round(dip.Y * scale);

            
            var anchor = ToolSidebarWindow.RectOf(edge) ?? EdgeGeometry.EdgeBar(edge, work);
            var (start, final) = EdgeGeometry.BesideAnchor(edge, anchor, work, w, h, scale);
            _finalRect = new RectInt32(final.X, final.Y, w, h);   

            _chrome.Present(start, w, h);
            _visible = true;

            _chrome.Finish(Root.ActualTheme == ElementTheme.Dark);   

            FlyoutFade.In(ContentHost, 160);
            _slide.Run(_chrome.AppWindow, start, final, FlyoutSlider.SlideInMs);

            VolumeFlyoutGroup.HoldSidebar();             
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("音量浮窗显示失败: " + ex.Message);
        }
    }

    private void HideSelf()
    {
        if (!_visible) return;
        _visible = false;
        Stop();

        try
        {
            var edge = MyEdge;
            var size = _chrome.AppWindow.Size;

            var from = _chrome.AppWindow.Position;
            var to = EdgeGeometry.FullyOut(from, edge, EdgeGeometry.Thickness(edge, size.Width, size.Height));

            _slide.Run(_chrome.AppWindow, from, to, FlyoutSlider.SlideOutMs, () =>
            {
                _chrome.HideDirect();
                VolumeFlyoutGroup.OnAnyHidden();          
            }, easeIn: true);
        }
        catch
        {
            _chrome.HideDirect();
            VolumeFlyoutGroup.OnAnyHidden();
        }
    }

    
    
    
    
    
    private void Reposition()
    {
        try
        {
            var edge = MyEdge;
            var scale = _chrome.Scale;
            var work = DisplayArea.Primary.WorkArea;

            var dip = SizeDip();
            var w = (int)Math.Round(dip.X * scale);
            var h = (int)Math.Round(dip.Y * scale);

            var anchor = ToolSidebarWindow.RectOf(edge) ?? EdgeGeometry.EdgeBar(edge, work);
            var (_, final) = EdgeGeometry.BesideAnchor(edge, anchor, work, w, h, scale);
            _finalRect = new RectInt32(final.X, final.Y, w, h);

            _chrome.Present(final, w, h);
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("浮窗改尺寸失败: " + ex.Message);
        }
    }

    
    
    
    
    
    
    
    
    
    
    
    private void MoveToEdge()
    {
        try
        {
            ApplyOrientation(EdgeGeometry.IsFlat(MyEdge));
            _chrome.HideDirect();          
            ShowSelf();                    
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("音量浮窗换边失败: " + ex.Message);
        }
    }

    
    
    
    
    
    private void ApplyTarget()
    {
        try
        {
            var volume = _target == Target.Volume;

            ActionToggle.IsChecked = false;
            _chrome.AppWindow.Title = volume ? "音量" : "屏幕亮度";
            _adaptiveOn = null;                          

            if (ActionToggle.Content is FontIcon fi)
                fi.Glyph = volume ? "\uE74F" : "\uE706";

            UpdateActionTooltip();

            ExpandToggle.Visibility = volume ? Visibility.Visible : Visibility.Collapsed;
            ApplyPollInterval();

            
            
            
            if (!volume) VolumeMixerWindow.CloseIfOpen();
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("浮窗换目标失败: " + ex.Message);
        }
    }

    
    private void UpdateActionTooltip(string? error = null)
    {
        if (_target == Target.Volume)
        {
            ToolTipService.SetToolTip(ActionToggle, "静音 / 取消静音");
            return;
        }

        ToolTipService.SetToolTip(ActionToggle, error ?? (BrightnessService.AdaptiveSupported
            ? "自动亮度（跟随环境光调节）"
            : "自动亮度不可用：设备不具备环境光传感器"));
    }

    
    private void SyncToggles()
    {
        if (AudioService.TryGetMaster(out _, out var muted)) ActionToggle.IsChecked = muted;
        ExpandToggle.IsChecked = VolumeMixerWindow.IsVisible;
    }

    

    private void Start()
    {
        _started = true;
        Refresh();

        _poll ??= DispatcherQueue.CreateTimer();
        _poll.IsRepeating = true;
        _poll.Tick -= OnPoll;
        _poll.Tick += OnPoll;
        _poll.Start();

        ApplyPollInterval();
    }

    
    
    
    
    private void ApplyPollInterval()
    {
        if (_poll is null) return;
        _poll.Interval = _target == Target.Volume
            ? TimeSpan.FromMilliseconds(1500)
            : TimeSpan.FromMilliseconds(3000);
    }

    private void Stop()
    {
        _started = false;
        _poll?.Stop();
        _brightnessReading = false;      
    }

    private void OnPoll(DispatcherQueueTimer sender, object args) => Refresh();

    private void Refresh()
    {
        if (!_started || _dragging) return;
        if (_target == Target.Volume) RefreshVolume();
        else RefreshBrightness();
    }

    
    private void RefreshVolume()
    {
        if (!AudioService.TryGetMaster(out var percent, out var muted))
        {
            MasterSlider.IsEnabled = false;
            ActionToggle.IsEnabled = false;
            MasterPercentText.Text = "—";
            MasterCaptionText.Text = "未检测到设备";
            ToolTipService.SetToolTip(MasterCaptionText, "未检测到音量设备（无音频设备或音频服务未启动）");
            ActionToggle.IsChecked = false;
            return;
        }

        MasterSlider.IsEnabled = true;
        ActionToggle.IsEnabled = true;
        MasterCaptionText.Text = "主音量";
        ToolTipService.SetToolTip(MasterCaptionText, null);

        SetSliderSilently(percent);
        MasterPercentText.Text = percent + "%";
        MasterPercentText.Opacity = muted ? 0.45 : 1.0;

        SyncToggles();
    }

    
    
    
    
    private void RefreshBrightness()
    {
        ApplyBrightnessUi();
        KickBrightnessRead();
    }

    
    private void ApplyBrightnessUi()
    {
        if (!_brightnessReadable || _brightnessPercent < 0)
        {
            MasterSlider.IsEnabled = false;
            ActionToggle.IsEnabled = false;
            MasterPercentText.Text = "—";
            MasterCaptionText.Text = "无法读取设备";
            ToolTipService.SetToolTip(MasterCaptionText,
                "无法读取屏幕亮度：该显示器亮度不受系统管理（外接显示器通常如此）");
            ActionToggle.IsChecked = false;
            return;
        }

        MasterSlider.IsEnabled = true;
        ActionToggle.IsEnabled = BrightnessService.AdaptiveSupported;
        MasterCaptionText.Text = "屏幕亮度";
        ToolTipService.SetToolTip(MasterCaptionText, null);

        SetSliderSilently(_brightnessPercent);
        MasterPercentText.Text = _brightnessPercent + "%";
        MasterPercentText.Opacity = 1.0;

        
        if (_adaptiveOn.HasValue) ActionToggle.IsChecked = _adaptiveOn.Value;
    }

    
    
    
    
    private void KickBrightnessRead()
    {
        if (_brightnessReading) return;
        _brightnessReading = true;

        var dq = DispatcherQueue;
        var needAdaptive = !_adaptiveOn.HasValue;

        _ = Task.Run(() =>
        {
            var ok = BrightnessService.TryGet(out var percent);
            var adaptive = needAdaptive && BrightnessService.TryGetAdaptive(out var on) ? (bool?)on : null;

            _ = dq.TryEnqueue(() =>
            {
                _brightnessReading = false;
                if (!_started || _target != Target.Brightness) return;

                _brightnessReadable = ok;
                if (ok) _brightnessPercent = percent;
                if (adaptive.HasValue) _adaptiveOn = adaptive;

                ApplyBrightnessUi();
            });
        });
    }

    private void SetSliderSilently(int value)
    {
        if (Math.Abs(MasterSlider.Value - value) < 0.5) return;
        _syncing = true;
        try { MasterSlider.Value = value; }
        finally { _syncing = false; }
    }

    

    private void MasterSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing) return;
        var percent = (int)Math.Round(e.NewValue);
        MasterPercentText.Text = percent + "%";

        if (_target == Target.Volume)
        {
            AudioService.SetMasterPercent(percent);
            return;
        }

        
        
        _brightnessPercent = percent;
        BrightnessService.SetPercent(percent);
    }

    
    
    
    
    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_target == Target.Volume)
        {
            if (!AudioService.TryGetMaster(out _, out _)) return;
            AudioService.SetMasterMute(ActionToggle.IsChecked == true);
            Refresh();
            return;
        }

        if (!BrightnessService.AdaptiveSupported)
        {
            ActionToggle.IsChecked = false;
            return;
        }

        var want = ActionToggle.IsChecked == true;
        if (BrightnessService.SetAdaptive(want))
        {
            _adaptiveOn = want;                  
            UpdateActionTooltip();               
        }
        else
        {
            ActionToggle.IsChecked = !want;
            UpdateActionTooltip("无法修改自动亮度（可能需要管理员权限，或设备不支持）");
        }
    }

    
    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        VolumeMixerWindow.Toggle();
        ExpandToggle.IsChecked = VolumeMixerWindow.IsVisible;
    }
}
