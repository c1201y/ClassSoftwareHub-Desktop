using System;
using System.IO;
using ClassSoftwareHub.Desktop.Core;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 喊话提示音（2026-10-25）。
/// <para>
/// 默认音是内嵌的 <c>announce-alert.wav</c>（Nick 给的那份），运行时释放到 <c>cache\</c> 再交给 MediaPlayer ——
/// 单文件发布下旁边没有散落的 wav 可读，只能走内嵌这条路（与 <see cref="TimerAlarm"/> 同一套做法）。
/// 用户可以在接收端自选音效，路径存 <c>AnnounceSoundPath</c>；文件不在了自动退回内嵌那份。
/// </para>
/// </summary>
public static class AnnounceSound
{
    private const string DefaultAsset = "announce-alert.wav";

    /// <summary>选音效时允许的扩展名（都交给系统解码器）。</summary>
    public static readonly string[] Extensions =
        { ".wav", ".mp3", ".m4a", ".wma", ".aac", ".flac", ".ogg" };

    private static MediaPlayer? _player;
    private static string? _defaultPath;

    /// <summary>用户自定义音效的完整路径；空 = 用默认。直接读写 settings.json。</summary>
    public static string CustomPath
    {
        get => App.Settings.Current.AnnounceSoundPath ?? string.Empty;
        set
        {
            App.Settings.Current.AnnounceSoundPath = value ?? string.Empty;
            App.Settings.Save();
        }
    }

    /// <summary>界面上显示的名字。</summary>
    public static string DisplayName
    {
        get
        {
            var path = CustomPath;
            if (string.IsNullOrWhiteSpace(path)) return "默认提示音";
            try
            {
                var name = Path.GetFileName(path);
                return string.IsNullOrWhiteSpace(name) ? "自定义提示音" : name;
            }
            catch
            {
                return "自定义提示音";
            }
        }
    }

    /// <summary>当前该播哪个文件；自定义那份不在了就退回内嵌默认。</summary>
    public static string? ResolvePath()
    {
        var custom = CustomPath;
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom)) return custom;

        if (_defaultPath is not null && File.Exists(_defaultPath)) return _defaultPath;
        _defaultPath = EmbeddedAssets.ExtractToCache(DefaultAsset, DefaultAsset);
        return _defaultPath;
    }

    /// <summary>响一次。已经在响就先停再从头放，连点不会叠成两遍。</summary>
    public static void Play()
    {
        try
        {
            var path = ResolvePath();
            if (string.IsNullOrEmpty(path))
            {
                AppLog.Warn("announce", "提示音取不到（内嵌资源释放失败）");
                return;
            }

            if (_player is null)
            {
                _player = new MediaPlayer { AutoPlay = false };
                _player.MediaFailed += (_, args) => AppLog.Warn("announce", $"提示音播放失败：{args.ErrorMessage}");
            }

            _player.Pause();
            _player.Source = MediaSource.CreateFromUri(new Uri(path));
            _player.Play();
        }
        catch (Exception ex)
        {
            AppLog.Warn("announce", "播放提示音出错：" + ex.Message);
        }
    }

    public static void Stop()
    {
        try { _player?.Pause(); } catch { }
    }
}
