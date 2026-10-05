using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// 系统镜像下载：读 text/mirror-sites.json（随软件清单一并从站点仓库同步下来），
/// 一行一个入口，点了交系统浏览器。
/// ⚠️ 安装包不自带内容包之后，这份清单也是联网才有的 —— 所以本页要订阅
/// <c>App.Content.Changed</c>，否则首启时先点进来会一直停在"未读取到镜像清单"。
/// </summary>
public sealed partial class MirrorToolPage : Page
{
    public MirrorToolPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        App.Content.Changed -= OnContentChanged;   // 先退订再订阅：重复进出也只挂一次
        App.Content.Changed += OnContentChanged;
        Populate();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        App.Content.Changed -= OnContentChanged;
        base.OnNavigatedFrom(e);
    }

    private void OnContentChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnContentChanged);
            return;
        }
        Populate();
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

        // 空的时候说清楚是"还在拿"还是"没拿到"，别让用户以为这页坏了
        if (mirror.Sites.Count == 0)
            EmptyText.Text = App.Content.IsSyncing
                ? "正在获取镜像清单…"
                : "未读取到镜像清单：它随软件清单一并从网络获取，请先在「软件下载」页确认清单已获取到。";
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
