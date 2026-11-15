using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views.Submit;

/// <summary>
/// 一条下载项。字段与站点 <c>downloads[]</c> 一一对应，绑定到向导里的卡片。
/// <para>
/// 用 INotifyPropertyChanged 是因为上传完成后要回头改 <see cref="Url"/> 与 <see cref="Uploading"/>，
/// 卡片又躺在 DataTemplate 里，拿不到控件引用 —— 让绑定自己刷新最省事。
/// </para>
/// </summary>
public sealed class DownloadDraft : INotifyPropertyChanged
{
    private string _platform = "";
    private string _note = "";
    private string _size = "";
    private string _url = "";
    private string _hash = "";
    private string _kind = "";
    private int _number;
    private bool _uploading;
    private double _uploadProgress;
    private string _uploadHint = "";

    /// <summary>平台说明（如 Windows x64 安装版）。对应 downloads[].platform。</summary>
    public string Platform { get => _platform; set => Set(ref _platform, value); }

    /// <summary>备注（如 便携版）。对应 downloads[].note。</summary>
    public string Note { get => _note; set => Set(ref _note, value); }

    /// <summary>体积（如 1.6 MB）。对应 downloads[].size。</summary>
    public string Size { get => _size; set => Set(ref _size, value); }

    /// <summary>下载直链或本站上传后的 oss:// 键。</summary>
    public string Url { get => _url; set { if (Set(ref _url, value)) OnPropertyChanged(nameof(IsUrlLocked)); } }

    /// <summary>校验值（选填）。</summary>
    public string Hash { get => _hash; set => Set(ref _hash, value); }

    /// <summary>下载方式：空＝自动识别，其余见 <see cref="KindOptions"/>。</summary>
    public string Kind { get => _kind; set { if (Set(ref _kind, value)) OnPropertyChanged(nameof(KindIndex)); } }

    /// <summary>「下载方式」下拉的索引。⛔ 与网页版顺序一致，第 0 项是自动识别。</summary>
    public int KindIndex
    {
        get => Kind switch { "file" => 1, "netdisk" => 2, "store" => 3, _ => 0 };
        set => Kind = value switch { 1 => "file", 2 => "netdisk", 3 => "store", _ => "" };
    }

    /// <summary>本站上传回填的键是服务端生成的，改一个字符就失效 ⇒ 输入框只读。</summary>
    public bool IsUrlLocked => Url.Trim().StartsWith("oss://", StringComparison.OrdinalIgnoreCase);

    /// <summary>卡片标题里的序号，由页面统一重排。</summary>
    public int Number { get => _number; set { if (Set(ref _number, value)) OnPropertyChanged(nameof(NumberText)); } }

    public string NumberText => $"下载项 {Number}";

    /// <summary>正在上传（按钮变灰、进度条出现）。</summary>
    public bool Uploading
    {
        get => _uploading;
        set
        {
            if (Set(ref _uploading, value)) OnPropertyChanged(nameof(UploadVisibility));
        }
    }

    /// <summary>上传进度条与状态行的显隐（DataTemplate 不能挂转换器，直接给 Visibility）。</summary>
    public Microsoft.UI.Xaml.Visibility UploadVisibility
        => Uploading ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>上传进度 0–1。</summary>
    public double UploadProgress { get => _uploadProgress; set => Set(ref _uploadProgress, value); }

