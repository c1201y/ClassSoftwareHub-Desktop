using System;
using System.Collections.Generic;
using System.Linq;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 反馈中心的**表单页**：填标题 / 描述 / 联系方式，然后从三条出口之一提交。
///
/// 导航关系（2026-09-30 起是**正常页面导航**，不是同页 Visibility 互斥）：
///     <see cref="FeedbackPage"/>（选类型）--单击卡片--> 本页
///     本页 --导航栏返回按钮--> <see cref="FeedbackPage"/>
/// 类型由导航参数带进来（<c>e.Parameter</c>，值就是 <see cref="Feedback.KindReport"/> /
/// <see cref="Feedback.KindSuggestion"/>）；<c>--page=feedback-form</c> 直达时不带参数，
/// 退回草稿里的类型。
///
/// 三条出口（2026-10-04 起主出口不再需要登录 GitHub）：
///   · 「提交反馈」【主】= <see cref="FeedbackSubmit"/> 发到自建提交服务，令牌在服务端，
///     服务端直接建议题；**成功即清草稿**（接口明确回了 success，留着只会诱发重复提交）；
///   · 「在 GitHub 上提交」【兜底】= 拼一个 Issue 预填链接交给系统浏览器，需要用户登录 GitHub；
///     服务端连不上 / 还没这条路由时改用它；
///   · 「在 Q 群中反馈」= 打开 Q 群卡片 + 弹提醒浮窗，浮窗里点「复制」再摊开图文流程
///     （见 <see cref="QqFeedbackFlyoutWindow"/> / <see cref="QqFeedbackGuideWindow"/>）。
///
/// 逻辑都在 <see cref="Feedback"/> 里，草稿存在 <see cref="FeedbackDraftStore"/> 里，
/// 这里只负责把界面接到它们上面。
/// </summary>
public sealed partial class FeedbackFormPage : Page
{
    /// <summary>两张卡片的图（跟网页版同一套 PNG，从嵌入资源解出来）。</summary>
    private ImageSource? _iconReport;
    private ImageSource? _iconSuggestion;

    /// <summary>导航参数带来的类型；空串 = 没带（<c>--page=feedback-form</c> 直达）。</summary>
    private string _pendingKind = "";

    /// <summary>
    /// 本页「这次要填的类型」，用来在草稿被清空后还能把页头（图标 + 类型名 + 子类型框）撑住。
    /// 提交成功后草稿会被丢弃，<see cref="Feedback.Draft.Kind"/> 变成空串 —— 没有这个兜底，
    /// 页头会当场变成一片空白，看着像页面坏了。
    /// </summary>
    private string _pageKind = "";

    /// <summary>代码填控件时置位，避免 SelectionChanged 把刚设好的值又写回去（或误清子类型）。</summary>
    private bool _suppress;

    /// <summary>上次打开 GitHub 的时间，用于防连点（网页版是禁用按钮 3 秒）。</summary>
    private DateTimeOffset _lastOpen;

    /// <summary>正在提交（服务端那条路有网络往返，期间按钮要禁用、文案要换）。</summary>
    private bool _busy;

    /// <summary>服务端建好的议题地址（服务端愿意回才有），供「查看议题」用。</summary>
    private string? _issueUrl;

    /// <summary>本页是否已经初始化过 —— <see cref="FrameworkElement.Loaded"/> 可能触发多次，
    /// 而每次重新用草稿刷表单都会盖掉用户刚敲的字。</summary>
    private bool _inited;

    /// <summary>当前这份草稿（进程内共享，两个反馈页面拿到的是同一个实例）。</summary>
    private static Feedback.Draft Draft => FeedbackDraftStore.Current;

