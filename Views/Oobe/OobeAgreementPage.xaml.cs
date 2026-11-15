using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Oobe;





public sealed partial class OobeAgreementPage : Page, IOobeStepGuard
{
    public OobeAgreementPage()
    {
        InitializeComponent();
        BuildBody();
    }

    public bool CanAdvance => AgreeCheck.IsChecked == true;

    private void BuildBody()
    {
        
        
        
        
        
        var doc = Services.LegalTextSource.Find("agreement");
        if (doc is null)
        {
            AgreementBody.Children.Add(Paragraph("条款内容加载失败，请稍后重试。", secondary: true));
            return;
        }

        AgreementBody.Children.Add(new TextBlock
        {
            Text = $"{doc.Title}　{doc.VersionLine}",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        if (doc.Intro.Length > 0) AgreementBody.Children.Add(Paragraph(doc.Intro));

        foreach (var section in doc.Sections)
        {
            AgreementBody.Children.Add(new TextBlock
            {
                Text = section.Heading,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });

            foreach (var item in section.Items) AgreementBody.Children.Add(Paragraph(item));
        }
    }

    private static TextBlock Paragraph(string text, bool secondary = false) => new()
    {
        
        Text = text.Replace("**", ""),
        FontSize = 12.5,
        Opacity = secondary ? 0.6 : 0.8,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 19,
    };

    private void Agree_Click(object sender, RoutedEventArgs e)
    {
        AgreeHint.Visibility = CanAdvance ? Visibility.Collapsed : Visibility.Visible;
        OobeSession.Current?.RefreshChrome();
    }
}
