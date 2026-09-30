using System;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ClassSoftwareHub.Desktop.Core;

public enum ClockVeil { None, White, Black, Acrylic, Mica }
public enum ClockTone { Theme, Light, Dark }
public enum ClockInk { Auto, White, Black }

/// <summary>全屏时钟的外观设置（对齐网页版 tools/ClockTool.vue）。</summary>
public sealed class ClockSettings
{
    public string BackgroundImagePath { get; set; } = "";
    public ClockVeil Veil { get; set; } = ClockVeil.None;
    /// <summary>0 ~ 100</summary>
    public double VeilStrength { get; set; } = 55;
    public ClockTone Tone { get; set; } = ClockTone.Theme;
    public ClockInk Ink { get; set; } = ClockInk.Auto;
    public string FontFamily { get; set; } = "Bahnschrift";
    public double Scale { get; set; } = 1.0;
    public bool ShowSeconds { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public bool Hour12 { get; set; }

    /// <summary>
    /// 把另一份设置的值整体拷进来。
    ///
    /// ⚠️ 2026-09-28：这段以前在工具页和浮窗各写了一份（字段列表容易漏同步）。
    /// 现在只有这一处 —— 加字段就改这里，别在调用方再手抄一遍。
    /// </summary>
    public void CopyFrom(ClockSettings other)
    {
        if (other is null) return;
        BackgroundImagePath = other.BackgroundImagePath;
        Veil = other.Veil;
        VeilStrength = other.VeilStrength;
        Tone = other.Tone;
        Ink = other.Ink;
        FontFamily = other.FontFamily;
        Scale = other.Scale;
        ShowSeconds = other.ShowSeconds;
        ShowDate = other.ShowDate;
        Hour12 = other.Hour12;
    }

    /// <summary>复制一份。存"上次使用的样子"时要的是**快照**，不是引用。</summary>
    public ClockSettings Clone()
    {
        var copy = new ClockSettings();
        copy.CopyFrom(this);
        return copy;
    }
}

/// <summary>把设置翻译成颜色 / 画刷 / 文字（页面预览与全屏窗口共用，保证两边一致）。</summary>
public static class ClockRender
{
    public static readonly string[] VeilLabels = { "无", "白色蒙版", "黑色蒙版", "亚克力（Acrylic）", "云母（Mica）" };
    public static readonly string[] ToneLabels = { "跟随应用主题", "白色", "黑色" };
    public static readonly string[] InkLabels = { "自动（跟随底色）", "白色字", "黑色字" };
    public static readonly string[] FontLabels = { "等宽 · 与课堂计时器一致", "工业风 · Bahnschrift", "现代等宽 · Cascadia", "系统 UI · Segoe", "优雅衬线 · Georgia" };
    public static readonly string[] FontFamilies = { "Consolas", "Bahnschrift", "Cascadia Mono", "Segoe UI Variable Display", "Georgia" };

    private static Color Ink => Color.FromArgb(255, 17, 17, 17);
    private static Color Paper => Color.FromArgb(255, 255, 255, 255);
    private static Color Black => Color.FromArgb(255, 0, 0, 0);

    public static Color BaseColor(ClockTone tone, bool darkTheme) => tone switch
    {
        ClockTone.Light => Paper,
        ClockTone.Dark => Black,
        _ => darkTheme ? Black : Paper,
    };

    /// <summary>文字颜色：强制 > 有背景图（白蒙版给深色字，其余白字）> 跟随底色。</summary>
    public static Color FaceColor(ClockSettings s, bool darkTheme, bool hasPhoto)
    {
        if (s.Ink == ClockInk.White) return Paper;
        if (s.Ink == ClockInk.Black) return Ink;
        if (hasPhoto) return s.Veil == ClockVeil.White ? Ink : Paper;
        return IsLight(BaseColor(s.Tone, darkTheme)) ? Ink : Paper;
    }

    private static bool IsLight(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000 > 128;

    public static Brush BaseBrush(ClockSettings s, bool darkTheme)
        => new SolidColorBrush(BaseColor(s.Tone, darkTheme));

    /// <summary>蒙版 / 材质。亚克力、云母用 AcrylicBrush（真的会模糊底下的背景图）；资源不支持时退回半透明纯色。</summary>
    public static Brush VeilBrush(ClockSettings s)
    {
        var a = Math.Clamp(s.VeilStrength / 100.0, 0, 1);
        switch (s.Veil)
        {
            case ClockVeil.White:
                return new SolidColorBrush(Color.FromArgb((byte)(a * 255), 255, 255, 255));
            case ClockVeil.Black:
                return new SolidColorBrush(Color.FromArgb((byte)(a * 255), 0, 0, 0));
            case ClockVeil.Acrylic:
                return Acrylic(Color.FromArgb(255, 255, 255, 255), Math.Clamp(0.04 + 0.28 * a, 0, 1),
                               Color.FromArgb((byte)(255 * Math.Clamp(0.35 + a * 0.4, 0, 1)), 255, 255, 255));
            case ClockVeil.Mica:
                return Acrylic(Color.FromArgb(255, 12, 12, 12), Math.Clamp(0.10 + 0.55 * a, 0, 1),
                               Color.FromArgb((byte)(255 * Math.Clamp(0.45 + a * 0.45, 0, 1)), 12, 12, 12));
            default:
                return new SolidColorBrush(Colors.Transparent);
        }
    }

    private static Brush Acrylic(Color tint, double opacity, Color fallback)
    {
        try
        {
            return new AcrylicBrush
            {
                TintColor = tint,
                TintOpacity = opacity,
                FallbackColor = fallback,
            };
        }
        catch
        {
            return new SolidColorBrush(fallback);
        }
    }

    /// <summary>时间文字（网页版：12 小时制不带前导零，24 小时制补零）。</summary>
    public static string TimeText(DateTime now, bool hour12)
    {
        var h = hour12 ? (now.Hour % 12 == 0 ? 12 : now.Hour % 12) : now.Hour;
        return hour12 ? $"{h}:{now.Minute:00}" : $"{h:00}:{now.Minute:00}";
    }

    public static string SecText(DateTime now) => ":" + now.ToString("ss");

    public static string DateText(DateTime now)
    {
        var week = "日一二三四五六"[(int)now.DayOfWeek];
        return $"{now.Year} 年 {now.Month} 月 {now.Day} 日 · 星期{week}";
    }
}
