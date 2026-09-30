using System;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 反馈中心的**选择页**：两张类型卡 + 提交后的处理流程 + 查重入口 + 草稿条。
///
/// 单击类型卡 = **导航到 <see cref="FeedbackFormPage"/>**（2026-09-30 Nick 要求改成正常页面逻辑，
/// ⛔ 不再在同一页里靠 Visibility 互斥切换）。返回走导航栏那一个返回按钮。
///
/// 逻辑都在 <see cref="Feedback"/>（纯数据 + 拼装 + 校验）和
/// <see cref="FeedbackDraftStore"/>（草稿，进程内共享 + 落盘）里，这里只负责把界面接到它们上面。
/// </summary>
public sealed partial class FeedbackPage : Page
{
    /// <summary>两张卡片的图（跟网页版同一套 PNG，从嵌入资源解出来）。</summary>
    private ImageSource? _iconReport;
    private ImageSource? _iconSuggestion;

    public FeedbackPage()
    {
        InitializeComponent();
        // 每次进本页都跑一遍：从表单页返回时 Frame 会重建本页，草稿条要据此重算
        Loaded += (_, _) => Init();
    }

    private void Init()
    {
        // 卡片图：跟网页版 src/assets/feedback 是同一套文件
        _iconReport = LoadIcon("report.png", "feedback-report.png");
        _iconSuggestion = LoadIcon("suggest.png", "feedback-suggest.png");
        KindIconReport.Source = _iconReport;
        KindIconSuggestion.Source = _iconSuggestion;

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
    // 选类型 → 导航到表单页
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 单击类型卡：把卡片标签（<c>report</c> / <c>suggestion</c>）当导航参数带去表单页。
    /// ⚠️ 走 <c>ShellPage.NavigateToFeedbackForm</c> 而不是直接 <c>Frame.Navigate</c> ——
    ///    前者顺手把左侧导航高亮留在「反馈中心」（人确实还在反馈中心这一区里）。
    /// </summary>
    private void KindCard_Click(object sender, RoutedEventArgs e)
    {
        var key = (sender as FrameworkElement)?.Tag as string ?? "";
        if (key.Length == 0) return;
        NavigateToForm(key);
    }

    private static void NavigateToForm(string kind)
        => App.MainWindow?.Shell.NavigateToFeedbackForm(kind);

    // ════════════════════════════════════════════════════════════════
    // 草稿
    // ════════════════════════════════════════════════════════════════

    private void RefreshDraftBar()
    {
        var cur = FeedbackDraftStore.Current;
        DraftBar.IsOpen = cur.Kind.Length > 0
                          || cur.Title.Trim().Length > 0
                          || FeedbackDraftStore.Load() is not null;
    }

    private void ResumeDraft_Click(object sender, RoutedEventArgs e)
    {
        var draft = FeedbackDraftStore.Current;

        // 内存里已经填着（用户点过卡片又退回来的）→ 直接带着这个类型进表单页，
        // 别用文件把它盖回去 —— 文件里可能是更早的一份
        if (draft.Kind.Length == 0)
        {
            var saved = FeedbackDraftStore.Load();
            if (saved is null) return;

            // ⚠️ 就地拷（CopyFrom），不能换引用：Current 是这个进程长期共享的同一个实例
            draft.CopyFrom(saved);
        }

        if (draft.Kind.Length == 0) return;
        NavigateToForm(draft.Kind);
    }

    private void OpenIssueList_Click(object sender, RoutedEventArgs e)
        => App.MainWindow?.OpenExternal(Feedback.IssueListUrl);
}
