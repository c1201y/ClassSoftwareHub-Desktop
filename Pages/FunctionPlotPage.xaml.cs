using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace ClassSoftwareHub.Desktop.Pages;















public sealed partial class FunctionPlotPage : Page
{
    
    
    private const double PadLeft = 54;
    private const double PadTop = 16;
    private const double PadRight = 20;
    private const double PadBottom = 30;

    
    private const double DragSamplesPerPixel = 0.35;

    private sealed class Curve
    {
        public Grid Row = null!;
        public TextBox Box = null!;
        public TextBlock Tip = null!;
        public Border Preview = null!;   
        public XamlPath Path = null!;
        public Func<double, double>? Fn;
        public bool Invalid;
    }

    private readonly List<Curve> _curves = new();
    private readonly PlotViewport _vp = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(220) };

    
    
    private readonly DispatcherTimer _analysisTimer = new() { Interval = TimeSpan.FromMilliseconds(420) };

    private bool _ready;
    private bool _dragging;
    private Point _dragFrom;
    private int _colorIndex;
    private Brush[] _strokes = Array.Empty<Brush>();

    
    private bool _analysisCollapsed;

    
    
    
    private static readonly Color[] DarkPalette =
    {
        Color.FromArgb(255, 0x4C, 0xC2, 0xFF),  
        Color.FromArgb(255, 0xFF, 0xA9, 0x4D),  
        Color.FromArgb(255, 0x69, 0xDB, 0x7C),  
        Color.FromArgb(255, 0xFF, 0x87, 0x87),  
        Color.FromArgb(255, 0xB1, 0x97, 0xFC),  
        Color.FromArgb(255, 0xFF, 0xD4, 0x3B),  
        Color.FromArgb(255, 0x66, 0xD9, 0xE8),  
        Color.FromArgb(255, 0xF7, 0x83, 0xAC),  
    };

    private static readonly Color[] LightPalette =
    {
        Color.FromArgb(255, 0x0F, 0x6C, 0xBD),  
        Color.FromArgb(255, 0xD9, 0x53, 0x09),  
        Color.FromArgb(255, 0x0F, 0x7B, 0x0F),  
        Color.FromArgb(255, 0xC4, 0x2B, 0x1C),  
        Color.FromArgb(255, 0x6B, 0x3F, 0xC4),  
        Color.FromArgb(255, 0x9A, 0x6A, 0x00),  
        Color.FromArgb(255, 0x0B, 0x6E, 0x8A),  
        Color.FromArgb(255, 0xB0, 0x2A, 0x6E),  
    };

    public FunctionPlotPage()
    {
        InitializeComponent();

        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Reparse();
        };

        _analysisTimer.Tick += (_, _) =>
        {
            _analysisTimer.Stop();
            RefreshAnalysis();
        };

        Loaded += OnLoaded;
        SizeChanged += Page_SizeChanged;
        Unloaded += (_, _) =>
        {
            _debounce.Stop();
            _analysisTimer.Stop();
        };
    }

    
    private AngleMode Mode => AngleBox.SelectedIndex == 1 ? AngleMode.Degrees : AngleMode.Radians;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_ready) return;

        
        var palette = ThemeBrush.IsDark(this) ? DarkPalette : LightPalette;
        _strokes = palette.Select(c => (Brush)new SolidColorBrush(c)).ToArray();

        _ready = true;

        
        AddCurveRow("sin(x)", focus: false);

        

        BuildKeypadIfReady();

        Reparse();
    }

    

    private void AddCurve_Click(object sender, RoutedEventArgs e) => AddCurveRow("", focus: true);

    private void AddCurveRow(string text, bool focus)
    {
        var stroke = _strokes.Length > 0
            ? _strokes[_colorIndex++ % _strokes.Length]
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);

        var dot = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = stroke,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var box = new TextBox
        {
            Text = text,
            PlaceholderText = "例如 sin(x)、x² − 3x + 1、2ˣ",
            TextWrapping = TextWrapping.NoWrap,
            
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            
            
            InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Formula) } },
        };
        box.TextChanged += Curve_TextChanged;
        box.KeyDown += Curve_KeyDown;
        
        box.GotFocus += (_, _) => _keypadTarget = box;

        
        var prefix = new TextBlock
        {
            Text = "y =",
            FontSize = 14,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };

        var tip = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = ThemeBrush.Get(this, "SystemFillColorCriticalBrush"),
            Margin = new Thickness(2, 2, 0, 0),
        };

        
        
        var preview = new Border
        {
            
            Padding = new Thickness(11, 2, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = new TextBlock { FontSize = 12 },
        };

        var remove = new Button
        {
            Content = new FontIcon { Glyph = "\uE74D", FontSize = 13 },
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(remove, "移除这条曲线");

        
        
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetColumn(dot, 0);
        Grid.SetRow(dot, 0);

        Grid.SetColumn(prefix, 1);
        Grid.SetRow(prefix, 0);

        Grid.SetColumn(box, 2);
        Grid.SetRow(box, 0);

        Grid.SetColumn(remove, 3);
        Grid.SetRow(remove, 0);

        var under = new StackPanel { Spacing = 0 };
        under.Children.Add(preview);
        under.Children.Add(tip);
        Grid.SetColumn(under, 2);
        Grid.SetRow(under, 1);

        row.Children.Add(dot);
        row.Children.Add(prefix);
        row.Children.Add(box);
        row.Children.Add(remove);
        row.Children.Add(under);

        var line = new XamlPath
        {
            Stroke = stroke,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        CurveLayer.Children.Add(line);

        var curve = new Curve { Row = row, Box = box, Tip = tip, Preview = preview, Path = line };
        _curves.Add(curve);

        remove.Click += (_, _) => RemoveCurve(curve);
        CurveList.Children.Add(row);

        UpdateTargetBox();

        if (focus) box.Focus(FocusState.Programmatic);
    }

    private void RemoveCurve(Curve curve)
    {
        if (!_curves.Remove(curve)) return;

        CurveLayer.Children.Remove(curve.Path);
        CurveList.Children.Remove(curve.Row);

        
        if (_curves.Count == 0) AddCurveRow("", focus: true);

        Reparse();
    }

    private void Curve_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;

        
        _debounce.Stop();
        _debounce.Start();
    }

    private void Curve_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;

        _debounce.Stop();
        Reparse();

        
        if (sender is TextBox box && _curves.Count > 0
            && ReferenceEquals(_curves[^1].Box, box)
            && !_curves[^1].Invalid
            && !string.IsNullOrWhiteSpace(box.Text))
        {
            AddCurveRow("", focus: true);
        }
    }

    private void AngleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        
        if (!_ready) return;

        Reparse();   
    }

    

    private void Reparse()
    {
        if (!_ready) return;

        foreach (var curve in _curves)
        {
            var text = curve.Box.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                curve.Fn = null;
                curve.Invalid = false;
                ShowTip(curve, null);
                UpdatePreview(curve, null);
            }
            else if (ExpressionParser.TryCompile(text, Mode, out var fn, out var error))
            {
                curve.Fn = fn;
                curve.Invalid = false;
                ShowTip(curve, null);

                
                
                ExpressionParser.TryParseTree(text, Mode, out var lambda, out _);
                UpdatePreview(curve, lambda);
            }
            else
            {
                curve.Fn = null;
                curve.Invalid = true;
                ShowTip(curve, error);
                UpdatePreview(curve, null);
            }
        }

        Redraw();
        UpdateTargetBox();
        ScheduleAnalysis();
    }

    
    
    
    
    private void UpdatePreview(Curve curve, LambdaExpression? lambda)
    {
        if (lambda is null)
        {
            curve.Preview.Child = null;
            curve.Preview.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var brush = ThemeBrush.Get(this, "TextFillColorSecondaryBrush");
            var sized = MathTypesetter.Render(lambda.Body, 13, brush, degrees: Mode == AngleMode.Degrees);

            
            if (sized is FrameworkElement fe) fe.HorizontalAlignment = HorizontalAlignment.Left;

            curve.Preview.Child = sized;
            curve.Preview.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("plot", ex, "渲染数学排版预览");
            curve.Preview.Child = null;
            curve.Preview.Visibility = Visibility.Collapsed;
        }
    }

    
    private void ScheduleAnalysis()
    {
        if (!_ready) return;
        _analysisTimer.Stop();
        _analysisTimer.Start();
    }

    private void ShowTip(Curve curve, string? error)
    {
        if (string.IsNullOrEmpty(error))
        {
            curve.Tip.Text = "";
            curve.Tip.Visibility = Visibility.Collapsed;

            
            curve.Box.ClearValue(Control.BorderBrushProperty);
        }
        else
        {
            curve.Tip.Text = error;
            curve.Tip.Visibility = Visibility.Visible;
            curve.Box.BorderBrush = ThemeBrush.Get(this, "SystemFillColorCriticalBrush");
        }
    }

    private void PlotArea_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    
    
    
    
    
    
    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyAnalysisLayout(e.NewSize.Width);
    }

    
    
    
    
    private void ApplyAnalysisLayout(double width)
    {
        if (!_ready && width <= 0) return;

        var roomy = width >= 900;
        var show = !_analysisCollapsed && roomy;

        AnalysisCol.Width = show ? new GridLength(272) : new GridLength(0);
        AnalysisPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        
        AnalysisExpand.Visibility = _analysisCollapsed && roomy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AnalysisToggle_Click(object sender, RoutedEventArgs e)
    {
        _analysisCollapsed = !_analysisCollapsed;
        ApplyAnalysisLayout(ActualWidth);
    }

    private PlotFrame MeasureFrame()
    {
        var w = PlotArea.ActualWidth;
        var h = PlotArea.ActualHeight;
        return new PlotFrame(
            PadLeft, PadTop,
            Math.Max(1, w - PadLeft - PadRight),
            Math.Max(1, h - PadTop - PadBottom));
    }

    private void Redraw()
    {
        if (!_ready) return;

        if (PlotArea.ActualWidth <= 1 || PlotArea.ActualHeight <= 1) return;

        var frame = MeasureFrame();

        DrawChrome(frame);
        DrawCurves(frame);
    }

    
    private static string Num(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        if (double.IsPositiveInfinity(v)) return "+∞";
        if (double.IsNegativeInfinity(v)) return "-∞";

        var abs = Math.Abs(v);

        
        
        
        if (abs < 1e-9) return "0";

        if (abs >= 1e5 || abs < 1e-4)
            return v.ToString("0.###e+0", CultureInfo.InvariantCulture);

        return Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);
    }

    private void DrawChrome(PlotFrame f)
    {
        ChromeLayer.Children.Clear();

        
        
        var gridBrush = ThemeBrush.Get(this, "ControlStrokeColorDefaultBrush");
        var axisBrush = ThemeBrush.Get(this, "TextFillColorTertiaryBrush");
        var labelBrush = ThemeBrush.Get(this, "TextFillColorSecondaryBrush");

        
        var xStep = FunctionPlot.NiceStep(_vp.XSpan, Math.Max(2, (int)(f.Width / 88)));
        var ySpan = _vp.YSpan(f);
        var yStep = FunctionPlot.NiceStep(ySpan, Math.Max(2, (int)(f.Height / 62)));

        var xTicks = FunctionPlot.Ticks(_vp.XMin, _vp.XMax, xStep);
        var yMin = _vp.YMin(f);
        var yMax = _vp.YMax(f);
        var yTicks = FunctionPlot.Ticks(yMin, yMax, yStep);

        
        var grid = new PathGeometry();
        foreach (var x in xTicks)
        {
            var px = f.Left + (x - _vp.XMin) / _vp.XSpan * f.Width;
            grid.Figures.Add(Segment(new Point(px, f.Top), new Point(px, f.Top + f.Height)));
        }
        foreach (var y in yTicks)
        {
            var py = f.Top + (yMax - y) / (yMax - yMin) * f.Height;
            grid.Figures.Add(Segment(new Point(f.Left, py), new Point(f.Left + f.Width, py)));
        }
        ChromeLayer.Children.Add(new XamlPath { Data = grid, Stroke = gridBrush, StrokeThickness = 1 });

        
        var onScreenAxis = yMin <= 0 && yMax >= 0;
        var axes = new PathGeometry();
        if (_vp.XMin <= 0 && _vp.XMax >= 0)
        {
            var px = f.Left + (0 - _vp.XMin) / _vp.XSpan * f.Width;
            axes.Figures.Add(Segment(new Point(px, f.Top), new Point(px, f.Top + f.Height)));
        }
        if (onScreenAxis)
        {
            var py = f.Top + yMax / (yMax - yMin) * f.Height;
            axes.Figures.Add(Segment(new Point(f.Left, py), new Point(f.Left + f.Width, py)));
        }
        ChromeLayer.Children.Add(new XamlPath
        {
            Data = axes,
            Stroke = axisBrush,
            StrokeThickness = 1.4,
            Opacity = 0.85,
        });

        
        var xLabelY = onScreenAxis
            ? Math.Clamp(f.Top + yMax / (yMax - yMin) * f.Height + 5, f.Top, f.Top + f.Height - 18)
            : f.Top + f.Height + 5;

        foreach (var x in xTicks)
        {
            
            if (Math.Abs(x) < xStep * 1e-6) continue;

            var px = f.Left + (x - _vp.XMin) / _vp.XSpan * f.Width;
            PlaceXLabel(FunctionPlot.FormatTick(x, xStep), f, px, xLabelY, labelBrush);
        }

        
        foreach (var y in yTicks)
        {
            var py = f.Top + (yMax - y) / (yMax - yMin) * f.Height;
            PlaceYLabel(FunctionPlot.FormatTick(y, yStep), f.Left - 8, py, labelBrush);
        }
    }

    private static PathFigure Segment(Point a, Point b) => new()
    {
        StartPoint = a,
        IsClosed = false,
        IsFilled = false,
        Segments = { new LineSegment { Point = b } },
    };

    
    private void PlaceXLabel(string text, PlotFrame f, double px, double py, Brush brush)
    {
        var label = MakeLabel(text, brush);
        var half = label.DesiredSize.Width / 2;

        
        var left = Math.Max(f.Left + half, Math.Min(px, f.Left + f.Width - half));

        Canvas.SetLeft(label, left - half);
        Canvas.SetTop(label, py);
        ChromeLayer.Children.Add(label);
    }

    
    private void PlaceYLabel(string text, double px, double py, Brush brush)
    {
        var label = MakeLabel(text, brush);

        Canvas.SetLeft(label, px - label.DesiredSize.Width);
        Canvas.SetTop(label, py - label.DesiredSize.Height / 2);
        ChromeLayer.Children.Add(label);
    }

    private static TextBlock MakeLabel(string text, Brush brush)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = brush,
            IsHitTestVisible = false,
        };

        
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return label;
    }

    private void DrawCurves(PlotFrame f)
    {
        var density = _dragging ? DragSamplesPerPixel : FunctionPlot.SamplesPerPixel;

        foreach (var curve in _curves)
        {
            if (curve.Fn is null)
            {
                curve.Path.Data = null;
                continue;
            }

            var geometry = new PathGeometry();

            
            foreach (var segment in FunctionPlot.Sample(curve.Fn, _vp, f, density))
            {
                var figure = new PathFigure { StartPoint = segment[0], IsClosed = false, IsFilled = false };
                for (var i = 1; i < segment.Count; i++)
                    figure.Segments.Add(new LineSegment { Point = segment[i] });
                geometry.Figures.Add(figure);
            }

            curve.Path.Data = geometry;
        }

        
        var rect = new Rect(f.Left, f.Top, f.Width, f.Height);
        if (CurveLayer.Clip is not RectangleGeometry current || current.Rect != rect)
            CurveLayer.Clip = new RectangleGeometry { Rect = rect };
    }

    

    private void Plot_Wheel(object sender, PointerRoutedEventArgs e)
    {
        if (!_ready) return;

        var point = e.GetCurrentPoint(PlotArea);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;

        var f = MeasureFrame();
        _vp.Zoom(delta > 0 ? 1 / 1.15 : 1.15,
                 point.Position.X - f.Left,
                 point.Position.Y - f.Top,
                 f);

        Redraw();
        ScheduleAnalysis();   
        e.Handled = true;
    }

    private void Plot_Pressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(PlotArea);
        if (!point.Properties.IsLeftButtonPressed) return;

        _dragging = true;
        _dragFrom = point.Position;
        PlotArea.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Plot_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        var position = e.GetCurrentPoint(PlotArea).Position;
        var dx = position.X - _dragFrom.X;
        var dy = position.Y - _dragFrom.Y;

        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) return;

        _dragFrom = position;
        _vp.Pan(dx, dy, MeasureFrame());
        Redraw();
        e.Handled = true;
    }

    private void Plot_Released(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        PlotArea.ReleasePointerCapture(e.Pointer);
        Redraw();   
        ScheduleAnalysis();   
        e.Handled = true;
    }

    private void Plot_CaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        Redraw();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _vp.Reset();
        Redraw();
        ScheduleAnalysis();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomByButton(1 / 1.25);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomByButton(1.25);

    private void ZoomByButton(double factor)
    {
        var f = MeasureFrame();
        _vp.Zoom(factor, f.Width / 2, f.Height / 2, f);
        Redraw();
        ScheduleAnalysis();
    }

    

    
    private void UpdateTargetBox()
    {
        if (!_ready) return;

        var previous = TargetBox.SelectedIndex;

        
        TargetBox.SelectionChanged -= TargetBox_Changed;
        TargetBox.Items.Clear();
        foreach (var curve in _curves)
            TargetBox.Items.Add(string.IsNullOrWhiteSpace(curve.Box.Text) ? "（空表达式）" : curve.Box.Text);

        TargetBox.SelectedIndex = previous >= 0 && previous < _curves.Count ? previous : 0;
        TargetBox.SelectionChanged += TargetBox_Changed;

        
        TargetBox.Visibility = _curves.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TargetBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ScheduleAnalysis();
    }

    private Curve? AnalysisTarget()
    {
        if (_curves.Count == 0) return null;

        var index = TargetBox.SelectedIndex;
        if (index < 0 || index >= _curves.Count) index = 0;
        return _curves[index];
    }

    private void RefreshAnalysis()
    {
        if (!_ready) return;

        var curve = AnalysisTarget();

        if (curve?.Fn is null)
        {
            AnalysisEmpty.Text = curve is null
                ? "输入表达式后显示分析结果。"
                : "当前表达式无法解析，暂不能分析。";
            AnalysisEmpty.Visibility = Visibility.Visible;
            AnalysisMode.Text = "";
            AnalysisList.Children.Clear();
            return;
        }

        
        
        
        ClosedForm? closed = null;
        if (ExpressionParser.TryParseTree(curve.Box.Text, Mode, out var lambda, out _)
            && lambda is not null)
        {
            closed = FunctionExact.Match(lambda, Mode);
        }

        AnalysisList.Children.Clear();
        AddNumericAnalysis(curve);

        if (closed is null)
        {
            AnalysisMode.Text = "无解析结论";
            AnalysisEmpty.Text = "该表达式暂无可给出的闭式结论。";
            AnalysisEmpty.Visibility = Visibility.Visible;
            return;
        }

        AnalysisMode.Text = "精确结论";
        foreach (var (label, value) in closed.Fields)
            AnalysisList.Children.Add(Field(label, value));

        if (closed.Note is { Length: > 0 } note)
            AnalysisList.Children.Add(Footnote(note));

        AnalysisEmpty.Visibility = Visibility.Collapsed;
    }


    
    private void AddNumericAnalysis(Curve curve)
    {
        if (curve.Fn is null) return;
        var fn = curve.Fn!;
        var a = _vp.XMin;
        var b = _vp.XMax;

        var derivative = MathSolver.Derivative(fn, 0);
        AnalysisList.Children.Add(Field("f′(0)", MathSolver.Format(derivative)));

        var integral = MathSolver.Integral(fn, a, b);
        var integralText = double.IsNaN(integral) ? "不可积" : MathSolver.Format(integral);
        AnalysisList.Children.Add(Field("定积分", $"∫[{MathSolver.Format(a)}, {MathSolver.Format(b)}] = {integralText}"));

        var roots = MathSolver.Solve(fn, a, b);
        AnalysisList.Children.Add(Field("零点", roots.Count == 0 ? "当前视口内无" : string.Join(", ", roots.Select(MathSolver.Format))));
    }

    
    private UIElement Field(string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var caption = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            
            
            
            
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        
        
        var content = MathText.Render(value, 13, ThemeBrush.Get(this, "TextFillColorPrimaryBrush"));

        Grid.SetColumn(caption, 0);
        Grid.SetColumn(content, 1);
        grid.Children.Add(caption);
        grid.Children.Add(content);

        return grid;
    }

    
    private static UIElement Footnote(string text) => new TextBlock
    {
        Text = text,
        FontSize = 11,
        Opacity = 0.55,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 0),
    };

    

    
    
    
    
    
    
    
    
    
    
    
    
    private sealed record KeyCap(
        string Label,
        string Insert,
        int CaretBack = 0,
        string? WrapSelection = null,
        string? Tooltip = null,
        bool IsCommand = false,
        int SelectAt = 0);

    
    
    
    
    
    
    
    private static readonly KeyCap[] Caps =
    {
        
        new("sin", "sin()", 1, "sin({0})"),
        new("cos", "cos()", 1, "cos({0})"),
        new("tan", "tan()", 1, "tan({0})"),
        new("7", "7"), new("8", "8"), new("9", "9"),

        
        new("asin", "asin()", 1, "asin({0})", "反正弦"),
        new("acos", "acos()", 1, "acos({0})", "反余弦"),
        new("atan", "atan()", 1, "atan({0})", "反正切"),
        new("4", "4"), new("5", "5"), new("6", "6"),

        
        new("ln", "ln()", 1, "ln({0})", "自然对数"),
        new("log", "log()", 1, "log({0})", "单参数按常用对数算；写 log(x, 2) 是换底"),
        new("√", "√()", 1, "√({0})", "算术平方根"),
        new("1", "1"), new("2", "2"), new("3", "3"),

        
        
        new("x²", "²", 0, "({0})²", "选中一段后按此键，给它加平方"),
        new("x³", "³", 0, "({0})³"),
        new("xⁿ", "^", 0, "({0})^", "乘方：底数后按 ^，再输指数"),
        new("0", "0"), new(".", "."), new("⌫", "", 0, null, "删除光标前一个字符", IsCommand: true),

        
        
        new("|x|", "abs()", 1, "abs({0})", "绝对值"),
        new("π", "π"),
        new("e", "e"),
        new("+", "+"), new("−", "−"), new("×", "×"),

        
        new("(", "(", 0, "({0})"),
        new(")", ")"),
        new("x", "x", 0, null, "自变量"),
        new("÷", "÷"), new(",", ","), new("清空", "", 0, null, "清空这一条表达式", IsCommand: true),
    };

    
    private const int KeypadColumns = 6;

    
    private TextBox? _keypadTarget;

    
    
    
    
    private void BuildKeypadIfReady()
    {
        if (KeypadGrid.Children.Count > 0) return;
        BuildKeypad();
    }

    
    
    
    
    
    private TextBox? FindFocusedBox()
    {
        if (_keypadTarget is not null && _curves.Any(c => ReferenceEquals(c.Box, _keypadTarget)))
            return _keypadTarget;

        return null;
    }

    
    private Button MakeKeyButton(KeyCap cap)
    {
        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = cap.Label,
                FontSize = 12,
                
                FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
            },
            MinWidth = 44,
            Height = 32,
            Padding = new Thickness(8, 0, 8, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Tag = cap,
        };

        if (cap.Tooltip is not null) ToolTipService.SetToolTip(btn, cap.Tooltip);
        btn.Click += Keypad_Click;
        return btn;
    }

    private void BuildKeypad()
    {
        if (KeypadGrid.Children.Count > 0) return;

        
        
        KeypadGrid.Children.Clear();
        KeypadGrid.RowDefinitions.Clear();
        KeypadGrid.ColumnDefinitions.Clear();

        for (var i = 0; i < KeypadColumns; i++)
            KeypadGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var rows = (int)Math.Ceiling(Caps.Length / (double)KeypadColumns);
        for (var i = 0; i < rows; i++)
            KeypadGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < Caps.Length; i++)
        {
            var btn = MakeKeyButton(Caps[i]);
            Grid.SetRow(btn, i / KeypadColumns);
            Grid.SetColumn(btn, i % KeypadColumns);
            KeypadGrid.Children.Add(btn);
        }
    }

    private void Keypad_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: KeyCap cap }) return;

        var box = _keypadTarget;
        if (box is null || !_curves.Any(c => ReferenceEquals(c.Box, box)))
        {
            box = FindFocusedBox() ?? _curves.FirstOrDefault()?.Box;
            _keypadTarget = box;
        }

        if (box is null) return;

        var text = box.Text ?? "";
        var selStart = box.SelectionStart;
        var selLength = box.SelectionLength;

        
        if (cap.IsCommand)
        {
            if (cap.Label == "清空")
            {
                box.Text = "";
            }
            else if (selLength > 0)
            {
                box.Text = text.Remove(selStart, selLength);
                box.SelectionStart = selStart;
            }
            else if (selStart > 0)
            {
                box.Text = text.Remove(selStart - 1, 1);
                box.SelectionStart = selStart - 1;
            }

            box.Focus(FocusState.Programmatic);
            return;
        }

        string ins;
        int caret;
        int preselectAt = -1;

        if (selLength > 0 && cap.WrapSelection is not null)
        {
            
            var picked = text.Substring(selStart, selLength);
            ins = cap.WrapSelection.Replace("{0}", picked);
            caret = selStart + ins.Length;
        }
        else
        {
            ins = cap.Insert;

            
            if (selLength > 0)
            {
                text = text.Remove(selStart, selLength);
                box.Text = text;
            }

            caret = selStart + ins.Length - cap.CaretBack;

            
            if (cap.SelectAt > 0) preselectAt = selStart + cap.SelectAt;
        }

        box.Text = text.Insert(selStart, ins);

        
        if (preselectAt >= 0 && preselectAt < box.Text.Length)
        {
            box.SelectionStart = preselectAt;
            box.SelectionLength = 1;
        }
        else
        {
            box.SelectionStart = Math.Clamp(caret, 0, box.Text.Length);
            box.SelectionLength = 0;
        }

        box.Focus(FocusState.Programmatic);
    }

    

    private async void Basis_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = XamlRoot,
            Title = "分析依据",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            Content = BuildBasis(),
        };

        await dialog.ShowAsync();
    }

    private static UIElement BuildBasis()
    {
        var panel = new StackPanel { Spacing = 6, MaxWidth = 500 };

        panel.Children.Add(Line("下列结论由数值采样与数值微分得出，不是符号推导。"));

        panel.Children.Add(Section("零点"));
        panel.Children.Add(Line("在采样点上找符号变化，再用二分法精化到约 1e-12。"));
        panel.Children.Add(Line("曲线只是擦过 x 轴时（重根，例如 x²）符号不变，改由极值点判定。"));

        panel.Children.Add(Section("单调性与极值"));
        panel.Children.Add(Line("用中心差分求导，在导数变号处用三分法精化出极值点。"));
        panel.Children.Add(Line("单调区间的端点取精化后的极值点位置，不是原始采样点。"));

        panel.Children.Add(Section("奇偶性"));
        panel.Children.Add(Line("在关于原点对称的点上分别验证 f(-x) = f(x) 与 f(-x) = -f(x)，两侧都要有定义。"));
        panel.Children.Add(Line("定义域本身不对称时（例如 ln(x)）不给结论。"));

        panel.Children.Add(Section("周期性"));
        panel.Children.Add(Line("先列出候选周期 —— 常见的 2π、π、2 等，以及从零点间距、极值点间距反推出来的值 ——"));
        panel.Children.Add(Line("再在多个点上验证 f(x + T) = f(x)。只要有两点不符即否定。"));
        panel.Children.Add(Line("宁可漏报，不会误报。"));

        panel.Children.Add(Section("渐近线"));
        panel.Children.Add(Line("垂直：相邻采样点跨过 0 且量级远超典型值时判定；定义域边界处函数发散也计入。"));
        panel.Children.Add(Line("水平：在 x 从 10² 到 10⁶ 上取样，最后三点趋于同一个值才算数。"));

        panel.Children.Add(Section("取值上下界"));
        panel.Children.Add(Line("取显示范围内的采样极值，并用精化后的极值点校正。"));
        panel.Children.Add(Line("识别为周期函数时，改取一个周期内的上下界 —— 那才是数学意义上的值域。"));

        panel.Children.Add(Section("已知的边界"));
        panel.Children.Add(Line("·　结论只反映当前显示范围内的情况，范围外的行为不参与判断。"));
        panel.Children.Add(Line("·　极窄的特征（宽度小于采样步长）可能被跨过去，例如孤立的定义域空洞、很窄的尖峰。"));
        panel.Children.Add(Line("·　定义域边界的位置与开闭由采样估计，可能有约半个采样步长的偏差。"));
        panel.Children.Add(Line("·　不提供导数表达式、积分与化简，那需要符号计算。"));

        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = XamlRoot,
            Title = "关于函数图像",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            Content = BuildAbout(),
        };

        await dialog.ShowAsync();
    }

    private static UIElement BuildAbout()
    {
        var panel = new StackPanel { Spacing = 6, MaxWidth = 500 };

        panel.Children.Add(Section("输入"));
        panel.Children.Add(Line("输入含 x 的表达式即可，例如 sin(x)、x² − 3x + 1、2ˣ。可同时绘制多条曲线比较。"));
        panel.Children.Add(Line("乘方写 ^（如 x^3），上标字符 ² ³ 也可以；× ÷ − π √ 这些数学符号都能识别。"));
        panel.Children.Add(Line("相邻项之间可以省略乘号：2x、2π、(x+1)(x−1) 都按乘法处理。"));

        panel.Children.Add(Section("支持的函数与常数"));
        panel.Children.Add(Line("三角与反三角：sin cos tan sec csc cot asin acos atan（arcsin 等写法也可以）。"));
        panel.Children.Add(Line("双曲：sinh cosh tanh asinh acosh atanh。"));
        panel.Children.Add(Line("对数与指数：ln（自然对数）、lg 与 log(x)（常用对数）、log(x, b)（换底）、exp。"));
        panel.Children.Add(Line("幂与根：^、√、∛、pow(x, y)。其他：abs sign floor ceil round trunc min max mod hypot rad deg。"));
        panel.Children.Add(Line("常数：pi（π）、tau（τ）、e。"));

        panel.Children.Add(Section("视角"));
        panel.Children.Add(Line("滚轮缩放（以光标为中心），按住拖动平移，「重置视图」回到初始视角。"));
        panel.Children.Add(Line("三角函数可切换弧度制 / 角度制。"));

        panel.Children.Add(Section("函数分析"));
        panel.Children.Add(Line("给出的每一项都是函数本身的性质，与当前视图无关。"));
        panel.Children.Add(Line("标准形式与线性包裹（sin(x)、x²、ln(x)、sin(x)−6 这类）给出闭式结论；商式 f(x)/g(x) 在分母为一次式时给出定义域、零点与竖直渐近线。"));
        panel.Children.Add(Line("其余项目（值域、单调性、极值、拐点）需要求导后的闭式，未给出解析结论时不列出，也不以采样值代替。"));
        panel.Children.Add(Line("标题右侧的箭头可收起整栏，便于投屏讲解；收起后画布左上角出现展开入口。"));

        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private static TextBlock Line(string text) => new()
    {
        Text = text,
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 20,
    };

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 8, 0, 0),
    };
}
