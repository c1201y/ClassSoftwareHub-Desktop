using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.Storage.Pickers;

namespace ClassSoftwareHub.Desktop.Pages;










public sealed partial class ShellPage : UserControl
{
    
    public Grid TitleBarElement => TitleBarArea;

    public ShellPage()
    {
        InitializeComponent();

        
        

        InitSearch();

        
        
        ApplyNavLayout();

        
        BuildExperimentalNavItems();

        
        
        

        
        
        
        Loaded += (_, _) => UpdateTitleBarPassthrough();

        
        Services.DownloadManager.Current.Changed += UpdateDownloadBadge;
        UpdateDownloadBadge();

        
        Nav.DisplayModeChanged += (_, _) => ApplyPaneInset();
        ApplyPaneInset();

        
        
        
        
        ContentFrame.Navigating += (_, e) => PerfLog.NavBegin(CurrentTag);
        ContentFrame.Navigated += (_, _) =>
        {
            PerfLog.NavEnd(ContentFrame.Content);
            UpdateBackButton();
            Services.MemoryTrimmer.ScheduleAfterNavigate();
        };
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private void ApplyNavLayout()
    {
        var managed = new HashSet<string>(
            NavLayout.All.Select(p => p.Tag), StringComparer.OrdinalIgnoreCase);

        
        for (var i = Nav.MenuItems.Count - 1; i >= 0; i--)
        {
            if (Nav.MenuItems[i] is NavigationViewItem { Tag: string tag } && managed.Contains(tag))
                Nav.MenuItems.RemoveAt(i);
        }

        
        ClassroomToolsNav.MenuItems.Clear();
        UtilityToolsNav.MenuItems.Clear();
        SystemToolsNav.MenuItems.Clear();

        
        var top = NavLayout.All
            .Where(p => p.AboveFunctionPlot == false && NavLayout.IsTopLevel(p.Tag))
            .OrderBy(p => p.Group switch { ToolGroup.System => 2, ToolGroup.Utility => 1, _ => 0 })
            .ToList();

        var at = Nav.MenuItems.IndexOf(ClassroomToolsNav);
        foreach (var placeable in top)
            Nav.MenuItems.Insert(at++, NavigationItemFor(placeable));

        
        foreach (var placeable in NavLayout.Pages.Where(p => p.AboveFunctionPlot))
        {
            if (NavLayout.IsTopLevel(placeable.Tag))
                Nav.MenuItems.Insert(Nav.MenuItems.IndexOf(FunctionPlotNav), NavigationItemFor(placeable));
        }

        
        foreach (var group in new[] { ToolGroup.Classroom, ToolGroup.Utility, ToolGroup.System })
        {
            var parent = group switch
            {
                ToolGroup.System => SystemToolsNav,
                ToolGroup.Utility => UtilityToolsNav,
                _ => ClassroomToolsNav,
            };
            foreach (var placeable in NavLayout.SecondLevel(group))
            {
                parent.MenuItems.Add(new NavigationViewItem
                {
                    Content = placeable.Name,
                    Tag = placeable.Tag,
                    Icon = new FontIcon { Glyph = placeable.Glyph },
                });
            }
        }

        
        SelectTag(CurrentTag);

        

        AppLog.Step("shell", $"导航布局：一级 {Nav.MenuItems.OfType<NavigationViewItem>().Count()} 项，"
            + $"课堂工具 {ClassroomToolsNav.MenuItems.Count} 项，实用工具 {UtilityToolsNav.MenuItems.Count} 项，系统工具 {SystemToolsNav.MenuItems.Count} 项");
    }

    
    
    
    private NavigationViewItem NavigationItemFor(NavPlaceable placeable)
    {
        NavigationViewItem? existing = placeable.Tag switch
        {
            "announce" => AnnounceNav,
            "dictionary" => DictionaryNav,
            "docs" => DocsNav,
            _ => null,
        };

        return existing ?? new NavigationViewItem
        {
            Content = placeable.Name,
            Tag = placeable.Tag,
            Icon = new FontIcon { Glyph = placeable.Glyph },
        };
    }

    
    
    
    public void ReloadNavLayout()
    {
        ApplyNavLayout();

        
        
        if (CurrentTag is "tools-classroom" or "tools-utility" or "tools-system") NavigateTo(CurrentTag);
    }

    
    
    
    
    
    
    
    
    
    
    
    private void BuildExperimentalNavItems()
    {
        foreach (var def in ExperimentalFeatureCatalog.All)
        {
            ExperimentalNav.MenuItems.Add(new NavigationViewItem
            {
                Content = def.Name,
                Tag = def.Tag,
                Icon = new FontIcon { Glyph = def.Glyph },
            });
        }
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    

    
    
    
    
    
    
    
    

    

    
    
    
    
    
    
    
    

    
    
    
    
    

    

    
    
    
    
    
    
    
    
    
    
    

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    

    

    
    private void UpdateDownloadBadge()
    {
        try
        {
            var active = Services.DownloadManager.Current.ActiveCount;
            DownloadsNav.InfoBadge = active > 0 ? new InfoBadge { Value = active } : null;
        }
        catch
        {
            
        }
    }

    
    private void UpdateBackButton()
    {
        Nav.IsBackEnabled = ContentFrame.CanGoBack;
    }

    
    
    
    
    
    
    
    private void ApplyPaneInset()
    {
        var inset = Nav.DisplayMode == NavigationViewDisplayMode.Minimal ? 48d : 0d;
        if (Math.Abs(ContentFrame.Margin.Top - inset) > 0.5)
            ContentFrame.Margin = new Thickness(0, inset, 0, 0);
    }

    private void Nav_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
        UpdateBackButton();
    }

    
    
    
    
    public static string CurrentTag { get; private set; } = "home";

    
    public void SetIcon(Microsoft.UI.Xaml.Media.ImageSource? source) => TitleIcon.Source = source;

    
    public void Init()
    {
        TitleText.Text = ShellConfig.AppName;

        var start = StartupPageTag();
        SelectTag(start);
        NavigateTag(start);

        
        
        if (StartupToolId() is { } toolId
            && ToolCatalog.All.FirstOrDefault(t => t.Id == toolId)?.Page is { } page)
        {
            NavigateToTool(page);
        }
    }

    
    private static string? StartupToolId()
    {
        try
        {
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (!arg.StartsWith("--tool=", StringComparison.OrdinalIgnoreCase)) continue;
                var id = arg["--tool=".Length..].Trim();
                if (id.Length > 0) return id;
            }
        }
        catch
        {
            
        }

        return null;
    }

    
    
    
    
    
    
    
    
    
    private static string StartupPageTag()
    {
        try
        {
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (!arg.StartsWith("--page=", StringComparison.OrdinalIgnoreCase)) continue;
                var tag = arg["--page=".Length..].Trim();
                if (tag.Length > 0) return tag;
            }
        }
        catch
        {
            
        }

