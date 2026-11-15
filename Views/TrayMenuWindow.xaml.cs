using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;















public sealed partial class TrayMenuWindow : Window
{
    private static TrayMenuWindow? _instance;

    private readonly AppWindow _appWindow;

    private MenuFlyout? _menu;
    private Action<string>? _invoke;

    
    private IReadOnlyList<TrayMenuItem>? _pendingItems;

    private TrayMenuWindow()
    {
        InitializeComponent();

        ThemeHost.Apply(Root);

        var hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        _appWindow.Title = "托盘菜单";
        _appWindow.IsShownInSwitchers = false;      

        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }

        WindowChrome.RemoveBorder(hwnd, rounded: false, dark: Root.ActualTheme == ElementTheme.Dark);
    }

    
    public static bool Show(IReadOnlyList<TrayMenuItem> items, Action<string> invoke)
    {
        try
        {
            _instance ??= new TrayMenuWindow();
            return _instance.Present(items, invoke);
        }
        catch (Exception ex)
        {
            AppLog.Info("tray", "WinUI 托盘菜单失败: " + ex);
            return false;
        }
    }

    private bool Present(IReadOnlyList<TrayMenuItem> items, Action<string> invoke)
    {
        if (!GetCursorPos(out var pt)) return false;

        if (_menu is not null)
        {
            try { _menu.Hide(); } catch { }
            _menu = null;
        }

        _invoke = invoke;

        
        _appWindow.MoveAndResize(new RectInt32(pt.X, pt.Y, 1, 1));

        
        
        if (Root.XamlRoot is null)
        {
            _pendingItems = items;
            Root.Loaded -= Root_Loaded;
            Root.Loaded += Root_Loaded;
        }
        else
        {
            ShowFlyout(items);
        }

        Activate();
        return true;
    }

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= Root_Loaded;
        var items = _pendingItems;
        _pendingItems = null;
        if (items is not null) ShowFlyout(items);
    }

    private void ShowFlyout(IReadOnlyList<TrayMenuItem> items)
    {
        try
        {
            var menu = Build(items);
            menu.Closed += Menu_Closed;
            menu.Opened += Menu_Opened;
            _menu = menu;

            menu.ShowAt(Root, new FlyoutShowOptions
            {
                Position = new Windows.Foundation.Point(0, 0),
                ShowMode = FlyoutShowMode.Standard,
            });
        }
        catch (Exception ex)
        {
            AppLog.Info("tray", "WinUI 托盘菜单弹出失败: " + ex);
            try { _appWindow.Hide(); } catch { }
        }
    }

    private MenuFlyout Build(IReadOnlyList<TrayMenuItem> items)
    {
        var menu = new MenuFlyout();

        foreach (var item in items)
        {
            if (item.Separator)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                continue;
            }

            var entry = new MenuFlyoutItem
            {
                Text = item.Text,
                
                FontWeight = item.IsDefault
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
            };

            var command = item.Command;
            entry.Click += (_, _) =>
            {
                
                try { _menu?.Hide(); } catch { }
                _invoke?.Invoke(command);
            };

            menu.Items.Add(entry);
        }

        return menu;
    }

    
    
    
    
    
    private void Menu_Opened(object? sender, object e)
    {
        try
        {
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot))
            {
                if (popup.Child is MenuFlyoutPresenter presenter)
                {
                    presenter.Focus(FocusState.Pointer);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Info("tray", "托盘菜单聚焦失败: " + ex.Message);
        }
    }

    private void Menu_Closed(object? sender, object e)
    {
        if (!ReferenceEquals(sender, _menu)) return;
        _menu = null;
        try { _appWindow.Hide(); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
