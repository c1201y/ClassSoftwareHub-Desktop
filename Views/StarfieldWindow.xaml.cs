using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Views;

public sealed partial class StarfieldWindow : Window
{
    private sealed class StarPoint
    {
        public Ellipse Shape { get; init; } = null!;
        public double X { get; init; }
        public double Y { get; init; }
        public double Z { get; init; }
        public double Brightness { get; init; }
    }

    private static StarfieldWindow? _instance;

    private readonly List<StarPoint> _stars = new();
    private readonly Random _random = new();

    private AppWindow? _appWindow;
    private DispatcherTimer? _renderTimer;
    private DispatcherTimer? _meteorTimer;
    private DispatcherTimer? _clickTimer;
    private DispatcherTimer? _shakeTimer;

    private double _yaw;
    private double _pitch;
    private double _zoom = 1.0;
    private double _lastX;
    private double _lastY;
    private bool _dragging;
    private bool _dialogOpen;
    private bool _cometBusy;
    private int _planetClicks;

    private StarfieldWindow()
    {
        InitializeComponent();
        Title = "ClassSoftwareHub · 星海";

        ConfigureWindow();
        Root.Loaded += OnLoaded;
        Root.SizeChanged += (_, _) => RenderStars();
        Closed += (_, _) => Teardown();
    }

    public static void Show()
    {
        try
        {
            _instance ??= new StarfieldWindow();
            _instance.Activate();
        }
        catch (Exception ex)
        {
            AppLog.Warning("starfield", "打开星海窗口失败：" + ex.Message);
        }
    }

    private void ConfigureWindow()
    {
        try
        {
            var handle = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(handle));
            _appWindow.Title = Title;

            var bar = _appWindow.TitleBar;
            bar.BackgroundColor = Color.FromArgb(255, 3, 5, 12);
            bar.ForegroundColor = Colors.White;
            bar.ButtonBackgroundColor = Color.FromArgb(255, 3, 5, 12);
            bar.ButtonInactiveBackgroundColor = Color.FromArgb(255, 3, 5, 12);
            bar.ButtonForegroundColor = Colors.White;
            bar.ButtonInactiveForegroundColor = Colors.LightGray;
        }
        catch (Exception ex)
        {
            AppLog.Warn("starfield", "星海窗口外观初始化失败：" + ex.Message);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_renderTimer is not null) return;

        ApplyWindowSize();
        BuildStars();
        RenderStars();

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _renderTimer.Tick += (_, _) => RenderStars();
        _renderTimer.Start();

