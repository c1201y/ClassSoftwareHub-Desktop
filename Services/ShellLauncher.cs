using System;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 右键菜单 / 第二实例唤醒的实际动作：一律开独立窗口，不经过主界面。
/// 主窗口此时可能正处于托盘隐藏状态，所以这里不碰主窗口的任何成员。
/// </summary>
public static class ShellLauncher
{
    public static void Open(string kind, string path)
    {
        try
        {
            switch (kind)
            {
                case "md":
                    Views.Markdown.MarkdownWindow.Show(path);
                    break;

                case "qr":
                    Views.Share.QrShareWindow.Show(path);
                    break;

                case "color":
                    Views.ImageColorWindow.Show(path);
                    break;

                case "hash":
                    Views.HashCheckWindow.Show(path);
                    break;

                default:
                    AppLog.Warn("shell", "认不出的右键目标类型：" + kind);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("shell", "打开右键目标失败：" + ex.Message);
        }
    }
}
