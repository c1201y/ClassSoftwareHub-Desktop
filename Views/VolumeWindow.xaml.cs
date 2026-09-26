using System;
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

/// <summary>
/// 单滑块浮窗：点侧边栏「音量」/「屏幕亮度」模块弹出，**挨着边条**长出来一栏（边条不收起，见 VolumeFlyoutGroup）。
///
/// ⚠️ 类名是历史遗留（一开始只伺候音量）—— 现在**音量 + 屏幕亮度共用这一个窗口**，
///    靠 <see cref="Target"/> 区分：文案、数值来源、下面那颗键的含义都不一样。
///    两个模块共用一个窗口还有个好处：同一时刻只可能开一栏，不会两块都贴在边条上。
///
/// 从上到下（贴左/右时）= 数值 + 垂直滑块 + 第一颗键（音量：静音 / 亮度：自动亮度）+ 第二颗键（仅音量：展开合成器）；
/// 贴上/下时自动换成**横版**（从左到右一排），见 <see cref="ApplyOrientation"/>。
///
/// 窗口外壳 / 材质（跟边条同一套）/ 贴边几何都在 <see cref="FlyoutChrome"/> + <see cref="EdgeGeometry"/> 里，
/// 本文件只管「长什么样、报什么数」。
/// </summary>
public sealed partial class VolumeWindow : Window
{
    /// <summary>这一栏现在在伺候谁。</summary>
    public enum Target
    {
        /// <summary>系统主音量（下面第一颗键 = 静音）。</summary>
        Volume,
        /// <summary>屏幕亮度（下面第一颗键 = 自动亮度；**没有**二级浮窗）。</summary>
        Brightness,
    }

    // 兜底尺寸（量不出来的时候用），dip
    private const int TallWidthDip = 72;
    private const int TallHeightDip = 300;
    private const int FlatWidthDip = 330;
    private const int FlatHeightDip = 64;

    // 再挤也别小于这个（不然圆角/描边会把内容吃掉）
    private const int MinPanelWidthDip = 68;
    private const int MinPanelHeightDip = 120;

    private static VolumeWindow? _instance;

    private readonly FlyoutChrome _chrome;
    private readonly FlyoutSlider _slide = new();

    private DispatcherQueueTimer? _poll;
    private bool _visible;
    private bool _started;
    private bool _syncing;
    private bool _dragging;             // 滑块正被拖着（音量和亮度共用这一个标志）
    private bool _flat;                 // 当前是不是横版（贴着上/下）
    private Target _target = Target.Volume;

    private VolumeWindow()
    {
        InitializeComponent();
        _chrome = new FlyoutChrome(this, Root, Panel, "音量");
        Configure();
    }

    /// <summary>开 / 关（再点一次同一个模块就收起来；点另一个模块就换成那一栏）。</summary>
    public static void Toggle(Target target)
    {
        try
        {
            _instance ??= new VolumeWindow();
            var w = _instance;

            if (w._visible && w._target == target)
            {
                w.HideSelf();
                return;
            }

            var switched = w._target != target;
            w._target = target;
            if (switched) w.ApplyTarget();

            if (w._visible) w.Reposition();      // 已经开着：按新内容重新量一遍尺寸挪一下（不重播滑入）
            else w.ShowSelf();
        }
        catch
        {
        }
    }

    public static void CloseIfOpen()
    {
        if (_instance?._visible == true) _instance.HideSelf();
    }

    /// <summary>主音量浮窗现在开着吗。</summary>
    public static bool IsVisible => _instance?._visible == true;

    /// <summary>侧边栏贴哪条边（跟侧边栏同一个设置）。竖/横版、滑入方向、合成器排哪儿全靠它。</summary>
    public static string Edge => App.Settings.Current.SidebarEdge is "left" or "top" or "bottom"
        ? App.Settings.Current.SidebarEdge
        : "right";

    /// <summary>主音量浮窗当前在屏幕上的矩形（给合成器浮窗「贴着它排」用）。</summary>
    public static RectInt32? CurrentRect => _instance?._chrome.CurrentRect;

