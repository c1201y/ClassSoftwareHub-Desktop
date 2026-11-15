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






















public sealed partial class FeedbackFormPage : Page
{
    
    private ImageSource? _iconReport;
    private ImageSource? _iconSuggestion;

    
    private string _pendingKind = "";

    
    
    
    
    
    private string _pageKind = "";

    
    private bool _suppress;

    
    private DateTimeOffset _lastOpen;

    
    private bool _busy;

    
    private string? _issueUrl;

    
    
    private bool _inited;

    
    private static Feedback.Draft Draft => FeedbackDraftStore.Current;

    public FeedbackFormPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Init();
    }

    
    
    
    
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pendingKind = e.Parameter as string ?? "";
    }

    
    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);

        
        SyncDraftFromForm();
        FeedbackDraftStore.Save(Draft);
    }

    private void Init()
    {
        if (_inited) return;
        _inited = true;

        SubKindCombo.ItemsSource = Feedback.ReportSubKinds;
        AppCombo.ItemsSource = App.Content.Apps;

        
        _iconReport = LoadIcon("report.png", "feedback-report.png");
        _iconSuggestion = LoadIcon("suggest.png", "feedback-suggest.png");

        
        if (_pendingKind.Length > 0) Draft.Kind = _pendingKind;
        if (Draft.Kind.Length == 0)
        {
            var saved = FeedbackDraftStore.Load();
            if (saved is not null) Draft.CopyFrom(saved);
        }
        if (Draft.Kind.Length == 0) Draft.Kind = Feedback.KindReport;   
        _pageKind = Draft.Kind;

        
        
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
        catch { return null; }        
    }

    
    
    
    
    
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

    
    private void ApplyKindHeader()
    {
        
        var key = Draft.Kind.Length > 0 ? Draft.Kind : _pageKind;
        FormKindText.Text = Feedback.FindKind(key)?.Title ?? "";
        FormKindImage.Source = key == Feedback.KindSuggestion ? _iconSuggestion : _iconReport;
        SubKindCombo.Visibility = key == Feedback.KindReport
            ? Visibility.Visible : Visibility.Collapsed;
    }

    
    
    

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
        
        EnvPreview.Opacity = IncludeEnvCheck.IsChecked == true ? 1.0 : 0.4;
    }

    
    
    

    
    
    
    
    
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

    
    private Feedback.IssueLink? Prepare()
    {
        var outgoing = PrepareOutgoing();
        if (outgoing is null) return null;

        var link = Feedback.BuildIssueLink(outgoing, SelectedApp(), EnvRowsIfEnabled());
        TruncateBar.IsOpen = link.Truncated;
        return link;
    }

    
    
    
    
    
    
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

    
    
    

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var outgoing = PrepareOutgoing();
        if (outgoing is null) return;

        
        
        
        TruncateBar.IsOpen = false;
        var title = outgoing.Title.Trim();
        var detail = Feedback.BuildSubmitDetail(outgoing, EnvRowsIfEnabled());

        
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

    
    private void ResetForm()
    {
        _suppress = true;
        TitleBox.Text = "";
        DetailBox.Text = "";
        ContactBox.Text = "";
        _suppress = false;

        UpdateEnvPreview();
        ApplyKindHeader();      
    }

    private void ViewIssue_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_issueUrl)) App.MainWindow?.OpenExternal(_issueUrl!);
    }

    
    
    

    private void OpenIssue_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var link = Prepare();
        if (link is null) return;

        
        FeedbackDraftStore.Save(Draft);

        
        if (DateTimeOffset.Now - _lastOpen < TimeSpan.FromSeconds(3)) return;
        _lastOpen = DateTimeOffset.Now;

        App.MainWindow?.OpenExternal(link.Url);
    }

    
    
    

    
    
    
    
    
    
    
    
    
    private void QqGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var text = PrepareCopyText();
        if (text is null) return;

        
        FeedbackDraftStore.Save(Draft);

        QqFeedbackFlyoutWindow.Show(text);
        App.MainWindow?.OpenExternal(QqFeedback.GroupUrl);
    }

    
    
    
    
    
    
    
    
    private string? PrepareCopyText()
    {
        var outgoing = PrepareOutgoing();
        if (outgoing is null) return null;

        var body = Feedback.BuildBody(outgoing, SelectedApp(), EnvRowsIfEnabled());
        return Feedback.BuildTitle(outgoing) + "\n\n" + body;
    }

    
    
    

    
    
    
    
    
    
    
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

        
        return rows.Where(r => r.Value.Length > 0).ToList();
    }

    
    
    
    
    
    
    private List<Feedback.EnvRow>? EnvRowsIfEnabled() => Draft.IncludeEnv ? EnvRows() : null;

    private void UpdateEnvPreview()
    {
        EnvPreview.Text = "将附上：" + string.Join(" · ", EnvRows().Select(r => $"{r.Label} {r.Value}"));
    }
}
