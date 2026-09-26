using System;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>系统镜像下载：读内容包 text/mirror-sites.json，一行一个入口，点了交系统浏览器。</summary>
public sealed partial class MirrorToolPage : Page
{
    public MirrorToolPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Populate();
    }

    private void Populate()
    {
        var mirror = App.Content.Mirror;

        TitleText.Text = string.IsNullOrWhiteSpace(mirror.Title) ? "系统镜像下载" : mirror.Title;
        SubtitleText.Text = mirror.Subtitle;
        NoteText.Text = mirror.Note;

        if (!string.IsNullOrWhiteSpace(mirror.Disclaimer))
        {
            DisclaimerBar.Message = mirror.Disclaimer;
            DisclaimerBar.IsOpen = true;
        }

        Rows.ItemsSource = mirror.Sites;
        EmptyText.Visibility = mirror.Sites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Row_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            var url = fe.Tag as string;
            if (string.IsNullOrWhiteSpace(url) && fe.DataContext is MirrorSite site) url = site.Url;
            App.MainWindow?.OpenExternal(url);
        }
    }
}
