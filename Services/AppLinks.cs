using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 应用链接（<c>csh://</c>）的落地：注册协议、从命令行取链接、在实例之间传递链接。
///
/// <para>
/// 需求 1:18。三件事各管一段：
/// <list type="number">
///   <item><see cref="RegisterProtocol"/> —— 往 HKCU 写 <c>csh</c> 协议，之后浏览器 / 文档里点
///         <c>csh://settings/update</c> 就能唤起本应用。<b>只写当前用户</b>，不需要 UAC，
///         也不需要管理员；卸载时那条键会跟着安装目录一起被用户删掉（见下方注意事项）。</item>
///   <item><see cref="ExtractFromCommandLine"/> —— 从启动参数里捞出链接（Windows 唤起协议时
///         把链接原样作为参数传进来）。</item>
///   <item><see cref="Queue"/> / <see cref="TryTakeQueued"/> —— 应用已经开着的时候又点了一个链接：
///         第二个进程把链接**写进文件**、敲一下叫醒事件就退出，第一个进程醒来读走并执行。</item>
/// </list>
/// </para>
///
/// <para>
/// ⚠️ 为什么用文件而不是命名管道：这套传递只需要"一次一个、几十字节"，而管道要处理连接失败、
///    对方已退出、异步读写三种异常路径。文件方案只有一个坑（写的时候对方正在读），加个重试就够。
///    真正从根上保证正确性的是<b>时间戳</b>：只有 30 秒内的链接才会被执行，旧文件一律丢弃。
/// </para>
/// </summary>
public static class AppLinks
{
    /// <summary>HKCU 下的协议键。</summary>
    private const string ProtocolKeyPath = @"Software\Classes\" + AppUri.Scheme;

    /// <summary>第二个实例留给第一个实例的"信箱"。</summary>
    private static string QueueFile => Path.Combine(AppPaths.DataDir, "pending-link.txt");

    /// <summary>超过这个时长的待处理链接直接丢弃（防止上次崩溃留的旧文件在下次开机时被执行）。</summary>
    private static readonly TimeSpan QueueMaxAge = TimeSpan.FromSeconds(30);

    // ── 协议注册 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 确保 <c>csh</c> 协议指向**当前这个 exe**。已经指对了就什么都不写（每次启动都会调一次）。
    ///
    /// <para>
    /// ⚠️ 会覆盖之前注册的路径：Debug 版跑过之后，链接就指向 <c>bin\Debug\…</c> 那个 exe，
    ///    直到正式版再跑一次才改回来。这是刻意接受的 —— 反过来（只在正式安装路径下注册）
    ///    会让开发期完全测不到这条链路。
    /// </para>
    /// <para>
    /// ⚠️ 写入范围只有 <c>HKCU\Software\Classes</c>：不需要管理员、不改系统全局设置，
    ///    删掉这一个键就彻底撤销。
    /// </para>
    /// </summary>
    public static bool RegisterProtocol()
    {
        try
        {
            var exe = ExecutablePath();
            if (string.IsNullOrEmpty(exe)) return false;

            var command = $"\"{exe}\" \"%1\"";

            using var key = Registry.CurrentUser.CreateSubKey(ProtocolKeyPath, writable: true);
            if (key is null) return false;

            // 已经指对了就别再写：每次启动都改注册表会让安全软件频繁报警
            if (key.GetValue(null) is string existing && existing.Equals(command, StringComparison.OrdinalIgnoreCase))
            {
                using var icon = key.OpenSubKey("DefaultIcon", writable: false);
                if (icon?.GetValue(null) is string iconValue && iconValue.Equals($"\"{exe}\",0", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            key.SetValue(null, $"URL:{ShellConfig.AppName} Link");
            key.SetValue("URL Protocol", "");

            // 图标：用 exe 自己的图标，资源管理器与浏览器提示里都能看到熟悉的图标
            using (var icon = key.CreateSubKey("DefaultIcon", writable: true))
                icon?.SetValue(null, $"\"{exe}\",0");

            using (var open = key.CreateSubKey(@"shell\open\command", writable: true))
                open?.SetValue(null, command);

            AppLog.Step("links", $"已注册 {AppUri.Scheme}:// 协议 → {exe}");
            return true;
        }
        catch (Exception ex)
        {
            // 注册失败不影响应用本身：只是从外部点链接唤起这条路走不通
            AppLog.Warn("links", "注册协议失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>撤销协议注册（留给将来"设置 → 取消关联"用；当前没有入口调用）。</summary>
    public static bool UnregisterProtocol()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(ProtocolKeyPath, throwOnMissingSubKey: false);
            AppLog.Step("links", $"已撤销 {AppUri.Scheme}:// 协议注册");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("links", "撤销协议注册失败：" + ex.Message);
            return false;
        }
    }

    // ── 命令行 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 从启动参数里取链接。Windows 唤起协议时会把链接作为独立参数传进来
    /// （形如 <c>"C:\…\ClassSoftwareHub.exe" "csh://settings/update"</c>）。
    /// </summary>
    public static string? ExtractFromCommandLine()
    {
        try
        {
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                var text = (arg ?? "").Trim().Trim('"');
                if (text.Length == 0) continue;

                // 只认本应用的链接：ms-settings: 之类由应用**主动**发起，不从命令行接
                if (text.StartsWith(AppUri.Scheme + ":", StringComparison.OrdinalIgnoreCase)) return text;
            }
        }
        catch
        {
            // 命令行读不到就当没给（跟 ShellPage 的两处参数解析同一个处理）
        }

        return null;
    }

    // ── 实例之间的传递 ───────────────────────────────────────────────────

    /// <summary>
    /// 把链接留给已经在跑的实例（本进程随后会被退出）。
    /// <para>写失败不是致命问题：退化成"只是把主窗口叫出来"，不会丢窗口。</para>
    /// </summary>
    public static bool Queue(string uri)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);

            // 第一行时间戳、第二行链接。读的时候按时间戳判过期。
            var text = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
                       + Environment.NewLine + uri;

            // 对方可能正在读 → 用临时文件 + 替换，读的进程要么看到旧的、要么看到完整新的
            var tmp = QueueFile + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            File.Move(tmp, QueueFile, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("links", "传递链接失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 取走待处理的链接（取到就删文件）。没有、已过期、内容坏了都返回 <c>null</c>。
    /// </summary>
    public static string? TryTakeQueued()
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
                // 对方正在写：这一拍读不到就算了，下一次叫醒还会来
                return null;
            }

            File.Delete(QueueFile);

            var lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length < 2) return null;

            if (!long.TryParse(lines[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stamp))
                return null;

            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(stamp);
            if (age < TimeSpan.Zero || age > QueueMaxAge)
            {
                AppLog.Info("links", $"丢弃过期待处理链接（{age.TotalSeconds:0} 秒前留下）");
                return null;
            }

            var uri = string.Join("\n", lines, 1, lines.Length - 1).Trim();
            return uri.Length > 0 ? uri : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("links", "读取待处理链接失败：" + ex.Message);
            return null;
        }
    }

    // ── 小工具 ───────────────────────────────────────────────────────────

    /// <summary>当前进程的可执行文件完整路径。</summary>
    private static string? ExecutablePath()
    {
        try
        {
            // Environment.ProcessPath 在单文件发布下同样正确；取不到再退回主模块
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path)) return path;

            using var proc = Process.GetCurrentProcess();
            return proc.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}
