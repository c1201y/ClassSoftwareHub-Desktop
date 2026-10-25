using System;
using System.Collections.Generic;
using System.Linq;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Data;

/// <summary>一条下载项（对应 app JSON 里 downloads[] 的元素）。</summary>
public sealed class DownloadItem
{
    public string Platform { get; set; } = "";
    public string Note { get; set; } = "";
    public string Size { get; set; } = "";
    public string Url { get; set; } = "";
    public string Hash { get; set; } = "";

    /// <summary>数据里显式声明的落地方式（app JSON 的 downloads[].kind，可省）。</summary>
    public string DeclaredKind { get; set; } = "";

    /// <summary>这条链接的落地方式（判定见 <see cref="DownloadLinkKind"/>，与站点同源）。</summary>
    public DownloadKind Kind => DownloadLinkKind.Of(Url, DeclaredKind);

    /// <summary>
    /// 主按钮文案：能直接下载的才写「下载」，只能跳转的把去处写清楚。
    /// 与站点 文字设置.ts 的 detail.download / detail.open-page / detail.open-netdisk 一一对应。
    /// </summary>
    public string ActionText => Kind switch
    {
        DownloadKind.Netdisk => "前往网盘",
        DownloadKind.Page => "前往官网",
        _ => "下载",
    };

    /// <summary>
    /// 跳转项的一句说明（对应站点 detail.hint-page / detail.hint-netdisk）；
    /// 能直接下载的返回空串，界面上整行收起，不占位。
    /// </summary>
    public string KindHint => Kind switch
    {
        DownloadKind.Netdisk => "该链接为第三方网盘分享页，将跳转至网盘获取安装包",
        DownloadKind.Page => "该软件仅提供官方下载页，将跳转至官网获取安装包",
        _ => "",
    };

    public Microsoft.UI.Xaml.Visibility KindHintVisibility =>
        KindHint.Length > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>平台旁边的小字（备注 / 体积），都没填就返回空串。</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Note)) parts.Add(Note);
            if (!string.IsNullOrWhiteSpace(Size)) parts.Add(Size);
            return string.Join(" · ", parts);
        }
    }

    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);
    public bool HasHash => !string.IsNullOrWhiteSpace(Hash);

    /// <summary>校验值只显示首尾几位（完整值在按钮上点击复制）。</summary>
    public string HashShort => Hash.Length > 16 ? Hash[..8] + "…" + Hash[^6..] : Hash;

    private static readonly Dictionary<int, string> HashAlgorithms = new()
    {
        [32] = "MD5",
        [40] = "SHA-1",
        [56] = "SHA-224",
        [64] = "SHA-256",
        [96] = "SHA-384",
        [128] = "SHA-512",
    };

    /// <summary>按长度推断算法名（站点同款规则）。</summary>
    public string HashAlgorithmName =>
        HashAlgorithms.TryGetValue(Hash.Length, out var name) ? name : "校验值";

    /// <summary>校验值那一行按钮上的字：算法 + 首尾 + 复制。</summary>
    public string HashChipText => HasHash ? $"{HashAlgorithmName} {HashShort}   复制" : "";

    public Microsoft.UI.Xaml.Visibility HashVisibility =>
        HasHash ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>GitHub 直链才给「加速下载」入口（跳转类不给 —— 加速一个网页没有意义）。</summary>
    public Microsoft.UI.Xaml.Visibility MirrorVisibility =>
        Kind == DownloadKind.File && Core.GithubMirror.IsMirrorableUrl(Url)
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
}

