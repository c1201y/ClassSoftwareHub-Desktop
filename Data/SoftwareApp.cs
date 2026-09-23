using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Data;

/// <summary>一条下载项（对应 app JSON 里 downloads[] 的元素）。</summary>
public sealed class DownloadItem
{
    public string Platform { get; set; } = "";
    public string Note { get; set; } = "";
    public string Size { get; set; } = "";
    public string Url { get; set; } = "";
    public string Hash { get; set; } = "";

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

    /// <summary>GitHub 链接才给「加速下载」入口。</summary>
    public Microsoft.UI.Xaml.Visibility MirrorVisibility =>
        Core.GithubMirror.IsMirrorableUrl(Url)
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
    public string Website { get; set; } = "";
    public string Github { get; set; } = "";
    public string Notice { get; set; } = "";
    public string Store { get; set; } = "";
    public List<DownloadItem> Downloads { get; set; } = new();

    /// <summary>卡片副标题：有版本号显示版本，否则显示一句话简介。</summary>
    public string CardSubtitle => Version.Length > 0 ? "v" + Version : Tagline;

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

    /// <summary>卡片图标（http 地址就下载显示；空的话界面用占位字形兜底）。</summary>
    public Microsoft.UI.Xaml.Media.ImageSource? IconImage
    {
        get
        {
            if (_iconResolved) return _icon;
            _iconResolved = true;
            try
            {
                if (Icon.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    var uri = new Uri(Icon);
                    // SVG 图标（chrome 等）BitmapImage 解不了 → 用 SvgImageSource 解（矢量，缩放不糊）
                    if (uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                    {
                        // ⚠️ SvgImageSource 只有 Opened，没有 Failed 事件；解不出来就一直等着，
                        // 占位字形自然留在那儿（正好是想的效果）
                        var svg = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(uri);
                        svg.Opened += (_, _) => { _iconLoaded = true; Raise(nameof(IconPlaceholder)); };
                        _icon = svg;
                    }
                    else
                    {
                        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
                        bmp.ImageOpened += (_, _) => { _iconLoaded = true; Raise(nameof(IconPlaceholder)); };
                        bmp.ImageFailed += (_, _) => { _iconLoaded = false; Raise(nameof(IconPlaceholder)); };
                        _icon = bmp;
                    }
                }
            }
            catch { /* 图标地址坏了就当没有 */ }
            return _icon;
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

/// <summary>数据加载问题（首屏红条用；逻辑与网页端 dataLoadIssues 一致）。</summary>
public sealed class DataIssue
{
    public string File { get; set; } = "";
    public string Message { get; set; } = "";
}
