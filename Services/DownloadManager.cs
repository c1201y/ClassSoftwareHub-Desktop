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


public enum DownloadKind
{
    
    File,

    
    
    
    
    
    
    
    
    
    
    Update,

    
    
    
    
    
    
    
    
    
    
    
    Cleanup,
}


public enum DownloadState
{
    
    Running,

    
    Completed,

    
    Failed,

    
    Canceled,
}









public sealed class DownloadTask : INotifyPropertyChanged
{
    public DownloadTask(string url, string? suggestedName, string? title,
        DownloadKind kind = DownloadKind.File, string note = "", string? directory = null)
    {
        Url = url;
        SuggestedName = suggestedName;
        Kind = kind;
        Note = note;
        Dir = directory;              
        Title = string.IsNullOrWhiteSpace(title)
            ? DownloadService.ResolveFileName(url, suggestedName)
            : title!.Trim();
    }

    
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public string Url { get; }

    
    public string? SuggestedName { get; }

    
    public string Title { get; }

    
    public DownloadKind Kind { get; }

    
    
    
    
    
    public string? Dir { get; }

    
    public string Note { get; }

    
    public string UpdateTag { get; internal set; } = "";

    
    
    
    
    
    
    
    public CancellationTokenSource Cancellation => _adopted ?? _own;

    private readonly CancellationTokenSource _own = new();
    private CancellationTokenSource? _adopted;

    
    internal void AdoptCancellation(CancellationTokenSource cts) => _adopted = cts;

    private int _cancelRequested;

    
    
    
    
    
    
    
    internal bool CanceledByUser => _cancelRequested == 1;

    internal void MarkCancelRequested() => Interlocked.Exchange(ref _cancelRequested, 1);

    public DateTime StartedAt { get; } = DateTime.Now;

    

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
            
            OnPropertyChanged(nameof(Percent));
            OnPropertyChanged(nameof(BarValue));
            OnPropertyChanged(nameof(BarIndeterminate));
            OnPropertyChanged(nameof(SizeText));
            OnPropertyChanged(nameof(SpeedText));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    private string? _path;
    
    public string? Path
    {
        get => _path;
        internal set
        {
            if (_path == value) return;
            _path = value;
            
            _action = PackageKind.Of(value);
            _actionSpent = false;
            OnPropertyChanged(nameof(Path));
            OnPropertyChanged(nameof(CanOpen));
            RaiseAll();
        }
    }

    
    
    
    
    
    
    

    private PackageAction _action = PackageAction.None;
    private bool _actionSpent;          
    private string? _extractDir;        
    private string _workText = "";      
    private bool _working;              

    
    public PackageAction Action => _action;

    
    public string ActionText => PackageKind.ActionText(_action);

    
    public bool IsWorking
    {
        get => _working;
        internal set
        {
            if (_working == value) return;
            _working = value;
            OnPropertyChanged(nameof(IsWorking));
            OnPropertyChanged(nameof(WorkingVisibility));
            OnPropertyChanged(nameof(ActionVisibility));
            OnPropertyChanged(nameof(BarVisibility));   
        }
    }

    
    public string WorkText
    {
        get => _workText;
        internal set
        {
            if (_workText == value) return;
            _workText = value;
            OnPropertyChanged(nameof(WorkText));
            OnPropertyChanged(nameof(WorkTextVisibility));
        }
    }

    
    public string? ExtractedDir
    {
        get => _extractDir;
        internal set
        {
            if (_extractDir == value) return;
            _extractDir = value;
            _actionSpent = value is not null;
            OnPropertyChanged(nameof(ExtractedDir));
            OnPropertyChanged(nameof(PreferredRevealPath));
            OnPropertyChanged(nameof(ActionVisibility));
            OnPropertyChanged(nameof(ExtractFolderVisibility));
            OnPropertyChanged(nameof(RevealVisibility));
        }
    }

    
    
    
    
    public string PreferredRevealPath => _extractDir ?? _path ?? "";

    
    internal void SetWork(bool working, string text)
    {
        IsWorking = working;
        WorkText = text;
    }

    private string _fileName = "";
    
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
    
    public string? Error
    {
        get => _error;
        internal set { if (_error == value) return; _error = value; OnPropertyChanged(nameof(Error)); OnPropertyChanged(nameof(StatusText)); }
    }

    

    public bool IsRunning => State == DownloadState.Running;
    public bool IsFinished => State != DownloadState.Running;

    
    public bool IsUpdate => Kind == DownloadKind.Update;

    
    public bool IsCleanup => Kind == DownloadKind.Cleanup;

    
    public bool CanOpen =>
        State == DownloadState.Completed && !string.IsNullOrEmpty(Path) && File.Exists(Path);

    
    public string HeaderGlyph => Kind switch
    {
        DownloadKind.Update => "\uE895",
        DownloadKind.Cleanup => "\uE74D",
        _ => "\uE896",
    };

