using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// Markdown 写作页：左编辑、右呈现，导出纯文本 / HTML / PDF。
/// 2026-11-21 重做，去掉了页头标题与示例内容，改成全宽 1:2 分栏。
/// </summary>
public sealed partial class MarkdownToolPage : Page
{
    private WebView2? _web;
    private DispatcherQueueTimer? _renderTimer;
    private DispatcherQueueTimer? _noticeTimer;
    private string _status = "0 行 · 0 字";

    /// <summary>右键菜单「用 ClassSoftwareHub 打开」带过来的文件，页面导航后自动导入。</summary>
    public static string? PendingImport { get; set; }

    public MarkdownToolPage() : this(false) { }

    /// <param name="embedded">放进独立窗口时为 true：窗口标题栏已经写着工具名，页面里不再重复。</param>
    public MarkdownToolPage(bool embedded)
    {
        InitializeComponent();

        if (embedded)
        {
            PageTitle.Visibility = Visibility.Collapsed;
            PageRoot.RowSpacing = 0;
            PageRoot.Margin = new Thickness(20, 10, 20, 12);
        }

        Render();
        SetStatus(_status);
    }

    private void EditorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (EditorBox is null) return;

        if (_renderTimer is null)
        {
            _renderTimer = DispatcherQueue.CreateTimer();
            _renderTimer.Interval = TimeSpan.FromMilliseconds(120);
            _renderTimer.IsRepeating = false;
            _renderTimer.Tick += (_, _) =>
            {
                Render();
                SetStatus(Count(EditorBox.Text));
            };
        }

        // 连续输入只渲染最后一次，避免长文档每敲一个字都重排整页。
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    private void Render()
    {
        if (Preview is null || EditorBox is null) return;

        MarkdownView.Render(
            Preview,
            EditorBox.Text ?? "",
            Services.ThemeBrush.Get(this, "TextFillColorPrimaryBrush"));
    }