    private void Configure()
    {
        try
        {
            ThemeHost.Apply(Root);
            VolumeFlyoutGroup.MainHwnd = _chrome.Hwnd;

            // ⚠️ handledEventsToo: true —— Slider 内部会把 PointerPressed 标成 handled，
            //    普通 += 订阅收不到，"_dragging" 就永远是 false，
            //    轮询一到就把滑块值按真实值写回去（拖到一半被"拽回去"就是这个）。
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

    // ── 竖版 / 横版 ───────────────────────────────────────────────────────

    /// <summary>
    /// 贴着左/右 = 竖版（数值在上、滑块竖着、两个键在下）；
    /// 贴着上/下 = 横版（数值 / 滑块 / 两个键 从左到右一排）。
    /// 滑块自带的 Orientation 也一起换 —— 横条里塞一根竖滑块是很怪的。
    /// </summary>
    private void ApplyOrientation(bool flat)
    {
        _flat = flat;

        ContentHost.Orientation = flat ? Orientation.Horizontal : Orientation.Vertical;
        ContentHost.Spacing = 10;
        ContentHost.Padding = flat ? new Thickness(12, 10, 12, 10) : new Thickness(8, 12, 8, 8);

        // 竖版：两个键**撑满整宽**（跟边条上的键一个长相，也就不留左右空地）；
        // 横版：两个键并排，各自保持自己的宽度
        ButtonHost.Orientation = flat ? Orientation.Horizontal : Orientation.Vertical;
        ButtonHost.Spacing = flat ? 6 : 4;
        ButtonHost.HorizontalAlignment = flat ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;

        foreach (var b in new[] { ActionToggle, ExpandToggle })
        {
            b.Width = flat ? 40 : double.NaN;                 // NaN = 交给 Stretch 撑满
            b.Height = 36;
        }

        // 横版把两根滑块的方向也掉过来（横条里塞一根竖滑块太怪）
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

    /// <summary>
    /// 面板尺寸 = **内容实测**（量一遍，别写死）。
    ///
    /// 写死数字的后果就是二选一：要么夹字（数字被裁一半），要么留空（滑块两边能开蜜雪冰城）。
    /// 实测还有一个好处：量的时候把数字临时摆成最宽的「100%」，所以 99%→100% 那一下面板不会跳宽度。
    /// </summary>
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

            return new Windows.Foundation.Point(Math.Ceiling(w) + 2, Math.Ceiling(h) + 2);   // +2 = 1px 描边 ×2
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

    // ── 显示 / 收起 ───────────────────────────────────────────────────────

    private void ShowSelf()
    {
        try
        {
            ApplyTarget();                              // 文案 / 图标 / 第二颗键的显隐，先按当前 target 摆好
            ApplyOrientation(EdgeGeometry.IsFlat(Edge));

            var scale = _chrome.Scale;
            var work = DisplayArea.Primary.WorkArea;

            Start();                                    // 先把内容刷好（数字先摆对），再按内容量面板尺寸
            var dip = SizeDip();
            var w = (int)Math.Round(dip.X * scale);
            var h = (int)Math.Round(dip.Y * scale);

            // 锚点 = 边条（挨着它长）；边条拿不到就当成贴屏幕边
            var anchor = ToolSidebarWindow.CurrentRect ?? EdgeGeometry.EdgeBar(Edge, work);
            var (start, final) = EdgeGeometry.BesideAnchor(Edge, anchor, work, w, h, scale);

            _chrome.Present(start, w, h);
            _visible = true;

            _chrome.Finish(Root.ActualTheme == ElementTheme.Dark);   // ⚠️ 必须在显示之后调

            FlyoutFade.In(ContentHost, 160);
            _slide.Run(_chrome.AppWindow, start, final, FlyoutSlider.SlideInMs);

            VolumeFlyoutGroup.HoldSidebar();             // 边条按住，别让它缩回去
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
            var scale = _chrome.Scale;
            var size = _chrome.AppWindow.Size;
            var work = DisplayArea.Primary.WorkArea;

            var from = _chrome.AppWindow.Position;
            var to = EdgeGeometry.FullyOut(from, Edge, EdgeGeometry.Thickness(Edge, size.Width, size.Height));

            _slide.Run(_chrome.AppWindow, from, to, FlyoutSlider.SlideOutMs, () =>
            {
                _chrome.HideDirect();
                VolumeFlyoutGroup.OnAnyHidden();          // 都收完了才放开边条
            }, easeIn: true);
        }
        catch
        {
            _chrome.HideDirect();
            VolumeFlyoutGroup.OnAnyHidden();
        }
    }

    /// <summary>
    /// 已经开着的时候换 target（音量 ↔ 亮度）：面板高度可能不一样（亮度少一颗键），
    /// 所以按新内容重新量一遍尺寸再挪过去 —— 不重播滑入动画，免得看着像"闪了两下"。
    /// </summary>
    private void Reposition()
    {
        try
        {
            var scale = _chrome.Scale;
            var work = DisplayArea.Primary.WorkArea;

            var dip = SizeDip();
            var w = (int)Math.Round(dip.X * scale);
            var h = (int)Math.Round(dip.Y * scale);

            var anchor = ToolSidebarWindow.CurrentRect ?? EdgeGeometry.EdgeBar(Edge, work);
            var (_, final) = EdgeGeometry.BesideAnchor(Edge, anchor, work, w, h, scale);

            _chrome.Present(final, w, h);
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("浮窗改尺寸失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 按当前 target 摆「文案 / 图标 / 按钮」：
    ///   音量 → 第一颗键是静音（\uE74F），第二颗键（展开合成器）露面；
    ///   亮度 → 第一颗键是自动亮度（\uE706），第二颗键收起来（亮度没有二级浮窗）。
    /// </summary>
    private void ApplyTarget()
    {
        try
        {
            var volume = _target == Target.Volume;

            ActionToggle.IsChecked = false;
            _chrome.AppWindow.Title = volume ? "音量" : "屏幕亮度";

            if (ActionToggle.Content is FontIcon fi)
                fi.Glyph = volume ? "\uE74F" : "\uE706";

            ToolTipService.SetToolTip(ActionToggle, volume
                ? "静音 / 取消静音"
                : BrightnessService.AdaptiveSupported
                    ? "自动亮度（跟着环境光调）"
                    : "自动亮度：这台机器没有环境光传感器，系统自带的也用不了");

            ExpandToggle.Visibility = volume ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("浮窗换目标失败: " + ex.Message);
        }
    }

    /// <summary>两个 ToggleButton 按真实状态点亮（静音中 / 合成器开着）。</summary>
    private void SyncToggles()
    {
        if (AudioService.TryGetMaster(out _, out var muted)) ActionToggle.IsChecked = muted;
        ExpandToggle.IsChecked = VolumeMixerWindow.IsVisible;
    }

    // ── 轮询 ──────────────────────────────────────────────────────────────

    private void Start()
    {
        _started = true;
        Refresh();

        _poll ??= DispatcherQueue.CreateTimer();
        _poll.Interval = TimeSpan.FromMilliseconds(1500);
        _poll.IsRepeating = true;
        _poll.Tick -= OnPoll;
        _poll.Tick += OnPoll;
        _poll.Start();
    }

    private void Stop()
    {
        _started = false;
        _poll?.Stop();
    }

    private void OnPoll(DispatcherQueueTimer sender, object args) => Refresh();

    private void Refresh()
    {
        if (!_started || _dragging) return;
        if (_target == Target.Volume) RefreshVolume();
        else RefreshBrightness();
    }

    /// <summary>主音量：读不到设备（没声卡 / 音频服务停了）就**如实说**，别装成 0%。</summary>
    private void RefreshVolume()
    {
        if (!AudioService.TryGetMaster(out var percent, out var muted))
        {
            MasterSlider.IsEnabled = false;
            ActionToggle.IsEnabled = false;
            MasterPercentText.Text = "—";
            MasterCaptionText.Text = "读不到设备";
            ToolTipService.SetToolTip(MasterCaptionText, "读不到音量设备（没声卡或音频服务没起来）");
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

    /// <summary>
    /// 屏幕亮度：笔记本内屏能调，**外接显示器基本调不了**（系统亮度接口不管它）→ 读不到就如实说。
    /// 自动亮度那颗键：没有环境光传感器就置灰（点了也没用，别骗人）。
    /// </summary>
    private void RefreshBrightness()
    {
        if (!BrightnessService.TryGet(out var percent))
        {
            MasterSlider.IsEnabled = false;
            ActionToggle.IsEnabled = false;
            MasterPercentText.Text = "—";
            MasterCaptionText.Text = "读不到设备";
            ToolTipService.SetToolTip(MasterCaptionText,
                "读不到屏幕亮度：这台显示器的亮度不由系统管（外接显示器一般是这样）");
            ActionToggle.IsChecked = false;
            return;
        }

        MasterSlider.IsEnabled = true;
        ActionToggle.IsEnabled = BrightnessService.AdaptiveSupported;
        MasterCaptionText.Text = "屏幕亮度";
        ToolTipService.SetToolTip(MasterCaptionText, null);

        SetSliderSilently(percent);
        MasterPercentText.Text = percent + "%";
        MasterPercentText.Opacity = 1.0;

        ActionToggle.IsChecked = BrightnessService.TryGetAdaptive(out var adaptive) && adaptive;
    }

    private void SetSliderSilently(int value)
    {
        if (Math.Abs(MasterSlider.Value - value) < 0.5) return;
        _syncing = true;
        try { MasterSlider.Value = value; }
        finally { _syncing = false; }
    }

    // ── 交互 ──────────────────────────────────────────────────────────────

    private void MasterSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing) return;
        var percent = (int)Math.Round(e.NewValue);
        MasterPercentText.Text = percent + "%";

        if (_target == Target.Volume) AudioService.SetMasterPercent(percent);
        else BrightnessService.SetPercent(percent);
    }

    /// <summary>
    /// 第一颗键：音量模式 = 静音开关；亮度模式 = 自动亮度开关。
    /// 改不动（比如电源设置不让改）就把开关弹回原样 —— 界面不能显示一个假的状态。
    /// </summary>
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
        if (!BrightnessService.SetAdaptive(want))
        {
            ActionToggle.IsChecked = !want;
            ToolTipService.SetToolTip(ActionToggle, "改不了自动亮度（可能需要管理员权限，或这台机器不支持）");
        }
    }

    /// <summary>展开 / 收起合成器浮窗（它贴着本窗往屏幕里侧排）。亮度模式下这颗键藏着，点不到。</summary>
    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        VolumeMixerWindow.Toggle();
        ExpandToggle.IsChecked = VolumeMixerWindow.IsVisible;
    }
}
