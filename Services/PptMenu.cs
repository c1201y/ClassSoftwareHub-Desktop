using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>菜单项（只描述「显示什么、点了做什么」，不含任何界面代码）。</summary>
public sealed class PptMenuItem
{
    public string Id { get; init; } = "";

    public string Text { get; init; } = "";

    /// <summary>分隔线。是分隔线时 <see cref="Id"/> 与 <see cref="Text"/> 都为空。</summary>
    public bool IsSeparator { get; init; }

    /// <summary>
    /// 图标：Segoe Fluent Icons 的字形码。
    ///
    /// <para>
    /// ⚠️ 用字形码而不是 <c>Symbol</c> 枚举 —— 枚举名和实际画出来的图案对不上号
    /// （`Symbol.EraseTool` 根本不存在，`ViewAll` 画出来是项目符号），
    /// 而字形码可以逐个渲染出来肉眼核对（2026-11-03 已全部核对过一遍）。
    /// 加新项时先去字体里看一眼再填。
    /// </para>
    /// </summary>
    public string? Glyph { get; init; }

    /// <summary>子菜单。非空时这一项点开是二级菜单，<see cref="Id"/> 不用。</summary>
    public List<PptMenuItem>? Children { get; init; }
}

/// <summary>
/// 长按弹出的菜单：**内容清单**与**点完之后干什么**（需求清单第 2 条的延伸，2026-11-03）。
///
/// <para>
/// 来由：WPS 演示放映时长按屏幕会弹出右键菜单，PowerPoint 长按只会切到激光笔。
/// 之前试过「长按 → 补一次注入右键」，让 PowerPoint 自己弹它那个菜单 —— 走不通：
/// PowerPoint 进入激光笔之后会吞掉注入的右键（细节见 <see cref="PptTouch"/> 类注释）。
/// 现在的做法是**完全不用 PowerPoint 的菜单**，由本程序自己弹、自己执行。
/// </para>
///
/// <para>
/// ⛔ 菜单项全部写死在这张清单里，<see cref="Invoke"/> 按 id 分支 —— 不要做成「菜单文案由配置决定」，
/// 文案与动作分家之后很容易出现「写着结束放映、点下去却翻页」。加项就加一条常量 + 一个 case。
/// </para>
///
/// <para>
/// ⚠️ 执行手段只有一条：**先把 PowerPoint 拉回前台，再注入按键**。
/// 菜单里每一项都对应 PowerPoint 放映时的一个快捷键（见 <c>PptKey</c>），这也意味着
/// 凡是 PowerPoint 没给快捷键的功能（保持幻灯片更新、更新幻灯片、自定义放映、显示演示者视图、
/// 上次查看的位置），本菜单**做不了** —— 它没有对外开放的调用入口，不是懒得做。
/// </para>
/// </summary>
public static class PptMenu
{
    // ── 菜单项 id ────────────────────────────────────────────

    public const string IdPrev = "prev";
    public const string IdNext = "next";
    public const string IdPointer = "pointer";
    public const string IdPointerArrow = "pointer.arrow";
    public const string IdPointerPen = "pointer.pen";
    public const string IdPointerHighlighter = "pointer.highlighter";
    public const string IdPointerEraser = "pointer.eraser";
    public const string IdPointerLaser = "pointer.laser";
    public const string IdPause = "pause";
    public const string IdExit = "exit";

    // 以下属于「额外项」，默认不出现在菜单里，由用户在设置页单独打开。
    public const string IdSeeAll = "seeall";
    public const string IdZoom = "zoom";
    public const string IdBlack = "screen.black";
    public const string IdWhite = "screen.white";
    public const string IdSubtitles = "subtitles";
    public const string IdHelp = "help";

    /// <summary>可选的额外菜单项（设置页按这个顺序列出来）。</summary>
    public static readonly IReadOnlyList<(string Id, string Text, string Note, string Glyph)> Extras = new[]
    {
        (IdSeeAll,    "查看所有幻灯片", "弹出全部幻灯片的缩略图，点一张即跳转。", "\uE8A9"),
        (IdZoom,      "放大幻灯片",     "放大当前这一张，配合手指拖动查看细节。", "\uE71E"),
        (IdBlack,     "显示黑屏",       "把屏幕变成纯黑，再点一次恢复。",         "\uE708"),
        (IdWhite,     "显示白屏",       "把屏幕变成纯白，再点一次恢复。",         "\uE706"),
        (IdSubtitles, "启动字幕",       "打开或关闭实时字幕。",                 "\uE7F0"),
        (IdHelp,      "帮助",           "打开 PowerPoint 的放映快捷键说明。",   "\uE897"),
    };

