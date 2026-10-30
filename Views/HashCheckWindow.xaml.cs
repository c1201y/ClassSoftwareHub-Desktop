using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 哈希校验悬浮窗（需求 1:16，2026-10-30）。
///
/// <para>
/// 用途：核对下载到的安装包。拖进文件 → 立刻出四种哈希 → 粘贴官网给的校验值 → 自动比对。
/// 原来这件事只能进「系统工具 → 编码 / 哈希工具」，为核对一个文件把整个应用拉出来太重，
/// 所以单开一扇窗，可以一直摆在旁边。
/// </para>
///
/// <para>
/// ⚠️ 哈希计算用的是 <see cref="FileHash"/>（跟编码页同一份实现），本窗只负责界面与比对展示。
/// </para>
///
/// <para>
/// ⚠️ 关掉 = **真销毁**，跟学科工具窗一致：这扇窗没有"留着复用"的价值，
///    真关最省事，也不会在退出链路上留一个没关的窗口把进程吊住。
/// </para>
/// </summary>
public sealed partial class HashCheckWindow : Window
{
    private const int DefaultWidthDip = 560;
    private const int DefaultHeightDip = 640;

    private const string IdleTitle = "把文件拖到这里，或单击选择";

    private static HashCheckWindow? _instance;

    private AppWindow? _appWindow;

    /// <summary>四种算法各一行（顺序见 <see cref="FileHash.Algorithms"/>）。</summary>
    private readonly ObservableCollection<HashLine> _lines = new();

    private Dictionary<string, string> _hashes = new();

    /// <summary>用户换文件时作废上一轮计算（大文件算到一半再换，不能让它把新结果盖掉）。</summary>
    private CancellationTokenSource? _cts;

    private HashCheckWindow()
    {
        InitializeComponent();
        Configure();

        foreach (var name in FileHash.Algorithms) _lines.Add(new HashLine(name));
        HashList.ItemsSource = _lines;
        SetRowsEmpty("—");
    }

    // ── 对外 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 摊开这扇窗；已经开着就把它调到前面（不重新建、不丢当前算好的结果）。
    /// <para>
    /// ⚠️ 方法名 <c>Show</c> 是约定：<c>ToolSidebarWindow</c> 打开「专用窗口」类模块时
    ///    按名字反射调用它（见 <c>Data/SidebarModule.WindowType</c>），改名会静默失效。
    /// </para>
    /// </summary>
    public static void Show()
    {
        try
        {
            _instance ??= new HashCheckWindow();
            _instance.BringUp();
        }
        catch (Exception ex)
        {
            AppLog.Exception("hash-check", ex, "打开哈希校验窗口");
        }
    }

