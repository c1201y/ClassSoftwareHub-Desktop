using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace ClassSoftwareHub.Desktop.Core;




















public static class WheelRelay
{
    
    private const double NotchDip = 48.0;

    
    private sealed class Hook
    {
        public PointerEventHandler Handler = null!;
        public DependencyObject[] Targets = Array.Empty<DependencyObject>();
    }

    private static readonly ConditionalWeakTable<UIElement, Hook> Hooks = new();

    
    
    
    
    
    
    
    
    public static void Attach(UIElement scrollHost, params DependencyObject[]? targets)
    {
        if (Hooks.TryGetValue(scrollHost, out var old))
            scrollHost.RemoveHandler(UIElement.PointerWheelChangedEvent, old.Handler);

        var hook = new Hook
        {
            Targets = targets is { Length: > 0 } ? targets : Array.Empty<DependencyObject>(),
        };
        hook.Handler = (_, e) => OnWheel(scrollHost, hook, e);

        Hooks.Remove(scrollHost);
        Hooks.Add(scrollHost, hook);
        scrollHost.AddHandler(UIElement.PointerWheelChangedEvent, hook.Handler, handledEventsToo: false);
    }

    private static void OnWheel(UIElement host, Hook hook, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(host);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;

        
        ScrollViewer? pick = null;
        ScrollViewer? first = null;
        foreach (var target in hook.Targets)
        {
            var sv = Resolve(target);
            if (sv is null || sv.ScrollableHeight <= 0) continue;   
            first ??= sv;
            if (Contains(sv, host, point.Position))
            {
                pick = sv;
                break;
            }
        }

        pick ??= first;
        if (pick is null) return;

        var to = pick.VerticalOffset - (delta / 120.0 * NotchDip);
        if (to < 0) to = 0;
        else if (to > pick.ScrollableHeight) to = pick.ScrollableHeight;

        e.Handled = true;
        pick.ChangeView(null, to, null, disableAnimation: true);
    }

    
    private static ScrollViewer? Resolve(DependencyObject? target)
    {
        if (target is null) return null;
        if (target is ScrollViewer direct) return direct;

        
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(target);
        for (var guard = 0; queue.Count > 0 && guard < 512; guard++)
        {
            var node = queue.Dequeue();
            int count;
            try { count = VisualTreeHelper.GetChildrenCount(node); }
            catch { continue; }
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is ScrollViewer sv && sv.ActualHeight > 0) return sv;
                queue.Enqueue(child);
            }
        }
        return null;
    }

    
    private static bool Contains(ScrollViewer sv, UIElement host, Point p)
    {
        try
        {
            var rect = sv.TransformToVisual(host).TransformBounds(new Rect(0, 0, sv.ActualWidth, sv.ActualHeight));
            return rect.Contains(p);
        }
        catch { return false; }
    }
}
