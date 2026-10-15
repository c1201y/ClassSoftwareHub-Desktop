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

/// <summary>任务类型 —— 决定卡片上摆哪些按钮、状态文案怎么说。</summary>
public enum DownloadKind
{
    /// <summary>普通文件（「软件下载」页点下来的那些）。</summary>
    File,

    /// <summary>
    /// 应用自身的更新包。
    /// 与普通文件的差别有三处，都不是 cosmetic：<list type="number">
    /// <item>完成后的落点是「安装」而不是「打开」—— 直接双击安装包会因为单实例 Mutex 被拒，
    ///       必须走 <c>UpdateFlow.InstallPendingNowAsync</c>（先退应用、后起安装程序）；</item>
    /// <item>下载要过校验（MD5 / SHA256），失败得能说清是哪一步；</item>
    /// <item>重试不是「把这条链接再下一遍」—— 更新包要多候选线路 + 校验，
    ///       所以更新任务不给「重试」，要重来请回设置页重新检查更新。</item>
    /// </list>
    /// </summary>
    Update,
}

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
    public DownloadTask(string url, string? suggestedName, string? title,
        DownloadKind kind = DownloadKind.File, string note = "")
    {
        Url = url;
        SuggestedName = suggestedName;
        Kind = kind;
        Note = note;
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

    /// <summary>普通文件 / 更新包（见 <see cref="DownloadKind"/>）。</summary>
    public DownloadKind Kind { get; }

    /// <summary>卡片标题下面那行固定说明（更新包用；普通下载为空则整行不占位）。</summary>
    public string Note { get; }

    /// <summary>更新包的版本号（<see cref="DownloadKind.Update"/> 才有）。</summary>
    public string UpdateTag { get; internal set; } = "";

    /// <summary>
    /// 用户点了取消 → 拿它掐断（<see cref="DownloadState.Canceled"/> 就是这么来的）。
    ///
    /// ⚠️ 更新包这条路是**外部托管**的（下载跑在 <see cref="Updating.UpdateFlow"/> 里），
    ///    令牌由那边提供 —— 卡片上的「取消」必须能掐到**真正在跑的那一次传输**，
    ///    否则按钮看着能用、点了没反应（见 <see cref="AdoptCancellation"/>）。
    /// </summary>
    public CancellationTokenSource Cancellation => _adopted ?? _own;

    private readonly CancellationTokenSource _own = new();
    private CancellationTokenSource? _adopted;

    /// <summary>外部托管的任务把真正在用的令牌交进来，卡片的「取消」从此走它。</summary>
    internal void AdoptCancellation(CancellationTokenSource cts) => _adopted = cts;

    private int _cancelRequested;

    /// <summary>
    /// 这条任务的取消**是不是用户点的**（而不是闲置超时之类的自动中止）。
    ///
    /// ⚠️ 不能靠「令牌是不是被取消了」来判断：外部托管任务里，用户点取消和闲置超时
    ///    取消的是**同一个**令牌，事后分不出来 —— 而这两件事在日志和界面上说法完全不同
    ///    （"你取消的" vs "网卡住了"）。
    /// </summary>
    internal bool CanceledByUser => _cancelRequested == 1;

    internal void MarkCancelRequested() => Interlocked.Exchange(ref _cancelRequested, 1);

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
        internal set
        {
            if (_path == value) return;
            _path = value;
            // 文件一落盘，类型就定了 —— 卡片上该给「安装」还是「解压」全看它（见 PackageKind）
            _action = PackageKind.Of(value);
            _actionSpent = false;
            OnPropertyChanged(nameof(Path));
            OnPropertyChanged(nameof(CanOpen));
            RaiseAll();
        }
    }

    // ---------------- 完成之后还能做什么：安装 / 解压 ----------------
    //
    // 下到的东西如果是安装包或压缩包，「打开」并不是用户真正想干的事 ——
    // 安装包要说「安装」，压缩包要说「解压」。两类都由扩展名判定（PackageKind），
    // 认不出来的普通文件什么都不多给。
    //
    // ⚠️ 更新包不掺和这一套：它的「安装」要先退应用（见 IsUpdate），走的是另一条路。

    private PackageAction _action = PackageAction.None;
    private bool _actionSpent;          // 解压过就不再给「解压」（再点一次只会解出个 foo (2)）
    private string? _extractDir;        // 解压产物目录
    private string _workText = "";      // 解压/安装的过程说明与结果
    private bool _working;              // 正在解压（安装是拉起外部程序，不算）

    /// <summary>这个文件是安装包还是压缩包（普通文件为 <see cref="PackageAction.None"/>）。</summary>
    public PackageAction Action => _action;

    /// <summary>卡片上那颗按钮写什么（认不出来给空串）。</summary>
    public string ActionText => PackageKind.ActionText(_action);

    /// <summary>正在解压：卡片上换成不确定进度条，别让人以为卡住了。</summary>
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
            OnPropertyChanged(nameof(BarVisibility));   // 解压时下载那条进度条要让位
        }
    }

    /// <summary>解压 / 安装的过程说明与结果（空串时整行不占位）。</summary>
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

    /// <summary>解压产物目录（解压成功才有）。</summary>
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

    /// <summary>
    /// 「打开文件夹」该去哪：解压过就去产物目录，否则回到文件本身。
    /// 解压完之后用户想看的显然是解出来的那一堆，不是那个压缩包。
    /// </summary>
    public string PreferredRevealPath => _extractDir ?? _path ?? "";

    /// <summary>一次把「解压中 / 解压完」的状态与文案写进去（必须在 UI 线程调）。</summary>
    internal void SetWork(bool working, string text)
    {
        IsWorking = working;
        WorkText = text;
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

    /// <summary>是不是应用自身的更新包（卡片上摆「安装」而不是「打开」）。</summary>
    public bool IsUpdate => Kind == DownloadKind.Update;

    /// <summary>文件还在不在（用户可能已经把它挪走了，别让「打开」点了报错）。</summary>
    public bool CanOpen =>
        State == DownloadState.Completed && !string.IsNullOrEmpty(Path) && File.Exists(Path);

    /// <summary>卡片的图标：普通下载＝下载箭头；更新包＝同步箭头，一眼分得开。</summary>
    public string HeaderGlyph => IsUpdate ? "\uE895" : "\uE896";

    public double Percent => Progress?.Percent ?? 0;

    /// <summary>进度条的值：完成的一律拉满，不让它停在 99%。</summary>
    public double BarValue => State == DownloadState.Completed ? 100 : Percent;

    /// <summary>服务端没给 Content-Length 时走不确定进度。</summary>
    public bool BarIndeterminate => State == DownloadState.Running && (Progress?.Indeterminate ?? true);

    public string SizeText => Progress?.SizeText ?? "";

    public string SpeedText => IsRunning ? (Progress?.SpeedText ?? "") : "";

    public string StatusText => State switch
    {
        // 更新包的完成态要说清「下一步干什么」，否则用户看到「已完成」还是会去找安装包在哪
        DownloadState.Completed => IsUpdate
            ? $"下载完成并已通过校验  ·  {DownloadProgress.Size(Bytes)}  ·  可立即安装"
            : "已完成  ·  " + DownloadProgress.Size(Bytes),

        DownloadState.Running => Progress is { Indeterminate: false, Received: > 0 } p
            ? $"{p.Percent:0}%   {p.SizeText}" + (p.SpeedText.Length > 0 ? "  ·  " + p.SpeedText : "")
            : Progress is { Received: > 0 } u
                ? "已接收 " + u.SizeText + (u.SpeedText.Length > 0 ? "  ·  " + u.SpeedText : "")
                : "正在连接",

        DownloadState.Failed => (IsUpdate ? "更新包下载失败" : "下载失败")
            + (string.IsNullOrWhiteSpace(Error) ? "" : "  ·  " + Error),

        _ => IsUpdate ? "已取消更新下载" : "已取消",
    };

    // 卡片上的按钮/进度条按状态显隐（x:Bind 不会自动把 bool 转成 Visibility，所以在这儿转好）
    // ⚠️ 解压时把它收起来：它已经停在 100%（或停在下载完成那一刻），再和下面那条解压进度条并排
    //    会让人以为"怎么有两条进度"。
    public Visibility BarVisibility =>
        State is DownloadState.Running or DownloadState.Completed && !_working
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility RunningVisibility => IsRunning ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>说明行（只有更新包有，普通下载不占位）。</summary>
    public Visibility NoteVisibility => Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 「打开」只给普通下载。
    /// ⚠️ 更新包**故意不给**：直接双击安装包会被 Inno 的 AppMutex 拦下（应用还在跑），
    ///    用户端只表现为"装不上、版本没变" —— 那条路必须走「安装」（先退应用、后起安装程序）。
    /// ⚠️ 安装包也不给：那颗按钮点下去和「安装」是同一个动作（都是 shell 启动它），
    ///    并排摆两颗做同一件事的按钮只会让人犹豫。
    /// </summary>
    public Visibility OpenVisibility =>
        CanOpen && !IsUpdate && _action != PackageAction.Install ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 「打开文件夹」两种都给 —— 找不到包的时候，至少能自己去看一眼。
    /// ⚠️ 解压过之后收起来：那时候该看的是产物目录，那颗按钮由 <see cref="ExtractFolderVisibility"/> 接管。
    /// </summary>
    public Visibility RevealVisibility =>
        CanOpen && _extractDir is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>「安装」只在更新包下完、包还在的时候出现。</summary>
    public Visibility InstallVisibility => CanOpen && IsUpdate ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 普通下载下完之后多给的那颗按钮：安装包写「安装」、压缩包写「解压」。
    /// ⚠️ 更新包不给（它有自己的「安装」，那条要先退应用），解压过的也不再给。
    /// </summary>
    public Visibility ActionVisibility =>
        CanOpen && !IsUpdate && _action != PackageAction.None && !_actionSpent
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>解压中的不确定进度条。</summary>
    public Visibility WorkingVisibility => _working ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>解压 / 安装的说明行（没内容就不占位）。</summary>
    public Visibility WorkTextVisibility => _workText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 解压过之后才出现的那颗亮色按钮：直接去产物目录。
    /// 解压本来就是为了去拿里面的东西，所以它比「打开压缩包」有用得多。
    /// </summary>
    public Visibility ExtractFolderVisibility =>
        _extractDir is { Length: > 0 } && Directory.Exists(_extractDir)
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>「移除」：跑完的都让清走（只删记录，不动硬盘上的文件）。</summary>
    public Visibility RemoveVisibility => IsFinished ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 「重试」只给普通下载。更新包的重来是「多候选线路 + 校验 + 待装标记」那整套，
    /// 拿一条链接再下一遍没有意义 —— 那种情况请回设置页重新检查更新。
    /// </summary>
    public Visibility RetryVisibility => IsFinished && !IsUpdate ? Visibility.Visible : Visibility.Collapsed;

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
        task.MarkCancelRequested();   // 先留痕：之后要分得清"用户点的"和"闲置超时自动中止的"
        try { task.Cancellation.Cancel(); } catch { /* 已经结束了就随便它 */ }
    }

    /// <summary>失败/取消的任务点「重试」：把原参数原样再开一条。</summary>
    public void Retry(string id)
    {
        var task = Find(id);
        if (task is null || task.IsRunning) return;
        // 更新包没有「重试」这一说（按钮也不给）：它的重来要带多候选线路与校验，
        // 拿一条链接再下一遍纯属白费 —— 要重来请回设置页重新检查更新。
        if (task.IsUpdate) return;
        Remove(id);
        Start(task.Url, task.SuggestedName, task.Title);
    }

    // ── 外部托管任务（更新包）────────────────────────────────────────
    //
    // 为什么不让更新下载也跑 RunAsync：更新那条路要「多候选线路 → 逐条校验 → 待装标记」，
    // 和普通下载（一条链接、一个文件）不是同一件事，硬塞进 RunAsync 只会两边都拧巴。
    // 所以这里只提供「台账」：**进度与结局由 UpdateFlow 回报进来**，
    // 任务页、导航徽标、完成通知这些观察者照样工作，一行都不用改。

    /// <summary>
    /// 登记一条更新包任务并显示到「任务进行」页（下载由调用方自己跑）。
    /// ⚠️ 必须在 UI 线程调用（ObservableCollection 不能跨线程改）。
    /// </summary>
    public DownloadTask TrackUpdate(string title, string updateTag, string note, string url = "")
    {
        var task = new DownloadTask(url, null, title, DownloadKind.Update, note) { UpdateTag = updateTag };
        Tasks.Insert(0, task);
        Changed?.Invoke();
        return task;
    }

    /// <summary>回报进度（外部托管任务专用）。</summary>
    public void ReportProgress(DownloadTask task, DownloadProgress progress) => task.Progress = progress;

    /// <summary>回报成功收尾：文件已落盘且校验通过。</summary>
    public void CompleteExternal(DownloadTask task, string path, long bytes)
    {
        task.FileName = Path.GetFileName(path);
        task.Path = path;
        task.Bytes = bytes;
        task.State = DownloadState.Completed;   // 状态放最后：界面看到"完成"时数据已经齐了
        Finished?.Invoke(task);
        Changed?.Invoke();
    }

    /// <summary>回报失败收尾。</summary>
    public void FailExternal(DownloadTask task, string error)
    {
        task.Error = error;
        task.State = DownloadState.Failed;
        Finished?.Invoke(task);
        Changed?.Invoke();
    }

    /// <summary>回报用户取消（**不**触发 <see cref="Finished"/>：自己取消的不需要打扰他）。</summary>
    public void CancelExternal(DownloadTask task)
    {
        task.State = DownloadState.Canceled;
        Changed?.Invoke();
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
            Core.AppLog.Info("download", $"用户取消下载「{task.Title}」");
        }
        catch (Exception ex)
        {
            task.Error = ex.Message;
            task.State = DownloadState.Failed;
            // ⚠️ 这里**不重复记异常** —— DownloadService.DownloadAsync 已经带着完整堆栈记过了。
            //    这条是"任务面板上它落到失败态"的收尾标记，顺带兜住"异常抛在 DownloadService 之前"的情况。
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
