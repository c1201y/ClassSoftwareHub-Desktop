using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;


public sealed class PptMenuItem
{
    public string Id { get; init; } = "";

    public string Text { get; init; } = "";

    
    public bool IsSeparator { get; init; }

    
    
    
    
    
    
    
    
    
    
    public string? Glyph { get; init; }

    
    public List<PptMenuItem>? Children { get; init; }
}























public static class PptMenu
{
    

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

    
    public const string IdSeeAll = "seeall";
    public const string IdZoom = "zoom";
    public const string IdBlack = "screen.black";
    public const string IdWhite = "screen.white";
    public const string IdSubtitles = "subtitles";
    public const string IdHelp = "help";

    
    public static readonly IReadOnlyList<(string Id, string Text, string Note, string Glyph)> Extras = new[]
    {
        (IdSeeAll,    "查看所有幻灯片", "弹出全部幻灯片的缩略图，点一张即跳转。", "\uE8A9"),
        (IdZoom,      "放大幻灯片",     "放大当前这一张，配合手指拖动查看细节。", "\uE71E"),
        (IdBlack,     "显示黑屏",       "把屏幕变成纯黑，再点一次恢复。",         "\uE708"),
        (IdWhite,     "显示白屏",       "把屏幕变成纯白，再点一次恢复。",         "\uE706"),
        (IdSubtitles, "启动字幕",       "打开或关闭实时字幕。",                 "\uE7F0"),
        (IdHelp,      "帮助",           "打开 PowerPoint 的放映快捷键说明。",   "\uE897"),
    };

    
    public static bool IsOpen { get; set; }

    
    public static long OpenedTick { get; set; }

    
    
    
    
    
    
    
    
    
    public static bool IsBlocking =>
        IsOpen && unchecked(Environment.TickCount64 - OpenedTick) < 60_000;

    
    public static long LastInvokeTick { get; private set; }

    
    public static void MarkDismissed() => LastInvokeTick = Environment.TickCount64;

    
    public static void HideMenu() => Views.PptMenuWindow.HideMenu();

    
    
    
    public static List<PptMenuItem> Build(ICollection<string> extras)
    {
        var items = new List<PptMenuItem>
        {
            
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

    
    
    
    public static void ShowAt(int x, int y) => Views.PptMenuWindow.Show(x, y);

    
    
    
    
    
    
    
    
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

        
        
        PptTouch.ForceForeground(hwnd);

        Task.Run(() =>
        {
            try
            {
                Thread.Sleep(90);

                
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

    
    public const ushort OemPlus = 0xBB;
}
