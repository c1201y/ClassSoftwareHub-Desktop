using System;
using System.Text;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core.Dictionary;
using ClassSoftwareHub.Desktop.Services.Dictionary;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 查阅单词（需求清单第 17 条，2026-11-01）。
///
/// <para>
/// **两块独立区域**：上面「查单词」、下面「翻译句子」，各有各的输入框（2026-11-01 Nick 定）。
/// 不合成一个框的理由：查词是敲几个字母挑候选，翻译是粘一整段话 —— 挤在一个框里，
/// 补全列表在翻译时纯属碍事，而回车提交到底是"查"还是"翻"也得靠猜。
/// </para>
/// <para>
/// 页面只负责"把请求送出去、把结果摆出来"，词源怎么选由 <see cref="DictionaryService"/> 决定 ——
/// 这里⛔ 不判断"有没有网""该不该先查本地"，否则换个源就要改界面。
/// </para>
/// </summary>
public sealed partial class DictionaryPage : Page
{
    /// <summary>最近查询最多留几条。十条够回看，再多就把页面顶得没地方放结果了。</summary>
    private const int HistoryLimit = 10;

    /// <summary>补全候选最多几个。太多会盖住页面，八个一屏内够挑。</summary>
    private const int SuggestLimit = 8;

    /// <summary>空状态里摆几个示例词，点了直接查。</summary>
    private static readonly string[] Samples = { "apple", "classroom", "experiment", "光合作用" };

    private bool _wordBusy;
    private bool _transBusy;

    /// <summary>补全请求的序号。异步回来发现序号变了说明用户又敲了字，旧结果直接丢掉。</summary>
    private int _suggestToken;

    /// <summary>当前显示的词条，复制按钮要拿它拼文本。</summary>
    private DictEntry? _lastEntry;

    public DictionaryPage()
    {
        InitializeComponent();
        RenderHistory();
        RenderSamples();

        // 输入框拿到焦点再查询，省一次点击 —— 打开这页十有八九就是要查个词
        Loaded += (_, _) => WordBox.Focus(FocusState.Programmatic);
    }

    // ═══════════════════════ 一、查单词 ═══════════════════════

    private async void WordBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // 只有"用户在打字"才补全；程序设置 Text（比如点最近查询）不算，否则会自己弹一次列表
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        var text = sender.Text ?? "";
        var token = ++_suggestToken;

        // 中文不打补全：词库只存英文词，拿中文当前缀一个候选都出不来
        if (LexiconFormat.HasChinese(text) || text.Trim().Length == 0)
        {
            sender.ItemsSource = null;
            return;
        }

        await LocalLexicon.EnsureLoadedAsync();
        if (token != _suggestToken) return;   // 期间又敲了字，这次的结果作废

