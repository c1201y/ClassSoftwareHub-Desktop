using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 「示例名单」对话框（2026-09-29 Nick 提）。
///
/// 起因：导入名单这个功能本身没什么难的，难的是**用户不知道该交什么样的文件**——
/// 一列姓名？两列？要不要表头？txt 要不要逗号分隔？以前只能在导入失败后从一行报错里猜。
/// 现在把答案摆在动手之前：两张示意截图（Excel / txt 各一张），加一份可以直接用的示例文件。
///
/// ⚠️ 两张图与两份示例文件都是**内嵌资源**（见 csproj &lt;EmbeddedResource&gt;），
///    单文件发布下不会依赖任何外部文件；图片先释放到缓存目录再喂给 BitmapImage。
/// </summary>
public sealed partial class RosterSampleDialog : ContentDialog
{
    /// <summary>用户点了「用示例名单试一下」——由调用方据此把示例装进抽取池。</summary>
    public bool UseSampleRequested { get; private set; }

    /// <param name="hasExistingRoster">
    /// 抽取池里已经有名单时，把按钮文案改成「替换」——避免用户以为只是"预览一下"，
    /// 结果辛苦导入的名单被覆盖掉。
    /// </param>
    public RosterSampleDialog(bool hasExistingRoster)
    {
        InitializeComponent();

        if (hasExistingRoster) UseButton.Content = "用示例名单替换当前名单";

        LoadSampleImages();
    }

    private void LoadSampleImages()
    {
        var excel = Services.EmbeddedAssets.ExtractToCache("sample-excel.png", "roster-sample-excel.png");
        if (excel is not null) ExcelImage.Source = new BitmapImage(new Uri(excel));

        var txt = Services.EmbeddedAssets.ExtractToCache("sample-txt.png", "roster-sample-txt.png");
        if (txt is not null) TxtImage.Source = new BitmapImage(new Uri(txt));
    }

    // ══════════ 三个动作 ══════════
    private void Use_Click(object sender, RoutedEventArgs e)
    {
        UseSampleRequested = true;
        Hide();          // 立刻关掉，由调用方接着装填抽取池
    }

    /// <summary>底部的「关闭」（Esc 也能关，两套都在）。</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private async void DownloadExcel_Click(object sender, RoutedEventArgs e)
        => await SaveSampleAsync("roster-sample.xlsx", "名单示例.xlsx", "Excel 名单示例");

    private async void DownloadTxt_Click(object sender, RoutedEventArgs e)
        => await SaveSampleAsync("roster-sample.txt", "名单示例.txt", "纯文本名单示例");

    /// <summary>
    /// 把内嵌的示例文件另存到用户挑的位置。
    /// ⚠️ 保存对话框**不关闭**当前对话框：用户可能两个格式都想要，也可能挑错地方想重来。
    /// </summary>
    private async Task SaveSampleAsync(string embeddedName, string suggestedName, string typeLabel)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName),
            };
            picker.FileTypeChoices.Add(typeLabel, new List<string> { Path.GetExtension(suggestedName) });

            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;      // 用户取消

            Note(Services.EmbeddedAssets.ExtractTo(embeddedName, file.Path)
                ? $"已保存到 {file.Path}"
                : "保存失败：示例文件未能写出。");
        }
        catch (Exception ex)
        {
            Note("保存失败：" + ex.Message);
        }
    }

    private void Note(string text)
    {
        NoteText.Text = text;
        NoteText.Visibility = Visibility.Visible;
    }
}