    /// <summary>菜单是否正开着。钩子据此把手上的触摸交给菜单、不再补按键。</summary>
    public static bool IsOpen { get; set; }

    /// <summary>菜单打开的时刻。</summary>
    public static long OpenedTick { get; set; }

    /// <summary>
    /// 钩子该不该把这次触摸让给菜单。
    ///
    /// <para>
    /// ⚠️ 带 60 秒上限：菜单万一没能正常关掉（浮层被系统收走、窗口被别的操作拆了），
    /// <see cref="IsOpen"/> 会一直是 true —— 那样 PowerPoint 就**再也点不动**了。
    /// 超过一分钟自动放行，宁肯偶尔多补一次点击，也不能让放映彻底僵住。
    /// </para>
    /// </summary>
    public static bool IsBlocking =>
        IsOpen && unchecked(Environment.TickCount64 - OpenedTick) < 60_000;

    /// <summary>最近一次点菜单项的时刻，用来挡住「关菜单那一下顺手补了个翻页」。</summary>
    public static long LastInvokeTick { get; private set; }

    /// <summary>记一次「菜单刚被收掉」，让紧跟着的抬起消息不再补动作。</summary>
    public static void MarkDismissed() => LastInvokeTick = Environment.TickCount64;

    /// <summary>收起当前菜单（点在菜单外面时由钩子叫过来）。</summary>
    public static void HideMenu() => Views.PptMenuWindow.HideMenu();

    /// <summary>
    /// 生成菜单。顺序对照 PowerPoint 原生菜单，只保留投影时真用得上的部分。
    /// </summary>
    public static List<PptMenuItem> Build(ICollection<string> extras)
    {
        var items = new List<PptMenuItem>
        {
            // 顺序照 PowerPoint 原生菜单：下一张在前、上一张在后
            new() { Id = IdNext, Text = "下一张", Glyph = "\uE893" },
            new() { Id = IdPrev, Text = "上一张", Glyph = "\uE892" },
            new() { IsSeparator = true },
            new()
            {
                Id = IdPointer,
                Text = "指针选项",
                Glyph = "\uE7C9",
                Children = new List<PptMenuItem>
                {
                    new() { Id = IdPointerArrow, Text = "箭头", Glyph = "\uE8B0" },
                    new() { Id = IdPointerPen, Text = "画笔", Glyph = "\uE70F" },
                    new() { Id = IdPointerHighlighter, Text = "荧光笔", Glyph = "\uE7E6" },
                    new() { Id = IdPointerEraser, Text = "橡皮擦", Glyph = "\uE75C" },
                    new() { Id = IdPointerLaser, Text = "激光笔", Glyph = "\uEA3A" },
                },
            },
        };

        // 额外项插在「暂停 / 结束放映」之前：这两项是收尾动作，永远该在最后。
        var extra = new List<PptMenuItem>();
        foreach (var (id, text, _, glyph) in Extras)
        {
            if (extras.Contains(id))
                extra.Add(new PptMenuItem { Id = id, Text = text, Glyph = glyph });
        }

        if (extra.Count > 0)
        {
            items.Add(new PptMenuItem { IsSeparator = true });
            items.AddRange(extra);
        }

        items.Add(new PptMenuItem { IsSeparator = true });
        items.Add(new PptMenuItem { Id = IdPause, Text = "暂停", Glyph = "\uE769" });
        items.Add(new PptMenuItem { Id = IdExit, Text = "结束放映", Glyph = "\uE71A" });

        return items;
    }

    /// <summary>
    /// 在屏幕坐标（物理像素）处弹出菜单。由钩子线程调用，内部会切到界面线程。
    /// </summary>
    public static void ShowAt(int x, int y) => Views.PptMenuWindow.Show(x, y);

