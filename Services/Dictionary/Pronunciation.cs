using System;
using ClassSoftwareHub.Desktop.Core;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace ClassSoftwareHub.Desktop.Services.Dictionary;













public static class Pronunciation
{
    private static MediaPlayer? _player;

    
    public static void Play(string word)
    {
        var text = (word ?? "").Trim();
        if (text.Length == 0) return;

        try
        {
            _player ??= new MediaPlayer { AutoPlay = false };

            
            var uri = new Uri("https://dict.youdao.com/dictvoice?audio="
                              + Uri.EscapeDataString(text) + "&type=2");
            _player.Source = MediaSource.CreateFromUri(uri);

            
            _player.PlaybackSession.Position = TimeSpan.Zero;
            _player.Play();
        }
        catch (Exception ex)
        {
            AppLog.Warning("dict", "播放发音失败：" + ex.Message);
        }
    }
}
