using System;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClassSoftwareHub.Desktop.Pages;










public sealed partial class FeedbackPage : Page
{
    
    private ImageSource? _iconReport;
    private ImageSource? _iconSuggestion;

    public FeedbackPage()
    {
        InitializeComponent();
        
        Loaded += (_, _) => Init();
    }

    private void Init()
    {
        
        _iconReport = LoadIcon("report.png", "feedback-report.png");
        _iconSuggestion = LoadIcon("suggest.png", "feedback-suggest.png");
        KindIconReport.Source = _iconReport;
        KindIconSuggestion.Source = _iconSuggestion;

        RefreshDraftBar();

        
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
        catch { return null; }        
    }

    
    
    
    
    
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
                EndPoint = new Windows.Foundation.Point(1, 0.18),     
            };

            brush.GradientStops.Add(new GradientStop
            {
                Offset = 0,
                Color = Windows.UI.Color.FromArgb(41, accent.R, accent.G, accent.B),   
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

    
    
    

    
    
    
    
    
    private void KindCard_Click(object sender, RoutedEventArgs e)
    {
        var key = (sender as FrameworkElement)?.Tag as string ?? "";
        if (key.Length == 0) return;
        NavigateToForm(key);
    }

    private static void NavigateToForm(string kind)
        => App.MainWindow?.Shell.NavigateToFeedbackForm(kind);

    
    
    

    
    
    
    
    
    
    
    
    
    
    private void RefreshDraftBar()
    {
        var saved = FeedbackDraftStore.Load();
        DraftBar.IsOpen = HasTyped(FeedbackDraftStore.Current) || (saved is not null && HasTyped(saved));
    }

    
    private static bool HasTyped(Feedback.Draft draft) =>
        draft.Title.Trim().Length > 0
        || draft.Detail.Trim().Length > 0
        || draft.Contact.Trim().Length > 0;

    private void ResumeDraft_Click(object sender, RoutedEventArgs e)
    {
        var draft = FeedbackDraftStore.Current;

        
        
        if (draft.Kind.Length == 0)
        {
            var saved = FeedbackDraftStore.Load();
            if (saved is null) return;

            
            draft.CopyFrom(saved);
        }

        if (draft.Kind.Length == 0) return;
        NavigateToForm(draft.Kind);
    }

    
    
    
    
    
    
    
    
    
    private async void DraftBar_Close(InfoBar sender, object args)
    {
        var dialog = new ContentDialog
        {
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = XamlRoot,
            Title = "丢弃这份反馈草稿？",
            Content = "已填写的内容会被删除，且无法恢复。",
            PrimaryButtonText = "丢弃",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,     
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            DraftBar.IsOpen = true;      
            return;
        }

        FeedbackDraftStore.Discard();
        DraftBar.IsOpen = false;
    }

    private void OpenIssueList_Click(object sender, RoutedEventArgs e)
        => App.MainWindow?.OpenExternal(Feedback.IssueListUrl);
}
