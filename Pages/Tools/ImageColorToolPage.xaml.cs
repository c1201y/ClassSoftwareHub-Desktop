using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;

namespace ClassSoftwareHub.Desktop.Pages.Tools;







public sealed partial class ImageColorToolPage : Page
{
    
    
    private const int MaxSide = 120;
    
    private const int AlphaThreshold = 125;
    
    private const int TargetClusters = 8;
    
    private const double BaseLMin = 25, BaseLMax = 75, BaseSMin = 30, BaseSMax = 85;

    
    private static readonly (string Key, string Label, double Delta)[] Variants =
    {
        ("Light3", "亮色 3", 48),
        ("Light2", "亮色 2", 32),
        ("Light1", "亮色 1", 16),
        ("Base",   "基准色",  0),
        ("Dark1",  "暗色 1", -14),
        ("Dark2",  "暗色 2", -26),
        ("Dark3",  "暗色 3", -36),
    };

    private readonly List<(string Hex, FontIcon Check)> _checks = new();
    private bool _busy;

    
    private readonly bool _embedded;

    
    private double _swatchHeight = 60;

    public ImageColorToolPage() : this(false) { }

    
    
    
    
    
    
    
    
    
    
    
    
    public ImageColorToolPage(bool embedded)
    {
        InitializeComponent();
        _embedded = embedded;

        
        Core.WheelRelay.Attach(this, ContentScroll);

        if (embedded) Loaded += (_, _) => ApplyCompact();
    }

    
    public IntPtr HostWindowHandle { get; set; }

    
    public Action<bool>? ContentStateChanged { get; set; }

    
    private bool _compactApplied;

    private void ApplyCompact()
    {
        if (_compactApplied) return;
        _compactApplied = true;

        try
        {
            HeaderPanel.Visibility = Visibility.Collapsed;
            BodyGrid.Margin = new Thickness(16, 10, 16, 16);

            
            DropZone.MinHeight = 130;
            Preview.MaxWidth = 150;
            Preview.MaxHeight = 80;

            
            SwatchHost.ItemWidth = 120;
            SwatchHost.ItemHeight = 96;
            HslHost.ItemWidth = 108;
            _swatchHeight = 44;
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("image-color", "浮窗紧凑版式设置失败：" + ex.Message);
        }
    }


    

    private void Replace_Click(object sender, RoutedEventArgs e) => _ = PickAsync();

    private void Clear_Click(object sender, RoutedEventArgs e) => ResetAll();

    private void DropZone_Tapped(object sender, TappedRoutedEventArgs e) => _ = PickAsync();

    private async Task PickAsync()
    {
        if (_busy) return;
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.Thumbnail,
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            };
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" })
                picker.FileTypeFilter.Add(ext);

            
            