    /// <summary>
    /// 执行一个菜单项。调用前菜单窗口应当已经收起来。
    ///
    /// <para>
    /// ⚠️ 全程在后台线程做：拉前台 + 等一帧 + 注入按键，这一串要几十毫秒，
    /// 放 UI 线程上会让菜单收起的那一下卡住。
    /// </para>
    /// </summary>
    public static void Invoke(string id)
    {
        LastInvokeTick = Environment.TickCount64;
        IsOpen = false;

        var hwnd = PptTouch.SlideshowWindow();
        if (hwnd == IntPtr.Zero)
        {
            AppLog.Info("pptmenu", $"点「{id}」时没有找到 PowerPoint 放映窗口，已忽略");
            return;
        }

        // ⚠️ 必须在这一刻、在这个线程（界面线程）上先把前台切回去：
        //    AttachThreadInput 只对「有输入队列的线程」有效，线程池线程没有。
        PptTouch.ForceForeground(hwnd);

        Task.Run(() =>
        {
            try
            {
                Thread.Sleep(90);

                // ⚠️ 切不过去就**绝不能**发键：Ctrl+S、Esc 这些落到别的程序上会干出别的事。
                if (!PptTouch.IsForeground(hwnd))
                {
                    PptTouch.ForceForeground(hwnd);
                    Thread.Sleep(60);
                }

                if (!PptTouch.IsForeground(hwnd))
                {
                    AppLog.Warning("pptmenu", $"放映窗口没能回到前台，菜单项「{id}」已放弃");
                    return;
                }

                switch (id)
                {
                    case IdPrev: PptTouch.SendCombo(PptKey.PageUp); break;
                    case IdNext: PptTouch.SendCombo(PptKey.PageDown); break;

                    case IdPointerArrow: PptTouch.SendCombo(PptKey.Ctrl, PptKey.A); break;
                    case IdPointerPen: PptTouch.SendCombo(PptKey.Ctrl, PptKey.P); break;
                    case IdPointerHighlighter: PptTouch.SendCombo(PptKey.Ctrl, PptKey.I); break;
                    case IdPointerEraser: PptTouch.SendCombo(PptKey.Ctrl, PptKey.E); break;
                    case IdPointerLaser: PptTouch.SendCombo(PptKey.Ctrl, PptKey.L); break;

                    case IdPause: PptTouch.SendCombo(PptKey.S); break;
                    case IdExit: PptTouch.SendCombo(PptKey.Escape); break;

                    case IdSeeAll: PptTouch.SendCombo(PptKey.Ctrl, PptKey.S); break;
                    case IdZoom: PptTouch.SendCombo(PptKey.OemPlus); break;
                    case IdBlack: PptTouch.SendCombo(PptKey.B); break;
                    case IdWhite: PptTouch.SendCombo(PptKey.W); break;
                    case IdSubtitles: PptTouch.SendCombo(PptKey.J); break;
                    case IdHelp: PptTouch.SendCombo(PptKey.F1); break;

                    default:
                        AppLog.Warning("pptmenu", "未知菜单项：" + id);
                        return;
                }

                AppLog.Info("pptmenu", "菜单项已执行：" + id);
            }
            catch (Exception ex)
            {
                AppLog.Exception("pptmenu", ex, "执行菜单项 " + id);
            }
        });
    }
}

/// <summary>放映时用得上的虚拟键。只列 <see cref="PptMenu"/> 真会发的那些。</summary>
public static class PptKey
{
    public const ushort Escape = 0x1B;
    public const ushort PageUp = 0x21;
    public const ushort PageDown = 0x22;
    public const ushort Ctrl = 0x11;
    public const ushort F1 = 0x70;

    public const ushort A = 0x41;
    public const ushort B = 0x42;
    public const ushort E = 0x45;
    public const ushort I = 0x49;
    public const ushort J = 0x4A;
    public const ushort L = 0x4C;
    public const ushort P = 0x50;
    public const ushort S = 0x53;
    public const ushort W = 0x57;

    /// <summary>顶排的 <c>= / +</c> 键。放映时按它就是放大幻灯片。</summary>
    public const ushort OemPlus = 0xBB;
}
