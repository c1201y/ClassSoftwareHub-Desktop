using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Submit.Steps;




public sealed partial class SubmitExtraPage : Page
{
    private readonly SubmitDraft? _draft;

    private SubmitDraft? Draft => _draft;

    public SubmitExtraPage()
    {
        _draft = SubmitSession.Current?.Draft ?? SubmitDraft.CreateEmpty();
        InitializeComponent();
    }
}
