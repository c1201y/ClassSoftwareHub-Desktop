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







public sealed class RosterEntry : System.ComponentModel.INotifyPropertyChanged
{
    private string _name = "";
    private int _no;

    public RosterEntry(int no, string name)
    {
        _no = no;
        _name = name;
    }

    
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

    
    public string RowLabel => $"第 {_no} 行姓名";

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            
            
            Raise(nameof(Name));
            Raise(nameof(DeleteLabel));
        }
    }

    
    public string DeleteLabel => _name.Length == 0 ? "移除这一行（空行）" : $"把「{_name}」从名单中移除";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string propertyName)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
}










public sealed partial class RosterEditorDialog : ContentDialog
{
    
    
    
    
    
    
    private readonly ObservableCollection<RosterEntry> _rows = new();

    private readonly string _source;

    
    public List<string> Result { get; } = new();

    public RosterEditorDialog(IEnumerable<string> names, string source)
    {
        InitializeComponent();

        _source = source ?? "";
        RosterList.ItemsSource = _rows;

        foreach (var name in names) _rows.Add(new RosterEntry(_rows.Count + 1, (name ?? "").Trim()));

        SyncResult();
    }

    

    
    
    
    
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var entry = new RosterEntry(_rows.Count + 1, "");
        _rows.Add(entry);
        SyncResult();                 

        
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

        for (var i = 0; i < _rows.Count; i++) _rows[i].No = i + 1;   
        SyncResult();

        ToastText.Text = removed.Length == 0
            ? "已移除该空行。"
            : $"已移除「{removed}」；关闭本窗口后生效。";
    }

    
    private async void ExportExcel_Click(object sender, RoutedEventArgs e) => await ExportAsync(excel: true);

    private async void ExportTxt_Click(object sender, RoutedEventArgs e) => await ExportAsync(excel: false);

    
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
