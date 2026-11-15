using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Core;























public static class ModernWindow
{
    
    public static void ApplyTitleBar(Window window, UIElement dragElement, FrameworkElement themeRoot)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            
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

        
        WindowChrome.SetRounded(hwnd, rounded: true);
    }
}
