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
/// 回声洞「正文区」：把站点仓库「回声洞/messages/」目录里的字条逐条展示出来。
/// 那里一条一个文件（message1.json、message2.json……），文件里只有一句话，没有作者 / 日期。
///
/// 交互照 ClassIsland 的回声洞（<c>_refs/ClassIsland</c> 的 AboutSettingsPage + TypingControl）：
///
///   · **点击换一条 + 打字机逐字**，不是自动轮播；
///   · 进页面先静静显示一条，不打扰；点一下才动；
///   · 打字期间再点无效（<see cref="_isTyping"/> 挡住），不打断正在打的这一遍；
///   · 一轮之内不重复：整份数据洗成队列逐条出队，抽完才重洗。
///
/// 打字节奏与 ClassIsland 的 <c>TypingControl</c> 对齐：清空 → 等 150ms → 每字 40ms，
/// 光标 <c>_</c> 按 <c>(i/10)</c> 的奇偶闪 —— 不是逐字闪，是每 10 个字闪一次。
/// 逐字改文本没法用 Storyboard（Text 不是可动画属性），所以走 async/await + Task.Delay，
/// 并用一个自增的 <see cref="_typeGeneration"/> 让"上一遍"在下一个检查点自己退出 ——
/// 比嵌一层 CancellationTokenSource 简单，离开页面时也只需把代数 +1。
///
/// 本控件**只管正文那一句**，不带卡片外观、不写任何状态文字（它住在设置页 SettingsExpander 展开区的
/// SettingsCard 里，见 Pages/SettingsPage.xaml）；「投稿」按钮也在那边，点了调
/// <see cref="OpenSubmitPage"/> —— 投稿地址归本控件管，设置页不必知道。
/// ⛔ 卡面除了字条本身不许有任何文字：条数、取数状态、报错、投稿回执全都不在这里显示
/// （2026-10-03 Nick：「这一片永远不要显示文字，要最纯粹的回声洞」）。
/// 取数期间例外地摆一枚 <c>ProgressRing</c>（图形，不是文字，他要的"正在加载"提示）。
/// </summary>
public sealed partial class EchoCaveCard : UserControl
{
    /// <summary>起手停顿（照 ClassIsland：先清空，静一下，再开始打）。</summary>
    private const int ClearDelayMs = 150;

    /// <summary>每个字的间隔（照 ClassIsland）。</summary>
    private const int CharDelayMs = 40;

    /// <summary>光标闪动周期：每打这么多个字翻一次（照 ClassIsland 的 i/10）。</summary>
    private const int BlinkEvery = 10;

    /// <summary>
    /// 卡片上的常驻默认文字（Nick 2026-10-03 指定）：还没取到字条、或者洞里空着时就显示这句。
    /// ⛔ 别改文案 —— 这是他要的原文。
    /// </summary>
    private const string Placeholder = "点击此处可以查看 ClassSoftwareHub 群友逆天发言";

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

        MessageText.Text = Placeholder;   // 起手先摆默认文字，别让卡片空着（取数回来再换掉）

        // 取数挂 Loaded 而不是"页面展开时调一下"：
        // SettingsExpander 的折叠内容由 ItemsRepeater 实现，折叠时**可能压根没实例化**，
        // 那样页面在展开事件里根本拿不到本控件。挂 Loaded 则两种情形都对 ——
        // 早就实例化好了就提前悄悄取完，展开时才实例化的就正好在展开那一刻取。
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

        SetLoading(false);                 // 离屏就别再转圈了（占着合成线程）
        try { _cts?.Cancel(); } catch { /* 已经结束 */ }
        _cts = null;
    }

    // ══════════ 取数 ══════════

    private async Task ReloadAsync(bool force)
    {
        if (_busy) return;
        _busy = true;
        SetLoading(true);

        try
        {
            try { _cts?.Cancel(); } catch { /* 上一轮已经结束 */ }
            _cts = new CancellationTokenSource();
            var result = await EchoCaveService.LoadAsync(force, _cts.Token);
            if (!_loaded) return;

            _messages = result.Messages;
            _queue.Clear();                 // 数据换了，本轮队列作废

            if (_messages.Count > 0)
            {
                var first = TakeNext();
                if (first is not null)
                    MessageText.Text = first.Text;   // 首次直接显示全文，不打字（照 ClassIsland 的 _isFirstUpdate）
            }
            else
            {
                _typeGeneration++;                // 清场：别让上一遍打字还往空状态上写
                _isTyping = false;

                // 没字条就回到默认文字。"链路通、只是没人说话"和"网络不通"是两句不同的话，
                // 判据在 EchoCaveService —— 但那是给日志看的，界面上一个字都不说（Nick 2026-10-03）。
                // （点不点都没反应，外层卡片那边由 ShowNext 里的空数据判断挡住。）
                MessageText.Text = Placeholder;
            }
        }
        catch (OperationCanceledException)
        {
            // 已经离开页面，什么都不用做
        }
        catch (Exception ex)
        {
            // ⛔ 卡面永远只显示字条本身：取数异常只写日志，不在这一片留任何文字。
            ScreenCapture.Log("[echo-cave] 读取失败: " + ex.Message);
        }
        finally
        {
            _busy = false;
            SetLoading(false);
        }
    }

    /// <summary>
    /// 取数期间那枚转圈（Nick 2026-10-03：要有个"正在加载"的提示）。
    /// ⛔ 它是**图形不是文字** —— 卡面依然一个字都不写（见 XAML 头注释）。
    /// </summary>
    private void SetLoading(bool loading)
    {
        LoadingRing.IsActive = loading;
        LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>
    /// 换一条并打字（**点击由外面那张卡片整卡承接** —— SettingsCard IsClickEnabled="True"，
    /// 为此正文本身不再是个按钮，免得文字外圈再画出一道框）。
    /// 打字期间 / 没有数据时不动作。
    /// </summary>
    public async void ShowNext()
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

    // ══════════ 投稿 ══════════

    /// <summary>
    /// 打开投稿入口（设置页那颗「投稿」按钮的兜底链接点这个）：
    /// GitHub 上给字条目录「新建文件」的页面，与网页版指向同一处。
    /// </summary>
    /// <returns>浏览器是否被叫起来。false 时由调用方在「投稿」弹层里说一句 ——
    /// 卡面这一片不许出现任何文字。</returns>
    public async Task<bool> OpenSubmitPage()
    {
        try
        {
            return await Launcher.LaunchUriAsync(new Uri(EchoCaveService.SubmitUrl));
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("[echo-cave] 打开投稿页失败: " + ex.Message);
            return false;
        }
    }
}
