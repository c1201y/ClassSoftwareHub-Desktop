using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;











public sealed partial class SnipEditorWindow : Window
{
    private enum Tool
    {
        Pen,
        Marker,
        Arrow,
        Rect,
        Ellipse,
        Text,
        Mosaic,
    }

    private ScreenFrame? _frame;
    private int _cropX;
    private int _cropY;
    private int _pxW;
    private int _pxH;
    private int _shotX;
    private int _shotY;
    private byte[]? _bmp;

    private double _scale = 1.0;
    private double _dipW;
    private double _dipH;

    private Tool _tool = Tool.Pen;
    private Color _color = Color.FromArgb(255, 232, 17, 35);
    private readonly List<Color> _palette = new();
    private double _thickness = 6;                       
    private int _thicknessStep = 1;                      

    private readonly List<UIElement> _items = new();
    private readonly List<UIElement> _redo = new();

    
    private bool _drawing;
    private Point _start;
    private Polyline? _stroke;
    private Shape? _shape;
    private bool _textPending;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _statusTimer;

    private SnipEditorWindow()
    {
        InitializeComponent();

        InkLayer.PointerPressed += Ink_PointerPressed;
        InkLayer.PointerMoved += Ink_PointerMoved;
        InkLayer.PointerReleased += Ink_PointerReleased;
        InkLayer.PointerCaptureLost += (_, _) => _drawing = false;

        
        AddKey(BtUndo, VirtualKey.Z, VirtualKeyModifiers.Control);
        AddKey(BtRedo, VirtualKey.Y, VirtualKeyModifiers.Control);
        AddKey(BtCopy, VirtualKey.C, VirtualKeyModifiers.Control);
        AddKey(BtSave, VirtualKey.S, VirtualKeyModifiers.Control);
    }

