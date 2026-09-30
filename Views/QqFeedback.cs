using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 反馈中心「在 Q 群中反馈」这一路的两个窗口（提醒悬浮窗 + 图文流程窗）共用的小东西：
/// 目标群地址、剪贴板写入。
///
/// ⚠️ 群地址必须与首页 / 设置页快捷入口的「加入Q群」**同源**（同一个 <c>text/ui.json</c> 的 key，
///    见 <see cref="Core.QuickLinks"/>）—— 站点换了群链接，这两处一起变。
///    别再各写一份字面量，否则会出现"首页点进去是新群、反馈页点进去是旧群"。
/// </summary>
internal static class QqFeedback
{
    /// <summary>加入 QQ 群的群卡片页（qm.qq.com），交给系统浏览器 / QQ 打开。</summary>
    public static string GroupUrl =>
        App.Content.Ui.T("about.qq-group-url", "https://qm.qq.com/q/wByO7XG8Wk");

    /// <summary>
    /// 把反馈信息写进剪贴板。成功返回 true。
    /// ⚠️ 失败**不抛**，由调用方决定怎么提示 —— 剪贴板被别的进程占着是常事（历史上真踩过）。
    /// </summary>
    public static bool Copy(string text)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            return true;
        }
        catch (System.Exception ex)
        {
            ScreenCapture.Log("反馈信息复制失败: " + ex.Message);
            return false;
        }
    }
}
