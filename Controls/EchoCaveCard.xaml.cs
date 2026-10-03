using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace ClassSoftwareHub.Desktop.Controls;

/// <summary>
/// 回声洞卡片：把站点仓库「回声洞/messages/」目录里的字条逐条展示出来。
/// 那里一条一个文件（message1.json、message2.json……），文件里只有一句话，没有作者 / 日期。
///
/// 交互照 ClassIsland 的回声洞（<c>_refs/ClassIsland</c> 的 AboutSettingsPage + TypingControl，
/// 2026-10-03 按 Nick 要求把原来的「5 秒自动轮播」换掉）：
///
///   · **点击换一条 + 打字机逐字**，不再自动轮播（走动条一并去掉）；
///   · 进页面先静静显示一条，不打扰；点一下才动；
///   · 打字期间再点无效（<see cref="_isTyping"/> 挡住），不打断正在打的这一遍；
///   · 一轮之内不重复：整份数据洗成队列逐条出队，抽完才重洗。
///
/// 打字节奏与 ClassIsland 的 <c>TypingControl</c> 对齐：清空 → 等 150ms → 每字 40ms，
/// 光标 <c>_</c> 按 <c>(i/10)</c> 的奇偶闪 —— 不是逐字闪，是每 10 个字闪一次。
/// 逐字改文本没法用 Storyboard（Text 不是可动画属性），所以走 async/await + Task.Delay，
/// 并用一个自增的 <see cref="_typeGeneration"/> 让"上一遍"在下一个检查点自己退出 ——
/// 比嵌一层 CancellationTokenSource 简单，离开页面时也只需把代数 +1。
/// </summary>
public sealed partial class EchoCaveCard : UserControl
{
    /// <summary>起手停顿（照 ClassIsland：先清空，静一下，再开始打）。</summary>
    private const int ClearDelayMs = 150;

    /// <summary>每个字的间隔（照 ClassIsland）。</summary>
    private const int CharDelayMs = 40;

    /// <summary>光标闪动周期：每打这么多个字翻一次（照 ClassIsland 的 i/10）。</summary>
    private const int BlinkEvery = 10;

    private IReadOnlyList<EchoMessage> _messages = Array.Empty<EchoMessage>();

    /// <summary>本轮洗好的队列（抽一条少一条；空了重洗 ⇒ 一轮之内不重复）。</summary>
    private readonly List<EchoMessage> _queue = new();

    /// <summary>打字"代数"：每次开打 +1，旧的那一遍发现代数变了就自行退出。</summary>
    private int _typeGeneration;

    private bool _isTyping;
    private bool _busy;
    private bool _loaded;
    private CancellationTokenSource? _cts;

    public EchoCaveCard()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ══════════ 生命周期 ══════════

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        await ReloadAsync(force: false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;

        // 让正在跑的那一遍打字失效：它会在下一个检查点看到代数变了、自己退出
        _typeGeneration++;
        _isTyping = false;

        try { _cts?.Cancel(); } catch { /* 已经结束 */ }
        _cts = null;
    }

    // ══════════ 取数 ══════════

    private async Task ReloadAsync(bool force)
    {
        if (_busy) return;
        _busy = true;
        RefreshLink.IsEnabled = false;
        StatusText.Text = "正在读取…";

        try
        {
            try { _cts?.Cancel(); } catch { /* 上一轮已经结束 */ }
            _cts = new CancellationTokenSource();
            var result = await EchoCaveService.LoadAsync(force, _cts.Token);
            if (!_loaded) return;

            _messages = result.Messages;
            _queue.Clear();                 // 数据换了，本轮队列作废
            StatusText.Text = result.Message;

            if (_messages.Count > 0)
            {
                MessagePanel.Visibility = Visibility.Visible;
                EmptyText.Visibility = Visibility.Collapsed;

                var first = TakeNext();
                if (first is not null)
                {
                    MessageText.Text = first.Text;   // 首次直接显示全文，不打字（照 ClassIsland 的 _isFirstUpdate）
                }
            }
            else
            {
                _typeGeneration++;                // 清场：别让上一遍打字还往空状态上写
                _isTyping = false;

                MessagePanel.Visibility = Visibility.Collapsed;
                EmptyText.Visibility = Visibility.Visible;
                EmptyText.Text = result.Ok
                    ? "洞里还安安静静的 —— 可以点「投稿一条」补上第一声。"
                    : result.Message;
            }
        }
        catch (OperationCanceledException)
        {
            // 已经离开页面，什么都不用做
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            RefreshLink.IsEnabled = true;
        }
    }

    // ══════════ 洗牌队列（一轮之内不重复） ══════════

    /// <summary>取下一条；队列空了就重洗。没有数据返回 null。</summary>
    private EchoMessage? TakeNext()
    {
        if (_messages.Count == 0) return null;

        if (_queue.Count == 0) Reshuffle();
        if (_queue.Count == 0) return null;

        var message = _queue[0];
        _queue.RemoveAt(0);
        return message;
    }

    /// <summary>
    /// 把整份数据洗成一轮队列（Fisher-Yates）。
    /// ⚠️ ClassIsland 原版用的是 <c>Random.Next(0, Count - 1)</c> —— 上界是开区间，
    /// 索引 <c>Count-1</c> 那条**永远抽不到**（263 条里最后一条是死条）。这里按正确写法取到 Count。
    /// </summary>
    private void Reshuffle()
    {
        _queue.Clear();
        _queue.AddRange(_messages);

        for (var i = _queue.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (_queue[i], _queue[j]) = (_queue[j], _queue[i]);
        }
    }

    // ══════════ 点击换一条 + 打字机 ══════════

    private async void Stage_Click(object sender, RoutedEventArgs e)
    {
        // 打字期间不接受新的点击 —— 跟 ClassIsland 用 IsBusy 挡住重复点击同理
        if (_isTyping || _messages.Count == 0) return;

        var message = TakeNext();
        if (message is null) return;

        await TypeAsync(message.Text);
    }

    /// <summary>
    /// 逐字打出来。被打断（离开页面 / 清场）时不写最终文本，避免覆盖新内容。
    /// </summary>
    private async Task TypeAsync(string text)
    {
        var generation = ++_typeGeneration;
        _isTyping = true;

        try
        {
            MessageText.Text = "";
            await Task.Delay(ClearDelayMs);
            if (generation != _typeGeneration) return;

            for (var i = 0; i < text.Length; i++)
            {
                // 光标只在"前 i 个字之后"追加：每 BlinkEvery 个字亮一次，不是逐字闪
                var caret = (i / BlinkEvery) % 2 == 0 ? "_" : "";
                MessageText.Text = text[..i] + caret;

                await Task.Delay(CharDelayMs);
                if (generation != _typeGeneration) return;
            }

            MessageText.Text = text;        // 收尾补全，把可能留着的光标去掉
        }
        finally
        {
            if (generation == _typeGeneration) _isTyping = false;
        }
    }

    // ══════════ 页脚 ══════════

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync(force: true);

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri(EchoCaveService.SubmitUrl));
        }
        catch (Exception ex)
        {
            StatusText.Text = "无法打开浏览器：" + ex.Message;
        }
    }
}