/// <summary>一个软件（字段与站点 data/index.ts 的 PICK 白名单一致）。</summary>
public sealed class SoftwareApp : System.ComponentModel.INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Category { get; set; } = "";
    public string Tagline { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string Size { get; set; } = "";
    public string System { get; set; } = "";

    /// <summary>
    /// 适用的操作系统 key 列表（数据里 <c>platforms[]</c>，取值为 软件数据/platforms.json 的顶层 key：
    /// windows / macos / linux / android / ios / web）。
    ///
    /// ⚠️ 和 <see cref="System"/> 是两回事，别合并：System 是给人看的自由文字（「Windows XP+」「Android 8.0+」），
    /// platforms 是给筛选用的机器值。桌面版原来只读了 System，所以「按系统筛选」一直做不了
    /// （2026-10-24 对齐网页版 HomePage.vue 的 platformItems 才补上）。
    /// </summary>
    public List<string> Platforms { get; set; } = new();

    /// <summary>这条软件是否适配某个平台 key（空 key = 全部系统，恒真）。</summary>
    public bool SupportsPlatform(string key) => key.Length == 0 || Platforms.Contains(key);

    public string Website { get; set; } = "";
    public string Github { get; set; } = "";
    public string Notice { get; set; } = "";
    public string Store { get; set; } = "";
    public List<DownloadItem> Downloads { get; set; } = new();

    /// <summary>卡片副标题：有版本号显示版本，否则显示一句话简介。</summary>
    public string CardSubtitle => Version.Length > 0 ? "v" + Version : Tagline;

    /// <summary>
    /// 短简介（详情页右栏「推荐应用」用）：优先 Tagline，其次 Description，最后退分类名。
    /// ⚠️ 数据里 Description 可能很长，界面要自己限行数（MaxLines）。
    /// </summary>
    public string IntroText =>
        !string.IsNullOrWhiteSpace(Tagline) ? Tagline.Trim()
        : !string.IsNullOrWhiteSpace(Description) ? Description.Trim()
        : CategoryDisplay;

    /// <summary>分类显示名（载入内容后由 ContentStore 填，界面当徽标用）。</summary>
    public string CategoryDisplay { get; set; } = "";

    /// <summary>
    /// 卡片上的版本小字：数字开头才补个 v（数据里既有「3.5.2」也有「v0.9.0」「跟随官网」
    /// 「上次更新日期 2026/8/7」这类写法，不能无脑加前缀，否则会出「v上次更新日期」）。
    /// </summary>
    public string VersionText =>
        Version.Length == 0 ? "" : char.IsDigit(Version[0]) ? "v" + Version : Version;

    /// <summary>磁贴卡的一行小字：分类 · 版本 · 体积 · 系统（有哪个写哪个）。</summary>
    public string MetaText => Join(CategoryDisplay, VersionText, Size, System);

    /// <summary>紧凑卡的一行小字：版本 · 体积。</summary>
    public string CompactMeta => Join(VersionText, Size);

    private static string Join(params string[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));

    private Microsoft.UI.Xaml.Media.ImageSource? _icon;
    private bool _iconResolved;
    private bool _iconLoaded;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    /// <summary>
    /// 占位字形（购物袋）的显隐：**没图 / 正在下载 / 下载失败都显示**，只有图片真的加载成功才收掉。
    /// ⚠️ 必须由属性 + 通知驱动（而不是光靠 Image 元素事件）：GridView 会回收容器，
    /// 复用到一个"没有图标"的软件时，之前被藏掉的字形不会自己回来 → 卡片就空一块。
    /// </summary>
    public Microsoft.UI.Xaml.Visibility IconPlaceholder =>
        _iconLoaded ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>卡片图标。优先用本地缓存（file://，零网络、秒开）；没缓存才联网，并后台落盘供下次直接用。</summary>
    public Microsoft.UI.Xaml.Media.ImageSource? IconImage
    {
        get
        {
            if (_iconResolved) return _icon;
            _iconResolved = true;
            try
            {
                if (string.IsNullOrWhiteSpace(Icon)) return null;

                if (Icon.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    AppIconCache.TryGet(Icon, out var localHit, out var stale);   // 本机有没有这张图（含陈旧的）
                    var svg = IsSvg(Icon);

                    // ① 本机有且新鲜 → 直接加载，零网络、秒开。
                    //    SVG 陈旧也走这条：SvgImageSource 压根没有 Failed 事件，网络挂了收不到通知，
                    //    给它远程源等于没有退路，不如直接用本机那张，再让后台去换新的。
                    if (localHit is { } c && (!stale || svg))
                    {
                        _icon = c;
                        MarkLoadedWhenOpen(c);

                        // ⛔ 必须在这里就把占位收掉，不能只等 ImageOpened。
                        //    AppIconCache 的 _memo 复用**同一个 ImageSource 对象**：它可能早在别处
                        //    （列表页、或另一个软件用了同一张图）就已经 Opened 过，事件不会再触发，
                        //    结果就是图标明明画出来了、购物袋却一直盖在上面
                        //    （2026-10-24 Nick 报的「图标有加载但仍显示购物袋」，位置飘忽正是这个原因）。
                        //    本机文件不存在"网络失败"这一说，直接视为已加载是安全的。
                        _iconLoaded = true;

                        // ⚠️ 必须在这里就发通知：有的调用方是**先给占位符号求值、之后才取 IconImage**的
                        //    （首页推荐卡就是先建 monogram 再取 crisp），不发这一下它永远读不到已加载。
                        Raise(nameof(IconPlaceholder));

                        if (stale) _ = AppIconCache.EnsureCachedAsync(Icon);   // 后台换新，下次进来就已经是新的
                        return _icon;
                    }

                    // ② 陈旧 / 本机没有 → **网络优先**：先给远程源，拿到就是最新的；
                    //    网络超时或失败时自动退回本机旧图（见 MarkLoadedWhenOpen 的 fallback），
                    //    ⛔ 网络不通不该让卡片退化成一个占位字形 —— 那张旧图比空框有用得多。
                    var remote = MakeRemoteSource(Icon);
                    _icon = remote;
                    MarkLoadedWhenOpen(remote, fallback: localHit);
                    _ = AppIconCache.EnsureCachedAsync(Icon);
                    return _icon;
                }

                // 非 http（打包内资源等）→ 本地路径直接加载
                var local = MakeLocalSource(Icon);
                _icon = local;
                MarkLoadedWhenOpen(local);
                _iconLoaded = local is not null;   // 同上：本地源不等 Opened，直接收起占位
                if (_iconLoaded) Raise(nameof(IconPlaceholder));
                return _icon;
            }
            catch { /* 图标地址坏了就当没有 */ }
            return _icon;
        }
    }

    /// <summary>远程 http 源（和原来一致：SVG 走 SvgImageSource，其余限宽 128 解码）。</summary>
    private static Microsoft.UI.Xaml.Media.ImageSource MakeRemoteSource(string icon)
    {
        var uri = new Uri(icon);
        // SVG 图标（chrome 等）BitmapImage 解不了 → 用 SvgImageSource 解（矢量，缩放不糊）
        if (uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            return new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(uri);
        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
        // 卡片上图标最大也就 72px（详情页），按原图解码（动不动 256/512）纯属浪费：
        // 教学机 8G 内存，一页 50 多个图标就是几十 MB。这里限宽解码（等比，高度自动）。
        bmp.DecodePixelWidth = 128;
        return bmp;
    }

    /// <summary>本地路径源（file://，限宽 128 解码；SVG 走 SvgImageSource）。</summary>
    private static Microsoft.UI.Xaml.Media.ImageSource? MakeLocalSource(string icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        try
        {
            var uri = new Uri("file:///" + icon.Replace('\\', '/'));
            if (icon.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                return new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(uri);
            return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri) { DecodePixelWidth = 128 };
        }
        catch { return null; }
    }

    /// <summary>是不是 SVG（BitmapImage 解不了，必须走 SvgImageSource；后者没有 Failed 事件）。</summary>
    private static bool IsSvg(string icon)
    {
        try { return new Uri(icon).AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase); }
        catch { return icon.EndsWith(".svg", StringComparison.OrdinalIgnoreCase); }
    }

    /// <summary>
    /// 给源挂上「加载成功/失败」事件，驱动占位字形（购物袋）的显隐。
    /// </summary>
    /// <param name="fallback">
    /// 网络源加载不出来时**顶上去的本机旧图**（只在"本机还有一张"时才传）。
    /// 命中失败就把它换成当前图标并通知界面 —— 这是"网络超时用本地"这条容错指令的落点。
    /// ⚠️ 依赖绑定写成 <c>Mode=OneWay</c>：x:Bind 默认是一次性的 OneTime，那样 Raise 传不到界面。
    /// </param>
    private void MarkLoadedWhenOpen(Microsoft.UI.Xaml.Media.ImageSource? src,
                                   Microsoft.UI.Xaml.Media.ImageSource? fallback = null)
    {
        if (src is null) return;
        if (src is Microsoft.UI.Xaml.Media.Imaging.BitmapImage bmp)
        {
            bmp.ImageOpened += (_, _) => { _iconLoaded = true; Raise(nameof(IconPlaceholder)); };
            bmp.ImageFailed += (_, _) =>
            {
                if (fallback is { } old)
                {
                    _icon = old;                    // 网络这条路断了 ⇒ 换本机那张，再通知界面重取图标
                    _iconLoaded = true;
                    Raise(nameof(IconImage));
                    Raise(nameof(IconPlaceholder));
                }
                else
                {
                    _iconLoaded = false;            // 本机也没有 ⇒ 老实显示占位字形
                    Raise(nameof(IconPlaceholder));
                }
            };
        }
        else if (src is Microsoft.UI.Xaml.Media.Imaging.SvgImageSource svg)
        {
            // ⚠️ SvgImageSource 只有 Opened，没有 Failed 事件；解不出来就一直等着，
            // 占位字形自然留在那儿（正好是想的效果）。也因此它对 fallback 无感 —— 见 IconImage 分支 ①。
            svg.Opened += (_, _) => { _iconLoaded = true; Raise(nameof(IconPlaceholder)); };
        }
    }

    private Microsoft.UI.Xaml.Media.Brush? _iconBrush;
    private bool _iconBrushMade;

    /// <summary>
    /// 图标当画刷用（详情页顶栏、推荐列表的圆角小图标）。
    /// ⛔ 本项目的圆角 Border **不会裁剪子元素**，所以只能用 Background 画，
    /// 不能把 Image 放进去指望圆角生效（详情页大图标一直是这么做的）。
    /// </summary>
    public Microsoft.UI.Xaml.Media.Brush? IconBrush
    {
        get
        {
            if (_iconBrushMade) return _iconBrush;
            _iconBrushMade = true;
            try
            {
                if (IconImage is { } image)
                {
                    _iconBrush = new Microsoft.UI.Xaml.Media.ImageBrush
                    {
                        ImageSource = image,
                        Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                        AlignmentX = Microsoft.UI.Xaml.Media.AlignmentX.Center,
                        AlignmentY = Microsoft.UI.Xaml.Media.AlignmentY.Center,
                    };
                }
            }
            catch { /* 取不到就当没有，界面用占位字形兜底 */ }
            return _iconBrush;
        }
    }

    // 搜索用：名称 + 简介 + id 都参与匹配
    public bool Matches(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return true;
        var k = keyword.Trim();
        return Has(Name, k) || Has(Tagline, k) || Has(Id, k) || Has(Description, k);
    }

    private static bool Has(string haystack, string needle) =>
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}

/// <summary>分类（对应 软件数据/categories.json）。</summary>
public sealed class DownloadCategory
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    /// <summary>界面用：分类显示名 + 该类软件数量。</summary>
    public string Display { get; set; } = "";
}

/// <summary>
/// 一个操作系统平台（对应 软件数据/platforms.json 的顶层项）。
/// 只取 key / name 两级：versions 里那些细分版本（XP / 7 / 10 / 11…）
/// 现在只有提交页的「系统版本」多选会用到，列表筛选不展开（与网页版下拉一致）。
/// </summary>
public sealed class DownloadPlatform
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>数据加载问题（首屏红条用；逻辑与网页端 dataLoadIssues 一致）。</summary>
public sealed class DataIssue
{
    public string File { get; set; } = "";
    public string Message { get; set; } = "";
}
