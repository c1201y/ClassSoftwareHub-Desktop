using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 统一的「内容延伸标题栏」（2026-10-19）。
///
/// <para>
/// 效果：像 WinUI Gallery 那样——**没有独立标题条**，最小化/最大化/关闭浮在内容右上角，
/// 标题文字由我们自己画在左上角，整条可拖动。Nick 的原话：「使用标题栏非常的丑」。
/// </para>
/// <para>
/// ⚠️ 写法照着 <c>QqFeedbackGuideWindow</c> 那套（它早就这么做了），四件事缺一不可：
/// <list type="number">
///   <item><c>SetBorderAndTitleBar(true, true)</c> —— **保留**边框：阴影和圆角都靠系统。⛔ 别传 false，
///         那样窗口会变成没有圆角的硬方块，比原来的标题栏更难看。</item>
///   <item><c>ExtendsContentIntoTitleBar = true</c> + <c>SetTitleBar(元素)</c> —— 拖动交给系统（触屏也认）。</item>
///   <item>标题栏按钮透明，并按深浅色重算前景色 —— 否则深色窗体上黑按钮几乎看不见。</item>
///   <item>最后补一次 <c>WindowChrome.SetRounded</c> —— 第 2 步会把窗口圆角偏好按回 Default（实测变直角）。</item>
/// </list>
/// </para>
/// <para>
/// 调用前提：XAML 里要有一段高度 32~44 的拖拽区（<paramref name="dragElement"/>），
/// 并且给它留出右上角按钮的位置（右边留 140dip 左右，不然标题会钻到按钮底下）。
/// </para>
/// </summary>
public static class ModernWindow
{
    /// <summary>把 <paramref name="window"/> 换成自绘标题栏。<paramref name="themeRoot"/> 用来跟随深浅色。</summary>
    public static void ApplyTitleBar(Window window, UIElement dragElement, FrameworkElement themeRoot)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            // 边框留着：圆角与阴影全靠系统画
            presenter.SetBorderAndTitleBar(true, true);
        }

        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(dragElement);

        var bar = appWindow.TitleBar;
        bar.ExtendsContentIntoTitleBar = true;
        bar.PreferredHeightOption = TitleBarHeightOption.Standard;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;

        void UpdateColors()
        {
            var dark = themeRoot.ActualTheme == ElementTheme.Dark;

            bar.ButtonForegroundColor = dark ? Colors.White : Windows.UI.Color.FromArgb(255, 30, 30, 30);
            bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
            bar.ButtonHoverBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(26, 255, 255, 255)
                : Windows.UI.Color.FromArgb(20, 0, 0, 0);
            bar.ButtonPressedForegroundColor = dark
                ? Windows.UI.Color.FromArgb(255, 200, 200, 200)
                : Windows.UI.Color.FromArgb(255, 90, 90, 90);
            bar.ButtonInactiveForegroundColor = dark
                ? Windows.UI.Color.FromArgb(160, 255, 255, 255)
                : Windows.UI.Color.FromArgb(140, 0, 0, 0);
            bar.ButtonPressedBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(40, 255, 255, 255)
                : Windows.UI.Color.FromArgb(30, 0, 0, 0);
        }

        UpdateColors();
        themeRoot.ActualThemeChanged += (_, _) => UpdateColors();

        // ⚠️ 必须在 ExtendsContentIntoTitleBar 之后 —— 那一步会把圆角偏好按回 Default
        WindowChrome.SetRounded(hwnd, rounded: true);
    }
}
