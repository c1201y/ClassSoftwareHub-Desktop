using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Services.License;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace ClassSoftwareHub.Desktop.Views.Submit.Steps;

/// <summary>
/// 第 5 步：核对并提交。
///
/// <para>
/// 提交逻辑沿用旧提交页：按 <see cref="SubmitEndpoint.Ordered"/> 逐个入口试，
/// 第一个给出提交接口响应的就是结果；全不通则保留本机草稿并给出四条备用路径。
/// </para>
///
/// <para>
/// ⛔ 缺失项不要只写一句「带 * 的必填项未填完」：这里逐条列出来，每条都能跳回对应步骤。
/// </para>
/// </summary>
public sealed partial class SubmitReviewPage : Page
{
    // 投稿是受保护路由，必须带身份头 → 走认证处理器（见 Services/License/AuthHandler.cs）
    private static readonly HttpClient Http = new(new AuthHandler(new HttpClientHandler()));

    private static int SubmitTimeoutMs => SubmitEndpoint.TimeoutMs;

    private const string RepoNewFileUrl = "https://github.com/c1201y/ClassSoftwareHub/new/main/submissions";

    private readonly SubmitDraft? _draft;
    private Dictionary<string, object?>? _pending;
    private bool _busy;

    private SubmitDraft? Draft => _draft;

    public SubmitReviewPage()
    {
        _draft = SubmitSession.Current?.Draft ?? SubmitDraft.CreateEmpty();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (SubmitSession.Current?.Completed != true)
        {
            ResultBar.IsOpen = false;
            SuccessPanel.Visibility = Visibility.Collapsed;
        }

        BuildSummary();
        BuildMissing();
    }

    // ── 摘要 ─────────────────────────────────────────────────────────────

    private void BuildSummary()
    {
        if (_draft is null) return;

        SummaryName.Text = _draft.Name.Trim().Length > 0 ? _draft.Name.Trim() : "未填写软件名称";
        SummaryId.Text = _draft.Id.Trim().Length > 0 ? $"#/download/{_draft.Id.Trim()}" : "软件 ID 未填写";

        SummaryRows.Children.Clear();

        var category = _draft.Category.Trim().Length > 0
            ? App.Content.CategoryName(_draft.Category)
            : "未选择";
        Row("分类", category);

        var platforms = _draft.Platforms.Count > 0
            ? string.Join(" / ", _draft.Platforms.Select(App.Content.PlatformName))
            : "未勾选";
        Row("适用系统", platforms);

        Row("版本", _draft.Version.Trim().Length > 0 ? _draft.Version.Trim() : "未填写");

        var downloads = _draft.Downloads.Count(item => item.Url.Trim().Length > 0);
        Row("下载项", downloads > 0 ? $"{downloads} 条" : "未填写");

        Row("联系方式", _draft.Contact.Trim().Length > 0 ? "已填写（加密提交）" : "未填写");

        UpdateIcon(_draft.Icon.Trim());
    }

    private void Row(string label, string value)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        row.Children.Add(new TextBlock { Text = label, FontSize = 13, Opacity = 0.6 });

