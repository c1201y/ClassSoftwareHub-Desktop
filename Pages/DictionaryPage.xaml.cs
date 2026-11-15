using System;
using System.Text;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core.Dictionary;
using ClassSoftwareHub.Desktop.Services.Dictionary;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Pages;














public sealed partial class DictionaryPage : Page
{
    
    private const int HistoryLimit = 10;

    
    private const int SuggestLimit = 8;

    
    private static readonly string[] Samples = { "apple", "classroom", "experiment", "光合作用" };

    private bool _wordBusy;
    private bool _transBusy;

    
    private int _suggestToken;

    
    private DictEntry? _lastEntry;

    public DictionaryPage()
    {
        InitializeComponent();
        RenderHistory();
        RenderSamples();

        
        Loaded += (_, _) => WordBox.Focus(FocusState.Programmatic);
    }

    

    private async void WordBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        var text = sender.Text ?? "";
        var token = ++_suggestToken;

        
        if (LexiconFormat.HasChinese(text) || text.Trim().Length == 0)
        {
            sender.ItemsSource = null;
            return;
        }

        await LocalLexicon.EnsureLoadedAsync();
        if (token != _suggestToken) return;   

        var list = LocalLexicon.Suggest(text, SuggestLimit);
        sender.ItemsSource = list.Count > 0 ? list : null;
    }

    private void WordBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        
        
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

    

    private static void ShowNote(InfoBar bar, string note, InfoBarSeverity severity, string extra = "")
    {
        if (note.Length == 0 && extra.Length == 0) return;

        bar.Severity = severity;
        bar.Message = extra.Length > 0 ? note + "（" + extra + "）" : note;
        bar.IsOpen = true;
    }

    
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
