using System;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Core.Dictionary;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Services.Dictionary;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 查阅单词的小浮窗（2026-11-01）。
///
/// <para>
/// 侧边栏「查阅」按钮走的就是这扇窗（<c>SidebarModule.WindowType</c>）。之所以单开一扇，
/// 是因为讲课时把整扇主窗口拉出来会盖住课件；这扇只有 480×640，能一直摆在角落。
/// </para>
/// <para>
/// 跟主页面一样分两块：上面查单词、下面翻译句子，各有各的输入框。
/// 词源调度、双向翻译、本地词库全部复用 <see cref="DictionaryService"/> —— 这里只摆界面，
/// ⛔ 不要在这扇窗里另写一套查询逻辑，否则主界面和浮窗查出来的东西会不一样。
/// </para>
/// <para>
/// ⚠️ 关掉 = 真销毁，跟哈希校验窗一致：没有"留着复用"的价值，也别在退出链路上留窗口。
/// </para>
/// </summary>
public sealed partial class QuickLookupWindow : Window
{
    private const int WidthDip = 480;
    private const int HeightDip = 640;

    private static QuickLookupWindow? _instance;

    private AppWindow? _appWindow;
    private bool _wordBusy;
    private bool _transBusy;
    private int _suggestToken;
    private bool _placed;

    private QuickLookupWindow()
    {
        InitializeComponent();
        Configure();

        // ⚠️ Window 本身没有 Loaded 事件，要挂到根元素上
        Root.Loaded += (_, _) => WordBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// 摊开这扇窗；已经开着就调到前面。
    /// <para>
    /// ⚠️ 方法名 <c>Show</c> 是约定：<c>ToolSidebarWindow</c> 打开「专用窗口」类模块时按名字反射调用
    ///    （见 <c>Data/SidebarModule.WindowType</c>），改名会静默失效。
    /// </para>
    /// </summary>
    public static void Show()
    {
        try
        {
            _instance ??= new QuickLookupWindow();
            _instance.BringUp();
        }
        catch (Exception ex)
        {
            AppLog.Exception("dict-window", ex, "打开查阅单词窗口");
        }
    }

    /// <summary>退出应用时真正销毁（由 MainWindow 的退出链路调用；没开过就什么都不做）。</summary>
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
    }

    // ── 窗口配置 ─────────────────────────────────────────────────────────

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "查阅单词";

            if (_appWindow.Presenter is OverlappedPresenter p)
            {
                p.IsResizable = true;
                p.IsMaximizable = false;
                p.IsMinimizable = false;
            }

            WindowChrome.HideMinimizeMaximizeButtons(hwnd);

            ModernWindow.ApplyTitleBar(this, TitleBarHost, Root);

            // 跟应用主题（⛔ 不传 external：分体只作用于侧边栏与常用工具浮窗）
            BackdropHost.Apply(this, Root, App.Settings.Current.Backdrop);
            ThemeHost.Apply(Root);

            ApplySize();

