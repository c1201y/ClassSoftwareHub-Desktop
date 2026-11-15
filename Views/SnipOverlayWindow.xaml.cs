using System;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;



















public sealed partial class SnipOverlayWindow : Window
{
    private enum SnipMode
    {
        None,
        Rect,
        Window,
        Full,
    }

    private static SnipOverlayWindow? _instance;
    private static bool _starting;

    private ScreenFrame? _frame;
    private double _scale = 1.0;                 

    private SnipMode _mode = SnipMode.None;
    private bool _dragging;
    private Point _startDip;
    private Point _currentDip;

    
    private Rect? _hoverDip;

    private static readonly SolidColorBrush ActiveFill = new(Windows.UI.Color.FromArgb(40, 0x4C, 0xC2, 0xFF));
    private static readonly SolidColorBrush ActiveStroke = new(Windows.UI.Color.FromArgb(255, 0x4C, 0xC2, 0xFF));

    private SnipOverlayWindow()
    {
        InitializeComponent();

        var hwnd = WindowNative.GetWindowHandle(this);
        var app = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        app.Title = "截屏";
        app.IsShownInSwitchers = false;

        if (app.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }

        StickerWindow.KillSystemBorder(hwnd);           
        ThemeHost.Apply(Root);                          

        Root.PointerPressed += Root_PointerPressed;
        Root.PointerMoved += Root_PointerMoved;
        Root.PointerReleased += Root_PointerReleased;
        Root.PointerCaptureLost += (_, _) => _dragging = false;
        Root.KeyDown += Root_KeyDown;
        Root.RightTapped += (_, _) => Cancel();
        Root.Loaded += (_, _) => Reset();               

        
        Toolbar.PointerPressed += (_, e) => e.Handled = true;

        ApplyMode(SnipMode.None);

        Closed += (_, _) =>
        {
            if (ReferenceEquals(_instance, this)) _instance = null;
        };
    }

    
    
    
    public static void Begin(int delayMs = 0) => _ = BeginAsync(delayMs);

    private static async Task BeginAsync(int delayMs)
    {
        try
        {
            if (_instance is not null || _starting)
            {
                Log("已经开着（或在开）框选层了，忽略这次");
                return;
            }

            _starting = true;

            if (delayMs > 0) await Task.Delay(delayMs);      

            var (vx, vy, vw, vh) = ScreenCapture.VirtualScreen();
            var frame = ScreenCapture.Grab(vx, vy, vw, vh);
            if (frame is null)
            {
                Log("抓全屏失败，截屏中止");
                return;
            }

            var w = new SnipOverlayWindow();
            w._frame = frame;
            w._scale = w.DpiScale();

            w.AppWindow.MoveAndResize(new RectInt32(vx, vy, vw, vh));
            _instance = w;

            
            var bmp = ScreenCapture.ToBmp(frame);
            if (bmp is not null)
            {
                var img = await ScreenCapture.ToImageAsync(bmp);
                if (img is not null) w.Frozen.Source = img;
            }

            w.Reset();
            w.Activate();                                  
            try { w.KeyCatcher.Focus(FocusState.Programmatic); } catch { }

            Log($"框选层已开: 虚拟屏={vx},{vy} {vw}x{vh} 缩放={w._scale:0.##}");
        }
        catch (Exception ex)
        {
            Log("框选层打开失败: " + ex);
        }
        finally
        {
            _starting = false;
        }
    }

    

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;