    private static void AddKey(Button b, VirtualKey key, VirtualKeyModifiers mods)
    {
        b.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = key, Modifiers = mods });
    }

    
    public static SnipEditorWindow? Open(ScreenFrame frame, int cropX, int cropY, int cropW, int cropH)
    {
        try
        {
            var w = new SnipEditorWindow();
            w.Setup(frame, cropX, cropY, cropW, cropH);
            w.Activate();
            _ = w.AutoSaveAsync();                        
            ScreenCapture.Log($"编辑窗已开: {cropW}x{cropH}");
            return w;
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("编辑窗打开失败: " + ex);
            return null;
        }
    }

    
    
    
    
    private void ApplyRounded(AppWindow app)
    {
        try
        {
            var maximized = app.Presenter is OverlappedPresenter p
                            && p.State == OverlappedPresenterState.Maximized;
            Core.WindowChrome.SetRounded(WindowNative.GetWindowHandle(this), !maximized);
        }
        catch { }
    }

    private void Setup(ScreenFrame frame, int cropX, int cropY, int cropW, int cropH)
    {
        _frame = frame;
        _cropX = cropX;
        _cropY = cropY;
        _pxW = Math.Max(1, cropW);
        _pxH = Math.Max(1, cropH);
        _shotX = frame.X + cropX;
        _shotY = frame.Y + cropY;
        _bmp = ScreenCapture.ToBmp(frame, cropX, cropY, _pxW, _pxH);

        var hwnd = WindowNative.GetWindowHandle(this);
        var app = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        app.Title = $"截图编辑　{_pxW} × {_pxH}";
        app.IsShownInSwitchers = true;

        if (app.Presenter is OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = true;                      
            p.IsResizable = true;
            p.IsMaximizable = true;
            p.IsMinimizable = true;
        }

        
        
        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            TitleText.Text = $"截图编辑　{_pxW} × {_pxH}";

            var bar = app.TitleBar;
            bar.ExtendsContentIntoTitleBar = true;
            bar.PreferredHeightOption = TitleBarHeightOption.Standard;   
            bar.ButtonBackgroundColor = Colors.Transparent;              
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
            UpdateCaptionColors();
            RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionColors();

            
            
            
            ApplyRounded(app);
            app.Changed += (_, e) =>
            {
                if (e.DidPresenterChange || e.DidSizeChange) ApplyRounded(app);
            };
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("编辑窗标题栏定制失败: " + ex.Message);
        }

        
        
        BackdropHost.Apply(this, RootGrid);
        ThemeHost.Apply(RootGrid);                       

        _scale = DpiScale();
        _dipW = _pxW / _scale;
        _dipH = _pxH / _scale;

        BaseImage.Width = _dipW;
        BaseImage.Height = _dipH;
        InkLayer.Width = _dipW;
        InkLayer.Height = _dipH;
        CanvasHost.Width = _dipW;
        CanvasHost.Height = _dipH;

        
        var work = DisplayArea.Primary.WorkArea;
        var chromeH = (int)Math.Round(96 * _scale);      
        var maxW = Math.Max(360, work.Width - (int)Math.Round(40 * _scale));
        var maxH = Math.Max(260, work.Height - chromeH - (int)Math.Round(40 * _scale));
        var winW = Math.Min(Math.Max(_pxW + (int)Math.Round(24 * _scale), (int)Math.Round(720 * _scale)), maxW);
        var winH = Math.Min(_pxH, maxH) + chromeH;
        var x = work.X + Math.Max(0, (work.Width - winW) / 2);
        var y = work.Y + Math.Max(0, (work.Height - winH) / 4);
        app.MoveAndResize(new RectInt32(x, y, winW, winH));

        BuildPalette();
        SelectTool(Tool.Pen);

        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(2.5);
        _statusTimer.IsRepeating = false;
        _statusTimer.Tick += (_, _) => { try { StatusText.Text = ""; } catch { } };

        _ = LoadAsync();
    }

    
    private async Task AutoSaveAsync()
    {
        try
        {
            if (_frame is null || _bmp is null) return;
            if (!App.Settings.Current.ShotAutoSave) return;

            var path = await ShotSaver.SaveFrameAsync(_frame, _cropX, _cropY, _pxW, _pxH);
            if (string.IsNullOrEmpty(path)) return;

            var name = System.IO.Path.GetFileName(path);
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    Status("已自动保存：" + name);
                    ToolTipService.SetToolTip(StatusText, path);   
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("自动保存失败: " + ex.Message);
        }
    }

    
    private void UpdateCaptionColors()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var bar = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd)).TitleBar;
            var dark = RootGrid.ActualTheme == ElementTheme.Dark;
            bar.ButtonForegroundColor = dark ? Colors.White : Windows.UI.Color.FromArgb(255, 30, 30, 30);
            bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
            bar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(26, 255, 255, 255) : Windows.UI.Color.FromArgb(20, 0, 0, 0);
            bar.ButtonPressedForegroundColor = dark ? Windows.UI.Color.FromArgb(255, 200, 200, 200) : Windows.UI.Color.FromArgb(255, 90, 90, 90);
            bar.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(160, 255, 255, 255) : Windows.UI.Color.FromArgb(140, 0, 0, 0);
            bar.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(40, 255, 255, 255) : Windows.UI.Color.FromArgb(30, 0, 0, 0);
        }
        catch { }
    }

    private async Task LoadAsync()
    {
        try
        {
            if (_bmp is null) return;
            var img = await ScreenCapture.ToImageAsync(_bmp);
            if (img is not null) BaseImage.Source = img;
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("编辑窗载图失败: " + ex);
        }
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

    

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        SelectTool(tag switch
        {
            "marker" => Tool.Marker,
            "arrow" => Tool.Arrow,
            "rect" => Tool.Rect,
            "ellipse" => Tool.Ellipse,
            "text" => Tool.Text,
            "mosaic" => Tool.Mosaic,
            _ => Tool.Pen,
        });
    }

    private void SelectTool(Tool t)
    {
        _tool = t;
        var sel = new SolidColorBrush(Color.FromArgb(60, 0, 120, 212));
        var none = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

        foreach (var (btn, own) in new (Button, Tool)[]
        {
            (BtPen, Tool.Pen), (BtMarker, Tool.Marker), (BtArrow, Tool.Arrow),
            (BtRect, Tool.Rect), (BtEllipse, Tool.Ellipse), (BtText, Tool.Text), (BtMosaic, Tool.Mosaic),
        })
        {
            var on = own == t;
            btn.Background = on ? sel : none;
            btn.BorderBrush = new SolidColorBrush(on ? Color.FromArgb(255, 0, 120, 212) : Color.FromArgb(0, 0, 0, 0));
            btn.BorderThickness = new Thickness(1);
        }
    }

    private void BuildPalette()
    {
        _palette.Clear();
        _palette.Add(Color.FromArgb(255, 232, 17, 35));    
        _palette.Add(Color.FromArgb(255, 255, 140, 0));    
        _palette.Add(Color.FromArgb(255, 255, 214, 10));   
        _palette.Add(Color.FromArgb(255, 16, 185, 129));   
        _palette.Add(Color.FromArgb(255, 0, 120, 212));    
        _palette.Add(Color.FromArgb(255, 255, 255, 255));  
        _palette.Add(Color.FromArgb(255, 0, 0, 0));        

        foreach (var c in _palette)
        {
            var swatch = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(c),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(SwatchLine()),
                Tag = c,
            };
            swatch.Tapped += (s, _) =>
            {
                if (s is Border sb && sb.Tag is Color cc) { _color = cc; PaintPalette(); }
            };
            ColorStrip.Children.Add(swatch);
        }

        PaintPalette();
    }

    
    private Color SwatchLine()
        => RootGrid.ActualTheme == ElementTheme.Dark
            ? Color.FromArgb(70, 255, 255, 255)
            : Color.FromArgb(60, 0, 0, 0);

    private void PaintPalette()
    {
        foreach (var child in ColorStrip.Children)
        {
            if (child is not Border b || b.Tag is not Color c) continue;
            var isSel = c == _color;
            b.BorderThickness = new Thickness(isSel ? 3 : 2);
            b.BorderBrush = new SolidColorBrush(isSel ? Color.FromArgb(255, 0, 120, 212) : SwatchLine());
        }
    }

    private void Thickness_Click(object sender, RoutedEventArgs e)
    {
        _thicknessStep = (_thicknessStep + 1) % 3;
        (_thickness, ThicknessText.Text) = _thicknessStep switch
        {
            0 => (3.0, "细"),
            2 => (11.0, "粗"),
            _ => (6.0, "中"),
        };
    }

    

    private void Ink_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_drawing || _textPending) return;
        var pt = e.GetCurrentPoint(InkLayer).Position;

        if (_tool == Tool.Text)
        {
            _ = PromptTextAsync(pt);
            return;
        }

        _drawing = true;
        _start = pt;
        try { InkLayer.CapturePointer(e.Pointer); } catch { }

        var brush = new SolidColorBrush(_color);

        switch (_tool)
        {
            case Tool.Pen:
            case Tool.Marker:
                _stroke = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = _tool == Tool.Marker ? _thickness * 3 : _thickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Opacity = _tool == Tool.Marker ? 0.32 : 1.0,
                };
                _stroke.Points.Add(pt);
                InkLayer.Children.Add(_stroke);
                break;

            case Tool.Arrow:
                _stroke = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = _thickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                _stroke.Points.Add(pt);
                _stroke.Points.Add(pt);
                InkLayer.Children.Add(_stroke);
                break;

            case Tool.Rect:
                _shape = new Rectangle { Stroke = brush, StrokeThickness = _thickness, Fill = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)) };
                InkLayer.Children.Add(_shape);
                break;

            case Tool.Ellipse:
                _shape = new Ellipse { Stroke = brush, StrokeThickness = _thickness, Fill = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)) };
                InkLayer.Children.Add(_shape);
                break;

            case Tool.Mosaic:
                _shape = new Rectangle
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(255, 80, 80, 80)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                    Fill = new SolidColorBrush(Color.FromArgb(56, 0, 0, 0)),
                };
                InkLayer.Children.Add(_shape);
                break;
        }

        e.Handled = true;
    }

    private void Ink_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_drawing) return;
        var pt = e.GetCurrentPoint(InkLayer).Position;

        if (_stroke is not null)
        {
            if (_tool == Tool.Arrow)
            {
                UpdateArrow(_stroke, _start, pt);
            }
            else
            {
                _stroke.Points.Add(pt);
            }
        }
        else if (_shape is not null)
        {
            PlaceRect(_shape, new Rect(
                Math.Min(_start.X, pt.X), Math.Min(_start.Y, pt.Y),
                Math.Abs(pt.X - _start.X), Math.Abs(pt.Y - _start.Y)));
        }

        e.Handled = true;
    }

    private void Ink_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_drawing) return;
        _drawing = false;
        try { InkLayer.ReleasePointerCapture(e.Pointer); } catch { }

        var end = e.GetCurrentPoint(InkLayer).Position;
        var moved = Math.Abs(end.X - _start.X) + Math.Abs(end.Y - _start.Y);

        if (_stroke is not null)
        {
            
            if (moved < 3) InkLayer.Children.Remove(_stroke);
            else Commit(_stroke);
            _stroke = null;
        }
        else if (_shape is not null)
        {
            var rect = new Rect(
                Math.Min(_start.X, end.X), Math.Min(_start.Y, end.Y),
                Math.Abs(end.X - _start.X), Math.Abs(end.Y - _start.Y));

            if (rect.Width < 3 || rect.Height < 3 || moved < 3)
            {
                InkLayer.Children.Remove(_shape);
            }
            else if (_tool == Tool.Mosaic)
            {
                InkLayer.Children.Remove(_shape);        
                AddMosaic(rect);
            }
            else
            {
                Commit(_shape);
            }

            _shape = null;
        }

        e.Handled = true;
    }

    private void Commit(UIElement el)
    {
        _items.Add(el);
        _redo.Clear();
    }

    private static void UpdateArrow(Polyline line, Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        line.Points.Clear();
        if (len < 1)
        {
            line.Points.Add(a);
            line.Points.Add(b);
            return;
        }

        var ux = dx / len;
        var uy = dy / len;
        var head = Math.Clamp(len * 0.22, 9, 26);
        var hx = b.X - ux * head;
        var hy = b.Y - uy * head;
        var px = -uy * head * 0.45;
        var py = ux * head * 0.45;

        line.Points.Add(a);
        line.Points.Add(b);
        line.Points.Add(new Point(hx + px, hy + py));
        line.Points.Add(b);
        line.Points.Add(new Point(hx - px, hy - py));
    }

    private static void PlaceRect(Shape shape, Rect r)
    {
        Canvas.SetLeft(shape, r.X);
        Canvas.SetTop(shape, r.Y);
        shape.Width = Math.Max(0, r.Width);
        shape.Height = Math.Max(0, r.Height);
    }

    
    private void AddMosaic(Rect rDip)
    {
        var frame = _frame;
        if (frame is null) return;

        var x0 = Math.Clamp((int)Math.Round(rDip.X * _scale), 0, _pxW);
        var y0 = Math.Clamp((int)Math.Round(rDip.Y * _scale), 0, _pxH);
        var x1 = Math.Clamp((int)Math.Round((rDip.X + rDip.Width) * _scale), 0, _pxW);
        var y1 = Math.Clamp((int)Math.Round((rDip.Y + rDip.Height) * _scale), 0, _pxH);

        var w = x1 - x0;
        var h = y1 - y0;
        if (w < 4 || h < 4) return;

        var block = 10;
        while ((long)(w / block + 1) * (h / block + 1) > 1200 && block < 80) block *= 2;

        var group = new Canvas();
        var stride = frame.Width * 4;

        for (var by = y0; by < y1; by += block)
        {
            for (var bx = x0; bx < x1; bx += block)
            {
                var bw = Math.Min(block, x1 - bx);
                var bh = Math.Min(block, y1 - by);

                long r = 0, g = 0, bl = 0;
                var n = 0;
                for (var y = by; y < by + bh; y++)
                {
                    for (var x = bx; x < bx + bw; x++)
                    {
                        var i = (_cropY + y) * stride + (_cropX + x) * 4;
                        if (i < 0 || i + 2 >= frame.Bgra.Length) continue;
                        bl += frame.Bgra[i];
                        g += frame.Bgra[i + 1];
                        r += frame.Bgra[i + 2];
                        n++;
                    }
                }

                if (n == 0) continue;
                var col = Color.FromArgb(255, (byte)(r / n), (byte)(g / n), (byte)(bl / n));
                var cell = new Rectangle { Width = bw / _scale, Height = bh / _scale, Fill = new SolidColorBrush(col) };
                Canvas.SetLeft(cell, bx / _scale);
                Canvas.SetTop(cell, by / _scale);
                group.Children.Add(cell);
            }
        }

        InkLayer.Children.Add(group);
        Commit(group);
    }

    private async Task PromptTextAsync(Point at)
    {
        _textPending = true;
        try
        {
            var box = new TextBox { PlaceholderText = "输入文字", AcceptsReturn = false, Width = 260 };
            var dlg = new ContentDialog
            {
                        RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
                XamlRoot = RootGrid.XamlRoot,
                Title = "添加文字",
                Content = box,
                PrimaryButtonText = "添加",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };

            var res = await dlg.ShowAsync();
            var text = box.Text?.Trim();
            if (res != ContentDialogResult.Primary || string.IsNullOrEmpty(text)) return;

            var tb = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(_color),
                FontSize = 10 + _thickness * 1.6,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };
            Canvas.SetLeft(tb, at.X);
            Canvas.SetTop(tb, Math.Max(0, at.Y - 12));
            InkLayer.Children.Add(tb);
            Commit(tb);
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("文字失败: " + ex.Message);
        }
        finally
        {
            _textPending = false;
        }
    }

    

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;
        var last = _items[^1];
        _items.RemoveAt(_items.Count - 1);
        InkLayer.Children.Remove(last);
        _redo.Add(last);
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_redo.Count == 0) return;
        var el = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        InkLayer.Children.Add(el);
        _items.Add(el);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        InkLayer.Children.Clear();
        _items.Clear();
        _redo.Clear();
        Status("已清除所有标注");
    }

    

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var png = await ComposeAsync();
            if (png is null) { Status("合成失败"); return; }
            await ScreenCapture.CopyToClipboardAsync(png);
            Status("已复制到剪贴板");
            ScreenCapture.Log($"编辑窗：已复制 {_pxW}x{_pxH}");
        }
        catch (Exception ex)
        {
            Status("复制失败");
            ScreenCapture.Log("复制失败: " + ex.Message);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var png = await ComposeAsync();
            if (png is null) { Status("合成失败"); return; }

            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeChoices.Add("PNG 图片", new List<string> { ".png" });
            picker.SuggestedFileName = $"截图_{DateTime.Now:yyyyMMdd_HHmmss}";

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            await FileIO.WriteBytesAsync(file, png);
            Status("已保存：" + file.Name);
            ScreenCapture.Log($"编辑窗：已保存 {file.Path}");
        }
        catch (Exception ex)
        {
            Status("保存失败");
            ScreenCapture.Log("保存失败: " + ex.Message);
        }
    }

    private async void Pin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var png = await ComposeAsync();
            if (png is null) { Status("合成失败"); return; }
            StickerWindow.Pin(png, _shotX, _shotY, _pxW, _pxH);
            ScreenCapture.Log($"编辑窗：已钉图 {_pxW}x{_pxH}");
            Close();
        }
        catch (Exception ex)
        {
            Status("钉图失败");
            ScreenCapture.Log("钉图失败: " + ex.Message);
        }
    }

    
    private async Task<byte[]?> ComposeAsync()
    {
        try
        {
            var status = StatusText.Text;
            StatusText.Text = "";                        

            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(CanvasHost);
            var buf = await rtb.GetPixelsAsync();
            var w = rtb.PixelWidth;
            var h = rtb.PixelHeight;
            var bytes = new byte[buf.Length];
            using (var dr = DataReader.FromBuffer(buf))
            {
                dr.ReadBytes(bytes);
            }

            StatusText.Text = status;

            var ras = new InMemoryRandomAccessStream();
            var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras);
            enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)w, (uint)h, 96, 96, bytes);
            await enc.FlushAsync();

            var outBytes = new byte[ras.Size];
            using (var dr2 = new DataReader(ras.GetInputStreamAt(0)))
            {
                await dr2.LoadAsync((uint)ras.Size);
                dr2.ReadBytes(outBytes);
            }

            return outBytes;
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("合成失败: " + ex);
            return null;
        }
    }

    private void Status(string text)
    {
        try
        {
            StatusText.Text = text;
            _statusTimer?.Start();
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
