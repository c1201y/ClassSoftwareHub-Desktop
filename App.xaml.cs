using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Input;                 // PointerUpdateKind / PointerPoint（WinUI 3 在这一族命名空间下）
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace ClassSoftwareHub.Desktop;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    /// <summary>
    /// 「应用正在退出」的全局标记（2026-10-04 修）。
    ///
    /// ⛔ 为什么必须有：工具浮窗（<c>ToolPaletteWindow</c>）和 Q 群反馈窗（<c>QqFeedbackGuideWindow</c>）
    ///    都把 <c>AppWindow.Closing</c> 拦下来当"收起来"用（<c>args.Cancel = true</c>），这是它们自己的
    ///    正常语义。但 WinUI 的 <c>Application.Exit()</c> 是**逐个关窗**的，碰到被取消的就中止整条退出
    ///    流程 —— 结果：只要这两个窗里任意一个开着，托盘菜单「退出」就会变成
    ///    「主窗关了、托盘图标摘了、**进程却一直赖在任务管理器里**」。
    ///    实测复现：正常启动 → 托盘菜单开「常用工具」→ 托盘菜单「退出」→ 进程 15 分钟不退。
    ///    退出流程一开始就把它置 true，那两个窗口的 Closing 看到它就放行，不再拦。
    /// </summary>
    public static bool IsExiting { get; set; }

    public static SettingsStore Settings { get; } = new();
    public static ITelemetryService Telemetry { get; private set; } = new NoopTelemetryService(Settings);

    /// <summary>软件内容（原生界面用）。启动时 Load 一次，内含容错解析与问题清单。</summary>
    public static Core.ContentStore Content { get; } = new();

    // ── 文本框右键菜单（2026-10-06 补）─────────────────────────────────
    //
    // 症状：WinUI 3 的 TextBox 本该自带「剪切 / 复制 / 粘贴 / 全选 / 撤销」，
    // 但本机（WindowsAppSDK 2.5.1 自包含 + Windows 11 Insider 26220）实测**一个都不弹**。
    //
    // ✅ 根因已查明（实据，不是推断）：**菜单对象一直都在，断的是"触发"那一环。**
    //    进程内自测读**已加载**控件的属性，结果是：树里每个 TextBox 的 `ContextFlyout`
    //    都是 `TextCommandBarFlyout`（系统那个原生菜单，图标横排 + 「…」溢出）。
    //    ⇒ TextBox 内部的文本区是独立的内容岛（ContentIsland），右键在岛内就被吃掉了，
    //      XAML 侧的 ContextRequested 传不出去，模板里挂好的菜单于是永远没机会弹。
    //    ⛔⛔ 别再拿"新建 TextBox 的 ContextFlyout 是 null"当证据 —— `ContextFlyout` 是**模板
    //       Setter** 挂的，控件没进树 / 模板没应用之前必然是 null，什么都证明不了（我为它白绕两圈）。
    //
    // ✅ 现在的做法：**自己接触发，弹的是系统那个原生菜单**（见 TryOpenTextMenu）——
    //    优先 `tb.ContextFlyout.ShowAt(...)`，长相 100% 原生；
    //    只有它真的为 null 时才退回下面自建的 `_textEditMenu`（兜底，正常用不到）。
    //    ⚠️ 触屏长按走同一个入口（RightTapped 是平台合成的统一手势，见 OnRootRightTapped）；
    //      但**合成触摸在本机跑不通**（SM_DIGITIZER 说有线触摸屏，InjectTouchInput 却恒返 0x57
    //      ERROR_INVALID_PARAMETER，沙箱内外一样），所以长按那一下只能真机手测。
    //
    // ⛔⛔ 自建那套（`MenuFlyout`）**只能在代码里建**，不能写进 App.xaml —— 两条都是实测撞出来的：
    //   ① 在 App.xaml 里写 Click / Opening → 编译失败
    //      （WMC1005: Events cannot be set in the Application class XAML file）；
    //   ② 把 MenuFlyout / 隐式 Style 声明在 App.xaml → **应用启动即崩**，且 crash.log 零新增
    //      （死在 InitializeComponent，UnhandledException 都来不及挂）。挪到这里就能 try/catch。
    //
    // ⚠️ 自建菜单是**一个实例**，全应用的 TextBox 共用。同一时刻只可能打开一个
    //    ⇒ "当前目标"用 _textMenuTarget 静态字段传。
    //    ⛔ 别改用 MenuFlyoutItem.Parent 往上找 —— 它指向 MenuFlyoutPresenter，不是 TextBox。
    //
    // ⚠️⚠️ 关不掉的坑（2026-10-07 Nick 实测）：菜单弹出来后点别处，它会**闪一下**（收起又自己弹回来），
    //    得再点一下才真的消失。已排除"我们的触发重复"—— 每次右键在 ui.log 里都只有一条记录；
    //    那一下自己弹回来是**平台侧**的（TextCommandBarFlyout 本来就支持"选区/焦点变化时主动弹出"，
    //    见官方文档 proactive invocation），与我们请求的那次无关。
    //    ⇒ 第二版做法：**点别处时我们主动收**，并在随后一小段时间里压掉平台的重开，见 <see cref="AttachFlyoutGuard"/>。

    private static MenuFlyout? _textEditMenu;
    private static TextBox? _textMenuTarget;
    // 之所以是 FrameworkElement 而不是 UIElement：命中测试要读 ActualWidth / ActualHeight。
    private static FrameworkElement? _contentRoot;

    // 鼠标右键"按下待兑现"标记：按下时记下，抬起时兑现。
    private static bool _rightButtonDown;
    // 去重：同一次操作有可能既走"鼠标抬起"又走 RightTapped（笔的桶键就是两条都报）。
    private static long _menuOpenedTicks;
    private static Windows.Foundation.Point _menuOpenedAt;

    // 当前**打开着**的那个菜单弹层 —— "用户点了别处"时靠它定向收起（别的弹层此刻没开，不用管）。
    private static FlyoutBase? _openFlyout;
    // 已经挂过守卫的弹层。每个 TextBox 的 ContextFlyout 是各自一个实例 ⇒ 按引用记，挂过就不重复挂。
    private static readonly HashSet<FlyoutBase> _guardedFlyouts = new();
    // 「点了别处之后」的压制窗（存过期时刻，0 = 不压制）：这段时间里平台若把菜单弹回来，一律收起。
    private static long _suppressReopenUntilTicks;

    /// <summary>
    /// 建**兜底**菜单本体（一项 = 一条命令）。正常情况用不到它 —— 见 <see cref="TryOpenTextMenu"/>：
    /// 优先弹 TextBox 自带的 <c>TextCommandBarFlyout</c>，只有它真的为 null 才退回这套。
    /// 失败只记日志：菜单没有不影响任何别的东西。
    /// </summary>
    private void SetupTextEditContextMenu()
    {
        try
        {
            var menu = new MenuFlyout();
            menu.Opening += TextEditMenu_Opening;
            menu.Items.Add(MakeTextMenuItem("剪切", "cut", "\uE8C6", "Ctrl+X"));
            menu.Items.Add(MakeTextMenuItem("复制", "copy", "\uE8C8", "Ctrl+C"));
            menu.Items.Add(MakeTextMenuItem("粘贴", "paste", "\uE77F", "Ctrl+V"));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MakeTextMenuItem("全选", "selectall", "\uE8B3", "Ctrl+A"));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MakeTextMenuItem("撤销", "undo", "\uE7A7", "Ctrl+Z"));
            _textEditMenu = menu;

            // ⛔⛔ 别再想着"给 TextBox 挂个隐式样式、让 ContextFlyout 自动生效"：
            //   ① `Resources[typeof(TextBox)] = style` 在 WinUI 3 里**不生效**
            //      （ResourceDictionary 不认 C# 传进去的 Type 键）；
            //   ② 就算挂上也照样不弹 —— 菜单对象本来就在模板里挂着（实测 `ContextFlyout` 就是
            //      `TextCommandBarFlyout`），断的是"触发"那一环：TextBox 内部的内容岛把右键吃掉，
            //      ContextRequested 传不出来。
            //   所以唯一有效的路是 TryOpenTextMenu 那条"按坐标命中测试 → 手动 ShowAt"。

            Core.AppLog.Info("ui", "文本框右键菜单：兜底菜单已就绪（正常走系统自带的那个）");
        }
        catch (Exception ex)
        {
            Core.AppLog.Error("ui", $"文本框右键菜单构建失败：{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    private MenuFlyoutItem MakeTextMenuItem(string text, string tag, string glyph, string accel)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            Tag = tag,
            Icon = new FontIcon { Glyph = glyph },
        };

        // ⛔ 只能用它来"显示"提示文字。换成 KeyboardAccelerator 会真的注册加速键，
        //    与 TextBox 内置的 Ctrl+X/C/V 撞车、动作被执行两遍。
        item.KeyboardAcceleratorTextOverride = accel;
        item.Click += TextEditMenu_Click;
        return item;
    }

    private void TextEditMenu_Opening(object? sender, object e)
    {
        if (sender is not MenuFlyout flyout) return;

        var tb = flyout.Target as TextBox;
        _textMenuTarget = tb;
        if (tb is null) return;

        var hasSelection = tb.SelectionLength > 0;

        foreach (var item in flyout.Items)
        {
            if (item is not MenuFlyoutItem mi || mi.Tag is not string tag) continue;

            mi.IsEnabled = tag switch
            {
                "cut" => hasSelection && !tb.IsReadOnly,
                "copy" => hasSelection,
                "paste" => !tb.IsReadOnly,
                "selectall" => tb.Text.Length > 0,
                "undo" => tb.CanUndo,
                _ => true,
            };
        }
    }

    private async void TextEditMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem mi || mi.Tag is not string tag) return;
        if (_textMenuTarget is not { } tb) return;

        // ⚠️⚠️ WinUI 3 的 TextBox **没有** Cut() / Copy() / Paste() 方法
        //      （编译期实测：CS1061 找不到 Cut/Copy；Paste 只是个事件）。
        //      所以这里只能自己走剪贴板 + SelectedText。
        switch (tag)
        {
            case "cut":
                if (tb.SelectionLength == 0 || tb.IsReadOnly) break;
                CopyToClipboard(tb.SelectedText);
                tb.SelectedText = "";            // 给 SelectedText 赋空串 = 删掉选区
                break;

            case "copy":
                if (tb.SelectionLength == 0) break;
                CopyToClipboard(tb.SelectedText);
                break;

            case "paste":
                if (tb.IsReadOnly) break;
                await PasteFromClipboardAsync(tb);
                break;

            case "selectall":
                tb.SelectAll();
                break;

            case "undo":
                tb.Undo();
                break;
        }

        // 动作做完把焦点还回去，否则光标消失、接着打字没反应
        tb.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// 全应用文本框的右键菜单入口：在内容根上盯「鼠标右键 / 手指长按」。
    ///
    /// ⛔ 为什么系统自己的触发不管用：TextBox 内部那块文本区是**独立的内容岛**（ContentIsland），
    ///    右键在岛内就被吃掉了，ContextRequested 传不到 XAML 侧的 ContextFlyout
    ///    —— 菜单对象明明挂在模板上（实测就是 `TextCommandBarFlyout`），却永远没机会弹。
    ///    ⇒ 所以这一层只负责**接触发**，弹的还是系统那个菜单（见 <see cref="TryOpenTextMenu"/>）。
    ///
    /// ⛔⛔ **鼠标和触摸必须走两条不同的路**（2026-10-06 实测踩出来的，别合并）：
    ///   · **触摸（长按）只能靠 <see cref="OnRootRightTapped"/>** —— 长按不是"按键"，没有按钮可读，
    ///     平台把它合成成 RightTapped，这是唯一入口。
    ///   · **鼠标不能再靠 RightTapped** —— 它是个**手势**，判定要求按下与抬起之间"基本没动"
    ///     （系统拖拽阈值 SM_CXDRAG/SM_CYDRAG，默认 4 px）。真手上按一下就抖掉几个像素很常见
    ///     （高 DPI 鼠标更容易），于是手势判不成"点按"，**RightTapped 干脆不触发** ⇒ 表现为"右键没反应"。
    ///     👉 症状特征：注入式右键（零位移）能弹，真人右键不弹；触摸长按（按住不动）反而正常。
    ///   ⇒ 鼠标改读**指针本身的按钮状态**：<see cref="OnRootPointerPressed"/> 记下右键按下，
    ///     <see cref="OnRootPointerReleased"/> 见到"右键释放"就兑现。这条与位移无关，抖多少都算数。
    ///
    /// ⚠️ 目标文本框靠**自己遍历视觉树 + 矩形包含**找，见 <see cref="HitTestTextBox"/>。
    /// </summary>
    private void OnRootRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_contentRoot is null) return;

        try
        {
            // 触摸长按 / 笔的桶键走这里（鼠标那条见 OnRootPointerReleased）
            if (TryOpenTextMenu(e.GetPosition(_contentRoot), "触摸长按")) e.Handled = true;
        }
        catch (Exception ex)
        {
            Core.AppLog.Error("ui", "文本框右键菜单弹出失败（RightTapped）：" + ex.Message);
        }
    }

    /// <summary>
    /// 鼠标：记下"右键是否按下"，抬起时才知道这次是不是右键。
    /// 顺带处理「点别处关菜单」：这一次按下的如果不是右键，就当用户想走开 ——
    /// 主动把菜单收掉，并设一道压制窗拦住平台随后的"自己弹回来"（见 <see cref="AttachFlyoutGuard"/>）。
    /// </summary>
    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_contentRoot is null) return;

        try
        {
            _rightButtonDown = e.GetCurrentPoint(_contentRoot).Properties.IsRightButtonPressed;
        }
        catch
        {
            _rightButtonDown = false;      // 读不到就当不是右键，⛔ 不要抛（会打断正常的指针处理）
        }

        try
        {
            if (_rightButtonDown)
                _suppressReopenUntilTicks = 0;      // 用户又按右键了：解除压制，别妨碍这一次正常弹出
            else
                DismissTextMenuBecauseClickAway();
        }
        catch { /* 收菜单失败不能打断输入 */ }
    }

    /// <summary>
    /// 用户点了别处（非右键的那一次指针按下）：**我们主动**把菜单收干净，
    /// 并留一道 600ms 的压制窗 —— 平台随后还会自作主张地 Opening 一次，那一次由守卫收掉。
    ///
    /// ⛔ 不会误伤"点菜单里的菜单项"：菜单本体在 Popup 里，指针事件不经过 <see cref="_contentRoot"/>，
    ///    所以这里收到的一定是"点在菜单外面"。
    /// </summary>
    private static void DismissTextMenuBecauseClickAway()
    {
        var flyout = _openFlyout;
        if (flyout is null) return;

        _suppressReopenUntilTicks = Environment.TickCount64 + 600;

        if (!flyout.IsOpen) return;      // 平台可能已经先收掉了；压制窗照样留着拦重开

        try { flyout.Hide(); }
        catch { /* 收不回去也只是多个弹层，别为它打断输入 */ }
    }

    /// <summary>
    /// 鼠标：靠 `PointerUpdateKind` 认"右键释放"，与这次点击有没有位移无关。
    /// ⛔ 触摸的抬指也会走到这里，但那时 _rightButtonDown 是 false，直接让开。
    /// </summary>
    private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_rightButtonDown) return;
        _rightButtonDown = false;

        if (_contentRoot is null) return;

        try
        {
            var pt = e.GetCurrentPoint(_contentRoot);
            if (pt.Properties.PointerUpdateKind != PointerUpdateKind.RightButtonReleased) return;

            TryOpenTextMenu(pt.Position, "鼠标");
        }
        catch (Exception ex)
        {
            Core.AppLog.Error("ui", "文本框右键菜单弹出失败（指针）：" + ex.Message);
        }
    }

    /// <summary>
    /// 两条入口的合流处：按坐标命中文本框 → 弹菜单。返回是否真的弹了。
    ///
    /// ⛔ 找目标文本框这条路，试过两条都是死路，别再回头：
    ///   ① `e.OriginalSource` 沿 VisualTreeHelper.GetParent 往上找 —— 实测它给回来的是个
    ///      FrameworkElement，一路上去碰不到 TextBox；
    ///   ② `VisualTreeHelper.FindElementsInHostCoordinates(pt, root)` —— 实测同样返空。
    ///   真因：TextBox 里那块文本区是**独立的内容岛**（ContentIsland），不属于 XAML 视觉树，
    ///   所有走 XAML 命中测试的路子都够不着它。
    ///   ⇒ 所以改成自己**遍历视觉树 + 矩形包含判断**（见 <see cref="HitTestTextBox"/>）。
    /// </summary>
    private bool TryOpenTextMenu(Windows.Foundation.Point pt, string source)
    {
        if (_contentRoot is null) return false;

        // 去重：同一次操作被两条钩子各报一次时，只认先到的那个（笔的桶键最容易撞）
        var now = Environment.TickCount64;
        if (now - _menuOpenedTicks < 400 &&
            Math.Abs(pt.X - _menuOpenedAt.X) < 8 &&
            Math.Abs(pt.Y - _menuOpenedAt.Y) < 8)
        {
            return false;
        }

        var tb = HitTestTextBox(_contentRoot, pt);
        if (tb is null)
        {
            // 点在空白/说明文字上：不接管，保持默认行为
            Core.AppLog.Info("ui", $"右键菜单：来源={source} 点={pt.X:F0},{pt.Y:F0} 命中=（无）");
            return false;
        }

        Core.AppLog.Info("ui",
            $"右键菜单：来源={source} 点={pt.X:F0},{pt.Y:F0} 命中={tb.Name} "
            + $"自带菜单={tb.ContextFlyout?.GetType().Name ?? "（null）"}");

        try
        {
            // 菜单的 Position 是**相对目标元素**的偏移 ⇒ 换算一下，落到鼠标那一点上
            var tl = tb.TransformToVisual(_contentRoot).TransformPoint(new Windows.Foundation.Point(0, 0));
            var pos = new Windows.Foundation.Point(pt.X - tl.X, pt.Y - tl.Y);

            // ── 优先用 TextBox **自己模板里那个**菜单（2026-10-06）────────────────────
            // 实测定性的结论：**菜单对象一直都在，断的只是"触发"那一环**。
            // 树里每个 TextBox 的 ContextFlyout 都是 `TextCommandBarFlyout`（系统那个原生菜单，
            // 图标横排 + 「…」溢出），而 TextBox 内部的内容岛把右键吃掉了，ContextRequested
            // 传不到 XAML 侧 ⇒ 模板挂好的菜单永远没机会弹。
            // ⇒ 这里只做"接触发"：把那个原生菜单 ShowAt 出来，长相 / 命令启用逻辑 100% 系统原生。
            //    自建的那个 `MenuFlyout` **不插手**，只当 ContextFlyout 真的为 null 时的兜底。
            var native = tb.ContextFlyout;

            if (native is null)
            {
                native = _textEditMenu;          // 兜底：自建那套
                _textMenuTarget = tb;
            }
            else
            {
                // 原生那套的按钮启用态是按"这个框的选区 / 焦点"算的 ⇒ 先给它焦点（用 Pointer 态，
                // 与真实右键一致，不会把选区清掉）
                try { tb.Focus(FocusState.Pointer); } catch { /* 焦点给不上也照弹 */ }
            }

            if (native is null) return false;

            _menuOpenedTicks = now;
            _menuOpenedAt = pt;

            AttachFlyoutGuard(native);        // 关不掉的坑，见那个方法的注释

            // ⛔⛔ 千万别在这里**同步** ShowAt（2026-10-06 踩了两次）：
            //    此刻我们还堵在"指针抬起 / 长按"的派发过程中，弹层刚建好就被这次输入
            //    当成"点在弹层外面"给 light-dismiss 掉 —— 表现就是"右键没反应"，
            //    而同一次操作从**定时器**里 ShowAt 却能稳定停住（自测那版就是定时器，所以它一直是好的）。
            //    ⇒ 推到 DispatcherQueue 尾部：等这次输入彻底处理完，再弹。
            var toShow = native;
            if (!_contentRoot.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        toShow.ShowAt(tb, new FlyoutShowOptions { Position = pos });
                        _openFlyout = toShow;      // 记下"当前开着的是它"，点别处时定向收起
                    }
                    catch (Exception ex)
                    {
                        Core.AppLog.Error("ui", "文本框右键菜单弹出失败：" + ex.Message);
                    }
                }))
            {
                Core.AppLog.Warning("ui", "文本框右键菜单：DispatcherQueue 满了，这次没弹");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Core.AppLog.Error("ui", "文本框右键菜单弹出失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 给弹层挂一道「点别处就收干净」的守卫（2026-10-07 **第二版**）。
    ///
    /// **症状**：菜单弹出来后点一下别处 → 它**闪一下**（收起又自己弹回来），得再点一下才真的消失。
    /// **成因**：平台上会额外发一次 Opening（<c>TextCommandBarFlyout</c> 支持"选区 / 焦点变化时主动弹出"，
    /// 官方文档里的 proactive invocation），与我们请求的那次无关。"我们的触发重复"已排除。
    ///
    /// ⛔⛔ 第一版（"一次性令牌，只放行我们请求的那一次"）**已实测报废，别再回去**：
    ///    平台上每次弹出都会**多发一次** Opening —— ui.log 里"我们请求"那条之后 ~52ms 必跟一条无令牌 Opening。
    ///    令牌被前一个 Opening 吃掉 ⇒ 真正该打开的那次被判成"系统自己重开"给 Hide 掉 ⇒ **菜单完全不弹**
    ///    （2026-10-07 00:57 Nick 实测：「右键不生效了，没有菜单」）。
    ///    根子在于"光看 Opening 本身分辨不出哪次是我们请求的"，所以任何按次计数 / 一次性令牌都靠不住。
    ///
    /// ✅ 这一版只做**定向压制**：
    ///   · 正常弹出 **一律放行**（不看令牌、不看时间）—— 保证菜单一定弹得出来；
    ///   · 只有我们**主动收起**过（用户点了别处，见 <see cref="DismissTextMenuBecauseClickAway"/>），
    ///     才在随后 600ms 内把 Opening 压掉。用户下一次按右键时压制窗立刻清零 ⇒ 手多快都不误伤。
    ///
    /// ⛔ 别在 Opening 里**同步** Hide()：那一刻弹层还没真正打开，调用会被忽略 ⇒ 推到队列尾部再收。
    /// </summary>
    private static void AttachFlyoutGuard(FlyoutBase flyout)
    {
        if (!_guardedFlyouts.Add(flyout)) return;      // 同一个弹层只挂一次

        try
        {
            flyout.Opening += (_, _) =>
            {
                // 正常弹出：什么都不做、直接放行（⛔ 别在这里加任何"这次是不是我们请求的"判断）
                if (Environment.TickCount64 >= _suppressReopenUntilTicks) return;

                var queue = _contentRoot?.DispatcherQueue;
                if (queue is null) return;

                queue.TryEnqueue(() =>
                {
                    try
                    {
                        if (!flyout.IsOpen) return;
                        flyout.Hide();
                        Core.AppLog.Info("ui", "右键菜单：点别处之后平台又把它弹回来了，已收起");
                    }
                    catch
                    {
                        // 收不回去就算了，别为它把应用带崩
                    }
                });
            };

            flyout.Closed += (_, _) =>
            {
                if (ReferenceEquals(_openFlyout, flyout)) _openFlyout = null;
            };
        }
        catch (Exception ex)
        {
            Core.AppLog.Error("ui", "右键菜单：关闭守卫挂载失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 走一遍视觉树，返回「包含该点、且面积最小」的那个 TextBox（嵌套时取最内层）。
    /// 纯矩形判断，不依赖任何 XAML 命中测试 API —— 那套够不着 TextBox 内部的内容岛。
    /// </summary>
    private static TextBox? HitTestTextBox(DependencyObject node, Windows.Foundation.Point pt)
    {
        TextBox? best = null;
        var bestArea = double.MaxValue;

        void Walk(DependencyObject n)
        {
            if (n is TextBox tb && _contentRoot is not null)
            {
                try
                {
                    var tl = tb.TransformToVisual(_contentRoot).TransformPoint(new Windows.Foundation.Point(0, 0));
                    var rect = new Windows.Foundation.Rect(tl.X, tl.Y, tb.ActualWidth, tb.ActualHeight);

                    if (rect.Contains(pt))
                    {
                        var area = tb.ActualWidth * tb.ActualHeight;
                        if (area < bestArea)
                        {
                            bestArea = area;
                            best = tb;
                        }
                    }
                }
                catch
                {
                    // 元素还没挂进树时 TransformToVisual 会抛 —— 跳过就是了
                }
            }

            var count = VisualTreeHelper.GetChildrenCount(n);
            for (var i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(n, i));
        }

        Walk(node);
        return best;
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            var dp = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            dp.SetText(text);
            Clipboard.SetContent(dp);
        }
        catch
        {
            // 剪贴板被别的程序占着是常态，静默放弃即可，别为它崩掉
        }
    }

    private static async Task PasteFromClipboardAsync(TextBox tb)
    {
        try
        {
            var view = Clipboard.GetContent();
            if (view is null || !view.Contains(StandardDataFormats.Text)) return;

            var text = await view.GetTextAsync();
            if (string.IsNullOrEmpty(text)) return;

            // 有选区就替换选区，没选区就插在光标处 —— 两种情况都是这一个赋值
            tb.SelectedText = text;
        }
        catch
        {
        }
    }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;

        // 文本框右键菜单：⛔ 必须在代码里建，不能声明在 App.xaml（原因见 App.xaml 那段注释）
        // ⛔ 也不能在这里建 —— 构造函数期间连 Application.Resources 都读不了（E_UNEXPECTED）。
        //    改到 OnLaunched 里做，见那儿的调用。

        // 单实例。这个命名的互斥体同时也是安装程序 [Setup] AppMutex 用的名字 —— 装/升级时 Inno 靠它
        // 判断"应用还在跑"，所以进程活着期间必须一直持有，不能释放。
        var mutex = new Mutex(initiallyOwned: true, Core.ShellConfig.MutexName, out var isFirstInstance);

        if (isFirstInstance)
        {
            _instanceMutex = mutex;
            TryCreateActivateSignal();
            return;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  已经有一个实例在跑 —— 来的是"用户又点了一次图标"
        //
        //  ⛔ 这里以前就一句 Environment.Exit(0)：界面、提示、日志全无。而默认关闭窗口是
        //     **收进托盘**（CloseToTray 默认 true），进程并没有退出，所以用户看到的现象是
        //     "把软件关掉之后点桌面图标打不开，点三四次一点反应都没有，只能去任务管理器结束进程"
        //     （2026-09-30 用户实测反馈）。托盘图标如果又被系统折进溢出区，就彻底没有入口了。
        //
        //  现在改成：先敲门，让那个实例把主窗口叫出来；它确实还活着我们才退。
        // ═══════════════════════════════════════════════════════════════════════
        mutex.Dispose();

        if (WakeUpExistingInstance())
        {
            Environment.Exit(0);
            return;
        }

        // 敲门期间对方正好退出了（互斥体空了出来）→ 由本进程接管，照常启动
        _instanceMutex = new Mutex(initiallyOwned: true, Core.ShellConfig.MutexName, out _);
        TryCreateActivateSignal();
    }

    private static Mutex? _instanceMutex;

    /// <summary>第一个实例监听用的"叫醒"事件。</summary>
    private static EventWaitHandle? _activateSignal;

    /// <summary>建"叫醒"事件。建不出来（极罕见）只是丢掉这条兜底路径，不影响启动。</summary>
    private static void TryCreateActivateSignal()
    {
        try
        {
            _activateSignal = new EventWaitHandle(
                initialState: false, EventResetMode.AutoReset, Core.ShellConfig.ActivateEventName);
        }
        catch (Exception ex)
        {
            _activateSignal = null;
            Services.ScreenCapture.Log("[single] 叫醒事件建不出来: " + ex.Message);
        }
    }

    /// <summary>
    /// 敲一下"叫醒"事件，请已经在跑的那个实例把主窗口亮出来。
    /// 返回 <c>true</c> = 对方还活着（本实例该退）；<c>false</c> = 对方在这期间退出了（互斥体已空出来，本实例接管）。
    /// </summary>
    private static bool WakeUpExistingInstance()
    {
        var name = Core.ShellConfig.ActivateEventName;
        var knocked = false;

        // 第一个实例理论上可能还卡在启动途中（事件还没建），给几秒。正常情况第一次就成功。
        for (var attempt = 0; attempt < 30 && !knocked; attempt++)
        {
            try
            {
                using var handle = EventWaitHandle.OpenExisting(name);
                handle.Set();
                knocked = true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(100);
            }
            catch (Exception ex)
            {
                // 打不开又不是"不存在"（多半是权限）→ 保守当作对方活着，别冒险双开
                Services.ScreenCapture.Log("[single] 叫醒事件打不开: " + ex.Message);
                return true;
            }
        }

        if (!knocked) return true;

        // 给第一个实例一点时间把窗口亮出来；同时看它是不是正在退出 —— 互斥体一空出来就归我们。
        Thread.Sleep(1200);

        try
        {
            using var probe = new Mutex(initiallyOwned: false, Core.ShellConfig.MutexName);
            return !probe.WaitOne(0);
        }
        catch
        {
            return true;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Settings.Load();

        // ⛔⛔ 文本框右键菜单**只能在这里**装，不能挪进构造函数：实测构造函数期间访问
        //     Application.Resources 直接抛 COMException 0x8000FFFF(E_UNEXPECTED)，
        //     而这里（OnLaunched）资源字典已经就绪。放在 MainWindow 创建之前 ⇒ 页面里的
        //     TextBox 一出生就带着菜单。
        SetupTextEditContextMenu();

        // 数据目录分区：日志 → logs\，内嵌解包的图片/图标缓存 → cache\（旧版全堆在根上）。
        // 尽早搬 —— 后面任何模块一写日志就落到新位置了。
        try
        {
            Core.AppLog.MigrateLegacyFiles();
            Services.EmbeddedAssets.MigrateLegacyCache();
        }
        catch { /* 迁移失败不影响启动，文件留在原地 */ }

        Telemetry = new NoopTelemetryService(Settings);
        Telemetry.Track("app_launch", new Dictionary<string, object?>
        {
            ["shellVersion"] = Core.ShellConfig.ShellVersion,
            ["osBuild"] = Environment.OSVersion.Version.Build
        });

        // ⚠️⚠️ 顺序别改回去（2026-10-02）。
        //
        //  MainWindow 这个静态属性要等整个构造函数**返回**才被赋值，所以「建窗」这一段
        //  天然存在一个空档：窗口已经在建/已经显示，而 App.MainWindow 还是 null。
        //  唤醒监听线程原来排在 Activate() 之后 —— 空档期间来的敲门没人接，
        //  第二个实例等 1.2 秒后自己退掉，用户看到的是「点了一点反应都没有」，日志里一句话不留。
        //  所以改成：**先铺唤醒通道 → 再建窗 → 再激活**，每一步单独兜异常、单独留痕。
        //
        //  同日还修了另一半：工具侧边栏原来在 InitTray()（主窗口构造函数中途）就
        //  Activate 出来，抢在主窗口之前出现在屏幕上并置顶，主窗口那声 Activate 反倒成了配角。
        //  现在挪到主窗口首帧之后（MainWindow.OnFirstActivated → StartToolSidebar）。
        //  2026-10-02 有用户在 Win10 19045 上报的正是「有侧边栏、没主界面、任务栏没图标、
        //  点桌面图标没反应」这一组症状。
        StartActivateListener();

        try
        {
            MainWindow = new MainWindow();
        }
        catch (Exception ex)
        {
            Services.ScreenCapture.Log("[startup] 主窗口构造失败: " + ex);
        }

        try
        {
            MainWindow?.Activate();
        }
        catch (Exception ex)
        {
            Services.ScreenCapture.Log("[startup] 主窗口 Activate 失败: " + ex);
        }

        // 文本框右键菜单：三条钩子挂在内容根上，handledEventsToo=true。
        // ⛔⛔ 为什么是三条而不是一条：**鼠标右键和触摸长按走的不是同一条路**
        //    （鼠标那条依赖"按下/抬起"的按钮状态，触摸长按没有按钮可读、只能靠 RightTapped 合成手势），
        //    详见 OnRootRightTapped 的注释。少挂哪条，对应的那种输入就"右键没反应"。
        // 因为 TextBox 内部的文本控件自己处理右键，ContextRequested 传不到外面的 ContextFlyout。
        try
        {
            if (MainWindow?.Content is FrameworkElement contentRoot)
            {
                _contentRoot = contentRoot;      // 命中测试要用它当坐标系

                // 触摸长按 / 笔桶键
                contentRoot.AddHandler(UIElement.RightTappedEvent,
                                       new RightTappedEventHandler(OnRootRightTapped), true);

                // 鼠标右键：按下记标记、抬起兑现（不看位移，手抖也算）
                contentRoot.AddHandler(UIElement.PointerPressedEvent,
                                       new PointerEventHandler(OnRootPointerPressed), true);
                contentRoot.AddHandler(UIElement.PointerReleasedEvent,
                                       new PointerEventHandler(OnRootPointerReleased), true);

                Core.AppLog.Info("ui", "文本框右键菜单：已挂右键钩子（鼠标=按下/抬起，触摸=长按）");
            }
            else
            {
                Core.AppLog.Warning("ui", "文本框右键菜单：MainWindow.Content 不是 UIElement，兜底钩子没挂上");
            }
        }
        catch (Exception ex)
        {
            Core.AppLog.Error("ui", "文本框右键菜单兜底挂载失败：" + ex.Message);
        }

        // 走到这儿 MainWindow 还是 null，说明应用会停在「只有侧边栏、没有主界面」的状态。
        // 这条日志就是给那个场景留的指纹 —— 没有它，日志里只剩下一句"收到唤醒请求"。
        if (MainWindow is null)
            Services.ScreenCapture.Log("[startup] 主窗口未能建立，应用将停在「只有侧边栏」的状态");

        // 兜底：Activate() 返回 ≠ 窗口已经显示出来。隔一拍核一次可见性，
        //   不可见就再亮一次并把结果写进日志 —— 这条链路以后出问题能一眼定位。
        //   ⚠️ --minimized / --palette 是**故意**藏主窗口的，这两种启动方式不装兜底，
        //      否则刚藏起来就被自己叫回来。
        var startupHidesWindow = Environment.GetCommandLineArgs().Any(a =>
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--palette", StringComparison.OrdinalIgnoreCase));

        if (!startupHidesWindow) MainWindow?.ArmStartupVisibleWatchdog();

        // 两个实验性功能的到点巡检。⚠️ 都靠本进程内的定时器 —— 本程序没在跑就不会查杀
        //    （2026-09-28 Nick 确认按这个来，不往系统里装计划任务）。
        //    「白板专杀」与「程序专杀」各跑各的定时器、各存各的配置，刻意不合并（Nick 明确要求白板独立）。
        Data.EasiNoteGuard.Start();
        Data.ProcessGuard.Start();

        // 「自动关机」的到点巡检（2026-10-10 新增的实验性功能）。同样靠本进程内的定时器：
        //   本程序没在跑就不会关，没往系统里装计划任务（与上面两个专杀同一套口径）。
        //   ⚠️ 它排下去的 shutdown /s /t 是**由 Windows 自己数秒**的 —— 这中间退掉本程序，
        //      到点仍会关机；要反悔走侧边栏那颗「取消关机」（shutdown /a）。
        Data.AutoShutdown.Start();

        // 虚拟键盘（实验性功能）已于 2026-10-09 **整体下线** —— 原来这儿是
        //   Services.VirtualKeyboard.VirtualKeyboardService.Start();
        // 只留下一句收尾：虚拟键盘是那套功能里唯一写注册表的地方（把系统触摸键盘的
        // 桌面模式自动弹出关掉），功能删了之后就没人还原它了。Run() 按旧备份还原一次，
        // 幂等 —— 没有备份文件就什么都不碰。
        Services.VirtualKeyboardRetire.Run();

        // 更新安装包自动清理：updates 目录只留最近 N 个（默认 3），更早的删掉。
        // 走后台线程，不沾首帧；新版本装完后的第一次启动正好把旧包收掉。
        Services.Updating.InstallerCleanup.Start();

        // GitHub 加速节点的密钥就位情况 —— 只写一行日志，**绝不打印密钥本身**。
        // 密钥按设计不在仓库里（见 Services/MirrorSign.cs 的「密钥从哪来」），
        // 所以得有这么一句，才能一眼看出「这版包到底带没带钥匙」。
        // 没带 = 下载仍可用，只是绕过节点走公益镜像。
        Services.MirrorSign.LogKeyStatus();
    }

    /// <summary>
    /// 起一条后台线程守"叫醒"事件：用户又点了一次桌面图标 → 第二个实例 Set 这个事件 →
    /// 这里回到 UI 线程把主窗口叫出来（<see cref="MainWindow.ShowFromTray"/> 对"已可见"的窗口
    /// 也会重新激活并抢前台，所以"程序开着但被压在后面"时点图标同样有效）。
    ///
    /// ⚠️ <c>IsBackground = true</c>：这条线程绝不能拦住进程退出。
    /// ⚠️ 事件是 AutoReset 的 —— 第二次点击如果发生在处理途中，信号不会丢。
    /// </summary>
    private static void StartActivateListener()
    {
        var signal = _activateSignal;
        if (signal is null) return;

        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null) return;

        var listener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!signal.WaitOne()) break;
                }
                catch
                {
                    break;
                }

                // 落一条日志：这条链路出问题时，日志是唯一能分辨"根本没收到"还是"收到了但窗口没出来"的依据
                Services.ScreenCapture.Log("[single] 收到唤醒请求，把主窗口叫出来");

                queue.TryEnqueue(() =>
                {
                    var win = MainWindow;

                    // ⚠️ 这两条分支必须分开（2026-10-02）：
                    //    原来只有 MainWindow?.ShowFromTray() 一句 —— 属性为 null 时 ?. 会**静默跳过**，
                    //    日志里就只剩上面那句"收到唤醒请求"，跟"叫醒成功"长得一模一样。
                    //    于是「点桌面图标没反应」这条悬案根本分不出是"敲门没人听"还是"听见了但窗口没出来"。
                    if (win is null)
                    {
                        Services.ScreenCapture.Log("[single] 主窗口不存在，唤醒请求无人处理（见同日的 [startup] 记录）");
                        return;
                    }

                    try
                    {
                        win.ShowFromTray();
                    }
                    catch (Exception ex)
                    {
                        Services.ScreenCapture.Log("[single] 叫醒主窗口失败: " + ex.Message);
                    }
                });
            }
        })
        {
            IsBackground = true,
            Name = "csh-activate-listener",
        };

        listener.Start();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // ⚠️ 2026-09-29：「页面布局循环」（LayoutCycleException）不是致命错误 —— WinUI 会放弃本次布局、
        //    页面照样能用。但它会让 DEBUG 版直接 fail-fast（见下面 #if DEBUG），应用**瞬间消失**，
        //    连"当时在哪一页"都留不下。所以这一类单独兜住：只记日志、不让它把进程带走。
        var layoutCycle = e.Exception is Microsoft.UI.Xaml.LayoutCycleException;

        try
        {
            Telemetry.TrackException(e.Exception, "xaml_unhandled");
            var dir = SettingsStore.Dir;
            Directory.CreateDirectory(dir);

            // 排障信息：当时停在哪一页 + 窗口多大。
            // 「布局循环」这类问题只在特定尺寸下冒出来，没有这两个数根本无从复现。
            var where = "page=" + Pages.ShellPage.CurrentTag;
            try
            {
                if (MainWindow?.AppWindow is { } win)
                    where += $" window={win.Size.Width}x{win.Size.Height}";
            }
            catch { /* 拿不到窗口尺寸不影响记录 */ }
            if (layoutCycle) where += " ignored=1";

            Core.AppLog.Write("crash", Core.LogLevel.Error,
                $"[{where}] {e.Message}\n{e.Exception}");
        }
        catch { /* 记录失败也不影响 */ }

        // 开发期不静默退出，异常直接炸出来方便定位（VS 里能断到现场）。
        // 发布版必须兜底：老师正在上课，任何一个页面级异常（比如某个 {ThemeResource} 解析失败）
        // 都不该让整个应用消失、界面状态全丢 —— 那比"这个功能坏了"严重得多。
        // 上面已经写进 crash.log 了，事后能查；这里只负责"别死"。
#if DEBUG
        e.Handled = layoutCycle;
#else
        e.Handled = true;
#endif
    }
}
