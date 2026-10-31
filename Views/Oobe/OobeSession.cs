using System;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views.Oobe;

/// <summary>
/// 页面可以拦住「下一步」（例如用户协议还没勾选）—— 导航壳在前进前问它。
/// 页面状态变了要调 <see cref="OobeSession.RefreshChrome"/>，按钮才会跟着变灰/恢复。
/// </summary>
public interface IOobeStepGuard
{
    bool CanAdvance { get; }
}

/// <summary>
/// 一次向导运行的全部上下文：步骤表、当前位置、草稿，以及前进/后退/收尾。
///
/// <para>
/// 照 ClassIsland 的 <c>WelcomeWindow</c> 组织：窗口只放一个 Frame，
/// 页面类型排成一条线，前进/后退就是在下标上 ±1；页面实例由 Frame 缓存
/// （每个 Page 标了 <c>NavigationCacheMode="Required"</c>），
/// 所以往回翻时用户刚填的值还在。
/// </para>
///
/// <para>
/// 页面从这里拿草稿、发导航请求，不直接碰窗口。窗口销毁时置空 <see cref="Current"/>。
/// </para>
/// </summary>
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

    /// <summary>窗口第一次显示出来（Activated）时触发 —— 开场动画等它，避免在不可见时就播掉了。</summary>
    public event EventHandler? Started;

    public int Index { get; private set; }

    public bool IsLast => Index >= Steps.Length - 1;

    public static OobeSession Attach(WelcomeWindow window, OobeDraft draft) =>
        Current = new OobeSession(window, draft);

    public static void Detach() => Current = null;

    /// <summary>
    /// 构造窗口时就导航到第一页：页面与开场控件的构造放在窗口显示之前，
    /// 显示出来的第一帧就有内容，不会先黑一片再慢慢搭。动画仍等 <see cref="Play"/>。
    /// </summary>
    public void Prepare() => _window.NavigateToPage(Steps[0], forward: true, animate: false);

    /// <summary>窗口真的显示之后才调 —— 构造期间播动画，用户看不到。</summary>
    public void Play() => Started?.Invoke(this, EventArgs.Empty);

    public void Next()
    {
        if (!IsLast) GoTo(Index + 1, forward: true);
    }

    public void Back()
    {
        if (Index > 0) GoTo(Index - 1, forward: false);
    }

    /// <summary>完成页的第一个轮播页再往回，就是退回上一步。</summary>
    public void GoTo(int index, bool forward)
    {
        index = Math.Clamp(index, 0, Steps.Length - 1);
        Index = index;
        _window.NavigateToPage(Steps[index], forward, animate: true);
    }

    public void PreviewTheme(string theme) => _window.ThemeRoot.RequestedTheme = ThemeHost.Map(theme);

    /// <summary>背景材质预览：只改本窗显示，收尾时才写进设置。</summary>
    public void PreviewBackdrop(string kind) => _window.PreviewBackdrop(kind);

    /// <summary>向导窗口句柄 —— 目录选择器之类要它做 owner。</summary>
    public IntPtr WindowHandle => _window.WindowHandle;

    /// <summary>页内状态变了（比如完成页翻到最后一屏），让底部导航条重读一次按钮文案。</summary>
    public void RefreshChrome() => _window.UpdateNav();

    public void Finish() => _window.FinishOobe();

    public void FinishAndNavigate(string tag) => _window.FinishAndNavigate(tag);
}
