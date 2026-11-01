using ClassSoftwareHub.Desktop.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Oobe;

/// <summary>
/// 第 2 步：用户协议。正文取自 <see cref="LegalText"/>（与站点同步，别手抄）；
/// 勾选前不能前进（<see cref="CanAdvance"/> 由导航壳在「下一步」时询问）。
/// </summary>
public sealed partial class OobeAgreementPage : Page, IOobeStepGuard
{
    public OobeAgreementPage()
    {
        InitializeComponent();
        BuildBody();
    }

    public bool CanAdvance => AgreeCheck.IsChecked == true;

    private void BuildBody()
    {
        // ⚠️ 首装走到这里时一定是**内置版**：此时还没有任何条款缓存，网络拉取要等主窗口首帧
        //    之后才发生（MainWindow.OnFirstActivated → LegalTextSource.RefreshAsync）。
        //    这恰好是对的 —— 安装程序那一页（installer/Legal.rtf）与这里是同一次生成出来的，
        //    用户从安装到向导看到的是同一个版本，不会出现「刚同意完，进应用发现是另一份」。
        //    升级后重跑向导（改存档触发）则能读到缓存，显示的就是最新那份。
        var doc = Services.LegalTextSource.Find("agreement");
        if (doc is null)
        {
            AgreementBody.Children.Add(Paragraph("条款内容加载失败，请稍后重试。", secondary: true));
            return;
        }

        AgreementBody.Children.Add(new TextBlock
        {
            Text = $"{doc.Title}　{doc.VersionLine}",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        if (doc.Intro.Length > 0) AgreementBody.Children.Add(Paragraph(doc.Intro));

        foreach (var section in doc.Sections)
        {
            AgreementBody.Children.Add(new TextBlock
            {
                Text = section.Heading,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });

            foreach (var item in section.Items) AgreementBody.Children.Add(Paragraph(item));
        }
    }

    private static TextBlock Paragraph(string text, bool secondary = false) => new()
    {
        // 条款源里用 **…** 标重点；纯 TextBlock 不认 Markdown，去掉星号以免正文里露出符号
        Text = text.Replace("**", ""),
        FontSize = 12.5,
        Opacity = secondary ? 0.6 : 0.8,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 19,
    };

    private void Agree_Click(object sender, RoutedEventArgs e)
    {
        AgreeHint.Visibility = CanAdvance ? Visibility.Collapsed : Visibility.Visible;
        OobeSession.Current?.RefreshChrome();
    }
}
