using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 滚轮接力（2026-10-13 建。Nick：「首页左右两边的空白区还能滚，别的页面只有中间那一条能滚」）。
///
/// 现象：除了首页，其它页面为了把内容钉在中间 960 宽，滚动容器**只盖住中间那一列** ——
///   两侧留白、标题行、页脚这些"看着有地方、其实没有可滚的东西"的区域，
///   滚轮命中的是页面根节点；根节点默认没有背景、不参与命中测试，
///   事件从滚动容器**外面**开始冒泡，容器自己永远收不到 ⇒ 鼠标在那儿滚，页面纹丝不动。
///
/// 做法（两步，缺一不可）：
///   ① XAML：给**页面根 Grid** 加 <c>Background="Transparent"</c> —— 不画任何像素，
///      但把整块面积纳入命中测试（与 App.xaml 给全站 ScrollViewer 加 Transparent 是同一招）；
///   ② 代码：构造函数里 <c>Core.WheelRelay.Attach(this, 主滚动容器)</c>。
///
/// ⚠️ 接在**这一页（Page）**上、而不是根 Grid 上：Page 是冒泡路线上离得最近的共同祖先，
///    一条 AddHandler 就够，也不用给根 Grid 起名字。
/// ⚠️ <c>handledEventsToo</c> 传 false：指针落在**真的**滚动容器里时事件已被它自己消化，
///    这里不能再动一次（否则一格里滚两倍）。
/// ⚠️ 只做 ChangeView，不碰 ScrollMode —— 页面自己把滚动锁掉（如侧边布局拖动时）时自然不生效。
/// </summary>
public static class WheelRelay
{
    /// <summary>滚轮「一档」的位移。WinUI 的 ScrollViewer 默认一档＝3 行 ≈ 48 DIP，这里对齐它。</summary>
    private const double NotchDip = 48.0;

    /// <summary>一次挂载记录：接在谁身上、能转交给哪些容器、以及用来摘除的委托实例。</summary>
    private sealed class Hook
    {
        public PointerEventHandler Handler = null!;
        public DependencyObject[] Targets = Array.Empty<DependencyObject>();
    }

    private static readonly ConditionalWeakTable<UIElement, Hook> Hooks = new();

    /// <summary>
    /// 把 <paramref name="scrollHost"/> 上收到的滚轮，转交给 <paramref name="targets"/> 里
    /// 第一个「指针压在上面」或「真的滚得动」的容器。
    /// </summary>
    /// <param name="scrollHost">接事件的元素，传 <c>this</c>（页面自身）即可。</param>
    /// <param name="targets">主滚动容器，按优先级排。可直接给 ScrollViewer，
    /// 也可以给 ListView / GridView / ItemsControl —— 会自己找到它内部的滚动区。
    /// 同页互斥的两个滚动区（如抽号的抽取/分组视图）都传进来，指针压在哪个就转给哪个。</param>
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

        // 指针压在哪条滚动区上就转给哪条（同页有两个时才分得清）；都没压中就落到第一条。
        ScrollViewer? pick = null;
        ScrollViewer? first = null;
        foreach (var target in hook.Targets)
        {
            var sv = Resolve(target);
            if (sv is null || sv.ScrollableHeight <= 0) continue;   // 收起 / 没内容 / 装得下 ⇒ 跳过
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

    /// <summary>把目标解析成真正的 ScrollViewer：给的本来就是就直接用，给的是列表控件就找它内部的滚动区。</summary>
    private static ScrollViewer? Resolve(DependencyObject? target)
    {
        if (target is null) return null;
        if (target is ScrollViewer direct) return direct;

        // 广度优先 ⇒ 拿到的是**最外层**那个滚动区（ListView / GridView 模板里的那个）
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

    /// <summary>指针位置是否落在某个容器此刻的实际范围里（同页有多个滚动区时用来挑一个）。</summary>
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