        switch (tag)
        {
            case "rect":
                ApplyMode(SnipMode.Rect);
                break;

            case "window":
                ApplyMode(SnipMode.Window);
                break;

            case "full":
                ApplyMode(SnipMode.Full);
                CommitFullscreen();                        
                break;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();

    private void ApplyMode(SnipMode mode)
    {
        _mode = mode;
        _dragging = false;
        _hoverDip = null;

        HintText.Text = mode switch
        {
            SnipMode.Rect => "拖动以选择截取区域　·　Esc 取消",
            SnipMode.Window => "将鼠标移至目标窗口后单击　·　Esc 取消",
            SnipMode.Full => "正在截取全屏",
            _ => "选择截取方式：矩形 / 窗口 / 全屏　·　Esc 取消",
        };

        Highlight(ModeRect, mode == SnipMode.Rect);
        Highlight(ModeWindow, mode == SnipMode.Window);
        Highlight(ModeFull, mode == SnipMode.Full);

        
        
        Toolbar.Visibility = mode == SnipMode.None ? Visibility.Visible : Visibility.Collapsed;

        Reset();                                           
    }

    private static void Highlight(Button b, bool on)
    {
        try
        {
            if (on)
            {
                b.Background = ActiveFill;
                b.BorderBrush = ActiveStroke;
                b.BorderThickness = new Thickness(1);
            }
            else
            {
                b.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                b.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                b.BorderThickness = new Thickness(1);
            }
        }
        catch { }
    }

    

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_frame is null) return;
        var pt = e.GetCurrentPoint(Root).Position;

