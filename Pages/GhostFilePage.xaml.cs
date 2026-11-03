using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 结果列表里的一行。包一层是为了让勾选状态能双向绑回控件（<see cref="GhostFile.GhostEntry"/> 本身是只读的）。
/// </summary>
public sealed class GhostRow : INotifyPropertyChanged
{
    private bool _selected = true;      // 默认全勾上：用户扫完通常就是想把它们清掉

    public required GhostFile.GhostEntry Entry { get; init; }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }

    /// <summary>末尾的空格用 ␣ 画出来 —— 不然一行「新建文件夹」和「新建文件夹 」在屏幕上完全一样。</summary>
    public string DisplayName => Entry.DisplayName;

    public string SubText => (Entry.IsDirectory ? "文件夹" : "文件") + "　·　" + Entry.Reason;

    public string SizeText => Entry.IsDirectory ? "" : FormatSize(Entry.Size);

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string FormatSize(long bytes) => bytes switch
    {
        < 0 => "",
        < 1024 => bytes + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#") + " KB",
        _ => (bytes / 1024.0 / 1024.0).ToString("0.#") + " MB",
    };
}

/// <summary>
/// 「幽灵文件清理」页（实验性功能，2026-11-03）。
///
/// 判定与删除都在 <see cref="GhostFile"/>，这里只管界面与那道确认流程。
/// </summary>
public sealed partial class GhostFilePage : Page
{
    private List<GhostRow> _rows = new();

    public GhostFilePage()
    {
        InitializeComponent();

        // 回车直接扫，省得每次去点按钮
        PathBox.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            Scan_Click(this, new RoutedEventArgs());
        };
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
            };

            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            PathBox.Text = folder.Path;
            Scan_Click(this, new RoutedEventArgs());
        }
        catch (Exception ex)
        {
            AppLog.Exception("ghost", ex, "选择文件夹");
            ShowMessage(InfoBarSeverity.Warning, "无法打开文件夹选择器", "可以直接在输入框里手动填写路径。");
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim().Trim('"');
        if (path.Length == 0)
        {
            ShowMessage(InfoBarSeverity.Warning, "还没有填目录", "请填写幽灵文件所在的那一级文件夹。");
            return;
        }

        HideResult();

        var result = await Task.Run(() => GhostFile.Scan(path));
        ApplyScanResult(result);
    }

    /// <summary>把扫描结果铺到界面上（初次扫描和删完重扫都走这里）。</summary>
    private void ApplyScanResult(GhostFile.ScanResult result)
    {
        if (result.Error.Length > 0)
        {
            ShowMessage(InfoBarSeverity.Warning, "没法扫描这个目录", result.Error);
            return;
        }

        if (result.Items.Count == 0)
        {
            // 这一支很重要：扫不到不等于没救 —— 文件系统里根本没这东西，那是资源管理器没刷新视图。
            var detail = result.Total == 0
                ? "这个目录当前是空的。"
                : $"这个目录里共 {result.Total} 个条目，名字和访问都正常。";

            ShowMessage(InfoBarSeverity.Success, "没有发现幽灵文件",
                detail + "如果资源管理器里还挂着一个已经删掉的东西，那多半是显示没有刷新，点下面的按钮让它重新读一遍。");
            RefreshRow.Visibility = Visibility.Visible;
            return;
        }

        _rows = result.Items.Select(item => new GhostRow { Entry = item }).ToList();
        ResultList.ItemsSource = _rows;

        ResultPanel.Visibility = Visibility.Visible;
        ResultTitle.Text = $"扫描结果（{_rows.Count} 项）";

        AppLog.Info("ghost", $"扫到 {_rows.Count} 个幽灵条目（该目录共 {result.Total} 个）");
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var picked = _rows.Where(r => r.Selected).ToList();
        if (picked.Count == 0)
        {
            ShowMessage(InfoBarSeverity.Informational, "没有选中任何条目", "勾选要删除的条目后再点删除。");
            return;
        }

        if (!await ConfirmAsync(picked.Count)) return;

        DeleteButton.IsEnabled = false;
        try
        {
            var failures = new List<string>();
            var ok = 0;

            foreach (var row in picked)
            {
                var (success, error) = await Task.Run(() =>
                {
                    var done = GhostFile.Delete(row.Entry, out var message);
                    return (done, message);
                });

                if (success) ok++;
                else if (failures.Count < 3) failures.Add($"{row.DisplayName}：{error}");
            }

            AppLog.Info("ghost", $"删除完成：成功 {ok} 个，失败 {picked.Count - ok} 个");

            // 删完立刻重扫一遍，让列表反映真实情况（别让用户对着已经删掉的东西再点一次）
            var parent = picked[0].Entry.Parent;
            PathBox.Text = parent;

            var result = await Task.Run(() => GhostFile.Scan(parent));
            ApplyScanResult(result);

            if (failures.Count == 0)
            {
                ShowMessage(InfoBarSeverity.Success, $"已删除 {ok} 个条目", "列表已重新扫描。");
            }
            else
            {
                ShowMessage(InfoBarSeverity.Warning,
                    $"已删除 {ok} 个，{picked.Count - ok} 个没删掉",
                    "没删掉的原因：" + string.Join("；", failures));
            }
        }
        finally
        {
            DeleteButton.IsEnabled = true;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim().Trim('"');
        if (path.Length == 0) return;

        GhostFile.RefreshExplorer(path);
        ShowMessage(InfoBarSeverity.Informational, "已通知资源管理器刷新",
            "回到资源管理器看看那个图标还在不在。如果还在，把它所在的文件夹关掉再重新打开。");
    }

    /// <summary>
    /// 二次确认，带 3 秒缓冲。
    ///
    /// <para>
    /// 3 秒是刻意留的：这些东西是**永久删除、进不了回收站**，而这一页的操作往往是在
    /// 「咦怎么删不掉」的烦躁状态下做的 —— 手快连点两下就把别的东西删了。
    /// 缓冲期间主按钮是禁用的，按钮上的数字一跳一跳，用户能看清自己在确认什么。
    /// </para>
    /// </summary>
    private async Task<bool> ConfirmAsync(int count)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认永久删除",
            Content = $"将永久删除 {count} 个条目。\r\n这些条目不会进回收站，删除后无法恢复。",
            PrimaryButtonText = "永久删除（3）",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
        };

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);

        var left = 3;
        timer.Tick += (_, _) =>
        {
            left--;
            if (left > 0)
            {
                dialog.PrimaryButtonText = $"永久删除（{left}）";
                return;
            }

            timer.Stop();
            dialog.PrimaryButtonText = "永久删除";
            dialog.IsPrimaryButtonEnabled = true;
        };

        timer.Start();

        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            AppLog.Exception("ghost", ex, "删除确认对话框");
            return false;
        }
        finally
        {
            timer.Stop();
        }
    }

    private void HideResult()
    {
        ResultPanel.Visibility = Visibility.Collapsed;
        MessageBar.IsOpen = false;
        RefreshRow.Visibility = Visibility.Collapsed;
        DeleteButton.IsEnabled = true;
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string message)
    {
        MessageBar.Severity = severity;
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.IsOpen = true;

        if (severity != InfoBarSeverity.Success) RefreshRow.Visibility = Visibility.Collapsed;
    }
}
