using System.Collections.Generic;
using System.Linq;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace ClassSoftwareHub.Desktop.Pages;











public sealed partial class ExperimentalPage : Page
{
    public ExperimentalPage()
    {
        InitializeComponent();

        
        
        var all = ExperimentalFeatureCatalog.All;
        CardColumnLeft.ItemsSource = all.Where((_, i) => i % 2 == 0).ToList();
        CardColumnRight.ItemsSource = all.Where((_, i) => i % 2 == 1).ToList();
    }

    
    private void Card_Tapped(object sender, TappedRoutedEventArgs e)
    {
        
        if ((sender as FrameworkElement)?.DataContext is ExperimentalFeature f && f.Tag.Length > 0)
            App.MainWindow?.Shell.NavigateTo(f.Tag);
    }

    
    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Opacity = 0.88;
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Opacity = 1.0;
    }
}
