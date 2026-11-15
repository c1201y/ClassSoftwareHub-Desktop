using System;
using System.Collections.Generic;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;


















public sealed partial class PptMenuWindow : Window
{
    private static PptMenuWindow? _instance;

    private readonly AppWindow _appWindow;

    private MenuFlyout? _menu;

    
    private bool _pending;

    
    private DispatcherQueueTimer? _watchdog;

    private PptMenuWindow()
    {
        InitializeComponent();

        ThemeHost.Apply(Root);

        var hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        _appWindow.Title = "放映菜单";
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

    
    
    
    
    public static void Show(int x, int y)
    {
        var queue = App.MainWindow?.DispatcherQueue;
        if (queue is null)
        {
            AppLog.Warning("pptmenu", "主窗口不在了，长按菜单没弹出来");
            return;
        }

        queue.TryEnqueue(() =>
        {
            try
            {
                _instance ??= new PptMenuWindow();
                _instance.Present(x, y);
            }
            catch (Exception ex)
            {
                AppLog.Exception("pptmenu", ex, "弹出长按菜单");
                PptMenu.IsOpen = false;
            }
        });
    }

    private void Present(int x, int y)
    {
        
        if (_menu is not null)
        {
            try { _menu.Hide(); } catch { }
            _menu = null;
        }

        PptMenu.IsOpen = true;
        PptMenu.OpenedTick = Environment.TickCount64;

        
        
        
        
        
        
        _appWindow.MoveAndResize(new RectInt32(x, y, 1, 1));

        
        
        if (Root.XamlRoot is null)
        {
            _pending = true;
            Root.Loaded -= Root_Loaded;
            Root.Loaded += Root_Loaded;
        }
        else
        {
            ShowFlyout();
        }

        Activate();
        ArmWatchdog();
    }

    
    
    
    
    public static void HideMenu()
    {
        var queue = App.MainWindow?.DispatcherQueue;
        queue?.TryEnqueue(() =>
        {
            try { _instance?._menu?.Hide(); } catch { }
        });
    }

    
    
    
    
    
    
    
    
    
    private void ArmWatchdog()
    {
        if (_watchdog is not null) return;

        _watchdog = DispatcherQueue.CreateTimer();
        _watchdog.Interval = TimeSpan.FromSeconds(5);
        _watchdog.Tick += (_, _) =>
        {
            
            if (_menu is null) { DisarmWatchdog(); return; }

            if (unchecked(Environment.TickCount64 - PptMenu.OpenedTick) < 60_000) return;

            AppLog.Warning("pptmenu", "菜单开了 60 秒没收，强制收起");
            try { _menu?.Hide(); } catch { }
            PptMenu.IsOpen = false;
            _menu = null;
            try { _appWindow.Hide(); } catch { }
            DisarmWatchdog();
        };
        _watchdog.Start();
    }

    private void DisarmWatchdog()
    {
        if (_watchdog is null) return;
        _watchdog.Stop();
        _watchdog = null;
    }

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= Root_Loaded;
        if (!_pending) return;
        _pending = false;
        ShowFlyout();
    }

    private void ShowFlyout()
    {
        try
        {
            var menu = new MenuFlyout();
            Fill(menu.Items, PptMenu.Build(PptTouchConfig.Load().MenuExtras));

            menu.Opened += Menu_Opened;
            menu.Closed += Menu_Closed;
            _menu = menu;

            menu.ShowAt(Root, new FlyoutShowOptions
            {
                Position = new Windows.Foundation.Point(0, 0),
                ShowMode = FlyoutShowMode.Standard,
            });
        }
        catch (Exception ex)
        {
            AppLog.Exception("pptmenu", ex, "长按菜单弹出失败");
            PptMenu.IsOpen = false;
            try { _appWindow.Hide(); } catch { }
        }
    }

    
    
    
    
    private void Fill(IList<MenuFlyoutItemBase> target, IEnumerable<PptMenuItem> items)
    {
        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                target.Add(new MenuFlyoutSeparator());
                continue;
            }

            if (item.Children is { Count: > 0 } children)
            {
                var sub = new MenuFlyoutSubItem { Text = item.Text };
                ApplyIcon(sub, item);
                Fill(sub.Items, children);
                target.Add(sub);
                continue;
            }

            var id = item.Id;
            var entry = new MenuFlyoutItem { Text = item.Text };
            ApplyIcon(entry, item);
            entry.Click += (_, _) =>
            {
                
                try { _menu?.Hide(); } catch { }
                PptMenu.Invoke(id);
            };

            target.Add(entry);
        }
    }

    
    private static void ApplyIcon(MenuFlyoutItemBase target, PptMenuItem item)
    {
        if (string.IsNullOrEmpty(item.Glyph)) return;

        var icon = new FontIcon { Glyph = item.Glyph };

        switch (target)
        {
            case MenuFlyoutItem i: i.Icon = icon; break;
            case MenuFlyoutSubItem s: s.Icon = icon; break;
        }
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
        catch { }
    }

    private void Menu_Closed(object? sender, object e)
    {
        if (!ReferenceEquals(sender, _menu)) return;
        _menu = null;

        DisarmWatchdog();

        var clicked = !PptMenu.IsOpen;
        PptMenu.IsOpen = false;

        
        if (!clicked) PptMenu.MarkDismissed();

        try { _appWindow.Hide(); } catch { }

        
        
        if (!clicked) PptTouch.RestoreSlideshowForeground();
    }
}