        var text = new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        SummaryRows.Children.Add(row);
    }

    private void UpdateIcon(string url)
    {
        if (url.Length == 0)
        {
            SummaryIcon.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            SummaryIcon.Source = new BitmapImage(new Uri(url));
            SummaryIcon.Visibility = Visibility.Visible;
        }
        catch
        {
            SummaryIcon.Visibility = Visibility.Collapsed;
        }
    }

    // ── 缺失项 ───────────────────────────────────────────────────────────

    private void BuildMissing()
    {
        if (_draft is null) return;

        var missing = new List<(string Text, int Step)>();

        if (_draft.Name.Trim().Length == 0) missing.Add(("软件名称未填写", 1));
        if (_draft.Id.Trim().Length == 0) missing.Add(("软件 ID 未填写", 1));
        else if (!SoftwareSlug.IsValid(_draft.Id.Trim())) missing.Add(("软件 ID 格式不正确", 1));

        if (_draft.Category.Trim().Length == 0) missing.Add(("未选择分类", 1));
        if (_draft.Tagline.Trim().Length == 0) missing.Add(("一句话简介未填写", 1));
        if (_draft.Description.Trim().Length == 0) missing.Add(("详细介绍未填写", 1));
        if (_draft.Contact.Trim().Length == 0) missing.Add(("联系方式未填写", 1));
        if (_draft.Platforms.Count == 0) missing.Add(("未勾选适用系统", 2));
        if (!_draft.Downloads.Any(item => item.Url.Trim().Length > 0)) missing.Add(("没有下载直链", 2));

        MissingHost.Children.Clear();

        foreach (var (text, step) in missing)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });

            var jump = new Button { Content = "去修改", Tag = step, Padding = new Thickness(10, 0, 10, 0), FontSize = 13 };
            jump.Click += (_, _) =>
            {
                if (jump.Tag is int index) SubmitSession.Current?.JumpTo(index);
            };
            Grid.SetColumn(jump, 1);
            row.Children.Add(jump);

            MissingHost.Children.Add(row);
        }

        MissingPanel.Visibility = missing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 提交 ─────────────────────────────────────────────────────────────

    private readonly record struct Reply(bool Success, string? Message, string? Error);

    /// <summary>由窗口底部的「提交」按钮调用。</summary>
    public async Task SubmitAsync()
    {
        if (_busy || _draft is null) return;

        var payload = _draft.BuildPayload();
        var error = _draft.ValidateAll();
        if (error is not null)
        {
            BuildMissing();
            ShowResult(false, "还需补充", error);
            return;
        }

        _busy = true;
        Toast.Text = "";
        FallbackPanel.Visibility = Visibility.Collapsed;
        ShowResult(false, "", "");

        try
        {
            var (reply, endpoint) = await PostAsync(payload);

            if (reply is null)
            {
                // 所有入口都不通：草稿早已在本机，保留这次内容供兜底导出。
                // ⚠️ 这里不再开红色提示条 —— 下面那块警告已经把事情说清楚了，
                //    两条同时挂着只是把同一句话讲两遍。
                _draft.Save();
                _pending = payload;
                ShowResult(false, "", "");
                FallbackPanel.Visibility = Visibility.Visible;
                return;
            }

            if (endpoint is not null) SubmitEndpoint.Remember(endpoint);

            if (reply.Value.Success)
            {
                _pending = null;
                // ⚠️ 不能在这里再 Save()：MarkCompleted 会清草稿，先存后清等于白清
                SubmitSession.Current?.MarkCompleted();

                ShowResult(true, "已提交", string.IsNullOrWhiteSpace(reply.Value.Message)
                    ? "提交成功，审核通过后上架。"
                    : reply.Value.Message!);

                MissingPanel.Visibility = Visibility.Collapsed;
                FallbackPanel.Visibility = Visibility.Collapsed;
                SuccessPanel.Visibility = Visibility.Visible;
            }
            else
            {
                ShowResult(false, "提交未通过校验", reply.Value.Error ?? "服务端未接受该内容，检查后可重试。");
            }
        }
        catch (Exception ex)
        {
            ShowResult(false, "提交失败", "发生意外错误：" + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>按顺序试每个入口，返回第一个「确实是提交接口」的响应；全不通返回 (null, null)。</summary>
    private static async Task<(Reply? reply, string? endpoint)> PostAsync(Dictionary<string, object?> payload)
    {
        foreach (var baseUrl in SubmitEndpoint.Ordered())
        {
            try
            {
                using var cts = new CancellationTokenSource(SubmitTimeoutMs);
                using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var response = await Http.PostAsync(baseUrl + "/api/submit", content, cts.Token);
                var text = await response.Content.ReadAsStringAsync(cts.Token);

                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;

                var hasSuccess = root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
                var hasError = root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String;
                if (!hasSuccess && !hasError) continue;   // 回源没生效时会返回 nginx 错误页

                var message = root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String
                    ? msg.GetString()
                    : null;

                return (new Reply(hasSuccess, message, hasError ? err.GetString() : null), baseUrl);
            }
            catch
            {
                // 连不上 / 超时 / 响应不是 JSON → 换下一个入口
            }
        }

        return (null, null);
    }

    private void ShowResult(bool ok, string title, string message)
    {
        if (title.Length == 0)
        {
            ResultBar.IsOpen = false;
            return;
        }

        ResultBar.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        ResultBar.Title = title;
        ResultBar.Message = message;
        ResultBar.IsOpen = true;
    }

    // ── 兜底：重试 / 下载 / 复制 / 去 GitHub ─────────────────────────────

    private async void Retry_Click(object sender, RoutedEventArgs e) => await SubmitAsync();

    private void Restart_Click(object sender, RoutedEventArgs e) => SubmitSession.Current?.Restart();

    private (string Name, string Text)? BuildSubmissionFile()
    {
        var data = _pending ?? _draft?.BuildPayload();
        if (data is null) return null;

        var id = data.TryGetValue("id", out var value) ? value?.ToString() ?? "submission" : "submission";
        if (id.Trim().Length == 0) id = "submission";
        id = id.Trim();

        var now = DateTime.Now;
        var body = new Dictionary<string, object?>(data)
        {
            ["_提交时间"] = now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["_原始ID冲突"] = App.Content.Apps.Any(app => app.Id == id) ? true : null,
        };

        return ($"{id}-{now:yyyyMMdd-HHmmss}.json",
            JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async void DownloadSubmission_Click(object sender, RoutedEventArgs e)
    {
        var file = BuildSubmissionFile();
        if (file is null) { Toast.Text = "无可导出的内容"; return; }

        try
        {
            var picker = new FileSavePicker { SuggestedFileName = file.Value.Name };
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });

            var owner = SubmitSession.Current?.WindowHandle ?? IntPtr.Zero;
            if (owner != IntPtr.Zero) WinRT.Interop.InitializeWithWindow.Initialize(picker, owner);

            var target = await picker.PickSaveFileAsync();
            if (target is null) return;

            await FileIO.WriteTextAsync(target, file.Value.Text);
            Toast.Text = "提交文件已保存：" + target.Path;
        }
        catch (Exception ex)
        {
            Toast.Text = "保存失败：" + ex.Message;
        }
    }

    private void CopySubmission_Click(object sender, RoutedEventArgs e)
    {
        var file = BuildSubmissionFile();
        if (file is null) { Toast.Text = "无可复制的内容"; return; }

        try
        {
            var package = new DataPackage();
            package.SetText(file.Value.Text);
            Clipboard.SetContent(package);
            Toast.Text = "已复制提交 JSON";
        }
        catch (Exception ex)
        {
            Toast.Text = "复制失败：" + ex.Message;
        }
    }

    private async void OpenGithubSubmit_Click(object sender, RoutedEventArgs e)
    {
        var file = BuildSubmissionFile();
        var url = file is null
            ? RepoNewFileUrl
            : $"{RepoNewFileUrl}?filename={Uri.EscapeDataString(file.Value.Name)}";

        try { await Launcher.LaunchUriAsync(new Uri(url)); }
        catch (Exception ex) { Toast.Text = "无法打开浏览器：" + ex.Message; }
    }
}
