using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using VirtualKey = Windows.System.VirtualKey;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 名单编辑弹窗里的一行。
///
/// 为什么要有这么个东西：名单最多 2000 人，列表必须走 ListView 的**虚拟化**，
/// 那就只能是"数据驱动"的（每行不能我们自己 new 控件）。
/// </summary>
public sealed class RosterEntry : System.ComponentModel.INotifyPropertyChanged
{
    private string _name = "";
    private int _no;

    public RosterEntry(int no, string name)
    {
        _no = no;
        _name = name;
    }

    /// <summary>行号（从 1 起）。⚠️ 可写：删行 / 加行之后要整排重排。</summary>
    public int No
    {
        get => _no;
        set
        {
            if (_no == value) return;
            _no = value;
            Raise(nameof(No));
            Raise(nameof(NoText));
            Raise(nameof(RowLabel));
        }
    }

    public string NoText => _no.ToString();

    /// <summary>读屏用："第 N 行姓名"。不含名字本身，所以改名后不用重发通知。</summary>
    public string RowLabel => $"第 {_no} 行姓名";

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            // Name 一起发通知是有意为之：提交时会把首尾空白去掉，
            // 通知一发出，输入框里那串没去空白的原文会被刷成规范后的值 —— 用户能看见自己写的东西被"整"过。
            Raise(nameof(Name));
            Raise(nameof(DeleteLabel));
        }
    }

    /// <summary>删除按钮的读屏名 —— 必须带上是谁，否则十行读出来一模一样。</summary>
    public string DeleteLabel => _name.Length == 0 ? "移除这一行（空行）" : $"把「{_name}」从名单中移除";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string propertyName)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// 「查看 / 编辑名单」弹窗（2026-09-29 Nick 提）：
/// 把导进来的名字列出来，逐个改名、逐个删除、随时补人，改完还能导出回 Excel / txt。
/// 一条"导入 → 核对 → 修正 → 带走"的闭环。
///
/// 语义：**改动当场生效**（改名、删行、加行都直接反映到 <see cref="Result"/>），
/// 所以底部只有一个「关闭」，不做"确定 / 取消"—— 免得用户改了五十个名字点取消全丢。
/// 调用方在 ShowAsync 之后读 <see cref="Result"/> 即可。
/// </summary>
public sealed partial class RosterEditorDialog : ContentDialog
{
    /// <summary>
    /// 界面上的行 —— **唯一真相**。
    ///
    /// ⚠️ 为什么它（而不是 <see cref="Result"/>）才是真相：用户随时能「添加姓名」多出一行，
    /// 那一行在填上名字之前不属于名单。两个集合各管一段（界面 / 有效姓名）就不会互相打脸。
    /// </summary>
    private readonly ObservableCollection<RosterEntry> _rows = new();

    private readonly string _source;

    /// <summary>当前名单（只含填了名字的行）。关掉弹窗后由调用方读走。</summary>
    public List<string> Result { get; } = new();

    public RosterEditorDialog(IEnumerable<string> names, string source)
    {
        InitializeComponent();

        _source = source ?? "";
        RosterList.ItemsSource = _rows;

        foreach (var name in names) _rows.Add(new RosterEntry(_rows.Count + 1, (name ?? "").Trim()));

        SyncResult();
    }

    // ══════════ 增 / 删 / 改 ══════════

    /// <summary>
    /// 在末尾加一行，并把键盘焦点直接送进新行的输入框 —— 点完就能打字，不用再找一次该点哪儿。
    /// （2026-09-29 Nick：「明明有删除，却不点增加是何意味，只删不增是吧？」）
    /// </summary>
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var entry = new RosterEntry(_rows.Count + 1, "");
        _rows.Add(entry);
        SyncResult();                 // 空行不算人数，但"另有 N 行未填"的提示要跟着变

        // ⚠️ 虚拟化列表：容器是滚进可视区才生成的，所以必须先布局、再滚动、再取容器。
        RosterList.UpdateLayout();
        RosterList.ScrollIntoView(entry);
        RosterList.UpdateLayout();
        FocusName(entry);

