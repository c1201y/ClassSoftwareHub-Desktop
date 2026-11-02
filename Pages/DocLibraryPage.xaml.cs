using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services.Docs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 文档库列表页（需求 15）。
///
/// <para>
/// 这一页只读**清单**（标题 / 作者 / 简介 / 标签），正文要等点开某篇时才下载 ——
/// 见 <see cref="DocLibrary"/> 的两层缓存说明。
/// </para>
/// <para>
/// 搜索框只在清单里查（标题、简介、标签、作者）；应用全局搜索同样只查这些，不碰正文。
/// </para>
/// </summary>
public sealed partial class DocLibraryPage : Page
{
    private bool _syncing;

    public DocLibraryPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        ApplyFilter();
        _ = SyncAsync(force: false);
    }

    private async Task SyncAsync(bool force)
    {
        if (_syncing) return;
        _syncing = true;
        RefreshButton.IsEnabled = false;

        try
        {
            var progress = new Progress<string>(message => StatusText.Text = message);
            var result = await DocLibrary.SyncAsync(force, progress);

            StatusText.Text = result.Ok
                ? $"{result.Message}　·　共 {DocLibrary.Articles.Count} 篇"
                : result.Message;

            ApplyFilter();
        }
        catch (Exception ex)
        {
            AppLog.Exception("docs", ex, "同步文档库");
            StatusText.Text = "文档库同步失败，正在显示本机已缓存的清单。";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            _syncing = false;
        }
    }

    private void ApplyFilter()
    {
        var query = (SearchBox.Text ?? "").Trim();
        var all = DocLibrary.Articles;

        IReadOnlyList<DocArticle> rows = all;
        if (query.Length > 0)
        {
            var parts = query
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();

            rows = all.Where(article => parts.All(part =>
                SearchText(article).Contains(part, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        ArticleList.ItemsSource = rows;

        if (rows.Count == 0)
        {
            EmptyText.Text = all.Count == 0
                ? "文档库还没有文章。投稿通过后，教程会出现在这里。"
                : "没有找到匹配的文章。换个关键词试试。";
            EmptyPanel.Visibility = Visibility.Visible;
            ArticleList.Visibility = Visibility.Collapsed;
        }
        else
        {
            EmptyPanel.Visibility = Visibility.Collapsed;
            ArticleList.Visibility = Visibility.Visible;
        }
    }

    private static string SearchText(DocArticle article) =>
        article.Title + "\n" + article.Summary + "\n" + article.Author + "\n"
        + string.Join(" ", article.Tags);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = SyncAsync(force: true);

    private void Submit_Click(object sender, RoutedEventArgs e)
        => App.MainWindow?.OpenExternal(DocLibrary.SubmitUrl());

    private void ArticleList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DocArticle article)
            App.MainWindow?.Shell.NavigateToDocArticle(article.Id);
    }
}
