using System;
using System.Collections.Generic;
using System.Linq;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Data;


public sealed class DownloadItem
{
    public string Platform { get; set; } = "";
    public string Note { get; set; } = "";
    public string Size { get; set; } = "";
    public string Url { get; set; } = "";
    public string Hash { get; set; } = "";

    
    public string DeclaredKind { get; set; } = "";

    
    public DownloadKind Kind => DownloadLinkKind.Of(Url, DeclaredKind);

    
    
    
    
    public string ActionText => Kind switch
    {
        DownloadKind.Netdisk => "前往网盘",
        DownloadKind.Page => "前往官网",
        _ => "下载",
    };

    
    
    
    
    public string KindHint => Kind switch
    {
        DownloadKind.Netdisk => "该链接为第三方网盘分享页，将跳转至网盘获取安装包",
        DownloadKind.Page => "该软件仅提供官方下载页，将跳转至官网获取安装包",
        _ => "",
    };

    public Microsoft.UI.Xaml.Visibility KindHintVisibility =>
        KindHint.Length > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    
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

    
    public string HashAlgorithmName =>
        HashAlgorithms.TryGetValue(Hash.Length, out var name) ? name : "校验值";

    
    public string HashChipText => HasHash ? $"{HashAlgorithmName} {HashShort}   复制" : "";

    public Microsoft.UI.Xaml.Visibility HashVisibility =>
        HasHash ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    
    public Microsoft.UI.Xaml.Visibility MirrorVisibility =>
        Kind == DownloadKind.File && Core.GithubMirror.IsMirrorableUrl(Url)
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    
    
    
    
    public Microsoft.UI.Xaml.Visibility FileActionVisibility =>
        Kind == DownloadKind.File ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    
    public Microsoft.UI.Xaml.Visibility JumpActionVisibility =>
        Kind == DownloadKind.File ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
}


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

    
    
    
    
    
    
    
    
    public List<string> Platforms { get; set; } = new();

    
    public bool SupportsPlatform(string key) => key.Length == 0 || Platforms.Contains(key);

    public string Website { get; set; } = "";
    public string Github { get; set; } = "";

    
    
    
    
    public string Author { get; set; } = "";

    
    
    
    
    public string License { get; set; } = "";

    public string Notice { get; set; } = "";
    public string Store { get; set; } = "";
    public List<DownloadItem> Downloads { get; set; } = new();

    
    
    
    
    
    
    
    
    
    public bool HasOssDownload =>
        Downloads.Any(d => d.Url.StartsWith("oss://", StringComparison.OrdinalIgnoreCase));

    
    public string CardSubtitle => Version.Length > 0 ? "v" + Version : Tagline;

    
    
    
    
    public string IntroText =>
        !string.IsNullOrWhiteSpace(Tagline) ? Tagline.Trim()
        : !string.IsNullOrWhiteSpace(Description) ? Description.Trim()
        : CategoryDisplay;

    
    public string CategoryDisplay { get; set; } = "";

    
    
    
    
    public string VersionText =>
        Version.Length == 0 ? "" : char.IsDigit(Version[0]) ? "v" + Version : Version;

    
    public string MetaText => Join(CategoryDisplay, VersionText, Size, System);

    
    public string CompactMeta => Join(VersionText, Size);

    private static string Join(params string[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));

    private Microsoft.UI.Xaml.Media.ImageSource? _icon;
    private bool _iconResolved;
    private bool _iconLoaded;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    
    
    
    
    
    public Microsoft.UI.Xaml.Visibility IconPlaceholder =>
        _iconLoaded ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    
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
                    AppIconCache.TryGet(Icon, out var localHit, out var stale);   
                    var svg = IsSvg(Icon);

                    
                    
                    
                    if (localHit is { } c && (!stale || svg))
                    {
                        _icon = c;
                        MarkLoadedWhenOpen(c);

                        
                        
                        
                        
                        
                        
                        _iconLoaded = true;

                        
                        
                        Raise(nameof(IconPlaceholder));

                        if (stale) _ = AppIconCache.EnsureCachedAsync(Icon);   
                        return _icon;
                    }

                    
                    
                    
                    var remote = MakeRemoteSource(Icon);
                    _icon = remote;
                    MarkLoadedWhenOpen(remote, fallback: localHit);
                    _ = AppIconCache.EnsureCachedAsync(Icon);
                    return _icon;
                }

                
                var local = MakeLocalSource(Icon);
                _icon = local;
                MarkLoadedWhenOpen(local);
                _iconLoaded = local is not null;   
                if (_iconLoaded) Raise(nameof(IconPlaceholder));
                return _icon;
            }
            catch {  }
            return _icon;
        }
    }

    
    private static Microsoft.UI.Xaml.Media.ImageSource MakeRemoteSource(string icon)
    {
        var uri = new Uri(icon);
        
        if (uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            return new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(uri);
        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
        
        
        bmp.DecodePixelWidth = 128;
        return bmp;
    }

    
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

    
    private static bool IsSvg(string icon)
    {
        try { return new Uri(icon).AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase); }
        catch { return icon.EndsWith(".svg", StringComparison.OrdinalIgnoreCase); }
    }

    
    
    
    
    
    
    
    
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
                    _icon = old;                    
                    _iconLoaded = true;
                    Raise(nameof(IconImage));
                    Raise(nameof(IconPlaceholder));
                }
                else
                {
                    _iconLoaded = false;            
                    Raise(nameof(IconPlaceholder));
                }
            };
        }
        else if (src is Microsoft.UI.Xaml.Media.Imaging.SvgImageSource svg)
        {
            
            
            svg.Opened += (_, _) => { _iconLoaded = true; Raise(nameof(IconPlaceholder)); };
        }
    }

    private Microsoft.UI.Xaml.Media.Brush? _iconBrush;
    private bool _iconBrushMade;

    
    
    
    
    
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
            catch {  }
            return _iconBrush;
        }
    }

    
    public bool Matches(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return true;
        var k = keyword.Trim();
        return Has(Name, k) || Has(Tagline, k) || Has(Id, k) || Has(Description, k);
    }

    private static bool Has(string haystack, string needle) =>
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}


public sealed class DownloadCategory
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    
    public string Display { get; set; } = "";
}






public sealed class DownloadPlatform
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
}


public sealed class DataIssue
{
    public string File { get; set; } = "";
    public string Message { get; set; } = "";
}
