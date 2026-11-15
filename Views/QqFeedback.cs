using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views;









internal static class QqFeedback
{
    
    public static string GroupUrl =>
        App.Content.Ui.T("about.qq-group-url", "https://qm.qq.com/q/wByO7XG8Wk");

    
    
    
    
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
