namespace ClassSoftwareHub.Desktop.Core;


public static class ShellConfig
{
    
    
    
    
    public const string SiteUrl = "https://classsoftwarehub.us.ci";

    public const string AppName = "ClassSoftwareHub";
    public const string WindowTitle = "ClassSoftwareHub";

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    public const string ShellVersion = "1.2.3-insider1.1";

    
    public static bool IsInsider =>
        ShellVersion.Contains("insider", System.StringComparison.OrdinalIgnoreCase);

    
    
    
    
    
    
    
    
    
    
    
    
    
    public static string DisplayVersion => ShellVersion;

    
    public const string VersionPrefix = "dv";

    
    
    
    
    
    
    
    
    
    

    
    public const string UpdateRepoOwner = "c1201y";

    
    public const string UpdateRepoName = "ClassSoftwareHub-Desktop";

    
    public const string UpdateToken = "";

    
    
    
    
    public static string DefaultUpdateChannel => IsInsider ? "insider" : "stable";

    
    public const string SiteVersionTarget = "v2.3.4";

    
    
    
    
    
    
    
    
    
    
    
    
    
    public const string SiteVersionDisplay = "v2.3.4 - Tangram (20260927PR01)";

    public const string WebView2DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    public const string NetworkErrorMessage = "网络出现错误，请稍后重试";

    
    public const int TitleBarHeight = 48;

    
    public const int LoadTimeoutSeconds = 25;

    
    public const string MutexName = "ClassSoftwareHub.Desktop.SingleInstance";

    
    
    
    
    
    
    
    
    
    public const string ActivateEventName = "ClassSoftwareHub.Desktop.ActivateMainWindow";

    
    
    

    
    
    
    
    
    
    
    public const string SiteRepoOwner = "c1201y";
    public const string SiteRepoName = "ClassSoftwareHub";
    public const string SiteRepoBranch = "main";

    
    public const string SiteRepoDataDir = "软件数据";

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    public const string DocsRepoOwner = "Tiny-Nick";
    public const string DocsRepoName = "ClassSoftwareHub-Docs";
    public const string DocsRepoBranch = "main";

    
    
    
    
    
    
    
    
    
    
    
    
    
    public const string HubAiToolPackPath = "AI工具/toolpack.json";

    
    
    
    
    public static string HubAiToolPackUrl
    {
        get
        {
            var segments = HubAiToolPackPath.Split('/');
            var encoded = new string[segments.Length];
            for (var i = 0; i < segments.Length; i++)
                encoded[i] = Uri.EscapeDataString(segments[i]);

            return "https://raw.githubusercontent.com/" +
                   $"{SiteRepoOwner}/{SiteRepoName}/{SiteRepoBranch}/" +
                   string.Join('/', encoded);
        }
    }

    
    
    

    
    
    
    
    
    
    
    
    
    
    public static readonly string[] SubmitEndpoints = ServiceEndpoints.All;

    
    public const string SubmitEndpointFile = "submit-endpoint.txt";

    
    public const int SubmitTimeoutMs = 10000;

    
    public static System.Collections.Generic.List<string> OrderedSubmitEndpoints()
    {
        var list = new System.Collections.Generic.List<string>();
        try
        {
            var file = System.IO.Path.Combine(AppPaths.DataDir, SubmitEndpointFile);
            if (System.IO.File.Exists(file))
            {
                var remembered = System.IO.File.ReadAllText(file).Trim();
                if (System.Array.IndexOf(SubmitEndpoints, remembered) >= 0) list.Add(remembered);
            }
        }
        catch {  }

        foreach (var endpoint in SubmitEndpoints)
            if (!list.Contains(endpoint)) list.Add(endpoint);

        return list;
    }

    
    public static void RememberSubmitEndpoint(string baseUrl)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppPaths.DataDir);
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(AppPaths.DataDir, SubmitEndpointFile), baseUrl);
        }
        catch {  }
    }

    
    
    
    
    public const string ContentManifestUrl = "https://classsoftwarehub.us.ci/content/manifest.json";

    
    public const string ContentManifestUrlFallback = "https://pan.132614.xyz/dav/%E7%BD%91%E7%AB%99/content/manifest.json";

    
    
    
    
    public static string CachedContentDir => System.IO.Path.Combine(AppPaths.DataDir, "content");

    
    
    
    
    public const string DevContentDir =
        @"C:\Users\Programmer_Nick\OneDrive\文档\Visual Studio 18 项目文件\ClassSoftwareHub\dist\content";

    
    public const string SubmitPageUrl = "https://classsoftwarehub.us.ci/#/submit";
}


public static class AppPaths
{
    public static string DataDir { get; } = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "ClassSoftwareHub");
}
