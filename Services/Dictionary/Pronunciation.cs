using System;
using ClassSoftwareHub.Desktop.Core;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace ClassSoftwareHub.Desktop.Services.Dictionary;

/// <summary>
/// 单词发音：直接播有道词典的语音文件（<c>dictvoice</c>），不需要本地音频包。
///
/// <para>
/// ⚠️ 这是**在线**功能：断网、或词库里查出来的生僻词没有音频时会静默失败。
///    所以界面上的喇叭按钮不做成"永远可点"，播放失败要回一句提示，别让用户以为按钮坏了。
/// </para>
/// <para>
/// ⚠️ <see cref="MediaPlayer"/> 只建一次并复用：每次点都 new 一个的话，播到一半旧实例没人管，
///    而且 MediaPlayer 本身持有系统音频会话，攒多了会明显变卡。
/// </para>
/// </summary>
public static class Pronunciation
{
    private static MediaPlayer? _player;

    /// <summary>念一个词。词为空就什么都不做。</summary>
    public static void Play(string word)
    {
        var text = (word ?? "").Trim();
        if (text.Length == 0) return;

        try
        {
            _player ??= new MediaPlayer { AutoPlay = false };

            // type=2 是美音，type=1 是英音。教室里念的多半是美音，跟词库里取的音标一致。
            var uri = new Uri("https://dict.youdao.com/dictvoice?audio="
                              + Uri.EscapeDataString(text) + "&type=2");
            _player.Source = MediaSource.CreateFromUri(uri);

            // 同一秒里连点两次时，先停下这一次再从头播，否则第二次没反应
            _player.PlaybackSession.Position = TimeSpan.Zero;
            _player.Play();
        }
        catch (Exception ex)
        {
            AppLog.Warning("dict", "播放发音失败：" + ex.Message);
        }
    }
}
