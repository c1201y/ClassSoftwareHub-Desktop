using System;
using System.Globalization;
using System.IO;
using System.Text;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 第二实例（例如右键菜单又启动一次）把「要打开什么」交给正在运行的实例。
/// 走一个临时文件传一次，30 秒内有效，与 <see cref="AppLinks"/> 的链接队列同一个套路。
/// </summary>
public static class ShellTargetQueue
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private static string QueueFile => Path.Combine(AppPaths.DataDir, "pending-shell.txt");

    public static void Write(string kind, string path)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);

            var text = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
                       + Environment.NewLine + kind
                       + Environment.NewLine + path;

            var tmp = QueueFile + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            File.Move(tmp, QueueFile, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("shell", "转交右键目标失败：" + ex.Message);
        }
    }

    public static (string Kind, string Path)? TryTake()
    {
        try
        {
            if (!File.Exists(QueueFile)) return null;

            string text;
            try
            {
                text = File.ReadAllText(QueueFile, Encoding.UTF8);
            }
            catch (IOException)
            {
                return null;
            }

            File.Delete(QueueFile);

            var lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length < 3) return null;

            if (!long.TryParse(lines[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stamp))
                return null;

            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(stamp);
            if (age < TimeSpan.Zero || age > MaxAge) return null;

            var kind = lines[1].Trim();
            var path = lines[2].Trim();

            return kind.Length > 0 && path.Length > 0 ? (kind, path) : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("shell", "读取待处理右键目标失败：" + ex.Message);
            return null;
        }
    }
}
