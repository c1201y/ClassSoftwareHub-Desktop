using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Core;


public sealed class QuickLink : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Glyph { get; set; } = "";

    
    public string Url { get; set; } = "";

    
    
    
    
    
    public string Tag { get; set; } = "";

    
    public string? IconSvg { get; set; }

    
    public ImageSource? IconSource
    {
        get => _iconSource;
        set { _iconSource = value; OnChanged(nameof(IconSource)); }
    }
    private ImageSource? _iconSource;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}


public static class QuickLinks
{
    public static List<QuickLink> Build(Data.UiText ui)
    {
        
        var repoUrl = $"https://github.com/{ShellConfig.UpdateRepoOwner}/{ShellConfig.UpdateRepoName}";
        if (ShellConfig.UpdateRepoOwner.Length == 0 || ShellConfig.UpdateRepoName.Length == 0)
            repoUrl = ui.T("about.repository-url", "https://github.com/c1201y/ClassSoftwareHub-Desktop");

        return new List<QuickLink>
        {
            new() { Name = "项目仓库", Glyph = "\uE943", IconSvg = "GITHUB.svg", Url = repoUrl },
            new()
            {
                Name = "作者主页", Glyph = "\uE77B", IconSvg = "主页.svg",
                Url = ui.T("about.author-home-url", "https://space.bilibili.com/1274920807"),
            },
            
            
            
            new()
            {
                Name = "赞助作者", Glyph = "\uEB51",
                Url = ui.T("about.reward-url", "https://ifdian.net/a/TinyNickCSHub"),
            },
            new()
            {
                Name = "加入Q群", Glyph = "\uE8BD", IconSvg = "QQ.svg",
                Url = ui.T("about.qq-group-url", "https://qm.qq.com/q/wByO7XG8Wk"),
            },
            
            
            new() { Name = "更新日志", Glyph = "\uE72C", Tag = "changelog" },
        };
    }
}
