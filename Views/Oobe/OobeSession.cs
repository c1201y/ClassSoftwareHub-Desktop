using System;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views.Oobe;





public interface IOobeStepGuard
{
    bool CanAdvance { get; }
}















public sealed class OobeSession
{
    private static readonly Type[] Steps =
    {
        typeof(OobeWelcomePage),
        typeof(OobeAgreementPage),
        typeof(OobeUpdatePage),
        typeof(OobeAppearancePage),
        typeof(OobeSidebarPage),
        typeof(OobeSidebarPositionPage),
        typeof(OobeSidebarSizePage),
        typeof(OobeScreenshotPage),
        typeof(OobeStartupPage),
        typeof(OobeDownloadsPage),
        typeof(OobeAiPage),
        typeof(OobeFinishPage),
    };

    public static OobeSession? Current { get; private set; }

    private readonly WelcomeWindow _window;

    private OobeSession(WelcomeWindow window, OobeDraft draft)
    {
        _window = window;
        Draft = draft;
    }

    public OobeDraft Draft { get; }

    
    public event EventHandler? Started;

    public int Index { get; private set; }

    public bool IsLast => Index >= Steps.Length - 1;

    public static OobeSession Attach(WelcomeWindow window, OobeDraft draft) =>
        Current = new OobeSession(window, draft);

    public static void Detach() => Current = null;

    
    
    
    
    public void Prepare() => _window.NavigateToPage(Steps[0], forward: true, animate: false);

    
    public void Play() => Started?.Invoke(this, EventArgs.Empty);

    public void Next()
    {
        if (!IsLast) GoTo(Index + 1, forward: true);
    }

    public void Back()
    {
        if (Index > 0) GoTo(Index - 1, forward: false);
    }

    
    public void GoTo(int index, bool forward)
    {
        index = Math.Clamp(index, 0, Steps.Length - 1);
        Index = index;
        _window.NavigateToPage(Steps[index], forward, animate: true);
    }

    public void PreviewTheme(string theme) => _window.ThemeRoot.RequestedTheme = ThemeHost.Map(theme);

    
    public void PreviewBackdrop(string kind) => _window.PreviewBackdrop(kind);

    
    public IntPtr WindowHandle => _window.WindowHandle;

    
    public void RefreshChrome() => _window.UpdateNav();

    public void Finish() => _window.FinishOobe();

    public void FinishAndNavigate(string tag) => _window.FinishAndNavigate(tag);
}