        switch (_mode)
        {
            case SnipMode.Rect:
                _dragging = true;
                _startDip = pt;
                _currentDip = pt;
                try { Root.CapturePointer(e.Pointer); } catch { }
                Redraw();
                SelBorder.Visibility = Visibility.Collapsed;
                SizeBadge.Visibility = Visibility.Collapsed;
                break;

            case SnipMode.Window:
                UpdateHover(pt);
                CommitHover();
                break;

            default:
                break;                                     
        }
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_frame is null) return;
        var pt = e.GetCurrentPoint(Root).Position;

        if (_mode == SnipMode.Rect && _dragging)
        {
            _currentDip = pt;
            Redraw();
        }
        else if (_mode == SnipMode.Window)
        {
            UpdateHover(pt);
        }
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_mode != SnipMode.Rect || !_dragging) return;
        _dragging = false;
        try { Root.ReleasePointerCapture(e.Pointer); } catch { }

        var sel = SelectionDip();
        if (sel is null || sel.Value.Width < 4 || sel.Value.Height < 4)
        {
            Log($"框选太小({sel?.Width:0}x{sel?.Height:0})，当取消");
            Cancel();
            return;
        }

        CommitRect(sel.Value);
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            Cancel();
        }
    }

    

    private void Reset()
    {
        if (Layer is null) return;
        Dim.Data = new RectangleGeometry { Rect = new Rect(0, 0, Root.ActualWidth, Root.ActualHeight) };
        SelBorder.Visibility = Visibility.Collapsed;
        SizeBadge.Visibility = Visibility.Collapsed;
    }

    
    private void Redraw()
    {
        var r = _mode == SnipMode.Window ? _hoverDip : SelectionDip();
        if (r is null || r.Value.Width <= 0 || r.Value.Height <= 0)
        {
            Reset();
            return;
        }

        var rect = r.Value;

        
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry { Rect = new Rect(0, 0, Root.ActualWidth, Root.ActualHeight) });
        group.Children.Add(new RectangleGeometry { Rect = rect });
        Dim.Data = group;

        Canvas.SetLeft(SelBorder, rect.X);
        Canvas.SetTop(SelBorder, rect.Y);
        SelBorder.Width = rect.Width;
        SelBorder.Height = rect.Height;
        SelBorder.Visibility = Visibility.Visible;

        
        var pxW = (int)Math.Round(rect.Width * _scale);
        var pxH = (int)Math.Round(rect.Height * _scale);
        SizeText.Text = $"{pxW} × {pxH}";
        SizeBadge.Visibility = Visibility.Visible;

        var badgeLeft = Math.Clamp(rect.X, 0, Math.Max(0, Root.ActualWidth - 90));
        var badgeTop = rect.Y + rect.Height + 6;
        if (badgeTop > Root.ActualHeight - 30) badgeTop = Math.Max(0, rect.Y - 28);
        Canvas.SetLeft(SizeBadge, badgeLeft);
        Canvas.SetTop(SizeBadge, badgeTop);
    }

    private Rect? SelectionDip()
    {
        var x = Math.Min(_startDip.X, _currentDip.X);
        var y = Math.Min(_startDip.Y, _currentDip.Y);
        var w = Math.Abs(_currentDip.X - _startDip.X);
        var h = Math.Abs(_currentDip.Y - _startDip.Y);
        if (w <= 0 || h <= 0) return null;
        x = Math.Clamp(x, 0, Root.ActualWidth);
        y = Math.Clamp(y, 0, Root.ActualHeight);
        w = Math.Min(w, Root.ActualWidth - x);
        h = Math.Min(h, Root.ActualHeight - y);
        return new Rect(x, y, w, h);
    }

    
    private void UpdateHover(Point ptDip)
    {
        var frame = _frame;
        if (frame is null) return;

        var px = frame.X + (int)Math.Round(ptDip.X * _scale);
        var py = frame.Y + (int)Math.Round(ptDip.Y * _scale);

        var hit = ScreenCapture.WindowRectUnderPoint(px, py);
        if (hit is null)
        {
            if (_hoverDip is not null)
            {
                _hoverDip = null;
                Reset();
            }
            return;
        }

        var r = hit.Value;
        var dip = new Rect(
            (r.X - frame.X) / _scale,
            (r.Y - frame.Y) / _scale,
            r.W / _scale,
            r.H / _scale);

        _hoverDip = dip;
        Redraw();
    }

    

    private void CommitRect(Rect selDip)
    {
        var px = (int)Math.Round(selDip.X * _scale);
        var py = (int)Math.Round(selDip.Y * _scale);
        var pw = Math.Max(1, (int)Math.Round(selDip.Width * _scale));
        var ph = Math.Max(1, (int)Math.Round(selDip.Height * _scale));
        CommitRegion(px, py, pw, ph, "矩形");
    }

    private void CommitHover()
    {
        var frame = _frame;
        var dip = _hoverDip;
        if (frame is null || dip is null)
        {
            Log("窗口方式：光标下没找到窗口");
            return;
        }

        var px = (int)Math.Round(dip.Value.X * _scale);
        var py = (int)Math.Round(dip.Value.Y * _scale);
        var pw = Math.Max(1, (int)Math.Round(dip.Value.Width * _scale));
        var ph = Math.Max(1, (int)Math.Round(dip.Value.Height * _scale));
        CommitRegion(px, py, pw, ph, "窗口");
    }

    private void CommitFullscreen()
    {
        var frame = _frame;
        if (frame is null) { Cancel(); return; }
        CommitRegion(0, 0, frame.Width, frame.Height, "全屏");
    }

    
    private void CommitRegion(int px, int py, int pw, int ph, string how)
    {
        var frame = _frame;
        if (frame is null) { Cancel(); return; }

        var bmp = ScreenCapture.ToBmp(frame, px, py, pw, ph);
        var screenX = frame.X + px;
        var screenY = frame.Y + py;

        Close();

        if (bmp is null)
        {
            Log("裁剪失败");
            return;
        }

        Log($"截屏({how})：{pw}x{ph} @{screenX},{screenY}");
        _ = FinishAsync(frame, px, py, pw, ph);
    }

    private static async Task FinishAsync(ScreenFrame frame, int cropX, int cropY, int cropW, int cropH)
    {
        try
        {
            
            SnipEditorWindow.Open(frame, cropX, cropY, cropW, cropH);
            await Task.CompletedTask;
            TeachingActions.RefocusPrevious();
        }
        catch (Exception ex)
        {
            Log("打开编辑窗失败: " + ex);
        }
    }

    private void Cancel()
    {
        Log("截屏取消");
        Close();
    }

    private double DpiScale()
    {
        try
        {
            var dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
            if (dpi > 0) return dpi / 96.0;
        }
        catch { }
        return 1.0;
    }

    private static void Log(string msg) => ScreenCapture.Log(msg);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
