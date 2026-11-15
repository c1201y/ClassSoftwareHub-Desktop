using System;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services.License;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Views;










public static class LicenseDialog
{
    
    private static bool _open;

    
    public static async Task<bool> ShowAsync(XamlRoot? root, bool fromGate = false)
    {
        if (root is null || _open) return false;

        _open = true;
        try
        {
            var box = new TextBox
            {
                PlaceholderText = "CSH-XXXX-XXXX-XXXX-XXXX",
                MaxLength = 24,
                Margin = new Thickness(0, 4, 0, 0),
            };

            var hint = new TextBlock
            {
                Text = fromGate
                    ? "这次请求需要授权才能完成。激活后请重新操作一次。"
                    : "激活码共 16 位，字母和数字，中间的短横线可以不输。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                FontSize = 12,
                Opacity = 0.7,
            };

            var error = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                FontSize = 12,
                Visibility = Visibility.Collapsed,
                Foreground = (Brush)ServiceLocator.CriticalBrush(),
            };

            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = "输入激活码",
                PrimaryButtonText = "激活",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Content = new StackPanel { Children = { hint, box, error } },
            };

            dialog.PrimaryButtonClick += async (_, args) =>
            {
                
                var deferral = args.GetDeferral();
                try
                {
                    error.Visibility = Visibility.Collapsed;
                    args.Cancel = true;

                    var code = box.Text?.Trim() ?? "";
                    if (code.Length == 0)
                    {
                        error.Text = "请先输入激活码。";
                        error.Visibility = Visibility.Visible;
                        return;
                    }

                    dialog.PrimaryButtonText = "正在激活…";

                    var result = await ClientAuth.ActivateAsync(code);

                    if (result.Ok)
                    {
                        args.Cancel = false;      
                        return;
                    }

                    error.Text = result.Message;
                    error.Visibility = Visibility.Visible;
                }
                catch (Exception ex)
                {
                    AppLog.Exception("license", ex, "激活对话框");
                    error.Text = "激活时出了点问题，请再试一次。";
                    error.Visibility = Visibility.Visible;
                }
                finally
                {
                    dialog.PrimaryButtonText = "激活";
                    deferral.Complete();
                }
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
                AppLog.Info("license", "用户完成激活");

            return result == ContentDialogResult.Primary;
        }
        finally
        {
            _open = false;
        }
    }

    
    
    
    
    
    
    
    private static class ServiceLocator
    {
        public static Brush CriticalBrush()
        {
            try
            {
                if (Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out var v) && v is Brush b)
                    return b;
            }
            catch { }
            return new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
        }
    }
}
