using System;
using System.IO;
using ClassSoftwareHub.Desktop.Core;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace ClassSoftwareHub.Desktop.Services;
















public static class TimerAlarm
{
    
    private const string DefaultAsset = "timer-alarm.wav";

    
    public static readonly string[] Extensions =
        { ".wav", ".mp3", ".m4a", ".wma", ".aac", ".flac", ".ogg" };

    private static MediaPlayer? _player;
    private static string? _defaultPath;        

    
    public static string CustomPath
    {
        get => App.Settings.Current.TimerAlarmPath ?? string.Empty;
        set
        {
            App.Settings.Current.TimerAlarmPath = value ?? string.Empty;
            App.Settings.Save();
        }
    }

    
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

    
    public static void Stop()
    {
        try { _player?.Pause(); } catch { }
    }
}
