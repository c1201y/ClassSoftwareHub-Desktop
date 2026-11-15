using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Controls;








public static class AppCategoryLook
{
    
    public static readonly AppCategory[] Order =
    {
        AppCategory.Desktop,
        AppCategory.Runtime,
        AppCategory.Store,
        AppCategory.System,
    };

    
    public static string LabelOf(AppCategory category) => category switch
    {
        AppCategory.Runtime => "运行库",
        AppCategory.Store => "微软应用商店",
        AppCategory.System => "系统组件",
        _ => "桌面应用",
    };

    
    
    
    
    
    public static string GlyphOf(AppCategory category) => category switch
    {
        AppCategory.Runtime => "\uE8F1",   
        AppCategory.Store => "\uE719",     
        AppCategory.System => "\uE713",    
        _ => "\uE71D",                     
    };
}
