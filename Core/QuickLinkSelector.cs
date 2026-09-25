using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 快捷入口的两套模板：普通一颗 / 要突出的那颗（「赞助作者」）。
///
/// ⚠️ 为什么不在数据里塞 Brush：**颜色必须在 XAML 里用 `{ThemeResource ...}`** ——
/// 那玩意儿认的是"元素所在那棵树"的主题，会自己跟着深浅色变；
/// 而代码里 `Application.Current.Resources[...]` 查的是应用级（跟系统走）那一套，
/// 浅色界面 + 深色系统时就会取到白字（被 Nick 抓到过）。
/// </summary>
public sealed partial class QuickLinkSelector : DataTemplateSelector
{
    public DataTemplate? Normal { get; set; }
    public DataTemplate? Accent { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
        => item is QuickLink link && link.Accent ? Accent : Normal;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
