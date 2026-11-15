using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Submit.Steps;

/// <summary>
/// 第 4 步：补充信息。整页选填，不实现 <see cref="ISubmitStep"/> —— 没有可拦住前进的条件。
/// </summary>
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