        return "home";
    }

    
    public void NavigateTo(string tag)
    {
        
        
        
        SelectTag(tag switch
        {
            "feedback" or "feedback-form" or "changelog" => "about",
            _ => tag,
        });
        NavigateTag(tag);
    }

    
    
    
    
    
    
    
    public void NavigateToSettings(string? section)
    {
        NavigateTo("settings");
        if (ContentFrame.Content is SettingsPage page) page.ActivateSection(section);
    }

    
    
    
    
    
    public void NavigateToExperimental()
    {
        NavigateTo("experimental");
        ExperimentalNav.IsExpanded = true;
    }

    
    
    
    
    public void NavigateToTools()
    {
        NavigateTo("tools");
        ClassroomToolsNav.IsExpanded = true;
        SystemToolsNav.IsExpanded = false;
    }

    
    
    
    
    public void NavigateToClassroomTools()
    {
        NavigateTo("tools-classroom");
        ClassroomToolsNav.IsExpanded = true;
        SystemToolsNav.IsExpanded = false;
    }

    
    public void NavigateToSystemTools()
    {
        NavigateTo("tools-system");
        ClassroomToolsNav.IsExpanded = false;
        SystemToolsNav.IsExpanded = true;
    }

    public void NavigateToUtilityTools()
    {
        NavigateTo("tools-utility");
        ClassroomToolsNav.IsExpanded = false;
        UtilityToolsNav.IsExpanded = true;
        SystemToolsNav.IsExpanded = false;
    }

    
    
    
    
    public void NavigateToTool(Type pageType) => NavigateToTool(pageType, null, viaList: true);

    
    
    
    
    
    
    public void NavigateToTool(Type pageType, object? parameter, bool viaList = true)
    {
        SelectTag("tools");
        if (viaList && ContentFrame.CurrentSourcePageType != typeof(ToolsPage))
            ContentFrame.Navigate(typeof(ToolsPage));

        
        
        var def = ToolCatalog.All.FirstOrDefault(t => t.Page == pageType);
        if (def is not null && ToolCarrier.RequestFor(def.WebToolId, pageType, parameter) is { } request)
        {
            ContentFrame.Navigate(typeof(ToolHostPage), request);
            return;
        }

        ContentFrame.Navigate(pageType, parameter);
    }

    
    
    
    
    
    
    
    public void NavigateToFeedbackForm(string kind)
    {
        SelectTag("feedback");
        ContentFrame.Navigate(typeof(FeedbackFormPage), kind);
    }

    
    public void NavigateTag(string tag)
    {
        try
        {
            NavigateTagCore(tag);
        }
        catch (Exception ex)
        {
            
            
            LogNavFailure(tag, ex);
            if (tag != "home")
            {
                try { NavigateTagCore("home"); } catch { }
            }
        }
    }

    private void NavigateTagCore(string tag)
    {
        CurrentTag = tag;   

        
        
        var toolDef = ToolCatalog.All.FirstOrDefault(t => t.Id == tag);
        if (toolDef?.Page is not null)
        {
            
            
            
            
            
            if (ToolCarrier.RequestFor(toolDef.WebToolId, toolDef.Page, null) is { } toolRequest)
                ContentFrame.Navigate(typeof(ToolHostPage), toolRequest);
            else
                ContentFrame.Navigate(toolDef.Page);
            return;
        }

        switch (tag)
        {
            case "announce":
                
                
                ContentFrame.Navigate(typeof(Tools.AnnouncePage));
                return;
            case "submit":
                
                
                
                
                ContentFrame.Navigate(typeof(SubmitPage));
                return;
            case "about":
                
                ContentFrame.Navigate(typeof(AboutPage));
                return;
            case "feedback":
                ContentFrame.Navigate(typeof(FeedbackPage));
                return;
            case "feedback-form":
                
                
                
                ContentFrame.Navigate(typeof(FeedbackFormPage));
                return;
            case "downloads":
                ContentFrame.Navigate(typeof(DownloadsPage));
                return;
            case "machinecheck":
                ContentFrame.Navigate(typeof(MachineCheckPage));
                return;
            case "procguard":
                ContentFrame.Navigate(typeof(ProcessGuardPage));
                return;
            case "uninstaller":
                
                ContentFrame.Navigate(typeof(UninstallPage));
                return;
            case "startup":
                
                ContentFrame.Navigate(typeof(StartupPage));
                return;
            
            
            
            
            
            case "logs":
                ContentFrame.Navigate(typeof(LogViewerPage));
                return;
            
            
            
            case "dictionary":
                
                
                ContentFrame.Navigate(typeof(DictionaryPage));
                return;
            case "experimental":
                
                ContentFrame.Navigate(typeof(ExperimentalPage));
                return;
            case "changelog":
                ContentFrame.Navigate(typeof(ChangelogPage));
                return;
            case "settings-migration":
                Views.Migration.MigrationWindow.Show();
                return;
            case "settings":
                ContentFrame.Navigate(typeof(SettingsPage));
                return;
            case "sidebar":
                ContentFrame.Navigate(typeof(SidebarLayoutPage));
                return;
            case "tools-classroom":
                ContentFrame.Navigate(typeof(ToolsPage), "classroom");
                return;
            case "tools-utility":
                ContentFrame.Navigate(typeof(ToolsPage), "utility");
                return;
            case "tools-system":
                ContentFrame.Navigate(typeof(ToolsPage), "system");
                return;
            case "apps":
                ContentFrame.Navigate(typeof(SoftwarePage), "");
                return;
            case "hubai":
                
                ContentFrame.Navigate(typeof(HubAiPage));
                return;
            case "docs":
                
                ContentFrame.Navigate(typeof(DocLibraryPage));
                return;
            case "hubai-settings":
                
                ContentFrame.Navigate(typeof(HubAiSettingsPage));
                return;
            case "home":
            default:
                if (tag.StartsWith("cat:", StringComparison.Ordinal))
                    ContentFrame.Navigate(typeof(SoftwarePage), tag.Substring(4));
                else
                    ContentFrame.Navigate(typeof(WelcomePage));
                return;
        }
    }

    
    private static void LogNavFailure(string tag, Exception ex)
    {
        try
        {
            Core.AppLog.Error("crash", $"导航到「{tag}」失败: {ex}");
        }
        catch
        {
            
        }
    }

    private void SelectTag(string tag)
    {
        
        
        
        
        
        foreach (var top in AllTopItems())
        {
            if ((top.Tag as string) != tag) continue;

            if (top.MenuItems.Count > 0)
            {
                
                Nav.SelectedItem = null;
                foreach (var item in AllNavigationItems()) item.IsSelected = false;
                top.IsSelected = true;
            }
            else
            {
                
                foreach (var item in AllNavigationItems())
                    if (item.MenuItems.Count > 0) item.IsSelected = false;
                Nav.SelectedItem = top;
            }
            return;
        }

        
        
        foreach (var top in AllTopItems())
        {
            foreach (var child in top.MenuItems.OfType<NavigationViewItem>())
            {
                if ((child.Tag as string) != tag) continue;

                top.IsExpanded = true;
                Nav.SelectedItem = child;
                return;
            }
        }
    }

    
    private IEnumerable<NavigationViewItem> AllNavigationItems()
    {
        foreach (var top in AllTopItems())
        {
            yield return top;
            foreach (var child in top.MenuItems.OfType<NavigationViewItem>()) yield return child;
        }
    }

    
    private IEnumerable<NavigationViewItem> AllTopItems()
        => Nav.MenuItems.OfType<NavigationViewItem>()
              .Concat(Nav.FooterMenuItems.OfType<NavigationViewItem>());

    private void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem item) return;

        
        if (item.Tag is not string tag || tag.Length == 0) return;

        
        
        
        NavigateTo(tag);
    }

    
    
    
    
    
    
    
    
    
    
    

    private List<SearchHit> _hits = new();
    private string _lastOpenedKey = "";
    private DateTime _lastOpenedAt = DateTime.MinValue;

    private void InitSearch()
    {
        
        
        
        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        
        var accel = new KeyboardAccelerator { Key = VirtualKey.K, Modifiers = VirtualKeyModifiers.Control };
        accel.Invoked += (_, e) =>
        {
            e.Handled = true;
            FocusSearch();
        };
        RootGrid.KeyboardAccelerators.Add(accel);
    }

    
    private void FocusSearch()
    {
        
        if (FindDescendant<TextBox>(SearchBox) is { } input) input.Focus(FocusState.Programmatic);
        else SearchBox.Focus(FocusState.Programmatic);

        RefreshResults();
    }

    
    
    
    private void TitleBarArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var w = e.NewSize.Width;
        SearchBox.Width = w >= 880 ? 360d : w >= 720 ? 250d : 170d;

        
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateTitleBarPassthrough);
    }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    private void UpdateTitleBarPassthrough()
    {
        try
        {
            if (App.MainWindow is not { } main) return;
            if (XamlRoot is not { } root) return;
            if (SearchBox.ActualWidth <= 0 || SearchBox.ActualHeight <= 0) return;

            var bounds = SearchBox
                .TransformToVisual(null)
                .TransformBounds(new Windows.Foundation.Rect(
                    0, 0, SearchBox.ActualWidth, SearchBox.ActualHeight));

            var scale = root.RasterizationScale;
            var rect = new Windows.Graphics.RectInt32(
                (int)Math.Round(bounds.X * scale),
                (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale),
                (int)Math.Round(bounds.Height * scale));

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(main);
            var source = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));

            source.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, new[] { rect });
        }
        catch (Exception ex)
        {
            
            AppLog.Warn("shell", "搜索框拖动区透传设置失败：" + ex.Message);
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        
        
        if (args.Reason == AutoSuggestionBoxTextChangeReason.SuggestionChosen) return;

        
        
        RefreshResults();
    }

    
    private void RefreshResults()
    {
        var query = SearchBox.Text ?? "";
        _hits = SearchIndex.Query(query).SelectMany(g => g.Hits).ToList();

        SearchBox.ItemsSource = _hits;
        SearchBox.IsSuggestionListOpen = _hits.Count > 0;
    }

    
    private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not SearchHit hit) return;
        sender.IsSuggestionListOpen = false;
        OpenHit(hit);
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        
        if (args.ChosenSuggestion is SearchHit hit) OpenHit(hit);
        else if (_hits.Count > 0) OpenHit(_hits[0]);
    }

    private void OpenHit(SearchHit hit)
    {
        
        
        var now = DateTime.UtcNow;
        if (hit.Key == _lastOpenedKey && (now - _lastOpenedAt).TotalMilliseconds < 600) return;
        _lastOpenedKey = hit.Key;
        _lastOpenedAt = now;

        switch (hit.Kind)
        {
            case SearchHitKind.App:
                NavigateToApp(hit.Key);
                break;

            case SearchHitKind.Doc:
                NavigateToDocArticle(hit.Key);
                break;

            default:
                
                if (hit.Target == "submit")
                {
                    ClassSoftwareHub.Desktop.Views.Submit.SubmitWizardWindow.Show();
                    break;
                }
                NavigateTo(hit.Target);
                break;
        }
    }

    
    
    
    
    public void NavigateToDocArticle(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        SelectTag("docs");
        ContentFrame.Navigate(typeof(DocArticlePage), id);
    }

    
    
    
    
    private void NavigateToApp(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        SelectTag("apps");
        ContentFrame.Navigate(typeof(DetailPage), id);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindDescendant<T>(child) is { } deep) return deep;
        }
        return null;
    }
}