    public double Percent => Progress?.Percent ?? 0;

    
    public double BarValue => State == DownloadState.Completed ? 100 : Percent;

    
    public bool BarIndeterminate => State == DownloadState.Running && (Progress?.Indeterminate ?? true);

    public string SizeText => Progress?.SizeText ?? "";

    public string SpeedText => IsRunning ? (Progress?.SpeedText ?? "") : "";

    public string StatusText => State switch
    {
        
        DownloadState.Completed => IsCleanup
            ? "已清理  ·  释放 " + DownloadProgress.Size(Bytes)
            : IsUpdate
                ? $"下载完成并已通过校验  ·  {DownloadProgress.Size(Bytes)}  ·  可立即安装"
                : "已完成  ·  " + DownloadProgress.Size(Bytes),

        DownloadState.Running => IsCleanup
            ? Progress is { Indeterminate: false, Received: > 0 } c
                ? $"清理中  {c.Percent:0}%   {c.SizeText}"
                : "正在清理"
            : Progress is { Indeterminate: false, Received: > 0 } p
                ? $"{p.Percent:0}%   {p.SizeText}" + (p.SpeedText.Length > 0 ? "  ·  " + p.SpeedText : "")
                : Progress is { Received: > 0 } u
                    ? "已接收 " + u.SizeText + (u.SpeedText.Length > 0 ? "  ·  " + u.SpeedText : "")
                    : "正在连接",

        DownloadState.Failed => IsCleanup
            ? "清理未完成" + (string.IsNullOrWhiteSpace(Error) ? "" : "  ·  " + Error)
            : (IsUpdate ? "更新包下载失败" : "下载失败")
              + (string.IsNullOrWhiteSpace(Error) ? "" : "  ·  " + Error),

        _ => IsCleanup ? "已取消清理" : IsUpdate ? "已取消更新下载" : "已取消",
    };

    
    
    
    public Visibility BarVisibility =>
        State is DownloadState.Running or DownloadState.Completed && !_working
            ? Visibility.Visible
            : Visibility.Collapsed;

    
    
    
    
    
    public Visibility RunningVisibility =>
        IsRunning && !IsCleanup ? Visibility.Visible : Visibility.Collapsed;

    
    public Visibility NoteVisibility => Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    
    
    
    
    
    
    
    public Visibility OpenVisibility =>
        CanOpen && !IsUpdate && _action != PackageAction.Install ? Visibility.Visible : Visibility.Collapsed;

    
    
    
    
    public Visibility RevealVisibility =>
        CanOpen && _extractDir is null ? Visibility.Visible : Visibility.Collapsed;

    
    public Visibility InstallVisibility => CanOpen && IsUpdate ? Visibility.Visible : Visibility.Collapsed;

    
    
    
    
    public Visibility ActionVisibility =>
        CanOpen && !IsUpdate && _action != PackageAction.None && !_actionSpent
            ? Visibility.Visible
            : Visibility.Collapsed;

    
    public Visibility WorkingVisibility => _working ? Visibility.Visible : Visibility.Collapsed;

    
    public Visibility WorkTextVisibility => _workText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    
    
    
    
    public Visibility ExtractFolderVisibility =>
        _extractDir is { Length: > 0 } && Directory.Exists(_extractDir)
            ? Visibility.Visible
            : Visibility.Collapsed;

    
    public Visibility RemoveVisibility => IsFinished ? Visibility.Visible : Visibility.Collapsed;

    
    
    
    
    
    
    
    
    
    
    
    
    
    public Visibility RetryVisibility =>
        IsFinished && !IsUpdate && !IsCleanup ? Visibility.Visible : Visibility.Collapsed;

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
        OnPropertyChanged(nameof(RevealVisibility));
        OnPropertyChanged(nameof(InstallVisibility));
        OnPropertyChanged(nameof(Action));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(ActionVisibility));
        OnPropertyChanged(nameof(WorkingVisibility));
        OnPropertyChanged(nameof(WorkTextVisibility));
        OnPropertyChanged(nameof(ExtractFolderVisibility));
        OnPropertyChanged(nameof(PreferredRevealPath));
        OnPropertyChanged(nameof(RemoveVisibility));
        OnPropertyChanged(nameof(RetryVisibility));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(StatusText));
    }
}








public sealed class DownloadManager
{
    public static DownloadManager Current { get; } = new();

