using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>下载任务的状态。</summary>
public enum DownloadState
{
    /// <summary>正在下。</summary>
    Running,

    /// <summary>下完了（文件已经落盘、改好名）。</summary>
    Completed,

    /// <summary>失败了，原因在 <see cref="DownloadTask.Error"/> 里。</summary>
    Failed,

    /// <summary>用户自己取消的。</summary>
    Canceled,
}

/// <summary>
/// 一条下载任务 —— 同时是给界面看的视图模型（属性变更会通知绑定）。
///
/// ⚠️ 关键设计：**任务的生命周期跟任何对话框无关**。
/// 以前下载是「一个模态弹窗守着下载」，把弹窗关掉就等于取消；半个 G 的包只能干等着。
/// 现在弹窗只是它的一张脸：点「看看别的」把弹窗收掉，任务照样在
/// <see cref="DownloadManager"/> 里跑，左侧「任务进行」随时能看到进度、取消、打开（2026-09-26 起）。
/// </summary>
public sealed class DownloadTask : INotifyPropertyChanged
{
    public DownloadTask(string url, string? suggestedName, string? title)
    {
        Url = url;
        SuggestedName = suggestedName;
        Title = string.IsNullOrWhiteSpace(title)
            ? DownloadService.ResolveFileName(url, suggestedName)
            : title!.Trim();
    }

    /// <summary>进程内唯一标识（取消 / 移除时用它找）。</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public string Url { get; }

    /// <summary>界面上给的「软件名 + 平台 + 版本」；没有就让 DownloadService 从链接猜。</summary>
    public string? SuggestedName { get; }

    /// <summary>列表/弹窗标题（软件名，不是落盘文件名）。</summary>
    public string Title { get; }

    /// <summary>用户点了取消 → 拿它掐断（<see cref="DownloadState.Canceled"/> 就是这么来的）。</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    public DateTime StartedAt { get; } = DateTime.Now;

    // ---------------- 会变的字段 ----------------

    private DownloadState _state = DownloadState.Running;
    public DownloadState State
    {
        get => _state;
        internal set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged(nameof(State));
            RaiseAll();
        }
    }

    private DownloadProgress? _progress;
    public DownloadProgress? Progress
    {
        get => _progress;
        internal set
        {
            _progress = value;
            // 进度是高频事件（最多 8 次/秒），只通知真正跟着变的那几个，别整片刷
            OnPropertyChanged(nameof(Percent));
            OnPropertyChanged(nameof(BarValue));
            OnPropertyChanged(nameof(BarIndeterminate));
            OnPropertyChanged(nameof(SizeText));
            OnPropertyChanged(nameof(SpeedText));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    private string? _path;
    /// <summary>落盘后的完整路径（完成才有）。</summary>
    public string? Path
    {
        get => _path;
        internal set { if (_path == value) return; _path = value; OnPropertyChanged(nameof(Path)); OnPropertyChanged(nameof(CanOpen)); }
    }

    private string _fileName = "";
    /// <summary>落盘的最终文件名（服务端 Content-Disposition 可能改过名）。</summary>
    public string FileName
    {
        get => _fileName;
        internal set { if (_fileName == value) return; _fileName = value; OnPropertyChanged(nameof(FileName)); }
    }

    private long _bytes;
    public long Bytes
    {
        get => _bytes;
        internal set { if (_bytes == value) return; _bytes = value; OnPropertyChanged(nameof(Bytes)); OnPropertyChanged(nameof(StatusText)); }
    }

    private string? _error;
    /// <summary>失败原因（英文异常信息照原样留着，别吞）。</summary>
    public string? Error
    {
        get => _error;
        internal set { if (_error == value) return; _error = value; OnPropertyChanged(nameof(Error)); OnPropertyChanged(nameof(StatusText)); }
    }

    // ---------------- 界面直接绑的派生属性 ----------------

    public bool IsRunning => State == DownloadState.Running;
    public bool IsFinished => State != DownloadState.Running;

    /// <summary>文件还在不在（用户可能已经把它挪走了，别让「打开」点了报错）。</summary>
    public bool CanOpen =>
        State == DownloadState.Completed && !string.IsNullOrEmpty(Path) && File.Exists(Path);

    public double Percent => Progress?.Percent ?? 0;

    /// <summary>进度条的值：完成的一律拉满，不让它停在 99%。</summary>
    public double BarValue => State == DownloadState.Completed ? 100 : Percent;

    /// <summary>服务端没给 Content-Length 时走不确定进度。</summary>
    public bool BarIndeterminate => State == DownloadState.Running && (Progress?.Indeterminate ?? true);

    public string SizeText => Progress?.SizeText ?? "";

    public string SpeedText => IsRunning ? (Progress?.SpeedText ?? "") : "";

    public string StatusText => State switch
    {
        DownloadState.Running => Progress is { Indeterminate: false, Received: > 0 } p
            ? $"{p.Percent:0}%   {p.SizeText}" + (p.SpeedText.Length > 0 ? "  ·  " + p.SpeedText : "")
            : Progress is { Received: > 0 } u
                ? "已接收 " + u.SizeText + (u.SpeedText.Length > 0 ? "  ·  " + u.SpeedText : "")
                : "正在连接…",

        DownloadState.Completed => "已完成  ·  " + DownloadProgress.Size(Bytes),
        DownloadState.Failed => "下载失败" + (string.IsNullOrWhiteSpace(Error) ? "" : "  ·  " + Error),
        _ => "已取消",
    };

    // 卡片上的按钮/进度条按状态显隐（x:Bind 不会自动把 bool 转成 Visibility，所以在这儿转好）
    public Visibility BarVisibility =>
        State is DownloadState.Running or DownloadState.Completed ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RunningVisibility => IsRunning ? Visibility.Visible : Visibility.Collapsed;

    public Visibility OpenVisibility => CanOpen ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RetryVisibility => IsFinished ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(CanOpen));
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(BarValue));
        OnPropertyChanged(nameof(BarIndeterminate));
        OnPropertyChanged(nameof(BarVisibility));
        OnPropertyChanged(nameof(RunningVisibility));
        OnPropertyChanged(nameof(OpenVisibility));
        OnPropertyChanged(nameof(RetryVisibility));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(StatusText));
    }
}