    private static string Count(string? text)
    {
        var value = text ?? "";
        var lines = value.Length == 0 ? 0 : value.Replace("\r\n", "\n").Split('\n').Length;

        var words = 0;
        foreach (var ch in value)
            if (!char.IsWhiteSpace(ch)) words++;

        return $"{lines} 行 · {words} 字";
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add(".md");
            picker.FileTypeFilter.Add(".markdown");
            picker.FileTypeFilter.Add(".txt");
            WindowHandle.BindPicker(picker, this);

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var text = await FileIO.ReadTextAsync(file);
            SetEditorText(text);
            Flash("已导入 " + file.Name);
        }
        catch (Exception ex)
        {
            AppLog.Warn("markdown", "导入文件失败：" + ex.Message);
            Flash("无法读取该文件。");
        }
    }

    private void Stage_Click(object sender, RoutedEventArgs e)
    {
        var text = EditorBox.Text ?? "";
        if (text.Trim().Length == 0)
        {
            Flash("还没有内容。");
            return;
        }

        Views.Markdown.MarkdownStageWindow.Show(text);
    }

    /// <summary>统一成 \n 再进编辑器：TextBox 自己会用 \r，导出与解析都只按 \n 走。</summary>
    private void SetEditorText(string text)
    {
        EditorBox.Text = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        Render();
        SetStatus(Count(EditorBox.Text));
    }

    private void Bold_Click(object sender, RoutedEventArgs e) => Wrap("**");

    private void Italic_Click(object sender, RoutedEventArgs e) => Wrap("*");

    private void Code_Click(object sender, RoutedEventArgs e) => Wrap("`");

    private void Heading_Click(object sender, RoutedEventArgs e) => PrefixLine("## ");

    private void Bullet_Click(object sender, RoutedEventArgs e) => PrefixLine("- ");

    private void Quote_Click(object sender, RoutedEventArgs e) => PrefixLine("> ");

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (EditorBox is null) return;

        var text = EditorBox.Text ?? "";
        var start = Math.Clamp(EditorBox.SelectionStart, 0, text.Length);
        var length = Math.Clamp(EditorBox.SelectionLength, 0, text.Length - start);
        var label = length > 0 ? text.Substring(start, length) : "链接文字";

        EditorBox.Text = text[..start] + $"[{label}](https://)" + text[(start + length)..];
        EditorBox.SelectionStart = start + label.Length + 3;
        EditorBox.SelectionLength = 8;
        EditorBox.Focus(FocusState.Programmatic);
    }

    private void Image_Click(object sender, RoutedEventArgs e) => Insert("![](图片地址)", 4, 4);

    private void Table_Click(object sender, RoutedEventArgs e)
    {
        if (EditorBox is null) return;

        var text = EditorBox.Text ?? "";
        var start = Math.Clamp(EditorBox.SelectionStart, 0, text.Length);
        var lead = start > 0 && text[start - 1] != '\n' ? "\n" : "";

        Insert(lead + "| 列一 | 列二 |\n| --- | --- |\n| 内容 | 内容 |\n", lead.Length, 0);
    }

    /// <summary>在光标处插入片段，并把选择区落到需要用户替换的那一段。</summary>
    private void Insert(string snippet, int caretOffset, int selectionLength)
    {
        if (EditorBox is null) return;

        var text = EditorBox.Text ?? "";
        var start = Math.Clamp(EditorBox.SelectionStart, 0, text.Length);

        EditorBox.Text = text.Insert(start, snippet);
        EditorBox.SelectionStart = start + caretOffset;
        EditorBox.SelectionLength = selectionLength;
        EditorBox.Focus(FocusState.Programmatic);
    }

    /// <summary>给选区两侧加标记；选区已经包着同一标记时改为取消。</summary>
    private void Wrap(string token)
    {
        if (EditorBox is null) return;

        var text = EditorBox.Text ?? "";
        var start = Math.Clamp(EditorBox.SelectionStart, 0, text.Length);
        var length = Math.Clamp(EditorBox.SelectionLength, 0, text.Length - start);
        var selected = text.Substring(start, length);

        var wrapped = selected.Length >= token.Length * 2
                      && selected.StartsWith(token, StringComparison.Ordinal)
                      && selected.EndsWith(token, StringComparison.Ordinal);

        if (wrapped)
        {
            var inner = selected[token.Length..^token.Length];
            EditorBox.Text = text[..start] + inner + text[(start + length)..];
            EditorBox.SelectionStart = start;
            EditorBox.SelectionLength = inner.Length;
        }
        else
        {
            EditorBox.Text = text[..start] + token + selected + token + text[(start + length)..];
            EditorBox.SelectionStart = start + token.Length;
            EditorBox.SelectionLength = selected.Length;
        }

        EditorBox.Focus(FocusState.Programmatic);
    }

    /// <summary>在当前行首加减块级前缀，再点一次即取消。</summary>
    private void PrefixLine(string prefix)
    {
        if (EditorBox is null) return;

        var text = EditorBox.Text ?? "";
        var start = Math.Clamp(EditorBox.SelectionStart, 0, text.Length);
        var lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;

        if (string.CompareOrdinal(text, lineStart, prefix, 0, prefix.Length) == 0)
        {
            EditorBox.Text = text.Remove(lineStart, prefix.Length);
            EditorBox.SelectionStart = Math.Max(lineStart, start - prefix.Length);
        }
        else
        {
            EditorBox.Text = text.Insert(lineStart, prefix);
            EditorBox.SelectionStart = start + prefix.Length;
        }

        EditorBox.SelectionLength = 0;
        EditorBox.Focus(FocusState.Programmatic);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (EditorBox is null) return;

        if (EditorBox.Text.Length == 0) return;

        EditorBox.Text = "";
        Render();
        SetStatus(Count(""));
        EditorBox.Focus(FocusState.Programmatic);
    }

    private void CopyPlain_Click(object sender, RoutedEventArgs e)
    {
        var plain = MarkdownExport.ToPlainText(EditorBox.Text ?? "");
        if (plain.Length == 0)
        {
            Flash("还没有内容。");
            return;
        }

        try
        {
            var data = new DataPackage();
            data.SetText(plain);
            Clipboard.SetContent(data);
            Clipboard.Flush();
            Flash("已复制纯文本。");
        }
        catch (Exception ex)
        {
            AppLog.Warn("markdown", "复制纯文本失败：" + ex.Message);
            Flash("复制失败，请重试。");
        }
    }

    private async void ExportText_Click(object sender, RoutedEventArgs e)
    {
        var plain = MarkdownExport.ToPlainText(EditorBox.Text ?? "");
        if (plain.Length == 0)
        {
            Flash("还没有内容。");
            return;
        }

        if (await SaveAsync("纯文本", ".txt", plain)) Flash("已导出纯文本。");
    }

    private async void ExportHtml_Click(object sender, RoutedEventArgs e)
    {
        var text = EditorBox.Text ?? "";
        if (text.Trim().Length == 0)
        {
            Flash("还没有内容。");
            return;
        }

        var html = MarkdownExport.ToHtml(text, "Markdown");
        if (await SaveAsync("HTML 文档", ".html", html)) Flash("已导出 HTML。");
    }

    private async void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        var text = EditorBox.Text ?? "";
        if (text.Trim().Length == 0)
        {
            Flash("还没有内容。");
            return;
        }

        StorageFile? target;
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "markdown-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            };
            picker.FileTypeChoices.Add("PDF 文档", new List<string> { ".pdf" });
            WindowHandle.BindPicker(picker, this);
            target = await picker.PickSaveFileAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("markdown", "选择 PDF 保存位置失败：" + ex.Message);
            Flash("选择保存位置失败。");
            return;
        }

        if (target is null) return;

        Flash("正在生成 PDF…");

        try
        {
            var web = await EnsureWebAsync();
            if (web?.CoreWebView2 is null)
            {
                Flash("PDF 导出不可用：缺少网页运行环境。");
                return;
            }

            var loaded = new TaskCompletionSource<bool>();
            void OnNavigated(WebView2 _, CoreWebView2NavigationCompletedEventArgs args)
                => loaded.TrySetResult(args.IsSuccess);

            web.NavigationCompleted += OnNavigated;
            web.NavigateToString(MarkdownExport.ToHtml(text, "Markdown"));

            var ok = await loaded.Task;
            web.NavigationCompleted -= OnNavigated;

            if (!ok)
            {
                Flash("PDF 生成失败：页面未加载完成。");
                return;
            }

            var printed = await web.CoreWebView2.PrintToPdfAsync(target.Path, null);
            Flash(printed ? "已导出 PDF。" : "PDF 生成失败，请重试。");
        }
        catch (Exception ex)
        {
            AppLog.Warn("markdown", "导出 PDF 失败：" + ex.Message);
            Flash("PDF 生成失败，请重试。");
        }
    }

    private async Task<bool> SaveAsync(string typeName, string extension, string content)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "markdown-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            };
            picker.FileTypeChoices.Add(typeName, new List<string> { extension });
            WindowHandle.BindPicker(picker, this);

            var file = await picker.PickSaveFileAsync();
            if (file is null) return false;

            await FileIO.WriteTextAsync(file, content);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("markdown", "导出失败：" + ex.Message);
            Flash("导出失败，请换一个位置再试。");
            return false;
        }
    }

    /// <summary>PDF 需要真实的 WebView2 内核；只在第一次导出时创建，用完随页面释放。</summary>
    private async Task<WebView2?> EnsureWebAsync()
    {
        if (_web is not null) return _web;

        try
        {
            PdfHost.Width = 820;
            PdfHost.Height = 1120;

            var web = new WebView2
            {
                Width = 820,
                Height = 1120,
                IsHitTestVisible = false,
            };

            PdfHost.Children.Add(web);

            var environment = await App.WebRuntime.GetEnvironmentAsync();
            await web.EnsureCoreWebView2Async(environment);

            _web = web;
            return web;
        }
        catch (Exception ex)
        {
            AppLog.Warn("markdown", "PDF 渲染环境不可用：" + ex.Message);
            return null;
        }
    }

    private void SetStatus(string text)
    {
        _status = text;
        if (StatusText is not null) StatusText.Text = text;
    }

    /// <summary>状态行临时显示一句结果，2.5 秒后回到行数字数。</summary>
    private void Flash(string message)
    {
        SetStatus(message);

        if (_noticeTimer is null)
        {
            _noticeTimer = DispatcherQueue.CreateTimer();
            _noticeTimer.Interval = TimeSpan.FromSeconds(2.5);
            _noticeTimer.IsRepeating = false;
            _noticeTimer.Tick += (_, _) => SetStatus(Count(EditorBox.Text));
        }

        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (PendingImport is not { Length: > 0 } path) return;

        PendingImport = null;
        _ = ImportPathAsync(path);
    }

    /// <summary>独立窗口或右键菜单调用：直接读入一个 Markdown 文件。</summary>
    public async Task ImportFromPath(string path) => await ImportPathAsync(path);

    private async Task ImportPathAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            SetEditorText(await FileIO.ReadTextAsync(file));
            Flash("已导入 " + file.Name);
        }
        catch (Exception ex)
        {
            AppLog.Warn("markdown", "导入失败：" + ex.Message);
            Flash("无法读取该文件。");
        }
    }

    /// <summary>窗口关闭时释放 PDF 用的网页内核。</summary>
    public void CloseWebView()
    {
        _renderTimer?.Stop();
        _noticeTimer?.Stop();

        try { _web?.Close(); } catch { }
        _web = null;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        CloseWebView();
        base.OnNavigatedFrom(e);
    }
}
