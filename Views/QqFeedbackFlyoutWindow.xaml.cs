using System;
using System.Runtime.InteropServices;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 「在 Q 群中反馈」的提醒浮窗：Q 群卡片在浏览器里打开的同时弹出来，
/// 文案「加群后请复制反馈信息」，两颗键 = 复制（主）/ 取消。
///
/// 外壳走 <see cref="FlyoutChrome"/>（无边框 + 亚克力 + 圆角 + **置顶** + 不进 Alt+Tab），
/// 跟侧边栏那几个浮窗同一套材质 —— 摆在一起才像一个整体。
///
/// ⚠️ 规矩（踩过的，别改）：
///   1. **不因失焦自己收起**。音量浮窗会（见 VolumeFlyoutGroup），这个**绝不能**：
///      用户恰恰要切到浏览器 / QQ 去加群，提醒收了就等于没提醒。
///      退出只有三条路：复制 / 取消 / Esc。
///   2. 面板宽度写死、高度实测（<see cref="SizeDip"/>）—— 宽度不定的话里头的文案就没法换行。
///   3. <see cref="FlyoutChrome.Finish"/> 必须在窗口**显示之后**再调。
///
/// 面板里的横向排列一律用 Grid，不用横向 StackPanel（原因见 XAML 注释）。
/// </summary>
public sealed partial class QqFeedbackFlyoutWindow : Window
{
    /// <summary>面板宽度（dip）。够放下「加群后请复制反馈信息」一行 + 说明文字两行。</summary>
    private const int PanelWidthDip = 320;

    private const int MinPanelHeightDip = 118;

    /// <summary>离屏幕 / 主窗右下角的边距（dip）。</summary>
    private const int MarginDip = 22;

    private static QqFeedbackFlyoutWindow? _instance;

    private readonly FlyoutChrome _chrome;

    /// <summary>要复制的那段反馈信息（标题 + 正文），点「在 Q 群中反馈」时由反馈页拼好塞进来。</summary>
    private string _copyText = "";

    private bool _visible;

    private QqFeedbackFlyoutWindow()
    {
        InitializeComponent();
        _chrome = new FlyoutChrome(this, Root, Panel, "反馈信息");
        Configure();
    }

    /// <summary>这个提醒浮窗现在开着吗。</summary>
    public static bool IsVisible => _instance?._visible == true;

    /// <summary>
    /// 弹出来（已经开着就换掉里面的待复制文本再重新量一次尺寸）。
    /// <paramref name="copyText"/> = 点「复制」时要写进剪贴板的内容。
    /// </summary>
    public static void Show(string copyText)
    {
        try
        {
            _instance ??= new QqFeedbackFlyoutWindow();
            _instance.Present(copyText);
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("反馈提醒浮窗显示失败: " + ex.Message);
        }
    }

    public static void CloseIfOpen() => _instance?.HideSelf();

    private void Configure()
    {
        try
        {
            ThemeHost.Apply(Root);                 // 跟「设置」里的深浅色走

            // ⚠️ 故意**不**订阅 Activated —— 见类注释第 1 条：失焦不能自己收。
            Root.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Escape) HideSelf();
            };

            // 换主题时把材质薄纱重压一次（FlyoutChrome 自己会重画边框，这里只管内容）
            Root.ActualThemeChanged += (_, _) => _chrome.ApplyMaterial();
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("反馈提醒浮窗初始化失败: " + ex.Message);
        }
    }

    // ── 显示 / 收起 ───────────────────────────────────────────────────────

    private void Present(string copyText)
    {
        _copyText = copyText;

        var scale = _chrome.Scale;
        var w = (int)Math.Round(PanelWidthDip * scale);
        var h = (int)Math.Round(SizeDip() * scale);

        _chrome.Present(BestPosition(w, h), w, h);
        _visible = true;

        _chrome.Finish(Root.ActualTheme == ElementTheme.Dark);   // ⚠️ 必须在显示之后调
        FlyoutFade.In(ContentHost, 140);
    }

    private void HideSelf()
    {
        if (!_visible) return;
        _visible = false;
        _chrome.HideDirect();      // 不走滑出动画：这是提醒，不是贴边浮窗，不需要"滑回去"
    }

    /// <summary>
    /// 面板高度 = **实测**。宽度定死为 <see cref="PanelWidthDip"/>（去掉左右各 1px 描边），
    /// 让说明文字按最终宽度换行后再量高度 —— 写死高度要么夹字、要么底部空一截。
    /// </summary>
    private double SizeDip()
    {
        try
        {
            if (Panel.Child is not FrameworkElement host) return MinPanelHeightDip;

            host.Measure(new Windows.Foundation.Size(PanelWidthDip - 2, double.PositiveInfinity));
            return Math.Max(host.DesiredSize.Height + 2, MinPanelHeightDip);   // +2 = 上下描边
        }
        catch
        {
            return MinPanelHeightDip;
        }
    }

    /// <summary>
    /// 摆在**主窗右下角内侧**（用户刚在那儿点的按钮，视线不用跑），主窗不在就退回屏幕右下角。
    /// 坐标是物理像素，最后一律夹进工作区（贴着屏幕右边时会长出屏幕）。
    /// </summary>
    private PointInt32 BestPosition(int w, int h)
    {
        var work = DisplayArea.Primary.WorkArea;
        var margin = (int)Math.Round(MarginDip * _chrome.Scale);

        var x = work.X + work.Width - w - margin;
        var y = work.Y + work.Height - h - margin;

        if (App.MainWindow is { } main && TryMainRect(main, out var r))
        {
            // ⚠️ RectInt32 只有 X/Y/Width/Height，**没有** Right/Bottom（跟 System.Drawing.Rectangle 不一样）
            x = r.X + r.Width - w - margin;
            y = r.Y + r.Height - h - margin;
        }

        return new PointInt32(
            Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - w)),
            Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - h)));
    }

    /// <summary>
    /// 主窗在屏幕上的矩形；**窗口被收进托盘 / 最小化时返回 false** ——
    /// 那种情况下它的坐标要么是 (0,0) 要么是 -32000，拿来定位会把浮窗甩到屏幕外面去。
    /// </summary>
    private static bool TryMainRect(Window main, out RectInt32 rect)
    {
        rect = default;
        try
        {
            var hwnd = WindowNative.GetWindowHandle(main);
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
            if (!GetWindowRect(hwnd, out var r)) return false;
            if (r.Left < -10000 || r.Top < -10000) return false;

            rect = new RectInt32(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            return rect.Width > 0 && rect.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    // ── 交互 ──────────────────────────────────────────────────────────────

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        QqFeedback.Copy(_copyText);
        HideSelf();

        // 复制完顺势把图文流程摊开 —— 用户下一步就是"去群相册粘贴"，
        // 这时候他需要的是"怎么建相册"，不是再点一次按钮。
        QqFeedbackGuideWindow.Show(_copyText);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => HideSelf();

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape) HideSelf();
    }

    // ── Win32 ────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
}
