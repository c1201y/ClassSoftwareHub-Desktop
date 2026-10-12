using System;
using System.Diagnostics;
using System.Linq;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 条款阅读窗 —— 独立 AppWindow，站点「条款」页（#/agreement）的桌面版。
///
/// 三份文档（用户协议 / 隐私政策 / 免责声明）来自 <see cref="LegalText"/>，
/// 而它又是 tools/sync-legal.mjs 从**站点仓库根目录的 `法律文本.ts`**生成的
/// （唯一真源；站点「条款」页、站点设置页「关于」区、本窗口、安装程序那一页，
/// 四处内容同源）。⇒ 本窗口**完全离线**，不依赖网络、不动 WebView2。
///
/// 入口：设置页「条款与声明」组里的三张卡片（各带一个 key）。
///
/// = 标准窗口 + **系统标题栏**（不自绘）：读文档的窗，系统原生最省事。
///   故意留在任务栏 / Alt+Tab 里 —— 用户可能一边看条款一边操作应用。
/// ⚠️ 关掉 = 真销毁：条款窗没有"留着复用"的价值，真关掉最省事，
///    也不会在退出链路上留下没关掉的窗口把进程吊住（§V）。
/// </summary>
public sealed partial class LegalWindow : Window
{
    private const int DefaultWidthDip = 920;
    private const int DefaultHeightDip = 800;

    private static LegalWindow? _instance;

    private AppWindow? _appWindow;

    /// <summary>期望展示的文档 key（<see cref="Show"/> 传进来的）。SelectorBar 加载时会自己
    /// 选中第一项，所以 Loaded 之后要按它再拨一次，免得多档场景下"点隐私政策却显示用户协议"。</summary>
    private string _wantKey = "agreement";

    /// <summary>程序化改选中项时的闸门（防 SelectionChanged 里再渲染一遍）。</summary>
    private bool _suppress;

    private LegalWindow()
    {
        InitializeComponent();
        BuildTabs();
        Configure();

        // SelectorBar 在 Loaded 时会自动选中第一项（MiniPickNumber 那边踩过同样的坑）⇒
        // 这时按我们真正想要的那一档再拨一次；顺带把正文刷出来（Show 里那次可能早于布局）。
        Root.Loaded += (_, _) => ShowDoc(_wantKey);
    }

    // ── 对外 ─────────────────────────────────────────────────────────────

