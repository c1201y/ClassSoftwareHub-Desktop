using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// 磁盘空间可视化：squarified treemap（见 <see cref="TreemapLayout"/>）+ 逐层下钻。
///
/// <para>
/// ⛔ 扫描一律在后台线程跑，并且**可取消** —— 全盘几十万文件，扫到一半又不想等了是很正常的操作。
/// ⛔ 树图上的方块最多画 90 个，其余并成「其他」：再多既点不中也刷不动。
/// </para>
/// <para>
/// ⭐ 下钻不再重新扫描（2026-11-08 改）：一次扫描就把整棵目录树数完留在内存里，
/// 点方块、点面包屑、返回上一级全是查树，0 读取。
/// 原来的做法是每进一层就把那个子树重新数一遍 —— 越往下越慢，因为每层都在重扫。
/// </para>
/// </summary>
public sealed partial class DiskMapPage : Page
{
    /// <summary>一次最多画多少块（超过就并成「其他」）。</summary>
    private const int MaxTiles = 90;

    /// <summary>树图上的一块。目录带节点（可继续下钻），文件与「其他」没有。</summary>
    private sealed record TileItem(DiskEntry Entry, DiskNode? Node);

    private CancellationTokenSource? _cts;
    private DiskNode? _root;        // 本次扫描的树根
    private DiskNode? _current;     // 当前停留的目录
    private List<TileItem> _items = new();
    private string _scanPath = "";
    private bool _initialized;

