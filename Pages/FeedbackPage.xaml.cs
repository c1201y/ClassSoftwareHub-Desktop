using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 反馈中心：**照着网页版 <c>#/feedback</c> 一比一抄**（版式/字号/留白/图标都对齐，文案也取站点那批）。
///
/// 逻辑都在 <see cref="Feedback"/>（纯数据 + 拼装 + 校验）和
/// <see cref="FeedbackDraftStore"/>（草稿落盘）里，这里只负责把界面接到它们上面。
/// 「提交」= 拼一个 Issue 预填链接交给系统浏览器，**不发任何网络请求**。
/// </summary>
public sealed partial class FeedbackPage : Page
{
    /// <summary>正在填的这一份。整个页面生命周期内共用一份，切类型/返回选择态都不重置内容。</summary>
    private readonly Feedback.Draft _draft = new();

    /// <summary>两张卡片的图（跟网页版同一套 PNG，从嵌入资源解出来）。</summary>
    private ImageSource? _iconReport;
    private ImageSource? _iconSuggestion;

    /// <summary>代码填控件时置位，避免 SelectionChanged 把刚设好的值又写回去（或误清子类型）。</summary>
    private bool _suppress;

    /// <summary>上次打开 GitHub 的时间，用于防连点（网页版是禁用按钮 3 秒）。</summary>
    private DateTimeOffset _lastOpen;