    /// <summary>摊开这扇窗；已经开着就切到指定文档，再把它调到前面。</summary>
    /// <param name="key">agreement / privacy / disclaimer；不认识的值按第一份（用户协议）处理。</param>
    public static void Show(string? key)
    {
        try
        {
            _instance ??= new LegalWindow();
            _instance._wantKey = NormalizeKey(key);
            _instance.ShowDoc(_instance._wantKey);
            _instance.BringUp();
        }
        catch (Exception ex)
        {
            AppLog.Warning("legal", "打开条款窗口失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 退出应用时真正销毁（见 <see cref="MainWindow"/> 的 FinishExit）。
    /// ⛔ Application.Exit() 会漏窗口，没关掉的窗口把消息循环撑住 ⇒ 进程残留（§V）。
    /// </summary>
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
        AppLog.Info("exit", "条款窗口实例已请求 Close");
    }

    // ── 内容 ─────────────────────────────────────────────────────────────

    /// <summary>把三档标签按文档顺序建出来。文字直接用文档 title（跟站点「条款」页的标签一致）。</summary>
    private void BuildTabs()
    {
        try
        {
            foreach (var doc in LegalText.All)
            {
                DocSelector.Items.Add(new SelectorBarItem { Text = doc.Title, Tag = doc.Key });
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("legal", "条款档位生成失败：" + ex.Message);
        }
    }

    /// <summary>切到某一份文档：标题 / 版本行 / 选中档 / 正文一起换。</summary>
    private void ShowDoc(string key)
    {
        try
        {
            var doc = LegalText.Find(key) ?? LegalText.All.FirstOrDefault();
            if (doc is null) return;                       // 理论上不会（生成物固定三份）

            _wantKey = doc.Key;
            DocTitle.Text = doc.Title;
            DocVersion.Text = doc.VersionLine;

            _suppress = true;
            SelectTab(doc.Key);
            _suppress = false;

            Render(doc);
        }
        catch (Exception ex)
        {
            AppLog.Warning("legal", "条款渲染失败：" + ex.Message);
        }
    }

    /// <summary>把 SelectorBar 的选中项拨到某个 key（不触发渲染，调用方自己套 <c>_suppress</c>）。</summary>
    private void SelectTab(string key)
    {
        foreach (var obj in DocSelector.Items)
        {
            if (obj is SelectorBarItem item &&
                string.Equals(item.Tag as string, key, StringComparison.OrdinalIgnoreCase))
            {
                DocSelector.SelectedItem = item;
                return;
            }
        }
    }

    private void DocSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_suppress) return;
        if (sender.SelectedItem?.Tag is not string key) return;
        ShowDoc(key);
    }

    /// <summary>正文：引言 → 各节（小节标题 + 逐条）。样式都在 XAML 的根 Grid.Resources 里。</summary>
    private void Render(LegalDoc doc)
    {
        BodyHost.Children.Clear();

        if (!string.IsNullOrWhiteSpace(doc.Intro))
            BodyHost.Children.Add(NewLine(doc.Intro, "LegalIntroStyle"));

        foreach (var section in doc.Sections)
        {
            if (!string.IsNullOrWhiteSpace(section.Heading))
                BodyHost.Children.Add(NewLine(section.Heading, "LegalHeadingStyle"));

            foreach (var item in section.Items)
                BodyHost.Children.Add(NewLine(item, "LegalBodyStyle"));
        }

        // 换文档后回到顶部（不这么做的话：看完长的再切短的，会停在中段）
        try
        {
            if (BodyHost.Parent is ScrollViewer sv) sv.ChangeView(null, 0, null, true);
        }
        catch { /* 布局还没完成时忽略 */ }
    }

    private TextBlock NewLine(string text, string styleKey) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        Style = Root.Resources[styleKey] as Style,
    };

    private static string NormalizeKey(string? key)
        => string.IsNullOrWhiteSpace(key) ? "agreement" : key.Trim().ToLowerInvariant();

    // ── 窗口本身 ─────────────────────────────────────────────────────────

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "条款与声明";

            if (_appWindow.Presenter is OverlappedPresenter p)
            {
                p.SetBorderAndTitleBar(true, true);   // 系统标题栏（拖动 / 阴影 / 圆角交给系统）
                p.IsResizable = true;                 // 条款要能拉大着看
                p.IsMaximizable = true;
                p.IsMinimizable = true;
            }

            BackdropHost.Apply(this, Root, App.Settings.Current.Backdrop);
            ThemeHost.Apply(Root);

            ApplySize();
            CenterOnScreen();

            // 真关掉 ⇒ 清掉单例，下次 Show 重新建
            Closed += (_, _) => { _instance = null; };
        }
        catch (Exception ex)
        {
            AppLog.Warning("legal", "条款窗口初始化失败：" + ex.Message);
        }
    }

    /// <summary>按逻辑像素（dip）定客户区大小，并夹进当前屏幕（小屏上别长出屏幕外）。</summary>
    private void ApplySize()
    {
        if (_appWindow is null) return;
        try
        {
            var scale = Scale();
            var work = DisplayArea.Primary.WorkArea;

            var maxW = work.Width / scale - 40;
            var maxH = work.Height / scale - 60;

            var w = Math.Clamp(DefaultWidthDip, 620, Math.Max(620, maxW));
            var h = Math.Clamp(DefaultHeightDip, 460, Math.Max(460, maxH));

            _appWindow.ResizeClient(new SizeInt32(
                (int)Math.Round(w * scale), (int)Math.Round(h * scale)));
        }
        catch (Exception ex)
        {
            AppLog.Warn("legal", "条款窗尺寸设置失败：" + ex.Message);
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
            AppLog.Warn("legal", "条款窗定位失败：" + ex.Message);
        }
    }

    private double Scale()
    {
        try
        {
            var dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch { return 1.0; }
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
            AppLog.Exception("legal", ex, "唤醒条款窗");
        }
    }

    private const int SW_SHOW = 5;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