        ToastText.Text = "已添加一行，填好后按回车。空着不填的行，关闭后会自动丢弃。";
    }

    private void FocusName(RosterEntry entry)
    {
        if (RosterList.ContainerFromItem(entry) is not DependencyObject container) return;

        var box = FindDescendant<TextBox>(container);
        if (box is null) return;

        box.Focus(FocusState.Programmatic);
        box.SelectAll();
    }

    private static T? FindDescendant<T>(DependencyObject node) where T : DependencyObject
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i);
            if (child is T hit) return hit;

            var deep = FindDescendant<T>(child);
            if (deep is not null) return deep;
        }
        return null;
    }

    /// <summary>
    /// 按界面上的当前内容重建 <see cref="Result"/>。
    ///
    /// ⚠️ 为什么是"整份重建"而不是"按下标改一格"：用户可以加出空行，空行随时会被丢掉 ——
    /// 只要有任何一行可能被跳过，下标就不再对齐，逐格维护迟早会改错人。整份重建永远对。
    /// </summary>
    private void SyncResult()
    {
        Result.Clear();
        foreach (var row in _rows)
        {
            var name = (row.Name ?? "").Trim();
            if (name.Length > 0) Result.Add(name);
        }

        RefreshChrome();
    }

    /// <summary>人数 / 空状态 / 导出可用性 —— 三处都跟着名单走，集中在一处改。</summary>
    private void RefreshChrome()
    {
        var blanks = _rows.Count - Result.Count;

        CountText.Text = _rows.Count == 0
            ? "共 0 人"
            : $"共 {Result.Count} 人"
              + (blanks > 0 ? $"（另有 {blanks} 行未填，关闭后不保存）" : "")
              + (string.IsNullOrWhiteSpace(_source) ? "" : $"（{_source}）");

        RosterList.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ExportExcelButton.IsEnabled = Result.Count > 0;
        ExportTxtButton.IsEnabled = Result.Count > 0;
    }

    // 交互取舍：名字那一格**就是输入框**（不是"先点编辑再变输入框"），少一步、也不用猜哪儿能点；
    // 提交时机 = 回车 或 焦点离开；名字被清空**不提交**，还原成原值 —— 免得手一滑抽出个空白。
    // 真要删人就点右边那个删除键，两个动作分得开。
    private void Name_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        CommitName(sender as TextBox);
    }

    private void Name_LostFocus(object sender, RoutedEventArgs e) => CommitName(sender as TextBox);

    private void CommitName(TextBox? box)
    {
        if (box?.DataContext is not RosterEntry entry) return;

        var typed = (box.Text ?? "").Trim();

        if (typed.Length == 0)
        {
            // 清空一律还原成"这一行上次提交过的值"：老名字不会被手滑误删，
            // 刚加的空行也保持空着（不弹提示 —— 用户接着打字时弹提示更烦）。
            box.Text = entry.Name;
            return;
        }

        if (entry.Name == typed) return;

        entry.Name = typed;
        SyncResult();
        ToastText.Text = $"已改名为「{typed}」";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RosterEntry entry) return;

        var removed = entry.Name.Trim();
        _rows.Remove(entry);

        for (var i = 0; i < _rows.Count; i++) _rows[i].No = i + 1;   // 行号重排
        SyncResult();

        ToastText.Text = removed.Length == 0
            ? "已移除该空行。"
            : $"已移除「{removed}」；关闭本窗口后生效。";
    }

    // ══════════ 导出 ══════════
    private async void ExportExcel_Click(object sender, RoutedEventArgs e) => await ExportAsync(excel: true);

    private async void ExportTxt_Click(object sender, RoutedEventArgs e) => await ExportAsync(excel: false);

    /// <summary>导的是**当前这一份**（含刚才的手改），不是原来那个文件 —— 这样"改完导出"才有意义。</summary>
    private async System.Threading.Tasks.Task ExportAsync(bool excel)
    {
        if (Result.Count == 0)
        {
            ToastText.Text = "名单是空的，没有可导出的内容。";
            return;
        }

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "班级名单",
            };
            if (excel) picker.FileTypeChoices.Add("Excel 工作簿", new List<string> { ".xlsx" });
            else picker.FileTypeChoices.Add("文本文件", new List<string> { ".txt" });

            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            if (excel) NameRoster.WriteXlsx(file.Path, Result);
            else NameRoster.WriteText(file.Path, Result);

            ToastText.Text = $"已导出 {Result.Count} 个姓名到「{file.Name}」。";
        }
        catch (Exception ex)
        {
            ToastText.Text = "导出失败：" + ex.Message;
        }
    }

}