    /// <summary>退出应用时真正销毁（由 MainWindow 的退出链路调用；没开过就什么都不做）。</summary>
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
    }

    // ── 窗口配置 ─────────────────────────────────────────────────────────

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "哈希校验";

            if (_appWindow.Presenter is OverlappedPresenter p)
            {
                p.IsResizable = true;
                p.IsMaximizable = false;
                p.IsMinimizable = true;
            }

            ModernWindow.ApplyTitleBar(this, TitleBarHost, Root);

            // 跟应用主题（⛔ 不传 external：分体只作用于侧边栏与常用工具浮窗）
            BackdropHost.Apply(this, Root, App.Settings.Current.Backdrop);
            ThemeHost.Apply(Root);

            ApplySize();
            CenterOnScreen();

            Closed += (_, _) =>
            {
                _cts?.Cancel();
                _instance = null;
            };
        }
        catch (Exception ex)
        {
            AppLog.Exception("hash-check", ex, "哈希校验窗口初始化");
        }
    }

    private void ApplySize()
    {
        if (_appWindow is null) return;
        try
        {
            var scale = Scale();
            var work = DisplayArea.Primary.WorkArea;

            var maxW = work.Width / scale - 40;
            var maxH = work.Height / scale - 60;

            var w = Math.Clamp(DefaultWidthDip, 460, Math.Max(460, maxW));
            var h = Math.Clamp(DefaultHeightDip, 480, Math.Max(480, maxH));

            _appWindow.ResizeClient(new SizeInt32(
                (int)Math.Round(w * scale), (int)Math.Round(h * scale)));
        }
        catch (Exception ex)
        {
            AppLog.Warn("hash-check", "哈希校验窗尺寸设置失败：" + ex.Message);
        }
    }

    private void CenterOnScreen()
    {
        if (_appWindow is null) return;
        try
        {
            var work = DisplayArea.Primary.WorkArea;
            var size = _appWindow.Size;
            _appWindow.Move(new PointInt32(
                work.X + Math.Max(0, (work.Width - size.Width) / 2),
                work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
        }
        catch (Exception ex)
        {
            AppLog.Warn("hash-check", "哈希校验窗定位失败：" + ex.Message);
        }
    }

    private void BringUp()
    {
        try
        {
            Activate();
            var hwnd = WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, SW_SHOW);
            SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            AppLog.Exception("hash-check", ex, "激活哈希校验窗口");
        }
    }

    // ── 选文件 ───────────────────────────────────────────────────────────

    /// <summary>整条都能点：少一层「我到底该点哪个按钮」的犹豫。</summary>
    private async void DropZone_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.Downloads,
            };
            picker.FileTypeFilter.Add("*");

            // ⚠️ 必须用**本窗**的句柄：主窗口可能收在托盘里、根本没有可见窗口，
            //    拿 App.MainWindow 的句柄初始化会直接抛异常。
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            await LoadFileAsync(file.Path);
        }
        catch (Exception ex)
        {
            AppLog.Warn("hash-check", "选择文件失败：" + ex.Message);
            DropSub.Text = "选择文件失败：" + ex.Message;
        }
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        DropTitle.Text = "松开鼠标即开始计算";
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e) => DropTitle.Text = IdleTitle;

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        try
        {
            DropTitle.Text = IdleTitle;
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

            var items = await e.DataView.GetStorageItemsAsync();
            if (items.Count > 0 && items[0] is StorageFile file) await LoadFileAsync(file.Path);
        }
        catch (Exception ex)
        {
            AppLog.Warn("hash-check", "拖入文件失败：" + ex.Message);
            DropSub.Text = "拖入的文件读取失败：" + ex.Message;
        }
    }

    private async Task LoadFileAsync(string path)
    {
        _hashes = new();
        SetRowsEmpty("计算中…");
        HashProgress.Visibility = Visibility.Visible;
        MatchText.Visibility = Visibility.Collapsed;

        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            var info = new FileInfo(path);
            DropTitle.Text = IdleTitle;
            DropSub.Text = "已选文件，可再拖入新文件重新计算";
            FileInfo.Visibility = Visibility.Visible;
            FileInfo.Text = $"{info.Name}（{SizeText(info.Length)}）· {info.FullName}";

            var hashes = await Task.Run(() => FileHash.Compute(path, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;

            _hashes = hashes;
            foreach (var line in _lines)
                line.Value = hashes.TryGetValue(line.Name, out var v) && v.Length > 0 ? v : "—";
        }
        catch (OperationCanceledException)
        {
            // 换文件了，这一轮作废
        }
        catch (Exception ex)
        {
            AppLog.Warn("hash-check", "计算哈希失败：" + ex.Message);
            SetRowsEmpty("—");
            FileInfo.Text = "计算失败：" + ex.Message;
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                HashProgress.Visibility = Visibility.Collapsed;
                RefreshMatch();
            }
        }
    }

    // ── 比对 ─────────────────────────────────────────────────────────────

    private void Expected_TextChanged(object sender, TextChangedEventArgs e) => RefreshMatch();

    private void RefreshMatch()
    {
        var expected = FileHash.Normalize(ExpectedBox.Text);
        if (expected.Length == 0)
        {
            MatchText.Visibility = Visibility.Collapsed;
            return;
        }

        MatchText.Visibility = Visibility.Visible;

        // 还没算完 / 还没选文件：说清下一步是什么，别拿红字吓人
        if (_hashes.Count == 0)
        {
            var guess = FileHash.GuessAlgorithm(ExpectedBox.Text);
            MatchText.Text = guess is null
                ? "已填入校验值，选择文件后自动比对。"
                : $"已填入 {guess} 校验值（{expected.Length} 位），选择文件后自动比对。";
            MatchText.Foreground = Res("TextFillColorSecondaryBrush");
            return;
        }

        var hit = FileHash.Match(ExpectedBox.Text, _hashes);
        if (hit is not null)
        {
            MatchText.Text = $"✓ 与 {hit} 一致，文件与发布方给出的那份完全相同。";
            MatchText.Foreground = Res("SystemFillColorSuccessBrush");
            return;
        }

        // 不一致要分两种说：格式认得出来 → 大概率是版本不对；认不出来 → 多半粘错了东西。
        var kind = FileHash.GuessAlgorithm(ExpectedBox.Text);
        MatchText.Text = kind is null
            ? "✗ 与上面四种哈希值都不一致。这段文本不像是 MD5 / SHA-1 / SHA-256 / SHA-512，请确认粘的是校验值而不是网址或版本号。"
            : $"✗ 与上面四种哈希值都不一致。填入的是 {kind} 格式（{expected.Length} 位），请确认文件来源与版本是否对应。";
        MatchText.Foreground = Res("SystemFillColorCriticalBrush");
    }

    // ── 复制 ─────────────────────────────────────────────────────────────

    private void CopyLine_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HashLine line) return;
        if (line.Value.Length == 0 || line.Value == "—" || line.Value == "计算中…") return;

        try
        {
            var package = new DataPackage();
            package.SetText(line.Value);
            Clipboard.SetContent(package);
            AppLog.Step("hash-check", $"已复制 {line.Name} 值");
        }
        catch (Exception ex)
        {
            AppLog.Warn("hash-check", "复制失败：" + ex.Message);
        }
    }

    // ── 小工具 ───────────────────────────────────────────────────────────

    private void SetRowsEmpty(string placeholder)
    {
        foreach (var line in _lines) line.Value = placeholder;
    }

    private Brush Res(string key) => ThemeBrush.Get(Root, key);

    private static string SizeText(long bytes)
        => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.##} GB"
         : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.##} MB"
         : bytes >= 1L << 10 ? $"{bytes / (double)(1L << 10):0.#} KB"
         : $"{bytes} B";

    private double Scale()
    {
        try
        {
            var dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch { return 1.0; }
    }

    // ── Win32 ────────────────────────────────────────────────────────────

    private const int SW_SHOW = 5;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}

/// <summary>哈希列表的一行：算法名 + 值。值的更新走属性通知，不在界面里逐个找控件改。</summary>
public sealed class HashLine : INotifyPropertyChanged
{
    public HashLine(string name) => Name = name;

    /// <summary>算法名（MD5 / SHA-1 / SHA-256 / SHA-512），构造后不变。</summary>
    public string Name { get; }

    private string _value = "";
    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
