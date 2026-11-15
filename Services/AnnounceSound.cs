using System;
using System.IO;
using ClassSoftwareHub.Desktop.Core;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace ClassSoftwareHub.Desktop.Services;









public static class AnnounceSound
{
    private const string DefaultAsset = "announce-alert.wav";

    
    public static readonly string[] Extensions =
        { ".wav", ".mp3", ".m4a", ".wma", ".aac", ".flac", ".ogg" };

    private static MediaPlayer? _player;
    private static string? _defaultPath;

    
    public static string CustomPath
    {
        get => App.Settings.Current.AnnounceSoundPath ?? string.Empty;
        set
        {
            App.Settings.Current.AnnounceSoundPath = value ?? string.Empty;
            App.Settings.Save();
        }
    }

    
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

    
    public static string? ResolvePath()
    {
        var custom = CustomPath;
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom)) return custom;

        if (_defaultPath is not null && File.Exists(_defaultPath)) return _defaultPath;
        _defaultPath = EmbeddedAssets.ExtractToCache(DefaultAsset, DefaultAsset);
        return _defaultPath;
    }

    
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