    /// <summary>上传状态那一行文字。</summary>
    public string UploadHint { get => _uploadHint; set => Set(ref _uploadHint, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 一次投稿的全部内容 —— **唯一数据源**。
///
/// <para>
/// 五个步骤页只读写这个对象，提交时由 <see cref="BuildPayload"/> 统一生成请求体。
/// ⛔ 不要再让页面之间互相读控件：那正是旧提交页（<c>Pages/SubmitPage</c>）1139 行的来由 ——
/// 恢复草稿要手抄 20 个字段、一键读取要逐个比对、下载项还维护四套平行列表。
/// </para>
/// </summary>
public sealed class SubmitDraft : INotifyPropertyChanged
{
    private string _id = "";
    private string _name = "";
    private string _icon = "";
    private string _category = "";
    private string _tagline = "";
    private string _description = "";
    private string _contact = "";
    private string _version = "";
    private string _size = "";
    private string _system = "";
    private string _website = "";
    private string _github = "";
    private string _author = "";
    private string _license = "";
    private string _notice = "";
    private string _store = "";
    private string _sort = "";
    private string _gitHubRepo = "";
    private bool _preferPrerelease;
    private bool _idEdited;

    public string Id { get => _id; set => Set(ref _id, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Icon { get => _icon; set => Set(ref _icon, value); }
    public string Category { get => _category; set => Set(ref _category, value); }
    public string Tagline { get => _tagline; set => Set(ref _tagline, value); }
    public string Description { get => _description; set => Set(ref _description, value); }

    /// <summary>联系方式。提交前本地加密，草稿里也存密文。</summary>
    public string Contact { get => _contact; set => Set(ref _contact, value); }

    public string Version { get => _version; set => Set(ref _version, value); }
    public string Size { get => _size; set => Set(ref _size, value); }
    public string System { get => _system; set => Set(ref _system, value); }
    public string Website { get => _website; set => Set(ref _website, value); }
    public string Github { get => _github; set => Set(ref _github, value); }
    public string Author { get => _author; set => Set(ref _author, value); }
    public string License { get => _license; set => Set(ref _license, value); }
    public string Notice { get => _notice; set => Set(ref _notice, value); }
    public string Store { get => _store; set => Set(ref _store, value); }
    public string Sort { get => _sort; set => Set(ref _sort, value); }

    /// <summary>第 1 步读过的仓库地址，回到该页时保留。</summary>
    public string GitHubRepo { get => _gitHubRepo; set => Set(ref _gitHubRepo, value); }

    /// <summary>读取时是否优先取预发布版。</summary>
    public bool PreferPrerelease { get => _preferPrerelease; set => Set(ref _preferPrerelease, value); }

    /// <summary>用户手改过 ID 之后，名称再变也不覆盖它。</summary>
    public bool IdEdited { get => _idEdited; set => Set(ref _idEdited, value); }

    /// <summary>
    /// 适用系统（平台 key）。用 List 而不是 HashSet：提交时按勾选顺序写进 platforms[]，
    /// 与网页版产出的顺序一致。数量最多十来个，Contains 的开销可以忽略。
    /// </summary>
    public List<string> Platforms { get; } = new();

    /// <summary>下载项。至少一条，最多 <see cref="MaxDownloads"/> 条。</summary>
    public ObservableCollection<DownloadDraft> Downloads { get; } = new();

    /// <summary>与网页版 <c>MAX_DOWNLOADS</c> 同一个数，服务端按这个口径收。</summary>
    public const int MaxDownloads = 5;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>新建一份空白草稿，并放入一条空的下载项。</summary>
    public static SubmitDraft CreateEmpty()
    {
        var draft = new SubmitDraft();
        draft.Downloads.Add(new DownloadDraft { Number = 1 });
        return draft;
    }

    /// <summary>「再提交一个」用：清空全部内容，只留一条空下载项。</summary>
    public void Reset()
    {
        Id = "";
        Name = "";
        Icon = "";
        Category = "";
        Tagline = "";
        Description = "";
        Contact = "";
        Version = "";
        Size = "";
        System = "";
        Website = "";
        Github = "";
        Author = "";
        License = "";
        Notice = "";
        Store = "";
        Sort = "";
        GitHubRepo = "";
        PreferPrerelease = false;
        IdEdited = false;

        Platforms.Clear();
        Downloads.Clear();
        Downloads.Add(new DownloadDraft { Number = 1 });
    }

    // ── 校验 ─────────────────────────────────────────────────────────────

    /// <summary>第 2 步（软件信息）能否过关；返回 null 表示通过。</summary>
    public string? ValidateInfo()
    {
        if (Name.Trim().Length == 0) return "请填写软件名称。";

        var id = Id.Trim();
        if (id.Length == 0) return "请填写软件 ID，或先填写名称让它自动生成。";
        if (!SoftwareSlug.IsValid(id)) return "软件 ID 只能使用小写字母、数字与短横线，且不能以横线开头或结尾。";

        if (Category.Trim().Length == 0) return "请选择分类。";
        if (Tagline.Trim().Length == 0) return "请填写一句话简介，它会显示在首页卡片上。";
        if (Description.Trim().Length == 0) return "请填写详细介绍。";
        if (Contact.Trim().Length == 0) return "请填写联系方式，审核过程中可能需要与你确认内容。";
        return null;
    }

    /// <summary>第 3 步（安装包与系统）能否过关。</summary>
    public string? ValidatePackage()
    {
        if (Platforms.Count == 0) return "请至少勾选一项适用系统。";

        var withUrl = Downloads.Where(item => item.Url.Trim().Length > 0).ToList();
        if (withUrl.Count == 0) return "请至少填写一条下载直链，或把文件上传到本站存储。";

        for (var i = 0; i < Downloads.Count; i++)
        {
            var item = Downloads[i];
            if (item.Url.Trim().Length == 0) continue;

            var hash = NormalizeHash(item.Hash);
            if (hash.Length > 0 && !IsHashLike(hash))
                return $"第 {i + 1} 个下载项的校验值格式不正确：需为纯十六进制，位数需匹配一种算法（32/40/56/64/96/128）。";
        }

        return null;
    }

    /// <summary>提交前的完整校验（各步必填项 + 联系方式）。</summary>
    public string? ValidateAll()
        => ValidateInfo() ?? ValidatePackage() ?? (((string?)BuildPayload()["_联系方式"])!.Length == 0
            ? "联系方式加密失败，请重新填写联系方式。"
            : null);

    // ── 生成请求体 ───────────────────────────────────────────────────────

    /// <summary>
    /// 组装提交请求体。字段名与网页版、服务端白名单严格一致，⛔ 不要在这里改名或加字段 ——
    /// 服务端按白名单重建草稿，多出来的字段会被静默丢掉。
    /// </summary>
    public Dictionary<string, object?> BuildPayload()
    {
        var downloads = Downloads
            .Where(item => item.Url.Trim().Length > 0)
            .Select(item =>
            {
                var entry = new Dictionary<string, object?>
                {
                    ["platform"] = item.Platform.Trim(),
                    ["note"] = item.Note.Trim(),
                    ["size"] = item.Size.Trim(),
                    ["url"] = item.Url.Trim(),
                };

                var hash = NormalizeHash(item.Hash);
                if (hash.Length > 0) entry["hash"] = hash;
                if (item.Kind.Length > 0) entry["kind"] = item.Kind;
                return entry;
            })
            .ToList();

        var payload = new Dictionary<string, object?>
        {
            ["id"] = Id.Trim(),
            ["name"] = Name.Trim(),
            ["icon"] = Icon.Trim(),
            ["category"] = Category.Trim(),
            ["tagline"] = Tagline.Trim(),
            ["description"] = Description.Trim(),
            ["version"] = Version.Trim(),
            ["size"] = Size.Trim(),
            ["system"] = System.Trim(),
            // 列表页的系统筛选只认这个字段，缺了软件在任何平台下都查不到
            ["platforms"] = Platforms.ToList(),
            ["website"] = Website.Trim(),
            ["github"] = Github.Trim(),
            ["author"] = Author.Trim(),
            ["license"] = License.Trim(),
            ["notice"] = Notice.Trim(),
            ["store"] = Store.Trim(),
            ["downloads"] = downloads,
            // 下划线开头 = 只给审核工单看，合并时会被剥掉；本地加密后只传密文
            ["_联系方式"] = EncryptContact(),
        };

        var sortText = Sort.Trim();
        if (sortText.Length > 0 && long.TryParse(sortText, out var sort)) payload["sort"] = sort;

        return payload;
    }

    private string EncryptContact()
    {
        var contact = Contact.Trim();
        if (contact.Length == 0) return "";
        try { return AgeEncryption.EncryptToArmor(contact); }
        catch { return ""; }
    }

    /// <summary>校验值里可能带着「SHA-256:」前缀或空格，统一成纯十六进制。</summary>
    public static string NormalizeHash(string raw)
    {
        var text = (raw ?? "").Trim();
        text = Regex.Replace(text, @"^(md5|sha-?1|sha-?224|sha-?256|sha-?384|sha-?512)\s*[:=]?\s*", "",
            RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "^0x", "", RegexOptions.IgnoreCase);
        return Regex.Replace(text, @"[\s:]", "");
    }

    private static readonly int[] HashLengths = { 32, 40, 56, 64, 96, 128 };

    private static bool IsHashLike(string value)
        => Regex.IsMatch(value, "^[0-9a-fA-F]+$") && HashLengths.Contains(value.Length);

    // ── 草稿：本机留存，关窗不丢 ─────────────────────────────────────────
    //
    // 与旧提交页分开存（submit-draft.json 是旧页的），互不覆盖。

    private static string DraftFile => Path.Combine(AppPaths.DataDir, "submit-draft-v2.json");

    /// <summary>把当前内容写进本机草稿。每次翻页与关窗都调，写不进去不影响使用。</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);

            var file = new DraftSnapshot
            {
                Id = Id,
                Name = Name,
                Icon = Icon,
                Category = Category,
                Tagline = Tagline,
                Description = Description,
                // 草稿里也只留密文：本机的明文联系方式没有必要落盘
                Contact = EncryptContact(),
                Version = Version,
                Size = Size,
                System = System,
                Platforms = Platforms.ToList(),
                Website = Website,
                Github = Github,
                Author = Author,
                License = License,
                Notice = Notice,
                Store = Store,
                Sort = Sort,
                GitHubRepo = GitHubRepo,
                PreferPrerelease = PreferPrerelease,
                IdEdited = IdEdited,
                Downloads = Downloads.Select(item => new DraftDownload
                {
                    Platform = item.Platform,
                    Note = item.Note,
                    Size = item.Size,
                    Url = item.Url,
                    Hash = item.Hash,
                    Kind = item.Kind,
                }).ToList(),
            };

            File.WriteAllText(DraftFile, JsonSerializer.Serialize(file,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AppLog.Warn("submit", "草稿保存失败：" + ex.Message);
        }
    }

    /// <summary>读本机草稿；没有或读坏了返回 null。</summary>
    public static SubmitDraft? Load()
    {
        try
        {
            if (!File.Exists(DraftFile)) return null;

            var file = JsonSerializer.Deserialize<DraftSnapshot>(File.ReadAllText(DraftFile));
            if (file is null) return null;

            var draft = new SubmitDraft
            {
                Id = file.Id ?? "",
                Name = file.Name ?? "",
                Icon = file.Icon ?? "",
                Category = file.Category ?? "",
                Tagline = file.Tagline ?? "",
                Description = file.Description ?? "",
                // 密文解不开，只能让用户重填：草稿里不存明文
                Contact = (file.Contact ?? "").StartsWith("-----BEGIN AGE", StringComparison.Ordinal) ? "" : (file.Contact ?? ""),
                Version = file.Version ?? "",
                Size = file.Size ?? "",
                System = file.System ?? "",
                Website = file.Website ?? "",
                Github = file.Github ?? "",
                Author = file.Author ?? "",
                License = file.License ?? "",
                Notice = file.Notice ?? "",
                Store = file.Store ?? "",
                Sort = file.Sort ?? "",
                GitHubRepo = file.GitHubRepo ?? "",
                PreferPrerelease = file.PreferPrerelease,
                IdEdited = file.IdEdited,
            };

            foreach (var key in file.Platforms ?? new List<string>())
                if (key.Length > 0) draft.Platforms.Add(key);

            foreach (var item in file.Downloads ?? new List<DraftDownload>())
            {
                draft.Downloads.Add(new DownloadDraft
                {
                    Platform = item.Platform ?? "",
                    Note = item.Note ?? "",
                    Size = item.Size ?? "",
                    Url = item.Url ?? "",
                    Hash = item.Hash ?? "",
                    Kind = item.Kind ?? "",
                });
            }

            if (draft.Downloads.Count == 0) draft.Downloads.Add(new DownloadDraft());
            draft.Renumber();

            return draft;
        }
        catch (Exception ex)
        {
            AppLog.Warn("submit", "草稿读取失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>提交成功后清掉草稿。</summary>
    public static void ClearDraft()
    {
        try { if (File.Exists(DraftFile)) File.Delete(DraftFile); }
        catch (Exception ex) { AppLog.Warn("submit", "草稿清理失败：" + ex.Message); }
    }

    /// <summary>重新编号下载项（增删之后调用）。</summary>
    public void Renumber()
    {
        for (var i = 0; i < Downloads.Count; i++) Downloads[i].Number = i + 1;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ── 草稿文件结构 ─────────────────────────────────────────────────────

    private sealed class DraftSnapshot
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Icon { get; set; }
        public string? Category { get; set; }
        public string? Tagline { get; set; }
        public string? Description { get; set; }
        public string? Contact { get; set; }
        public string? Version { get; set; }
        public string? Size { get; set; }
        public string? System { get; set; }
        public List<string>? Platforms { get; set; }
        public string? Website { get; set; }
        public string? Github { get; set; }
        public string? Author { get; set; }
        public string? License { get; set; }
        public string? Notice { get; set; }
        public string? Store { get; set; }
        public string? Sort { get; set; }
        public string? GitHubRepo { get; set; }
        public bool PreferPrerelease { get; set; }
        public bool IdEdited { get; set; }
        public List<DraftDownload>? Downloads { get; set; }
    }

    private sealed class DraftDownload
    {
        public string? Platform { get; set; }
        public string? Note { get; set; }
        public string? Size { get; set; }
        public string? Url { get; set; }
        public string? Hash { get; set; }
        public string? Kind { get; set; }
    }
}