/// <summary>
/// 下载任务总台（进程级单例，<see cref="Current"/>）。
///
/// 为什么要它：下载是个「慢且可以离开」的过程，界面不该被它绑住。
/// 弹窗、左侧「任务进行」页、导航上的 InfoBadge、完成后的系统通知，
/// 全都是这份任务列表的观察者 —— 谁都不持有下载本身。
/// </summary>
public sealed class DownloadManager
{
    public static DownloadManager Current { get; } = new();

    private DownloadManager() { }

    /// <summary>任务列表，新的排最前。界面可以把它直接当 ItemsSource。</summary>
    public ObservableCollection<DownloadTask> Tasks { get; } = new();

    /// <summary>列表结构变了（新增 / 移除 / 跑完）→ 界面刷新徽标和汇总行。进度更新不走这里。</summary>
    public event Action? Changed;

    /// <summary>一条任务跑到终点（完成 / 失败）。**用户主动取消不触发**（那不是需要打扰他的事）。</summary>
    public event Action<DownloadTask>? Finished;

    /// <summary>正在跑的任务数（左侧导航 InfoBadge 用它）。</summary>
    public int ActiveCount => Tasks.Count(t => t.IsRunning);

    public DownloadTask? Find(string id) => Tasks.FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// 开一条下载，立刻返回（不等它下完）。
    ///
    /// ⚠️ **必须在 UI 线程调用**：内部用 <see cref="Progress{T}"/> 捕获当前的同步上下文，
    /// 好在 UI 线程上更新 <see cref="Tasks"/>（ObservableCollection 不能跨线程改）。
    /// </summary>
    public DownloadTask Start(string url, string? suggestedName = null, string? title = null)
    {
        var task = new DownloadTask(url, suggestedName, title);
        Tasks.Insert(0, task);        // 新任务排最前，用户一眼看到刚点的那个
        Changed?.Invoke();
        _ = RunAsync(task);
        return task;
    }

    /// <summary>取消一条正在跑的（已经结束的忽略）。</summary>
    public void Cancel(string id)
    {
        var task = Find(id);
        if (task is not { IsRunning: true }) return;
        try { task.Cancellation.Cancel(); } catch { /* 已经结束了就随便它 */ }
    }

    /// <summary>失败/取消的任务点「重试」：把原参数原样再开一条。</summary>
    public void Retry(string id)
    {
        var task = Find(id);
        if (task is null || task.IsRunning) return;
        Remove(id);
        Start(task.Url, task.SuggestedName, task.Title);
    }

    /// <summary>从列表里抹掉一条（**只删记录，不动已经下载到硬盘的文件**）。跑着的不让抹。</summary>
    public void Remove(string id)
    {
        var task = Find(id);
        if (task is null || task.IsRunning) return;
        Tasks.Remove(task);
        Changed?.Invoke();
    }

    /// <summary>「清除已完成」：把跑完的（完成/失败/取消）都清掉，正在跑的留着。</summary>
    public void ClearFinished()
    {
        var done = Tasks.Where(t => t.IsFinished).ToList();
        if (done.Count == 0) return;
        foreach (var task in done) Tasks.Remove(task);
        Changed?.Invoke();
    }

    private async Task RunAsync(DownloadTask task)
    {
        // 进度只写给任务自己：任务卡片靠它的 PropertyChanged 更新，
        // 不用每 120ms 再喊一遍 Changed（那是"列表结构变了"的信号，只在开始/结束时发）。
        var progress = new Progress<DownloadProgress>(p => task.Progress = p);

        try
        {
            var file = await DownloadService.DownloadAsync(
                task.Url, task.SuggestedName, null, progress, task.Cancellation.Token);

            task.FileName = file.FileName;
            task.Path = file.Path;
            task.Bytes = file.Bytes;
            task.State = DownloadState.Completed;             // 状态放最后：界面看到"完成"时数据已经齐了
            Finished?.Invoke(task);
        }
        catch (OperationCanceledException)
        {
            task.State = DownloadState.Canceled;              // 自己取消的，不打扰
        }
        catch (Exception ex)
        {
            task.Error = ex.Message;
            task.State = DownloadState.Failed;
            Finished?.Invoke(task);
        }
        finally
        {
            try { task.Cancellation.Dispose(); } catch { }
            Changed?.Invoke();
        }
    }
}