    public DiskMapPage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            FillDrives();
        };

        // 离开页面就把扫描停掉，别让后台线程继续啃硬盘
        Unloaded += (_, _) => _cts?.Cancel();
    }

    // ══════════════════════════════════════════════════════
    //  启动
    // ══════════════════════════════════════════════════════

    private void FillDrives()
    {
        var drives = DiskUsage.FixedDrives();
        if (drives.Count == 0)
        {
            StatusText.Text = "没有可扫描的磁盘。";
            return;
        }

        foreach (var drive in drives)
        {
            var total = drive.TotalSize > 0 ? DiskUsage.FormatBytes(drive.TotalSize) : "未知";
            var free = drive.AvailableFreeSpace > 0 ? DiskUsage.FormatBytes(drive.AvailableFreeSpace) : "未知";

            DriveBox.Items.Add(new ComboBoxItem
            {
                Content = $"{drive.Name.TrimEnd('\\')}　可用 {free} / 共 {total}",
                Tag = drive.RootDirectory.FullName,
            });
        }

        // 默认从**当前用户的目录**开始：一上来就扫整个 C:\ 要等很久，
        // 而绝大多数人想找的就是自己目录里的大文件
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var homeRoot = Path.GetPathRoot(home) ?? "";
        var index = 0;

        for (var i = 0; i < DriveBox.Items.Count; i++)
        {
            if (DriveBox.Items[i] is ComboBoxItem item
                && string.Equals(item.Tag as string, homeRoot, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        DriveBox.SelectedIndex = index;

        var start = Directory.Exists(home) ? home : drives[0].RootDirectory.FullName;
        _ = ScanAsync(start);
    }

    private void DriveBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (DriveBox.SelectedItem is not ComboBoxItem item || item.Tag is not string root) return;

        _ = ScanAsync(root);
    }

    // ══════════════════════════════════════════════════════
    //  扫描（只在这里读磁盘）
    // ══════════════════════════════════════════════════════

    private async Task ScanAsync(string path)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _scanPath = path;
        SetBusy(true);
        BusyRing.IsActive = true;
        ScanButton.Content = "取消";
        StatusText.Text = "正在统计…（大目录可能要十几秒）";
        EmptyText.Visibility = Visibility.Collapsed;
        MapCanvas.Children.Clear();
        TopList.Children.Clear();
        CrumbBar.Children.Clear();
        _items = new List<TileItem>();

        try
        {
            var result = await DiskUsage.ScanAsync(path, token, (dir, bytes, files) =>
            {
                // 进度来自后台线程 ⇒ 回 UI 线程再改文字
                if (!DispatcherQueue.HasThreadAccess)
                {
                    DispatcherQueue.TryEnqueue(() => ShowProgress(dir, bytes, files));
                    return;
                }

                ShowProgress(dir, bytes, files);
            });

            if (token.IsCancellationRequested) return;

            _root = result.RootNode;
            if (_root is null)
            {
                StatusText.Text = "这个位置没有可统计的内容。";
                return;
            }

            _current = _root;
            UpdateCrumb();
            RenderNode(_root, result);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消。";
        }
        catch (Exception ex)
        {
            AppLog.Exception("diskmap", ex, "扫描目录占用");
            StatusText.Text = "读取这个位置失败：" + ex.Message;
            EmptyText.Text = "读取失败：可能没有访问权限。\r\n可以选择别的目录，或用管理员身份运行。";
            EmptyText.Visibility = Visibility.Visible;
        }
        finally
        {
            SetBusy(false);
            BusyRing.IsActive = false;
            ScanButton.Content = "重新扫描";
        }
    }

    /// <summary>扫描中的进度：当前正在数哪个目录、已经数到多少。</summary>
    private void ShowProgress(string dir, long bytes, int files)
    {
        BusyRing.IsActive = true;
        StatusText.Text = $"正在统计 {DiskUsage.FormatBytes(bytes)}（{files:N0} 个文件）　{Shorten(dir)}";
    }

    private static string Shorten(string path)
    {
        const int max = 64;
        if (path.Length <= max) return path;
        return "…" + path[^max..];
    }

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        // 扫描中再点一次 = 取消
        if (_cts is { IsCancellationRequested: false } && BusyRing.IsActive)
        {
            _cts.Cancel();
            StatusText.Text = "正在停止…";
            return;
        }

        if (_scanPath.Length > 0) _ = ScanAsync(_scanPath);
    }

    private async void Pick_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
            };

            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            _ = ScanAsync(folder.Path);
        }
        catch (Exception ex)
        {
            AppLog.Exception("diskmap", ex, "选择文件夹");
            StatusText.Text = "无法打开文件夹选择器。";
        }
    }

    // ══════════════════════════════════════════════════════
    //  下钻（只查树，不读磁盘）
    // ══════════════════════════════════════════════════════

    /// <summary>进入下一层。数据已在树里，纯内存操作。</summary>
    private void NavigateTo(DiskNode node)
    {
        _current = node;
        _scanPath = node.FullPath;
        UpdateCrumb();
        RenderNode(node);
    }

    // ══════════════════════════════════════════════════════
    //  渲染
    // ══════════════════════════════════════════════════════

    /// <param name="scanned">刚扫描完时传结果（用来显示耗时与跳过数），下钻时传 null。</param>
    private void RenderNode(DiskNode node, DiskScanResult? scanned = null)
    {
        if (scanned is not null)
        {
            var skipped = scanned.SkippedDirectories > 0
                ? $"，{scanned.SkippedDirectories} 个目录无权限已跳过"
                : "";
            var cut = scanned.Truncated ? "，已按时间上限停止统计" : "";

            StatusText.Text =
                $"{DiskUsage.FormatBytes(scanned.TotalBytes)}　·　{scanned.FileCount:N0} 个文件" +
                $"　·　{scanned.Elapsed.TotalSeconds:0.0} 秒{skipped}{cut}";
        }
        else
        {
            StatusText.Text =
                $"{DiskUsage.FormatBytes(node.Bytes)}　·　{node.FileCount:N0} 个文件　·　本次统计的结果，未重新读取";
        }

        // 目录项要能点进下一层，所以先按路径把节点对回来，避免每次都翻一遍 Children
        var byPath = new Dictionary<string, DiskNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in node.Children) byPath[child.FullPath] = child;

        _items = DiskUsage.ToEntries(node)
            .Select(entry => new TileItem(
                entry,
                entry.IsDirectory && byPath.TryGetValue(entry.FullPath, out var child) ? child : null))
            .ToList();

        if (_items.Count == 0)
        {
            EmptyText.Text = "这个位置没有可统计的内容（空目录，或全部没有访问权限）。";
            EmptyText.Visibility = Visibility.Visible;
            MapCanvas.Children.Clear();
            TopList.Children.Clear();
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;
        RenderTopList(node);
        DrawTreemap();
    }

    private void DrawTreemap()
    {
        MapCanvas.Children.Clear();

        var width = MapCanvas.ActualWidth;
        var height = MapCanvas.ActualHeight;
        if (width < 20 || height < 20) return;
        if (_items.Count == 0) return;

        // 超过 MaxTiles 就并成「其他」：再多既点不中也刷不动
        var items = _items.ToList();
        var others = new List<TileItem>();

        if (items.Count > MaxTiles)
        {
            others = items.Skip(MaxTiles - 1).ToList();
            items = items.Take(MaxTiles - 1).ToList();
        }

        var weights = items.Select(i => (double)i.Entry.Bytes).ToList();
        if (others.Count > 0)
            weights.Add(others.Sum(o => (double)o.Entry.Bytes));

        var tiles = TreemapLayout.Squarify(weights, 0, 0, width - 4, height - 4);

        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            if (tile.Width < 3 || tile.Height < 3) continue;

            var isOther = i == items.Count;
            var item = isOther ? null : items[i];

            var border = BuildTile(tile, item, isOther, others);
            MapCanvas.Children.Add(border);
        }
    }

    private Border BuildTile(TreemapLayout.Tile tile, TileItem? item, bool isOther, List<TileItem> others)
    {
        var seed = item?.Entry.FullPath ?? "其他";
        var color = TileColor(Math.Abs(seed.GetHashCode()) % 360);

        var border = new Border
        {
            Width = Math.Max(2, tile.Width - 2),
            Height = Math.Max(2, tile.Height - 2),
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(color),
        };

        Canvas.SetLeft(border, tile.X + 1);
        Canvas.SetTop(border, tile.Y + 1);

        var entry = item?.Entry;
        var name = isOther ? $"其他 {others.Count} 项" : entry!.Name;
        var bytes = isOther ? others.Sum(o => o.Entry.Bytes) : entry!.Bytes;

        // 方块太小就只上色，不塞文字（塞了也是被裁一半的残字）
        if (tile.Width > 62 && tile.Height > 30)
        {
            var panel = new StackPanel { Padding = new Thickness(7, 5, 7, 5), Spacing = 1 };

            panel.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 12,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            });

            if (tile.Height > 46)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = DiskUsage.FormatBytes(bytes),
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 1,
                });
            }

            border.Child = panel;
        }

        ToolTipService.SetToolTip(border, isOther
            ? $"其余 {others.Count} 项，合计 {DiskUsage.FormatBytes(bytes)}"
            : $"{entry!.FullPath}\r\n{DiskUsage.FormatBytes(bytes)}　{entry.FileCount:N0} 个文件" +
              (item?.Node is not null ? "\r\n单击进入下一层" : ""));

        if (item?.Node is { } node)
        {
            border.PointerPressed += (_, args) =>
            {
                args.Handled = true;
                NavigateTo(node);
            };

            border.PointerEntered += (_, _) => border.Opacity = 0.82;
            border.PointerExited += (_, _) => border.Opacity = 1;
        }

        return border;
    }

    /// <summary>按色相给方块上色：同一目录下颜色各不相同，深浅统一，深色主题上也压得住。</summary>
    private static Color TileColor(int hue)
    {
        var h = hue % 360;
        const double s = 0.42;
        const double v = 0.62;

        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        var m = v - c;

        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };

        return Color.FromArgb(0xE6,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private void RenderTopList(DiskNode node)
    {
        TopList.Children.Clear();
        if (node.Bytes <= 0) return;

        foreach (var entry in _items.Take(5))
        {
            var percent = entry.Entry.Bytes * 100.0 / node.Bytes;

            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = entry.Entry.Name,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Opacity = 0.85,
            });

            var right = new TextBlock
            {
                Text = $"{DiskUsage.FormatBytes(entry.Entry.Bytes)}　{percent:0.0}%",
                FontSize = 12,
                Opacity = 0.55,
            };
            Grid.SetColumn(right, 1);
            row.Children.Add(right);

            TopList.Children.Add(row);
        }
    }

    // ══════════════════════════════════════════════════════
    //  面包屑
    // ══════════════════════════════════════════════════════

    private void UpdateCrumb()
    {
        CrumbBar.Children.Clear();

        if (_scanPath.Length == 0) return;

        var root = Path.GetPathRoot(_scanPath) ?? _scanPath;
        var rest = _scanPath.Length > root.Length ? _scanPath[root.Length..] : "";
        var parts = rest.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        var accumulated = root;
        CrumbBar.Children.Add(CrumbButton(root.TrimEnd('\\'), root, parts.Length == 0));

        foreach (var part in parts)
        {
            accumulated = Path.Combine(accumulated, part);
            CrumbBar.Children.Add(Chevron());
            CrumbBar.Children.Add(CrumbButton(part, accumulated, part == parts[^1]));
        }
    }

    private Button CrumbButton(string text, string path, bool isCurrent)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                FontWeight = isCurrent
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
            },
            Padding = new Thickness(6, 2, 6, 2),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Tag = path,
        };

        if (!isCurrent)
        {
            button.Click += (sender, args) =>
            {
                if (((Button)sender).Tag is not string target) return;

                // 优先在已有树里找：上钻同样是 0 读取
                var node = _root is null ? null : DiskUsage.Find(_root, target);
                if (node is not null)
                {
                    NavigateTo(node);
                    return;
                }

                // 树里没有（扫描被截断、或中途换过根），只能真扫
                _ = ScanAsync(target);
            };
        }

        return button;
    }

    private static TextBlock Chevron() => new()
    {
        Text = "›",
        FontSize = 12.5,
        Opacity = 0.4,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void MapCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 窗口尺寸变了要重画：树图是按像素铺的，不重排就会留白或溢出
        if (_items.Count > 0 && !BusyRing.IsActive) DrawTreemap();
    }

    /// <summary>扫描期间把下钻入口挡住，避免在旧树与新结果之间来回跳。</summary>
    private void SetBusy(bool busy)
    {
        CrumbBar.IsHitTestVisible = !busy;
        MapCanvas.IsHitTestVisible = !busy;
    }
}
