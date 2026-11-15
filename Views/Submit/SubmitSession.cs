using System;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Submit;









public interface ISubmitStep
{
    
    string? Validate();

    
    void ShowError(string message);
}















public sealed class SubmitSession
{
    private static readonly Type[] Steps =
    {
        typeof(Steps.SubmitStartPage),
        typeof(Steps.SubmitInfoPage),
        typeof(Steps.SubmitPackagePage),
        typeof(Steps.SubmitExtraPage),
        typeof(Steps.SubmitReviewPage),
    };

    private readonly SubmitWizardWindow _window;

    private SubmitSession(SubmitWizardWindow window, SubmitDraft draft)
    {
        _window = window;
        Draft = draft;
    }

    public static SubmitSession? Current { get; private set; }

    public SubmitDraft Draft { get; }

    public int Index { get; private set; }

    public int StepCount => Steps.Length;

    public bool IsLast => Index >= Steps.Length - 1;

    
    public bool Completed { get; private set; }

    
    public IntPtr WindowHandle => _window.WindowHandle;

    
    public Type StepTypeAt(int index) => Steps[Math.Clamp(index, 0, Steps.Length - 1)];

    public static SubmitSession Attach(SubmitWizardWindow window, SubmitDraft draft)
        => Current = new SubmitSession(window, draft);

    public static void Detach() => Current = null;

    
    public void Prepare() => _window.NavigateToStep(0, forward: true, animate: false);

    public void Next()
    {
        if (Completed || IsLast) return;
        GoTo(Index + 1, forward: true);
    }

    public void Back()
    {
        if (Completed || Index == 0) return;
        GoTo(Index - 1, forward: false);
    }

    public void GoTo(int index, bool forward)
    {
        index = Math.Clamp(index, 0, Steps.Length - 1);
        Index = index;
        SaveDraft();
        _window.NavigateToStep(index, forward, animate: true);
    }

    
    public void JumpTo(int index)
    {
        Completed = false;
        GoTo(index, forward: false);
    }

    
    public void MarkCompleted()
    {
        Completed = true;
        SubmitDraft.ClearDraft();
        _window.RefreshNav();
    }

    
    public void Restart()
    {
        Completed = false;
        Draft.Reset();
        Index = 0;
        SubmitDraft.ClearDraft();
        _window.NavigateToStep(0, forward: false, animate: true);
        _window.RefreshNav();
    }

    
    public void SaveDraft()
    {
        if (Completed) return;
        Draft.Save();
    }

    
    public void RefreshChrome() => _window.RefreshNav();

    public void Close() => _window.CloseWizard();
}