            Closed += (_, _) => _instance = null;
        }
        catch (Exception ex)
        {
            AppLog.Exception("dict-window", ex, "查阅单词窗口初始化");
        }
    }

    private void ApplySize()
    {
        if (_appWindow is null) return;

        try
        {
            var scale = Scale();
            var work = DisplayArea.Primary.WorkArea;

            var maxW = work.Width / scale - 40;
            var maxH = work.Height / scale - 60;

            var w = Math.Clamp(WidthDip, 400, Math.Max(400, maxW));
            var h = Math.Clamp(HeightDip, 420, Math.Max(420, maxH));

            _appWindow.ResizeClient(new SizeInt32((int)Math.Round(w * scale), (int)Math.Round(h * scale)));
        }
        catch (Exception ex)
        {
            AppLog.Warning("dict-window", "查阅单词窗尺寸设置失败：" + ex.Message);
        }
    }

    private double Scale()
    {
        try
        {
            var dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private void CenterOnScreen()
    {
        if (_appWindow is null) return;

        try
        {
            var work = DisplayArea.Primary.WorkArea;
            var size = _appWindow.Size;
            _appWindow.Move(new PointInt32(
                work.X + Math.Max(0, (work.Width - size.Width) / 2),
                work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
        }
        catch (Exception ex)
        {
            AppLog.Warning("dict-window", "查阅单词窗定位失败：" + ex.Message);
        }
    }

    private void BringUp()
    {
        try
        {
            // 第一次显示才居中：之后用户自己挪过位置就不要动它了
            if (!_placed)
            {
                CenterOnScreen();
                _placed = true;
            }

            Activate();
            WordBox.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            AppLog.Warning("dict-window", "查阅单词窗显示失败：" + ex.Message);
        }
    }

    // ── 一、查单词 ───────────────────────────────────────────────────────

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

        var list = LocalLexicon.Suggest(text, 6);
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
                RenderWord(result.Entry!);
                ShowNote(WordNotice, result.Note, InfoBarSeverity.Informational);
            }
            else
            {
                WordBlock.Visibility = Visibility.Collapsed;
                ShowNote(WordNotice, result.Note, InfoBarSeverity.Warning, result.Error);
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("dict-window", ex, "浮窗查询单词");
            ShowNote(WordNotice, "查询时出错：" + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _wordBusy = false;
        }
    }

    private void RenderWord(DictEntry entry)
    {
        WordText.Text = entry.Word;
        PhoneticText.Text = entry.Phonetic;
        PhoneticText.Visibility = entry.Phonetic.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        WordSource.Text = entry.SourceLabel;

        SensePanel.Children.Clear();
        for (var i = 0; i < entry.Senses.Count; i++)
        {
            if (i > 0 && !entry.IsZhToEn)
            {
                SensePanel.Children.Add(new Border
                {
                    Height = 1,
                    Opacity = 0.1,
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray)
                });
            }

            SensePanel.Children.Add(BuildSense(entry.Senses[i], entry.IsZhToEn));
        }

        WordBlock.Visibility = Visibility.Visible;
    }

    private static UIElement BuildSense(DictSense sense, bool zhToEn)
    {
        var stack = new StackPanel { Spacing = 2 };

        if (!zhToEn && sense.Pos.Length > 0)
        {
            stack.Children.Add(new TextBlock { Text = sense.Pos, FontSize = 11.5, Opacity = 0.55 });
        }

        stack.Children.Add(new TextBlock
        {
            Text = sense.Text,
            FontSize = zhToEn ? 15 : 13.5,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        });

        return stack;
    }

    // ── 二、翻译句子 ─────────────────────────────────────────────────────

    private async void Translate_Click(object sender, RoutedEventArgs e)
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
                TransDirection.Text = result.Translation.DirectionLabel;
                TransProvider.Text = result.Translation.Provider;
                TransTarget.Text = result.Translation.Target;
                TransSource.Text = result.Translation.Source;
                DirectionHint.Text = result.Translation.DirectionLabel;

                TransBlock.Visibility = Visibility.Visible;
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
            AppLog.Exception("dict-window", ex, "浮窗翻译句子");
            ShowNote(TransNotice, "翻译时出错：" + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _transBusy = false;
            TranslateButton.IsEnabled = true;
        }
    }

    private void CopyTranslation_Click(object sender, RoutedEventArgs e)
    {
        if (TransTarget.Text.Length == 0) return;

        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(TransTarget.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);

            ShowNote(TransNotice, "已复制译文。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowNote(TransNotice, "复制失败：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    // ── 公用 ─────────────────────────────────────────────────────────────

    private static void ShowNote(InfoBar bar, string note, InfoBarSeverity severity, string extra = "")
    {
        if (note.Length == 0 && extra.Length == 0) return;

        bar.Severity = severity;
        bar.Message = extra.Length > 0 ? note + "（" + extra + "）" : note;
        bar.IsOpen = true;
    }
}
