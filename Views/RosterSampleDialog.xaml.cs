using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace ClassSoftwareHub.Desktop.Views;











public sealed partial class RosterSampleDialog : ContentDialog
{
    
    public bool UseSampleRequested { get; private set; }

    
    
    
    
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

    
    private void Use_Click(object sender, RoutedEventArgs e)
    {
        UseSampleRequested = true;
        Hide();          
    }

    
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private async void DownloadExcel_Click(object sender, RoutedEventArgs e)
        => await SaveSampleAsync("roster-sample.xlsx", "名单示例.xlsx", "Excel 名单示例");

    private async void DownloadTxt_Click(object sender, RoutedEventArgs e)
        => await SaveSampleAsync("roster-sample.txt", "名单示例.txt", "纯文本名单示例");

    
    
    
    
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
            if (file is null) return;      

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
