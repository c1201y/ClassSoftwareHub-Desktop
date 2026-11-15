// 仅为了让 PackageRunner.cs 能脱离 WinUI 编译的最小替身 —— 本文件不属于产品。
using System;

namespace ClassSoftwareHub.Desktop.Core
{
    public static class AppLog
    {
        public static void Info(string module, string message) => Console.WriteLine($"  [log:{module}] {message}");
        public static void Done(string module, string message) => Console.WriteLine($"  [log:{module}] {message}");
        public static void Warn(string module, string message) => Console.WriteLine($"  [log:{module}] {message}");
        public static void Fail(string module, Exception ex, string what) => Console.WriteLine($"  [log:{module}] {what} · {ex.Message}");
        public static void Exception(string module, Exception ex, string what) => Console.WriteLine($"  [log:{module}] {what} · {ex.GetType().Name}: {ex.Message}");
    }
}

namespace ClassSoftwareHub.Desktop
{
    public sealed class SettingsStub
    {
        public bool AutoDeletePackage { get; set; }
        public bool AutoOpenExtractFolder { get; set; } = true;
    }

    public sealed class SettingsHost
    {
        public SettingsStub Current { get; } = new();
        public void Save() { }
    }

    public sealed class MainWindowStub
    {
        public int RevealCount;
        public string LastRevealed = "";

        public void RevealFile(string path)
        {
            RevealCount++;
            LastRevealed = path;
            Console.WriteLine($"  [shell] 打开目录 {path}");
        }
    }

    public static class App
    {
        public static SettingsHost Settings { get; } = new();
        public static MainWindowStub? MainWindow { get; set; }
    }
}

namespace ClassSoftwareHub.Desktop.Services
{
    public static class DownloadService
    {
        public static string DefaultDir => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>产品里这个类型在 DownloadManager.cs（依赖 Microsoft.UI.Xaml），这里只放 RunAsync 用得到的成员。</summary>
    public sealed class DownloadTask
    {
        public string? Path { get; init; }
        public PackageAction Action { get; init; }
        public string Title { get; init; } = "样本";
        public string? ExtractedDir { get; set; }
        public string WorkText { get; set; } = "";

        public void SetWork(bool working, string text)
        {
            WorkText = text;
            Console.WriteLine($"  [卡片] working={working} · {text}");
        }
    }
}