    public FeedbackPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Init();
    }

    private void Init()
    {
        SubKindCombo.ItemsSource = Feedback.ReportSubKinds;
        AppCombo.ItemsSource = App.Content.Apps;

        // 卡片图：跟网页版 src/assets/feedback 是同一套文件
        _iconReport = LoadIcon("report.png", "feedback-report.png");
        _iconSuggestion = LoadIcon("suggest.png", "feedback-suggest.png");
        KindIconReport.Source = _iconReport;
        KindIconSuggestion.Source = _iconSuggestion;

        // 默认勾选「附带本机信息」。放这里而不是 XAML 里：WinUI 3 对 CheckBox 的
        // IsChecked="True" 在 XAML 里赋值会抛「Failed to assign to property ToggleButton.IsChecked」。
        IncludeEnvCheck.IsChecked = true;

        // 环境信息只在「页面已经进树」之后才拿得到真实主题，所以放这里而不是构造函数里
        UpdateEnvPreview();
        RefreshDraftBar();

        // 主视觉横幅是主题色渐变，深/浅色各一套 —— 主题变了要重画
        ApplyHeroBrush();
        ActualThemeChanged += (_, _) => ApplyHeroBrush();
    }

    private static ImageSource? LoadIcon(string fileName, string cacheName)
    {
        try
        {
            var path = EmbeddedAssets.ExtractToCache(fileName, cacheName);
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
            return new BitmapImage(new Uri(path));
        }
        catch { return null; }        // 图读不到就空着，别把整页搞崩
    }

    /// <summary>
    /// 主视觉横幅的底：网页版是 <c>linear-gradient(100deg, accent 16%, 面色 58%, 卡片色 100%)</c>。
    /// 主题色那一段取系统的 <c>SystemAccentColor</c>（拿不到就退回默认蓝），后两段按深/浅色写死
    /// —— 主题色 + 中性面色这套没法用 ThemeResource 直接塞进 GradientStop.Color（那里要 Color 不是 Brush）。
    /// </summary>
    private void ApplyHeroBrush()
    {
        try
        {
            var dark = ActualTheme == ElementTheme.Dark;

            var accent = Application.Current.Resources["SystemAccentColor"] is Windows.UI.Color c
                ? c
                : Windows.UI.Color.FromArgb(255, 0, 95, 184);

            var brush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 0.18),     // ≈ 100°（略向右下）
            };

            brush.GradientStops.Add(new GradientStop
            {
                Offset = 0,
                Color = Windows.UI.Color.FromArgb(41, accent.R, accent.G, accent.B),   // 主题色 ≈16%
            });
            brush.GradientStops.Add(new GradientStop
            {
                Offset = 0.58,
                Color = dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
                             : Windows.UI.Color.FromArgb(255, 246, 246, 246),
            });
            brush.GradientStops.Add(new GradientStop
            {
                Offset = 1,
                Color = dark ? Windows.UI.Color.FromArgb(255, 43, 43, 43)
                             : Windows.UI.Color.FromArgb(255, 252, 252, 252),
            });

            HeroBanner.Background = brush;
        }
        catch { }
    }

    // ════════════════════════════════════════════════════════════════
    // 选择态 ↔ 表单态
    // ════════════════════════════════════════════════════════════════

    private void KindCard_Click(object sender, RoutedEventArgs e)
    {
        var key = (sender as FrameworkElement)?.Tag as string ?? "";
        if (key.Length == 0) return;

        _draft.Kind = key;

        // 只有「报告问题」有子类型。切到别的大类时把子类型清掉；
        // 切到/留在「报告问题」时**不动**它 —— 从草稿恢复过来的那一条要留着。
        if (key != Feedback.KindReport)
        {
            _draft.SubKind = "";
            _suppress = true;
            SubKindCombo.SelectedIndex = -1;
            _suppress = false;
        }

        ShowForm();
    }

    private void ShowForm()
    {
        var kind = Feedback.FindKind(_draft.Kind);
        FormKindText.Text = kind?.Title ?? "";

        // 表单头上的图标 = 卡片那张图的小号版（不是字体图标）
        FormKindImage.Source = _draft.Kind == Feedback.KindSuggestion ? _iconSuggestion : _iconReport;

        SubKindCombo.Visibility = _draft.Kind == Feedback.KindReport
            ? Visibility.Visible : Visibility.Collapsed;

        ChooseView.Visibility = Visibility.Collapsed;
        FormView.Visibility = Visibility.Visible;
        ErrorBar.IsOpen = false;
    }

    private void BackToChoose_Click(object sender, RoutedEventArgs e)
    {
        SyncDraftFromForm();          // 填了一半返回选择态，内容不能丢
        FormView.Visibility = Visibility.Collapsed;
        ChooseView.Visibility = Visibility.Visible;
        RefreshDraftBar();
    }

    // ════════════════════════════════════════════════════════════════
    // 表单 → 草稿
    // ════════════════════════════════════════════════════════════════

    private void SyncDraftFromForm()
    {
        _draft.Title = TitleBox.Text;
        _draft.Detail = DetailBox.Text;
        _draft.Contact = ContactBox.Text;
        _draft.IncludeEnv = IncludeEnvCheck.IsChecked == true;
    }

    private void SubKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        _draft.SubKind = SubKindCombo.SelectedItem is Feedback.SubKindDef s ? s.Key : "";
    }

    private void App_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        _draft.AppId = AppCombo.SelectedItem is SoftwareApp a ? a.Id : "";
    }

    private void IncludeEnv_Changed(object sender, RoutedEventArgs e)
    {
        // 只影响预览观感（关掉就把那行压暗），真正取不取用提交时读的 IsChecked
        EnvPreview.Opacity = IncludeEnvCheck.IsChecked == true ? 1.0 : 0.4;
    }

    // ════════════════════════════════════════════════════════════════
    // 草稿
    // ════════════════════════════════════════════════════════════════

    private void RefreshDraftBar()
    {
        DraftBar.IsOpen = _draft.Kind.Length > 0
                          || _draft.Title.Trim().Length > 0
                          || FeedbackDraftStore.Load() is not null;
    }

    private void ResumeDraft_Click(object sender, RoutedEventArgs e)
    {
        // 内存里已经填着（用户点过卡片又退回来的）→ 直接回到表单，别用文件把它盖回去
        if (_draft.Kind.Length > 0)
        {
            ShowForm();
            return;
        }

        var saved = FeedbackDraftStore.Load();
        if (saved is null) return;

        _suppress = true;
        _draft.Kind = saved.Kind;
        _draft.SubKind = saved.SubKind;
        _draft.AppId = saved.AppId;
        _draft.Title = saved.Title;
        _draft.Detail = saved.Detail;
        _draft.Contact = saved.Contact;
        _draft.IncludeEnv = saved.IncludeEnv;

        TitleBox.Text = saved.Title;
        DetailBox.Text = saved.Detail;
        ContactBox.Text = saved.Contact;
        IncludeEnvCheck.IsChecked = saved.IncludeEnv;
        SubKindCombo.SelectedIndex =
            Feedback.ReportSubKinds.ToList().FindIndex(s => s.Key == saved.SubKind);
        AppCombo.SelectedIndex = App.Content.Apps.FindIndex(a => a.Id == saved.AppId);
        _suppress = false;

        ShowForm();
    }

    // ════════════════════════════════════════════════════════════════
    // 提交 / 复制
    // ════════════════════════════════════════════════════════════════

    /// <summary>校验通过就返回拼好的链接；不通过时把提示填进 ErrorBar 并返回 null。</summary>
    private Feedback.IssueLink? Prepare()
    {
        SyncDraftFromForm();

        var error = Feedback.Validate(_draft);
        if (error.Length > 0)
        {
            ErrorBar.Message = error;
            ErrorBar.IsOpen = true;
            return null;
        }

        ErrorBar.IsOpen = false;
        var link = Feedback.BuildIssueLink(_draft, SelectedApp(), EnvRows());
        TruncateBar.IsOpen = link.Truncated;
        return link;
    }

    private SoftwareApp? SelectedApp() => App.Content.FindById(_draft.AppId);

    private void OpenIssue_Click(object sender, RoutedEventArgs e)
    {
        var link = Prepare();
        if (link is null) return;

        // 打开前先存草稿 —— 用户可能到了 GitHub 那边才发现要登录，回头再来时内容还在
        FeedbackDraftStore.Save(_draft);

        // 防连点：网页版是禁用按钮 3 秒，桌面版浏览器是外部进程、没法感知它开没开，用时间戳挡
        if (DateTimeOffset.Now - _lastOpen < TimeSpan.FromSeconds(3)) return;
        _lastOpen = DateTimeOffset.Now;

        App.MainWindow?.OpenExternal(link.Url);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        SyncDraftFromForm();

        var error = Feedback.Validate(_draft);
        if (error.Length > 0)
        {
            ErrorBar.Message = error;
            ErrorBar.IsOpen = true;
            return;
        }

        ErrorBar.IsOpen = false;

        // ⚠️ 复制的是**完整**正文（不做长度截断）——
        //    链接那条路会因为 URL 长度上限砍掉描述，这里正是给它兜底的出口，
        //    所以上面那句提示"完整内容请点复制反馈内容"才成立。
        //    另外把标题也带上：用户多半是整段贴到 Q 群里，没标题对方不知道在说什么。
        var body = Feedback.BuildBody(_draft, SelectedApp(), EnvRows());
        var text = Feedback.BuildTitle(_draft) + "\n\n" + body;

        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);

            CopyButton.Content = "已复制";
            // 两秒后还原按钮文字（页面用 NavigationCacheMode=Disabled，不担心定时器留在内存里）
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.IsRepeating = false;
            timer.Tick += (t, _) =>
            {
                CopyButton.Content = "复制反馈内容";
                t.Stop();
            };
            timer.Start();
        }
        catch
        {
            ErrorBar.Message = "复制失败了，可以手动选中正文复制。";
            ErrorBar.IsOpen = true;
        }
    }

    private void OpenIssueList_Click(object sender, RoutedEventArgs e)
        => App.MainWindow?.OpenExternal(Feedback.IssueListUrl);

    // ════════════════════════════════════════════════════════════════
    // 本机环境信息
    // ════════════════════════════════════════════════════════════════

    private List<Feedback.EnvRow> EnvRows()
    {
        var rows = new List<Feedback.EnvRow>
        {
            new("客户端版本", ShellConfig.VersionPrefix + ShellConfig.ShellVersion),
            new("更新通道", App.Settings.Current.UpdateChannel == "insider" ? "预览版" : "正式版"),
            new("操作系统", OsName()),
            new("系统架构", RuntimeInformation.OSArchitecture.ToString()),
            new("界面主题", ActualTheme == ElementTheme.Dark ? "深色" : "浅色"),
        };

        // 空值不出现在正文里（比如注册表读不到）
        return rows.Where(r => r.Value.Length > 0).ToList();
    }

    private void UpdateEnvPreview()
    {
        EnvPreview.Text = "会附上：" + string.Join(" · ", EnvRows().Select(r => $"{r.Label} {r.Value}"));
    }

    /// <summary>
    /// 取一个像样的 Windows 版本串。
    /// ⚠️ 注册表里的 <c>ProductName</c> 在 Win11 上**仍然写着 "Windows 10"**（这是系统自己的老值），
    ///    所以要拿内部版本号纠正一下：22000 及以上就是 Win11。
    /// </summary>
    private static string OsName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is null) return "";

            var product = (key.GetValue("ProductName") as string ?? "").Trim();
            var display = (key.GetValue("DisplayVersion") as string ?? "").Trim();
            var build = (key.GetValue("CurrentBuildNumber") as string ?? "").Trim();

            if (product.Contains("Windows 10", StringComparison.Ordinal)
                && int.TryParse(build, out var b) && b >= 22000)
                product = product.Replace("Windows 10", "Windows 11");

            var parts = new List<string>();
            if (product.Length > 0) parts.Add(product);
            if (display.Length > 0) parts.Add(display);
            if (build.Length > 0) parts.Add($"内部版本 {build}");
            return string.Join(" ", parts);
        }
        catch
        {
            return "";
        }
    }

    // ════════════════════════════════════════════════════════════════
}
