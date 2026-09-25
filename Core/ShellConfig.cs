namespace ClassSoftwareHub.Desktop.Core;

/// <summary>外壳常量集中处（改这里就行）。</summary>
public static class ShellConfig
{
    /// <summary>
    /// 站点地址 —— ⚠️ 当前是「测试版」：直接写死本机 dev server。
    /// 正式发版时**只改这一行**，换成 "https://classsoftwarehub.us.ci" 就行（不要再引入开关）。
    /// </summary>
    public const string SiteUrl = "https://classsoftwarehub.us.ci";

    public const string AppName = "ClassSoftwareHub";
    public const string WindowTitle = "ClassSoftwareHub";

    /// <summary>
    /// 原生外壳版本（跟站点版本无关）。当前 **正式版 1.0.0**。
    /// ⚠️ 规则（Nick 指定）：基数不随便抬（否则旧包会被强制顶掉）。以后要出 beta 就在后面接 `-insiderN`（如 `1.0.1-insider1`）。
    /// </summary>
    public const string ShellVersion = "1.0.0";

    /// <summary>桌面版的版本号前缀（Nick 指定：dv）。</summary>
    public const string VersionPrefix = "dv";

    // ════════════════════════════════════════════════════════════════
    // 更新（双通道：正式版 / 预览版）= GitHub Releases（公开仓库，匿名读，不用令牌）
    //   · 正式版（stable）→ 只看【非预发布】的 Release
    //   · 预览版（insider）→ 预发布 Release ＋ 非预发布（预览用户也能跟上正式版）
    //
    // 发版时按这套约定起名，才能被自动识别（别乱起）：
    //   tag  ：正式版 `dv1.0.0`      预发布 `dv1.0.0-insider1.4`（发布时勾 Pre-release）
    //   资产 ：`ClassSoftwareHub-Setup-<tag>.exe`（名字带 setup 才认）＋ 同名 `.md5`
    //   正文 ：会原样显示在更新对话框里 → 写本次更新内容
    // ════════════════════════════════════════════════════════════════

    /// <summary>更新仓库 owner（发布 Release 的那个仓库）。</summary>
    public const string UpdateRepoOwner = "c1201y";

    /// <summary>更新仓库名。正式版 = Release 的 Latest；预览版 = Pre-release。</summary>
    public const string UpdateRepoName = "ClassSoftwareHub-Desktop";

    /// <summary>可选：GitHub 令牌（留空走匿名，60 次/小时，够用）。⚠️ 永远不要写死在公开仓库里。</summary>
    public const string UpdateToken = "";

    /// <summary>
    /// 本版本默认走哪条通道：版本号里带 insider 的内部构建默认预览版，否则正式版。
    /// 用户可在设置页改；改过之后按 settings.json 里存的值来。
    /// </summary>
    public static string DefaultUpdateChannel =>
        ShellVersion.Contains("insider", System.StringComparison.OrdinalIgnoreCase) ? "insider" : "stable";

    /// <summary>与站点 v2.3.3 对齐的适配版本号（内容包里读不到 app.version 时的兜底）。</summary>
    public const string SiteVersionTarget = "v2.3.3";

    public const string WebView2DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    public const string NetworkErrorMessage = "网络出现错误，请稍后重试";

    /// <summary>标题栏高度（与 AppWindow PreferredHeightOption.Tall 对齐）。</summary>
    public const int TitleBarHeight = 48;

    /// <summary>导航超时（秒），超时视为网络错误。</summary>
    public const int LoadTimeoutSeconds = 25;

    /// <summary>单实例互斥名。</summary>
    public const string MutexName = "ClassSoftwareHub.Desktop.SingleInstance";

    // ════════════════════════════════════════════════════════════════
    // 内容（软件数据）—— 原生界面用，跟站点 dist/content/ 那套对应
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// **首选来源**：软件数据直接在站点仓库里，从 GitHub 读就是最新最全的（仓库是公开的，不用令牌）。
    /// 站点的 content/manifest.json 一直没发布，所以这里才是主力，manifest / 自带内容包是备用。
    /// </summary>
    public const string SiteRepoOwner = "c1201y";
    public const string SiteRepoName = "ClassSoftwareHub";
    public const string SiteRepoBranch = "main";

    /// <summary>仓库里软件数据所在目录（子目录 apps/ 一个软件一个 json，根上还有 categories.json）。</summary>
    public const string SiteRepoDataDir = "软件数据";

    /// <summary>
    /// 正式来源：站点上的内容清单（route 2 的产物，跟站点一起发布）。
    /// 桌面版只轮询这一个文件，按 sha256 增量拉变化的文件。
    /// </summary>
    public const string ContentManifestUrl = "https://classsoftwarehub.us.ci/content/manifest.json";

    /// <summary>备用来源（主站挂了可以从网盘拿）。</summary>
    public const string ContentManifestUrlFallback = "https://pan.132614.xyz/dav/%E7%BD%91%E7%AB%99/content/manifest.json";

    /// <summary>增量同步下来的内容缓存目录。</summary>
    public static string CachedContentDir => System.IO.Path.Combine(AppPaths.DataDir, "content");

    /// <summary>
    /// 安装包里自带的内容（安装目录\content，打包装机时塞进去的那份）。
    /// 装机就有软件清单，离线也不空；联网后 ContentUpdater 拉到新版会覆盖优先级更高的缓存。
    /// </summary>
    public static string BundledContentDir => System.IO.Path.Combine(AppContext.BaseDirectory, "content");

    /// <summary>
    /// 开发用：直接读站点工程的内容包（跑过 scripts/build-content.mjs 就有）。
    /// 只有缓存目录里没数据时才会用到它 —— 正式用户机器上这个路径不存在，自动跳过。
    /// </summary>
    public const string DevContentDir =
        @"C:\Users\Programmer_Nick\OneDrive\文档\Visual Studio 18 项目文件\ClassSoftwareHub\dist\content";

    /// <summary>提交软件页 —— 全站唯一走网页（WebView2）的页面。</summary>
    public const string SubmitPageUrl = "https://classsoftwarehub.us.ci/#/submit";
}

/// <summary>应用数据目录（设置、缓存、内容）。</summary>
public static class AppPaths
{
    public static string DataDir { get; } = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "ClassSoftwareHub");
}
