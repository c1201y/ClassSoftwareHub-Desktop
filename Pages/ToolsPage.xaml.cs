using System;
using System.Collections.Generic;
using System.Linq;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages;


public sealed partial class ToolsPage : Page
{
    public ToolsPage()
    {
        InitializeComponent();

        

        
        _loading = true;
        TraySwitch.IsOn = App.Settings.Current.CloseToTray;
        _loading = false;
    }

    private bool _loading;

    
    
    
    
    
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var group = string.Equals(e.Parameter as string, "system", StringComparison.OrdinalIgnoreCase)
            ? ToolGroup.System
            : ToolGroup.Classroom;

        
        
        
        var items = NavLayout.SecondLevel(group).ToList();

        
        CardColumnLeft.ItemsSource = items.Where((_, i) => i % 2 == 0).ToList();
        CardColumnRight.ItemsSource = items.Where((_, i) => i % 2 == 1).ToList();

        TitleText.Text = group == ToolGroup.System ? "系统工具" : "课堂工具";

        EmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        
        
        
        
        PaletteCard.Visibility = group == ToolGroup.System ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenPalette_Click(object sender, RoutedEventArgs e)
        => Views.ToolPaletteWindow.ShowTool();

    private void TraySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.MainWindow?.SetCloseToTray(TraySwitch.IsOn);
    }

    
    private void Card_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        
        if ((sender as FrameworkElement)?.DataContext is NavPlaceable placeable)
            App.MainWindow?.Shell.NavigateTo(placeable.Tag);
    }

    
    private void Card_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Border b) b.Opacity = 0.88;
    }

    private void Card_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Border b) b.Opacity = 1.0;
    }
}
