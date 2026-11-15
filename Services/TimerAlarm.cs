using System;
using System.IO;
using ClassSoftwareHub.Desktop.Core;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 倒计时到点的铃声。
///
/// 换它之前是 <c>kernel32!Beep(880, 250)</c> 响三下 —— 主板蜂鸣器那种又尖又干的音色，
/// 音量还挂在"系统声音"那个通道上，投影到教室后排基本听不见。
/// 2026-10-04（Nick）：改成用一段真录音当默认铃声，并且允许再挑一个自己的文件。
///
/// 三条规矩（别顺手改）：
///  ① **只有一个播放器实例**。闹铃语义是"到点响一次"，页面版和浮窗版各持一个播放器
///     只会互相打断、把上一次的声音掐掉。所以这里全局单例，<see cref="Play"/> 前先停。
///  ② **默认铃声是内嵌资源**，运行时释放到 <c>cache\</c> 再把路径交给 MediaPlayer ——
///     单文件发布下旁边没有散落的 wav 可读，只能走这条路（同 <see cref="EmbeddedAssets"/>）。
///  ③ **用户文件失效要能退回默认**：挑完的铃声可能被删 / 被移走 / 换了机器，
///     所以每次都是"文件真在才用它"，否则退回内嵌那份，绝不静默哑掉。
/// </summary>
public static class TimerAlarm
{
    /// <summary>内嵌默认铃声的文件名。<c>EmbeddedAssets</c> 是按"资源名结尾匹配"找的。</summary>
    private const string DefaultAsset = "timer-alarm.wav";

    /// <summary>挑铃声时过滤的扩展名。都交给系统解码器，多列几个也不怕。</summary>
    public static readonly string[] Extensions =
        { ".wav", ".mp3", ".m4a", ".wma", ".aac", ".flac", ".ogg" };

    private static MediaPlayer? _player;
    private static string? _defaultPath;        // 内嵌那份释放出来的路径（缓存一次）

    /// <summary>用户自定义铃声的完整路径；空 = 用默认。直接读写 settings.json。</summary>
    public static string CustomPath
    {
        get => App.Settings.Current.TimerAlarmPath ?? string.Empty;
        set
        {
            App.Settings.Current.TimerAlarmPath = value ?? string.Empty;
            App.Settings.Save();
        }
    }

    /// <summary>界面上显示的名字（按钮文案 / 提示用）。</summary>
    public static string DisplayName
    {
        get
        {
            var path = CustomPath;
            if (string.IsNullOrWhiteSpace(path)) return "默认铃声";
            try
            {
                var name = Path.GetFileName(path);
                return string.IsNullOrWhiteSpace(name) ? "自定义铃声" : name;
            }
            catch
            {
                return "自定义铃声";
            }
        }
    }

    /// <summary>当前该播哪个文件。自定义那份不在了就退回内嵌默认。</summary>
    public static string? ResolvePath()
    {
        var custom = CustomPath;
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom)) return custom;

        if (_defaultPath is not null && File.Exists(_defaultPath)) return _defaultPath;
        _defaultPath = EmbeddedAssets.ExtractToCache(DefaultAsset, DefaultAsset);
        return _defaultPath;
    }

    /// <summary>响铃。已经响着就先停再从头放（连点不会叠成两遍）。</summary>
    public static void Play()
    {
        try
        {
            var path = ResolvePath();
            if (string.IsNullOrEmpty(path)) return;

            if (_player is null)
            {
                _player = new MediaPlayer { AutoPlay = false };
                _player.MediaFailed += (_, args) =>
                    AppLog.Info("ring", $"铃声播放失败：{args.ErrorMessage}");
            }

            _player.Pause();
            _player.Source = MediaSource.CreateFromUri(new Uri(path));
            _player.Play();
        }
        catch (Exception ex)
        {
            AppLog.Info("ring", "响铃出错：" + ex.Message);
        }
    }

    /// <summary>停铃（重新开始 / 重置 / 换模式 / 离开页面时调，别让上一轮的铃追着响）。</summary>
    public static void Stop()
    {
        try { _player?.Pause(); } catch { }
    }
}