            var hwnd = HostWindowHandle != IntPtr.Zero
                ? HostWindowHandle
                : (App.MainWindow is { } main ? WinRT.Interop.WindowNative.GetWindowHandle(main) : IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
            {
                ShowError("无法打开文件选择框：请切换到主窗口后重试。");
                return;
            }

            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is not null) await LoadAsync(file);
        }
        catch (Exception ex)
        {
            ShowError("打开图片失败：" + ex.Message);
        }
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Copy;
        if (e.DragUIOverride is { } hint)
        {
            hint.Caption = "松开鼠标即提取配色";
            hint.IsCaptionVisible = true;
        }
        DropFrameAccent.Visibility = Visibility.Visible;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e) =>
        DropFrameAccent.Visibility = Visibility.Collapsed;

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropFrameAccent.Visibility = Visibility.Collapsed;
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.OfType<StorageFile>().FirstOrDefault() is { } file)
                await LoadAsync(file);
        }
        catch (Exception ex)
        {
            ShowError("读取拖入的文件失败：" + ex.Message);
        }
    }

    

    private async Task LoadAsync(StorageFile file)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);

            var srcW = decoder.OrientedPixelWidth;
            var srcH = decoder.OrientedPixelHeight;
            if (srcW == 0 || srcH == 0) { ShowError("无法读取图片尺寸，请更换格式。"); return; }

            var longest = Math.Max(srcW, srcH);
            var scale = Math.Min(1.0, MaxSide / (double)longest);
            var w = Math.Max(1u, (uint)R(srcW * scale));
            var h = Math.Max(1u, (uint)R(srcH * scale));

            var transform = new BitmapTransform
            {
                ScaledWidth = w,
                ScaledHeight = h,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };

            using var bmp = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);

            var bw = bmp.PixelWidth;
            var bh = bmp.PixelHeight;
            var buffer = new Windows.Storage.Streams.Buffer((uint)(bw * bh * 4));
            bmp.CopyToBuffer(buffer);

            byte[] bytes;
            using (var reader = DataReader.FromBuffer(buffer))
            {
                bytes = new byte[buffer.Length];
                reader.ReadBytes(bytes);
            }

            
            stream.Seek(0);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            Preview.Source = image;

            var pixels = new List<Rgb>(bytes.Length / 4);
            for (var i = 0; i + 3 < bytes.Length; i += 4)
            {
                if (bytes[i + 3] < AlphaThreshold) continue;      
                pixels.Add(new Rgb(bytes[i + 2], bytes[i + 1], bytes[i]));
            }

            ShowInfo($"原图 {srcW} × {srcH} · 采样 {bw} × {bh}");

            if (pixels.Count == 0)
            {
                HideResults();
                ShowError("该图片中没有可用的不透明像素（可能为全透明图片），请更换图片。");
                return;
            }

            var palette = ExtractPalette(pixels);
            if (palette is null)
            {
                HideResults();
                ShowError("未提取到可用配色，请更换图片。");
                return;
            }

            HideError();
            RenderPalette(palette);
        }
        catch (Exception ex)
        {
            HideResults();
            ShowError("图片处理失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    

    private void RenderPalette(Palette palette)
    {
        _checks.Clear();

        EmptyState.Visibility = Visibility.Collapsed;
        LoadedState.Visibility = Visibility.Visible;
        DropFrame.StrokeDashArray = null;            
        DropFrameAccent.StrokeDashArray = null;

        SeedChip.Background = new SolidColorBrush(palette.SeedColor);
        SeedText.Text = $"{palette.SeedHex} · {HslText(palette.SeedHsl)}";
        SeedCard.Visibility = Visibility.Visible;

        SwatchHost.Children.Clear();
        foreach (var variant in palette.Variants)
            SwatchHost.Children.Add(BuildSwatch(variant));

        HslHost.Children.Clear();
        foreach (var variant in palette.Variants)
            HslHost.Children.Add(BuildHslChip(variant));

        PaletteSection.Visibility = Visibility.Visible;
        HslSection.Visibility = Visibility.Visible;
        ActionRow.Visibility = Visibility.Visible;
        Toast.Text = "";

        ContentStateChanged?.Invoke(true);
    }

    private Button BuildSwatch(Variant variant)
    {
        var block = new Border
        {
            Height = _swatchHeight,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(ActualTheme == ElementTheme.Dark                        
                ? Color.FromArgb(0x1F, 255, 255, 255)
                : Color.FromArgb(0x14, 0, 0, 0)),
            Background = new SolidColorBrush(variant.Color),
        };

        var check = new FontIcon
        {
            Glyph = "\uE73E",
            FontSize = 20,
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        var blockHost = new Grid();
        blockHost.Children.Add(block);
        blockHost.Children.Add(check);

        var name = new TextBlock
        {
            Text = variant.Label,
            FontSize = 12,
            Opacity = 0.65,
        };
        var hex = new TextBlock
        {
            Text = variant.Hex,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontFamily = new FontFamily("Consolas"),
        };

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(blockHost);
        stack.Children.Add(name);
        stack.Children.Add(hex);

        var button = new Button
        {
            Padding = new Thickness(9),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = stack,
        };
        ToolTipService.SetToolTip(button, $"单击复制 {variant.Hex}");
        button.Click += (_, _) => CopyHex(variant.Hex);

        _checks.Add((variant.Hex, check));
        return button;
    }

    private Button BuildHslChip(Variant variant)
    {
        var text = HslShort(variant.Hsl);
        var button = new Button
        {
            Padding = new Thickness(6, 4, 6, 4),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                Opacity = 0.75,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        ToolTipService.SetToolTip(button, $"单击复制 {HslText(variant.Hsl)}");
        button.Click += (_, _) => CopyValue(HslText(variant.Hsl));
        return button;
    }

    private void ResetAll()
    {
        Preview.Source = null;
        EmptyState.Visibility = Visibility.Visible;
        LoadedState.Visibility = Visibility.Collapsed;
        ImageInfo.Text = "";
        DropFrame.StrokeDashArray = new DoubleCollection { 4, 3 };
        DropFrameAccent.StrokeDashArray = new DoubleCollection { 4, 3 };

        SwatchHost.Children.Clear();
        HslHost.Children.Clear();
        _checks.Clear();

        SeedCard.Visibility = Visibility.Collapsed;
        PaletteSection.Visibility = Visibility.Collapsed;
        HslSection.Visibility = Visibility.Collapsed;
        ActionRow.Visibility = Visibility.Collapsed;
        Toast.Text = "";
        HideError();

        ContentStateChanged?.Invoke(false);
    }

    private void HideResults()
    {
        SeedCard.Visibility = Visibility.Collapsed;
        PaletteSection.Visibility = Visibility.Collapsed;
        HslSection.Visibility = Visibility.Collapsed;
        ActionRow.Visibility = Visibility.Collapsed;

        ContentStateChanged?.Invoke(false);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;

    private void ShowInfo(string message)
    {
        HideError();
        Toast.Text = message;
    }

    private void CopyHex(string hex)
    {
        CopyValue(hex);
        foreach (var (value, check) in _checks)
            check.Visibility = value == hex ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopyValue(string value)
    {
        try
        {
            var dp = new DataPackage();
            dp.SetText(value);
            Clipboard.SetContent(dp);
            Toast.Text = "已复制 " + value;
        }
        catch
        {
            Toast.Text = "复制失败（剪贴板被占用）";
        }
    }

    

    private readonly record struct Rgb(int R, int G, int B);

    private readonly record struct Hsl(int H, int S, int L);

    private sealed record Variant(string Key, string Label, double Delta, Hsl Hsl, string Hex, Color Color);

    private sealed record Palette(
        string SeedHex, Hsl SeedHsl, Color SeedColor, IReadOnlyList<Variant> Variants);

    
    private static Palette? ExtractPalette(List<Rgb> pixels)
    {
        if (pixels.Count == 0) return null;

        var total = pixels.Count;
        var boxes = MedianCut(pixels, TargetClusters);

        
        var candidates = new List<(Rgb Rgb, Hsl Hsl, double Score)>();
        foreach (var box in boxes)
        {
            var rgb = AverageColor(box);
            var hsl = RgbToHsl(rgb.R, rgb.G, rgb.B);
            var s = hsl.S / 100.0;
            var l = hsl.L / 100.0;
            var frequency = box.Count / (double)total;

            var satWeight = s < 0.1 ? 0.15 : 1.0;                       
            var lightWeight = l < 0.12 || l > 0.88 ? 0.25 : 1.0;        
            var midSatBonus = 1 + 0.6 * (1 - Math.Min(1, Math.Abs(s - 0.55) / 0.45));

            candidates.Add((rgb, hsl, frequency * satWeight * lightWeight * midSatBonus));
        }
        if (candidates.Count == 0) return null;

        var seed = candidates[0];
        foreach (var item in candidates)
            if (item.Score > seed.Score) seed = item;

        
        var baseHsl = new Hsl(
            seed.Hsl.H,
            (int)Clamp(seed.Hsl.S, BaseSMin, BaseSMax),
            (int)Clamp(seed.Hsl.L, BaseLMin, BaseLMax));

        var variants = new List<Variant>();
        foreach (var (key, label, delta) in Variants)
        {
            var l = Clamp(baseHsl.L + delta, 0, 100);
            
            var s = Clamp(baseHsl.S - delta * 0.4, 0, 100);
            var hsl = new Hsl(baseHsl.H, R(s), R(l));
            var rgb = HslToRgb(hsl.H, hsl.S, hsl.L);
            variants.Add(new Variant(key, label, delta, hsl, HexOf(rgb), ToColor(rgb)));
        }

        return new Palette(
            HexOf(seed.Rgb), seed.Hsl, ToColor(seed.Rgb), variants);
    }

    
    
    
    
    private static List<List<Rgb>> MedianCut(List<Rgb> pixels, int target)
    {
        var boxes = new List<List<Rgb>> { pixels };
        while (boxes.Count < target)
        {
            var pick = -1;
            var maxRange = -1;
            for (var i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Count < 2) continue;
                var range = BoxRange(boxes[i]).Range;
                if (range > maxRange) { maxRange = range; pick = i; }
            }
            if (pick < 0) break;

            var box = boxes[pick];
            boxes.RemoveAt(pick);
            var channel = BoxRange(box).Channel;
            box.Sort((a, b) => Channel(a, channel) - Channel(b, channel));
            var mid = box.Count / 2;
            boxes.Add(box.GetRange(0, mid));
            boxes.Add(box.GetRange(mid, box.Count - mid));
        }
        return boxes.Where(b => b.Count > 0).ToList();
    }

    private static (int Range, int Channel) BoxRange(List<Rgb> box)
    {
        int rMin = 255, rMax = 0, gMin = 255, gMax = 0, bMin = 255, bMax = 0;
        foreach (var (r, g, b) in box)
        {
            if (r < rMin) rMin = r;
            if (r > rMax) rMax = r;
            if (g < gMin) gMin = g;
            if (g > gMax) gMax = g;
            if (b < bMin) bMin = b;
            if (b > bMax) bMax = b;
        }
        var rr = rMax - rMin;
        var gr = gMax - gMin;
        var br = bMax - bMin;
        var range = Math.Max(rr, Math.Max(gr, br));
        var channel = range == rr ? 0 : range == gr ? 1 : 2;
        return (range, channel);
    }

    private static int Channel(Rgb c, int index) => index switch
    {
        0 => c.R,
        1 => c.G,
        _ => c.B,
    };

    private static Rgb AverageColor(List<Rgb> box)
    {
        long r = 0, g = 0, b = 0;
        foreach (var (pr, pg, pb) in box) { r += pr; g += pg; b += pb; }
        var n = box.Count > 0 ? box.Count : 1;
        return new Rgb(R(r / (double)n), R(g / (double)n), R(b / (double)n));
    }

    
    private static Hsl RgbToHsl(int r, int g, int b)
    {
        var rn = r / 255.0;
        var gn = g / 255.0;
        var bn = b / 255.0;
        var max = Math.Max(rn, Math.Max(gn, bn));
        var min = Math.Min(rn, Math.Min(gn, bn));
        var l = (max + min) / 2;
        double h = 0, s = 0;
        if (max != min)
        {
            var d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == rn) h = (gn - bn) / d + (gn < bn ? 6 : 0);
            else if (max == gn) h = (bn - rn) / d + 2;
            else h = (rn - gn) / d + 4;
            h *= 60;
        }
        return new Hsl(R(h), R(s * 100), R(l * 100));
    }

    
    private static Rgb HslToRgb(double h, double s, double l)
    {
        var hn = ((h % 360) + 360) % 360 / 360.0;
        var sn = Clamp(s, 0, 100) / 100.0;
        var ln = Clamp(l, 0, 100) / 100.0;
        if (sn == 0)
        {
            var v = R(ln * 255);
            return new Rgb(v, v, v);
        }
        var q = ln < 0.5 ? ln * (1 + sn) : ln + sn - ln * sn;
        var p = 2 * ln - q;

        double Hue(double t)
        {
            var tt = t;
            if (tt < 0) tt += 1;
            if (tt > 1) tt -= 1;
            if (tt < 1.0 / 6) return p + (q - p) * 6 * tt;
            if (tt < 1.0 / 2) return q;
            if (tt < 2.0 / 3) return p + (q - p) * (2.0 / 3 - tt) * 6;
            return p;
        }

        return new Rgb(
            R(Hue(hn + 1.0 / 3) * 255),
            R(Hue(hn) * 255),
            R(Hue(hn - 1.0 / 3) * 255));
    }

    

    
    private static int R(double value) => (int)Math.Floor(value + 0.5);

    private static double Clamp(double value, double min, double max) =>
        Math.Min(max, Math.Max(min, value));

    private static string HexOf(Rgb rgb) =>
        $"#{Hex(rgb.R)}{Hex(rgb.G)}{Hex(rgb.B)}";

    private static string Hex(int value) =>
        Math.Clamp(value, 0, 255).ToString("X2");

    private static Color ToColor(Rgb rgb) => Color.FromArgb(
        255, (byte)Math.Clamp(rgb.R, 0, 255), (byte)Math.Clamp(rgb.G, 0, 255), (byte)Math.Clamp(rgb.B, 0, 255));

    private static string HslText(Hsl hsl) => $"hsl({hsl.H}, {hsl.S}%, {hsl.L}%)";

    private static string HslShort(Hsl hsl) => $"{hsl.H}° {hsl.S}% {hsl.L}%";
}
