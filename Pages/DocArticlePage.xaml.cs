using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Controls;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services.Docs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 文档正文页（需求 15）。
///
/// <para>
/// 进来先确保这篇的正文已缓存（<see cref="DocLibrary.EnsureContentAsync"/>：内容没变就不联网），
/// 再交给 <see cref="DocView"/> 渲染。正文缓存是无扩展名的随机文件，只有本程序知道怎么读。
/// </para>
/// <para>
/// ⚠️ 链接可能指向一篇还没同步过的文章（比如刚投稿、或本机从没进过文档库）：
/// 找不到就先同步一次清单再找，别直接报"不存在"。
/// </para>
/// </summary>
public sealed partial class DocArticlePage : Page
{
    private readonly DocView _view = new();

    private string _id = "";
    private CancellationTokenSource? _cts;
    private bool _loading;

    public DocArticlePage()
    {
        InitializeComponent();
        DocHost.Content = _view;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _id = e.Parameter as string ?? "";
        _ = LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _cts?.Cancel();
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        ShowLoading("正在读取文档正文。");

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var article = DocLibrary.Find(_id);
            if (article is null)
            {
                // 本机清单可能是空的（从没进过文档库页面）：先同步一次再找
                ShowLoading("正在检查文档库。");
                await DocLibrary.SyncAsync(force: false, progress: null, ct: ct);
                article = DocLibrary.Find(_id);
            }

            if (article is null)
            {
                ShowError("这篇文档不在当前的文档库里。它可能已经下架，或者链接写错了。");
                return;
            }

            ApplyHeader(article);

            var progress = new Progress<string>(message => LoadingText.Text = message);
            var content = await DocLibrary.EnsureContentAsync(article.Id, progress, ct);
            ct.ThrowIfCancellationRequested();

            ShowLoading("正在排版。");
            _view.Clear();
            await _view.RenderAsync(content, ct);

            HideOverlays();
        }
        catch (OperationCanceledException)
        {
            // 用户已经离开这一页
        }
        catch (Exception ex)
        {
            AppLog.Exception("docs", ex, $"打开文档 {_id}");
            ShowError("正文读取失败，可能是网络不通或仓库暂时无法访问。检查网络后可以点「重试」。");
        }
        finally
        {
            _loading = false;
        }
    }

    private void ApplyHeader(DocArticle article)
    {
        TitleText.Text = article.Title;

        var parts = new List<string>
        {
            "作者：" + (article.Author.Length > 0 ? article.Author : "未署名"),
        };
        if (article.DisplayDate.Length > 0) parts.Add(article.DisplayDate);
        parts.Add(article.TypeLabel);
        if (article.Tags.Count > 0) parts.Add(string.Join(" / ", article.Tags));

        MetaText.Text = string.Join("　·　", parts);
        SourceButton.IsEnabled = true;
    }

    private void ShowLoading(string message)
    {
        LoadingText.Text = message;
        LoadingPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        BodyScroll.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        LoadingPanel.Visibility = Visibility.Collapsed;
        BodyScroll.Visibility = Visibility.Collapsed;
    }

    private void HideOverlays()
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        BodyScroll.Visibility = Visibility.Visible;
    }

    private void Retry_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void Source_Click(object sender, RoutedEventArgs e)
        => App.MainWindow?.OpenExternal(DocLibrary.FolderUrl(_id));

    private void Submit_Click(object sender, RoutedEventArgs e)
        => App.MainWindow?.OpenExternal(DocLibrary.SubmitUrl(TitleText.Text));
}