    public FeedbackFormPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Init();
    }

    /// <summary>
    /// 接住导航参数。⚠️ 这个方法比 <see cref="Loaded"/> 先跑，所以这里**只记参数不动界面**，
    /// 界面统一在 <see cref="Init"/> 里按 <see cref="_pendingKind"/> 铺。
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pendingKind = e.Parameter as string ?? "";
    }

    /// <summary>离开本页（含导航栏返回、切去别的导航项）时把表单内容收回草稿并落盘。</summary>
    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);

        // 页面此刻还活着，控件里的字读得到
        SyncDraftFromForm();
        FeedbackDraftStore.Save(Draft);
    }

    private void Init()
    {
        if (_inited) return;
        _inited = true;

        SubKindCombo.ItemsSource = Feedback.ReportSubKinds;
        AppCombo.ItemsSource = App.Content.Apps;

        // 卡片图：跟网页版 src/assets/feedback 是同一套文件
        _iconReport = LoadIcon("report.png", "feedback-report.png");
        _iconSuggestion = LoadIcon("suggest.png", "feedback-suggest.png");

        // 本次点的是哪张卡（导航参数）优先；直达时退回草稿里记着的类型
        if (_pendingKind.Length > 0) Draft.Kind = _pendingKind;
        if (Draft.Kind.Length == 0)
        {
            var saved = FeedbackDraftStore.Load();
            if (saved is not null) Draft.CopyFrom(saved);
        }
        if (Draft.Kind.Length == 0) Draft.Kind = Feedback.KindReport;   // 兜底：别开出一张没有类型的表单
        _pageKind = Draft.Kind;

        // 只有「报告问题」有子类型。切到别的大类时把子类型清掉；
        // 留在「报告问题」时**不动**它 —— 从草稿恢复过来的那一条要留着。
        if (Draft.Kind != Feedback.KindReport) Draft.SubKind = "";

        FillForm();
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
    /// 用草稿刷一遍界面。
    /// ⚠️ 整个赋值过程带着 <see cref="_suppress"/>：不然 <c>SelectedIndex</c> 一设下去
    ///    SelectionChanged 就回头把草稿写花了（子类型还会被误清）。
    /// </summary>
    private void FillForm()
    {
        _suppress = true;
        TitleBox.Text = Draft.Title;
        DetailBox.Text = Draft.Detail;
        ContactBox.Text = Draft.Contact;
        IncludeEnvCheck.IsChecked = Draft.IncludeEnv;
        SubKindCombo.SelectedIndex = Draft.Kind == Feedback.KindReport
            ? Feedback.ReportSubKinds.ToList().FindIndex(s => s.Key == Draft.SubKind)
            : -1;
        AppCombo.SelectedIndex = App.Content.Apps.FindIndex(a => a.Id == Draft.AppId);
        _suppress = false;

        ApplyKindHeader();
        EnvPreview.Opacity = Draft.IncludeEnv ? 1.0 : 0.4;
        UpdateEnvPreview();
        ErrorBar.IsOpen = false;
    }

    /// <summary>页头图标 + 类型名，以及子类型下拉框显不显示（只有「报告问题」有）。</summary>
    private void ApplyKindHeader()
    {
        // 草稿被清空（提交成功）时用 _pageKind 兜住，页头不至于开天窗
        var key = Draft.Kind.Length > 0 ? Draft.Kind : _pageKind;
        FormKindText.Text = Feedback.FindKind(key)?.Title ?? "";
        FormKindImage.Source = key == Feedback.KindSuggestion ? _iconSuggestion : _iconReport;
        SubKindCombo.Visibility = key == Feedback.KindReport
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ════════════════════════════════════════════════════════════════
    // 表单 → 草稿
    // ════════════════════════════════════════════════════════════════

    private void SyncDraftFromForm()
    {
        Draft.Title = TitleBox.Text;
        Draft.Detail = DetailBox.Text;
        Draft.Contact = ContactBox.Text;
        Draft.IncludeEnv = IncludeEnvCheck.IsChecked == true;
    }

    private void SubKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        Draft.SubKind = SubKindCombo.SelectedItem is Feedback.SubKindDef s ? s.Key : "";
    }

    private void App_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        Draft.AppId = AppCombo.SelectedItem is SoftwareApp a ? a.Id : "";
    }

    private void IncludeEnv_Changed(object sender, RoutedEventArgs e)
    {
        // 只影响预览观感（关掉就把那行压暗），真正取不取用提交时读的 IsChecked
        EnvPreview.Opacity = IncludeEnvCheck.IsChecked == true ? 1.0 : 0.4;
    }

    // ════════════════════════════════════════════════════════════════
    // 提交 / 复制
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 校验 + 联系方式加密，返回一份「可以离开本机」的草稿副本；
    /// 不通过时把提示填进 <see cref="ErrorBar"/> 并返回 null。
    /// <see cref="Draft"/> 本身仍留着明文，只在本机内存与本地草稿文件里。
    /// </summary>
    private Feedback.Draft? PrepareOutgoing()
    {
        SyncDraftFromForm();

        var error = Feedback.Validate(Draft);
        if (error.Length > 0)
        {
            ShowError("无法提交", error);
            return null;
        }

        var outgoing = EncryptedDraft();
        if (outgoing is null) return null;

        ErrorBar.IsOpen = false;
        return outgoing;
    }

    /// <summary>校验通过就返回拼好的链接；不通过时把提示填进 ErrorBar 并返回 null。</summary>
    private Feedback.IssueLink? Prepare()
    {
        var outgoing = PrepareOutgoing();
        if (outgoing is null) return null;

        var link = Feedback.BuildIssueLink(outgoing, SelectedApp(), EnvRowsIfEnabled());
        TruncateBar.IsOpen = link.Truncated;
        return link;
    }

    /// <summary>
    /// 返回一份「联系方式已加密」的草稿副本，专供**离开本机**的那份内容用（提交 / Issue 链接 / 复制文本）。
    /// <see cref="Draft"/> 本身保留明文，只存在于本机内存与本地草稿文件里。
    /// ⛔ 只加密、绝不解密；联系方式为空时原样返回。
    /// 加密失败返回 null 并弹提示 —— 宁可让用户重试，也绝不把明文发出去。
    /// </summary>
    private Feedback.Draft? EncryptedDraft()
    {
        if (Draft.Contact.Trim().Length == 0) return Draft;

        try
        {
            var copy = new Feedback.Draft();
            copy.CopyFrom(Draft);
            copy.Contact = AgeEncryption.EncryptToArmor(Draft.Contact.Trim());
            return copy;
        }
        catch
        {
            ShowError("无法提交",
                "联系方式加密失败，请稍后重试。若反复失败，可改用「在 Q 群中反馈」联系维护者。");
            TruncateBar.IsOpen = false;
            return null;
        }
    }

    private void ShowError(string title, string message)
    {
        ErrorBar.Title = title;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private SoftwareApp? SelectedApp() => App.Content.FindById(Draft.AppId);

    // ════════════════════════════════════════════════════════════════
    // 出口①：提交反馈（走自建服务，不用登录）
    // ════════════════════════════════════════════════════════════════

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var outgoing = PrepareOutgoing();
        if (outgoing is null) return;

        // 服务端这条路没有 URL 长度限制，内容给完整的一份，不做截断。
        // ⚠️ 只发**原始字段**：标题不带 [类型] 前缀、正文不套模板 —— 那两样服务端自己会拼
        //    （见 Services/FeedbackSubmit 的契约注释）。在这里拼了，议题里会套两层。
        TruncateBar.IsOpen = false;
        var title = outgoing.Title.Trim();
        var detail = Feedback.BuildSubmitDetail(outgoing, EnvRowsIfEnabled());

        // 发出去之前先把草稿落盘：万一半路失败 / 应用被关掉，内容还在
        FeedbackDraftStore.Save(Draft);

        _busy = true;
        SetBusy(true);
        SuccessBar.IsOpen = false;

        try
        {
            var result = await FeedbackSubmit.SubmitAsync(
                outgoing.Kind, outgoing.SubKind, outgoing.AppId,
                title, detail, outgoing.Contact.Trim());

            if (!result.Ok)
            {
                ShowError("提交失败", result.Message + " 可改用「在 GitHub 上提交」或「在 Q 群中反馈」。");
                return;
            }

            // 接口明确回了成功 —— 这时清草稿是安全的。
            // ⚠️ 不清反而有害：草稿条会一直挂着，用户以为没提交成功、再点一次就建出重复议题。
            FeedbackDraftStore.Discard();
            ResetForm();

            _issueUrl = result.Url;
            ViewIssueButton.Visibility = string.IsNullOrWhiteSpace(result.Url)
                ? Visibility.Collapsed : Visibility.Visible;
            SuccessBar.Message = result.Message;
            SuccessBar.IsOpen = true;
            ErrorBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ShowError("提交失败", "发生意外错误：" + ex.Message);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        SubmitButton.IsEnabled = !busy;
        SubmitLabel.Text = busy ? "正在提交" : "提交反馈";
        OpenIssueButton.IsEnabled = !busy;
        QqGroupButton.IsEnabled = !busy;
    }

    /// <summary>提交成功后把表单清干净（草稿已被丢弃，输入框也得跟着空，否则容易被再提交一次）。</summary>
    private void ResetForm()
    {
        _suppress = true;
        TitleBox.Text = "";
        DetailBox.Text = "";
        ContactBox.Text = "";
        _suppress = false;

        UpdateEnvPreview();
        ApplyKindHeader();      // 草稿清空后靠 _pageKind 把页头撑住
    }

    private void ViewIssue_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_issueUrl)) App.MainWindow?.OpenExternal(_issueUrl!);
    }

    // ════════════════════════════════════════════════════════════════
    // 出口②：在 GitHub 上提交（兜底，需要登录）
    // ════════════════════════════════════════════════════════════════

    private void OpenIssue_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var link = Prepare();
        if (link is null) return;

        // 打开前先存草稿 —— 用户可能到了 GitHub 那边才发现要登录，回头再来时内容还在
        FeedbackDraftStore.Save(Draft);

        // 防连点：网页版是禁用按钮 3 秒，桌面版浏览器是外部进程、没法感知它开没开，用时间戳挡
        if (DateTimeOffset.Now - _lastOpen < TimeSpan.FromSeconds(3)) return;
        _lastOpen = DateTimeOffset.Now;

        App.MainWindow?.OpenExternal(link.Url);
    }

    // ════════════════════════════════════════════════════════════════
    // 出口③：在 Q 群中反馈
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 「在 Q 群中反馈」：打开 Q 群卡片 + 弹「加群后请复制反馈信息」浮窗。
    /// 浮窗里点「复制」才真正写剪贴板，并顺势摊开图文提交流程窗。
    ///
    /// ⚠️ 顺序必须**先弹浮窗、后开浏览器**：
    ///    浮窗走 <see cref="QqFeedbackFlyoutWindow"/>（FlyoutChrome），显示时会 SetForegroundWindow 抢一次前台；
    ///    若先开浏览器再弹浮窗，等于"刚把用户送到 Q 群卡片、又把前台抢回自己"。
    ///    反过来 = 最后抢到前台的是浏览器，浮窗靠置顶照样浮在最上面。
    /// </summary>
    private void QqGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var text = PrepareCopyText();
        if (text is null) return;

        // 去群里聊完回头再看，内容还在（同一个道理：跨进程读不到用户到底提交没有）
        FeedbackDraftStore.Save(Draft);

        QqFeedbackFlyoutWindow.Show(text);
        App.MainWindow?.OpenExternal(QqFeedback.GroupUrl);
    }

    /// <summary>
    /// 校验并拼出要交给 Q 群的完整反馈信息（标题 + 正文）。
    /// 返回 null = 校验没过，提示已经填进 <see cref="ErrorBar"/>。
    ///
    /// ⚠️ 拼的是**完整**正文（不做长度截断）—— 链接那条路会因为 URL 长度上限砍掉描述，
    ///    这里正是给它兜底的出口，所以截断提示条上"点这颗拿完整内容"的说法才成立。
    ///    标题也一并带上：用户多半整段贴到群相册，没标题对方不知道在说什么。
    /// </summary>
    private string? PrepareCopyText()
    {
        var outgoing = PrepareOutgoing();
        if (outgoing is null) return null;

        var body = Feedback.BuildBody(outgoing, SelectedApp(), EnvRowsIfEnabled());
        return Feedback.BuildTitle(outgoing) + "\n\n" + body;
    }

    // ════════════════════════════════════════════════════════════════
    // 本机环境信息
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 随反馈附上的本机信息（**2026-09-30 Nick 定的六项，顺序就是下面这个顺序**）：
    /// 客户端版本 / 系统版本 / OS 内部版本号 / CPU 型号 / 更新通道 / 主题颜色。
    ///
    /// ⚠️ 内部版本号要带 UBR（<c>26220.1234</c> 而不是 <c>26220</c>）—— 见 <see cref="SystemInfo.OsBuildEx"/>。
    /// ⚠️ CPU 只要**型号**，不要核数 / 频率 —— 反馈正文里维护者关心的是"这颗 CPU 会不会是原因"。
    /// </summary>
    private List<Feedback.EnvRow> EnvRows()
    {
        var rows = new List<Feedback.EnvRow>
        {
            new("客户端版本", ShellConfig.VersionPrefix + ShellConfig.ShellVersion),
            new("系统版本", SystemInfo.OsDisplay()),
            new("OS内部版本号", SystemInfo.OsBuildEx()),
            new("CPU型号", SystemInfo.CpuModel()),
            new("更新通道", App.Settings.Current.UpdateChannel == "insider" ? "预览版" : "正式版"),
            new("主题颜色", ActualTheme == ElementTheme.Dark ? "深色" : "浅色"),
        };

        // 空值不出现在正文里（比如注册表读不到）
        return rows.Where(r => r.Value.Length > 0).ToList();
    }

    /// <summary>
    /// 按「随反馈附上本机信息」的勾选状态给环境信息：没勾就返回 <c>null</c>，
    /// 正文 / 提交详情里那几行就整段不出现。
    /// ⚠️ 之前三个出口都无条件传 <see cref="EnvRows"/>，勾选框形同虚设 —— 用户取消勾选后
    ///    本机版本与系统信息照样跟着反馈出去（2026-10-04 修）。三条出口统一走这个方法。
    /// </summary>
    private List<Feedback.EnvRow>? EnvRowsIfEnabled() => Draft.IncludeEnv ? EnvRows() : null;

    private void UpdateEnvPreview()
    {
        EnvPreview.Text = "将附上：" + string.Join(" · ", EnvRows().Select(r => $"{r.Label} {r.Value}"));
    }
}
