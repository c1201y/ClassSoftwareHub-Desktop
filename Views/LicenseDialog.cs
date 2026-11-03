using System;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services.License;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 输入激活码的对话框。设置页的「授权状态」卡片和"请求被挡下来了"的提示都走它，
/// 两处共用一份，免得长得不一样。
///
/// <para>
/// 行为：点「激活」→ 就地提交；失败**不关窗**，把服务端那句话显示在下面，
/// 用户改一个字就能再试，不用从头点一遍。成功才关。
/// </para>
/// </summary>
public static class LicenseDialog
{
    /// <summary>同一时刻只允许一个 —— 两个 ContentDialog 同时 ShowAsync 会直接抛异常。</summary>
    private static bool _open;

    /// <summary>弹出对话框；返回是否激活成功。</summary>
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
                // 失败时**不关窗**，就地显示原因：让用户改一个字再试，不用从头点一遍
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
                        args.Cancel = false;      // 成功才关窗
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

    /// <summary>
    /// 取"危险色"画刷。
    /// <para>
    /// ⚠️ 对话框没有页面可挂，拿不到"元素所在那棵树"的主题，所以走应用级资源；
    /// 深浅两套都由系统按当前主题给，够用。
    /// </para>
    /// </summary>
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