        _meteorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3200) };
        _meteorTimer.Tick += (_, _) =>
        {
            _meteorTimer!.Stop();
            SpawnMeteor();
            ScheduleMeteor();
        };
        _meteorTimer.Start();

        _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(560) };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer!.Stop();
            if (_planetClicks <= 0 || _planetClicks >= 5) return;
            _planetClicks = 0;
            _ = ShowVersionAsync();
        };
    }

    private void ApplyWindowSize()
    {
        if (_appWindow is null) return;

        try
        {
            var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
            _appWindow.ResizeClient(new SizeInt32(
                (int)Math.Round(980 * scale),
                (int)Math.Round(680 * scale)));

            var work = DisplayArea.Primary.WorkArea;
            var size = _appWindow.Size;
            _appWindow.Move(new PointInt32(
                work.X + Math.Max(0, (work.Width - size.Width) / 2),
                work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
        }
        catch (Exception ex)
        {
            AppLog.Warn("starfield", "星海窗口尺寸设置失败：" + ex.Message);
        }
    }

    private void Teardown()
    {
        _renderTimer?.Stop();
        _meteorTimer?.Stop();
        _clickTimer?.Stop();
        _shakeTimer?.Stop();
        SceneShift.X = 0;
        SceneShift.Y = 0;
        _instance = null;
    }

    private void BuildStars()
    {
        if (_stars.Count > 0) return;

        var palette = new[]
        {
            Color.FromArgb(255, 255, 255, 255),
            Color.FromArgb(255, 198, 220, 255),
            Color.FromArgb(255, 157, 191, 255),
            Color.FromArgb(255, 255, 232, 190),
            Color.FromArgb(255, 188, 238, 255),
        };

        for (var i = 0; i < 420; i++)
        {
            var depth = _random.NextDouble() * 2.0 - 1.0;
            var ring = Math.Sqrt(Math.Max(0.0, 1.0 - depth * depth));
            var angle = _random.NextDouble() * Math.PI * 2.0;
            var distance = 0.7 + _random.NextDouble() * 1.25;

            var size = 1.0 + _random.NextDouble() * 1.8;
            if (i < 18) size = 4.2 + _random.NextDouble() * 1.6;

            var shape = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(palette[_random.Next(palette.Length)]),
                Opacity = 0.9,
                IsHitTestVisible = false,
            };

            StarCanvas.Children.Add(shape);
            _stars.Add(new StarPoint
            {
                Shape = shape,
                X = ring * Math.Cos(angle) * distance,
                Y = ring * Math.Sin(angle) * distance,
                Z = depth * distance,
                Brightness = 0.75 + _random.NextDouble() * 0.25,
            });
        }
    }

    private void RenderStars()
    {
        var width = StarCanvas.ActualWidth;
        var height = StarCanvas.ActualHeight;
        if (width < 2 || height < 2) return;

        var cosYaw = Math.Cos(_yaw);
        var sinYaw = Math.Sin(_yaw);
        var cosPitch = Math.Cos(_pitch);
        var sinPitch = Math.Sin(_pitch);
        var focal = Math.Min(width, height) * 1.16 * _zoom;

        foreach (var star in _stars)
        {
            var x1 = star.X * cosYaw - star.Z * sinYaw;
            var z1 = star.X * sinYaw + star.Z * cosYaw;
            var y1 = star.Y * cosPitch - z1 * sinPitch;
            var z2 = star.Y * sinPitch + z1 * cosPitch;
            var depth = z2 + 3.0;

            if (depth < 0.35)
            {
                star.Shape.Opacity = 0;
                continue;
            }

            var spanX = x1 / depth * focal;
            var spanY = y1 / depth * focal;
            var px = width / 2.0 + spanX;
            var py = height / 2.0 + spanY;

            if (px < -20 || px > width + 20 || py < -20 || py > height + 20)
            {
                star.Shape.Opacity = 0;
                continue;
            }

            var scale = Math.Clamp(2.6 * _zoom / depth, 0.75, 4.4);
            star.Shape.Width = scale;
            star.Shape.Height = scale;
            star.Shape.Opacity = Math.Clamp((4.2 - depth) * 0.58, 0.35, 1.0) * star.Brightness;
            Canvas.SetLeft(star.Shape, px - scale / 2.0);
            Canvas.SetTop(star.Shape, py - scale / 2.0);
        }
    }

    private void Root_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(Root).Properties.MouseWheelDelta;
        _zoom = Math.Clamp(_zoom * (delta > 0 ? 1.12 : 0.89), 0.55, 2.5);
        e.Handled = true;
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (!point.Properties.IsLeftButtonPressed) return;

        _dragging = true;
        _lastX = point.Position.X;
        _lastY = point.Position.Y;
        Root.CapturePointer(e.Pointer);
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        var point = e.GetCurrentPoint(Root);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _dragging = false;
            return;
        }

        var dx = point.Position.X - _lastX;
        var dy = point.Position.Y - _lastY;
        _lastX = point.Position.X;
        _lastY = point.Position.Y;

        _yaw += dx * 0.006;
        _pitch = Math.Clamp(_pitch - dy * 0.0045, -1.15, 1.15);
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        try { Root.ReleasePointerCapture(e.Pointer); } catch { }
    }

    private void Planet_Pressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        _planetClicks++;

        if (_planetClicks >= 5)
        {
            _planetClicks = 0;
            _clickTimer?.Stop();
            TriggerComet();
            return;
        }

        _clickTimer?.Stop();
        _clickTimer?.Start();
    }

    private async System.Threading.Tasks.Task ShowVersionAsync()
    {
        if (_dialogOpen || Root.XamlRoot is null) return;

        _dialogOpen = true;
        try
        {
            var text = new TextBlock
            {
                Text = $"{ShellConfig.AppName}\n版本 {ShellConfig.VersionPrefix}{ShellConfig.DisplayVersion}\n\n班级电教委员的装机与课堂工具集",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                LineHeight = 24,
            };

            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "ClassSoftwareHub",
                Content = text,
                CloseButtonText = "确定",
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warning("starfield", "版本信息弹窗失败：" + ex.Message);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void ScheduleMeteor()
    {
        if (_meteorTimer is null) return;
        _meteorTimer.Interval = TimeSpan.FromMilliseconds(2600 + _random.Next(3600));
        _meteorTimer.Start();
    }

    private void SpawnMeteor()
    {
        var width = MeteorCanvas.ActualWidth;
        var height = MeteorCanvas.ActualHeight;
        if (width < 2 || height < 2) return;

        var fromX = width * (0.72 + _random.NextDouble() * 0.26);
        var fromY = height * (0.04 + _random.NextDouble() * 0.18);
        var toX = fromX - width * (0.18 + _random.NextDouble() * 0.22);
        var toY = fromY + height * (0.12 + _random.NextDouble() * 0.16);

        var line = new Line
        {
            X1 = fromX,
            Y1 = fromY,
            X2 = fromX,
            Y2 = fromY,
            StrokeThickness = 2.2,
            Stroke = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0, 255, 255, 255), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(235, 210, 230, 255), Offset = 0.72 },
                    new GradientStop { Color = Color.FromArgb(255, 255, 255, 255), Offset = 1 },
                },
            },
            Opacity = 0.95,
            IsHitTestVisible = false,
        };

        var head = new Ellipse
        {
            Width = 4,
            Height = 4,
            Fill = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            IsHitTestVisible = false,
        };

        MeteorCanvas.Children.Add(line);
        MeteorCanvas.Children.Add(head);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        var progress = 0.0;
        timer.Tick += (_, _) =>
        {
            progress += 0.035;
            if (progress >= 1.0)
            {
                timer.Stop();
                MeteorCanvas.Children.Remove(line);
                MeteorCanvas.Children.Remove(head);
                return;
            }

            var tipX = fromX + (toX - fromX) * progress;
            var tipY = fromY + (toY - fromY) * progress;
            var tailX = tipX - (toX - fromX) * 0.18;
            var tailY = tipY - (toY - fromY) * 0.18;

            line.X1 = tailX;
            line.Y1 = tailY;
            line.X2 = tipX;
            line.Y2 = tipY;
            Canvas.SetLeft(head, tipX - head.Width / 2.0);
            Canvas.SetTop(head, tipY - head.Height / 2.0);
        };
        timer.Start();
    }

    private void TriggerComet()
    {
        if (_cometBusy) return;

        var width = MeteorCanvas.ActualWidth;
        var height = MeteorCanvas.ActualHeight;
        if (width < 2 || height < 2) return;

        _cometBusy = true;
        var fromX = width * 0.94;
        var fromY = height * 0.14;
        var toX = width / 2.0;
        var toY = height / 2.0;

        var line = new Line
        {
            X1 = fromX,
            Y1 = fromY,
            X2 = fromX,
            Y2 = fromY,
            StrokeThickness = 3.6,
            Stroke = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0, 255, 255, 255), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(220, 214, 232, 255), Offset = 0.6 },
                    new GradientStop { Color = Color.FromArgb(255, 255, 250, 235), Offset = 1 },
                },
            },
            Opacity = 1,
            IsHitTestVisible = false,
        };

        var head = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = new SolidColorBrush(Color.FromArgb(255, 255, 248, 224)),
            IsHitTestVisible = false,
        };

        MeteorCanvas.Children.Add(line);
        MeteorCanvas.Children.Add(head);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        var progress = 0.0;
        timer.Tick += (_, _) =>
        {
            progress += 0.032;
            if (progress >= 1.0)
            {
                timer.Stop();
                MeteorCanvas.Children.Remove(line);
                MeteorCanvas.Children.Remove(head);
                FlashPlanet(toX, toY);
                ShakeScene();
                _cometBusy = false;
                return;
            }

            var tipX = fromX + (toX - fromX) * progress;
            var tipY = fromY + (toY - fromY) * progress;
            var tailX = tipX - (toX - fromX) * 0.2;
            var tailY = tipY - (toY - fromY) * 0.2;

            line.X1 = tailX;
            line.Y1 = tailY;
            line.X2 = tipX;
            line.Y2 = tipY;
            Canvas.SetLeft(head, tipX - head.Width / 2.0);
            Canvas.SetTop(head, tipY - head.Height / 2.0);
        };
        timer.Start();
    }

    private void FlashPlanet(double x, double y)
    {
        var flash = new Ellipse
        {
            Width = 24,
            Height = 24,
            Fill = new SolidColorBrush(Color.FromArgb(255, 255, 244, 208)),
            IsHitTestVisible = false,
            Opacity = 0.95,
        };
        Canvas.SetLeft(flash, x - 12);
        Canvas.SetTop(flash, y - 12);
        MeteorCanvas.Children.Add(flash);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            flash.Width += 10;
            flash.Height += 10;
            flash.Opacity -= 0.055;
            Canvas.SetLeft(flash, x - flash.Width / 2.0);
            Canvas.SetTop(flash, y - flash.Height / 2.0);

            if (flash.Opacity <= 0)
            {
                timer.Stop();
                MeteorCanvas.Children.Remove(flash);
            }
        };
        timer.Start();
    }

    private void ShakeScene()
    {
        _shakeTimer?.Stop();

        var elapsed = 0;
        _shakeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _shakeTimer.Tick += (_, _) =>
        {
            elapsed += 16;
            if (elapsed >= 280)
            {
                SceneShift.X = 0;
                SceneShift.Y = 0;
                _shakeTimer!.Stop();
                return;
            }

            var amplitude = 3.4 * (1.0 - elapsed / 280.0);
            SceneShift.X = (_random.NextDouble() * 2.0 - 1.0) * amplitude;
            SceneShift.Y = (_random.NextDouble() * 2.0 - 1.0) * amplitude;
        };
        _shakeTimer.Start();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