        var list = LocalLexicon.Suggest(text, SuggestLimit);
        sender.ItemsSource = list.Count > 0 ? list : null;
    }

    private void WordBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // 提交后立刻收起候选列表：不收起的话下拉会一直盖在结果卡上面，
        // 用户以为"没查到"，其实结果就在底下（2026-11-01 截图踩到）。
        sender.ItemsSource = null;
        sender.IsSuggestionListOpen = false;

        _ = SearchWordAsync(args.ChosenSuggestion as string ?? args.QueryText);
    }

    private async Task SearchWordAsync(string? query)
    {
        if (_wordBusy) return;

        var text = (query ?? "").Trim();
        if (text.Length == 0) return;

        if (!string.Equals(WordBox.Text, text, StringComparison.Ordinal)) WordBox.Text = text;

        _wordBusy = true;
        WordNotice.IsOpen = false;

        try
        {
            // 明确按"单词"查：翻译有单独的区域，这里不再自动兜底翻译
            var result = await DictionaryService.LookupAsync(text, QueryModes.Word);

            if (result.Found)
            {
                RenderEntry(result.Entry!);
                Remember(text);
                ShowNote(WordNotice, result.Note, InfoBarSeverity.Informational);
            }
            else
            {
                _lastEntry = null;
                WordBlock.Visibility = Visibility.Collapsed;
                WordEmpty.Visibility = Visibility.Visible;
                ShowNote(WordNotice, result.Note, InfoBarSeverity.Warning, result.Error);
            }
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("dict", ex, "查询单词");
            ShowNote(WordNotice, "查询时出错：" + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _wordBusy = false;
        }
    }

    private void RenderEntry(DictEntry entry)
    {
        _lastEntry = entry;

        WordText.Text = entry.Word;
        WordSource.Text = entry.SourceLabel;
        PhoneticText.Text = entry.Phonetic;
        PhoneticText.Visibility = entry.Phonetic.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 中译英方向没有音标、也没法念，喇叭一并收起来
        SpeakButton.Visibility = entry.IsZhToEn ? Visibility.Collapsed : Visibility.Visible;

        SensePanel.Children.Clear();
        for (var i = 0; i < entry.Senses.Count; i++)
        {
            if (i > 0 && !entry.IsZhToEn) SensePanel.Children.Add(Separator());
            SensePanel.Children.Add(BuildSense(entry.Senses[i], entry.IsZhToEn));
        }

        ExamplePanel.Children.Clear();
        foreach (var example in entry.Examples) ExamplePanel.Children.Add(BuildExample(example));

        ExampleBlock.Visibility = entry.Examples.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        WordBlock.Visibility = Visibility.Visible;
        WordEmpty.Visibility = Visibility.Collapsed;
    }

    private UIElement BuildSense(DictSense sense, bool zhToEn)
    {
        // 中译英：每个英文词都能点一下复制 —— 抄进教案时省得手动选中
        if (zhToEn)
        {
            var link = new HyperlinkButton
            {
                Content = sense.Text,
                FontSize = 15,
                Padding = new Thickness(0, 1, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            link.Click += (_, _) => CopyText(sense.Text, WordNotice, "已复制「" + sense.Text + "」。");
            return link;
        }

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (sense.Pos.Length > 0)
        {
            var pos = new TextBlock
            {
                Text = sense.Pos,
                FontSize = 12.5,
                Opacity = 0.6,
                MinWidth = 30,
                Margin = new Thickness(0, 3, 0, 0)
            };
            Grid.SetColumn(pos, 0);
            row.Children.Add(pos);
        }

        var text = new TextBlock
        {
            Text = sense.Text,
            FontSize = 14,
            LineHeight = 21,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        Grid.SetColumn(text, sense.Pos.Length > 0 ? 1 : 0);
        Grid.SetColumnSpan(text, sense.Pos.Length > 0 ? 1 : 2);
        row.Children.Add(text);

        return row;
    }

    private static UIElement BuildExample(DictExample example)
    {
        var stack = new StackPanel { Spacing = 2 };

        stack.Children.Add(new TextBlock
        {
            Text = example.En,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        });

        if (example.Zh.Length > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = example.Zh,
                FontSize = 12.5,
                Opacity = 0.62,
                TextWrapping = TextWrapping.Wrap
            });
        }

        return stack;
    }

    private static UIElement Separator() => new Border
    {
        Height = 1,
        Opacity = 0.1,
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
        Margin = new Thickness(0, 2, 0, 2)
    };

    private void Speak_Click(object sender, RoutedEventArgs e)
    {
        if (_lastEntry is null) return;
        Pronunciation.Play(_lastEntry.Word);
    }

    private void CopyWord_Click(object sender, RoutedEventArgs e)
        => CopyText(BuildCopyText(_lastEntry), WordNotice, "已复制到剪贴板。");

    /// <summary>拼成能直接粘进教案的纯文本：词 + 音标 + 逐条释义 + 例句。</summary>
    private static string BuildCopyText(DictEntry? entry)
    {
        if (entry is null) return "";

        var sb = new StringBuilder();
        sb.Append(entry.Word);
        if (entry.Phonetic.Length > 0) sb.Append(' ').Append(entry.Phonetic);
        sb.AppendLine();

        foreach (var sense in entry.Senses)
        {
            if (sense.Pos.Length > 0) sb.Append(sense.Pos).Append(' ');
            sb.AppendLine(sense.Text);
        }

        foreach (var example in entry.Examples)
        {
            sb.AppendLine().AppendLine(example.En);
            if (example.Zh.Length > 0) sb.AppendLine(example.Zh);
        }

        return sb.ToString().TrimEnd();
    }

    // ═══════════════════════ 二、翻译句子 ═══════════════════════

    private async void Translate_Click(object sender, RoutedEventArgs e) => await TranslateAsync();

    private async Task TranslateAsync()
    {
        if (_transBusy) return;

        var text = (SentenceBox.Text ?? "").Trim();
        if (text.Length == 0)
        {
            ShowNote(TransNotice, "请先输入要翻译的句子。", InfoBarSeverity.Informational);
            return;
        }

        _transBusy = true;
        TranslateButton.IsEnabled = false;
        TransNotice.IsOpen = false;

        // 先给出方向提示：翻译要几秒，先让用户知道"它认出来这是中译英"
        DirectionHint.Text = LexiconFormat.HasChinese(text) ? "中 → 英" : "英 → 中";

        try
        {
            var result = await DictionaryService.LookupAsync(text, QueryModes.Sentence);

            if (result.Translation is { IsEmpty: false })
            {
                RenderTranslation(result.Translation);
                ShowNote(TransNotice, result.Note, InfoBarSeverity.Informational);
            }
            else
            {
                TransBlock.Visibility = Visibility.Collapsed;
                ShowNote(TransNotice, result.Note, InfoBarSeverity.Warning, result.Error);
            }
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("dict", ex, "翻译句子");
            ShowNote(TransNotice, "翻译时出错：" + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _transBusy = false;
            TranslateButton.IsEnabled = true;
        }
    }

    private void RenderTranslation(TranslationResult translation)
    {
        TransDirection.Text = translation.DirectionLabel;
        TransProvider.Text = translation.Provider;
        TransTarget.Text = translation.Target;
        TransSource.Text = translation.Source;

        DirectionHint.Text = translation.DirectionLabel;
        TransBlock.Visibility = Visibility.Visible;
    }

    private void CopyTranslation_Click(object sender, RoutedEventArgs e)
        => CopyText(TransTarget.Text, TransNotice, "已复制译文。");

    // ═══════════════════════ 公用 ═══════════════════════

    private static void ShowNote(InfoBar bar, string note, InfoBarSeverity severity, string extra = "")
    {
        if (note.Length == 0 && extra.Length == 0) return;

        bar.Severity = severity;
        bar.Message = extra.Length > 0 ? note + "（" + extra + "）" : note;
        bar.IsOpen = true;
    }

    /// <summary>写剪贴板并回一句提示。失败也不能崩：剪贴板被别的程序占着是常事。</summary>
    private static void CopyText(string text, InfoBar bar, string message)
    {
        if (text.Length == 0) return;

        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);

            ShowNote(bar, message, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowNote(bar, "复制失败：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    private void Remember(string word)
    {
        if (word.Length == 0) return;

        var history = App.Settings.Current.DictionaryHistory;
        history.RemoveAll(w => string.Equals(w, word, StringComparison.OrdinalIgnoreCase));
        history.Insert(0, word);

        while (history.Count > HistoryLimit) history.RemoveAt(history.Count - 1);

        App.Settings.Save();
        RenderHistory();
    }

    private void RenderHistory()
    {
        var history = App.Settings.Current.DictionaryHistory;

        HistoryPanel.Children.Clear();
        foreach (var word in history)
        {
            // 老存档里可能还有整句，一条长句能把整块撑开 —— 超过 18 个字就截断显示
            var label = word.Length > 18 ? word[..18] + "…" : word;

            var item = new HyperlinkButton
            {
                Content = label,
                FontSize = 13,
                Padding = new Thickness(0, 2, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            ToolTipService.SetToolTip(item, word);

            var captured = word;
            item.Click += (_, _) => _ = SearchWordAsync(captured);
            HistoryPanel.Children.Add(item);
        }

        HistoryBlock.Visibility = history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>清空最近查询。不弹确认 —— 这里存的是"查过哪些词"，丢了没有代价，多一次确认反而烦。</summary>
    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.DictionaryHistory.Clear();
        App.Settings.Save();
        RenderHistory();
    }

    private void RenderSamples()
    {
        SamplePanel.Children.Clear();
        foreach (var word in Samples)
        {
            var chip = new Button
            {
                Content = word,
                FontSize = 12.5,
                Padding = new Thickness(12, 5, 12, 5),
                CornerRadius = new CornerRadius(14)
            };

            var captured = word;
            chip.Click += (_, _) => _ = SearchWordAsync(captured);
            SamplePanel.Children.Add(chip);
        }
    }

    private void HubAiEntry_Click(object sender, RoutedEventArgs e) => AiFlyout.Toggle();
}