    private DownloadManager() { }

    
    public ObservableCollection<DownloadTask> Tasks { get; } = new();

    
    public event Action? Changed;

    
    public event Action<DownloadTask>? Finished;

    
    public int ActiveCount => Tasks.Count(t => t.IsRunning);

    public DownloadTask? Find(string id) => Tasks.FirstOrDefault(t => t.Id == id);

    
    
    
    
    
    
    public DownloadTask Start(string url, string? suggestedName = null, string? title = null, string? directory = null)
    {
        var task = new DownloadTask(url, suggestedName, title, directory: directory);
        Tasks.Insert(0, task);        
        Changed?.Invoke();
        _ = RunAsync(task);
        return task;
    }

    
    public void Cancel(string id)
    {
        var task = Find(id);
        if (task is not { IsRunning: true }) return;
        task.MarkCancelRequested();   
        try { task.Cancellation.Cancel(); } catch {  }
    }

    
    public void Retry(string id)
    {
        var task = Find(id);
        if (task is null || task.IsRunning) return;
        
        
        if (task.IsUpdate) return;
        
        
        if (task.IsCleanup) return;
        Remove(id);
        Start(task.Url, task.SuggestedName, task.Title, task.Dir);
    }

    
    
    
    
    
    

    
    
    
    
    public DownloadTask TrackUpdate(string title, string updateTag, string note, string url = "")
    {
        var task = new DownloadTask(url, null, title, DownloadKind.Update, note) { UpdateTag = updateTag };
        Tasks.Insert(0, task);
        Changed?.Invoke();
        return task;
    }

    
    public void ReportProgress(DownloadTask task, DownloadProgress progress) => task.Progress = progress;

    
    public void CompleteExternal(DownloadTask task, string path, long bytes)
    {
        task.FileName = Path.GetFileName(path);
        task.Path = path;
        task.Bytes = bytes;
        task.State = DownloadState.Completed;   
        Finished?.Invoke(task);
        Changed?.Invoke();
    }

    
    public void FailExternal(DownloadTask task, string error)
    {
        task.Error = error;
        task.State = DownloadState.Failed;
        Finished?.Invoke(task);
        Changed?.Invoke();
    }

    
    
    
    
    

    
    
    
    
    public DownloadTask TrackCleanup(string title, long totalBytes)
    {
        var task = new DownloadTask("", null, title, DownloadKind.Cleanup, "")
        {
            
            Progress = new DownloadProgress(0, Math.Max(1, totalBytes), 0),
        };

        Tasks.Insert(0, task);
        Changed?.Invoke();
        return task;
    }

    
    public void ReportCleanupProgress(DownloadTask task, long deletedBytes, long totalBytes) =>
        task.Progress = new DownloadProgress(Math.Max(0, deletedBytes), Math.Max(1, totalBytes), 0);

    
    public void CompleteCleanup(DownloadTask task, long freedBytes)
    {
        task.Bytes = Math.Max(0, freedBytes);
        task.Progress = new DownloadProgress(task.Bytes, Math.Max(1, task.Bytes), 0);
        task.State = DownloadState.Completed;   
        Finished?.Invoke(task);
        Changed?.Invoke();
    }

    
    public void CancelExternal(DownloadTask task)
    {
        task.State = DownloadState.Canceled;
        Changed?.Invoke();
    }

    
    public void Remove(string id)
    {
        var task = Find(id);
        if (task is null || task.IsRunning) return;
        Tasks.Remove(task);
        Changed?.Invoke();
    }

    
    public void ClearFinished()
    {
        var done = Tasks.Where(t => t.IsFinished).ToList();
        if (done.Count == 0) return;
        foreach (var task in done) Tasks.Remove(task);
        Changed?.Invoke();
    }

    private async Task RunAsync(DownloadTask task)
    {
        
        
        var progress = new Progress<DownloadProgress>(p => task.Progress = p);

        try
        {
            var file = await DownloadService.DownloadAsync(
                task.Url, task.SuggestedName, task.Dir, progress, task.Cancellation.Token);

            task.FileName = file.FileName;
            task.Path = file.Path;
            task.Bytes = file.Bytes;
            task.State = DownloadState.Completed;             
            Finished?.Invoke(task);
        }
        catch (OperationCanceledException)
        {
            task.State = DownloadState.Canceled;              
            Core.AppLog.Info("download", $"用户取消下载「{task.Title}」");
        }
        catch (Exception ex)
        {
            task.Error = ex.Message;
            task.State = DownloadState.Failed;
            
            
            Core.AppLog.Info("download", $"任务「{task.Title}」以失败结束：{ex.Message}");
            Finished?.Invoke(task);
        }
        finally
        {
            try { task.Cancellation.Dispose(); } catch { }
            Changed?.Invoke();
        }
    }
}
