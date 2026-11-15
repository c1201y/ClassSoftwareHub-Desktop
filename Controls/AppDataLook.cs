using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Controls;








public static class AppDataLook
{
    
    public static readonly AppDataKind[] Order =
    {
        AppDataKind.Temp,
        AppDataKind.Browser,
        AppDataKind.Store,
        AppDataKind.Dev,
        AppDataKind.Graphics,
    };

    public static string LabelOf(AppDataKind kind) => kind switch
    {
        AppDataKind.Browser => "浏览器缓存",
        AppDataKind.Store => "商店应用",
        AppDataKind.Dev => "开发工具",
        AppDataKind.Graphics => "图形缓存",
        _ => "系统临时",
    };

    public static string GlyphOf(AppDataKind kind) => kind switch
    {
        AppDataKind.Browser => "\uE774",    
        AppDataKind.Store => "\uE719",      
        AppDataKind.Dev => "\uE8F1",        
        AppDataKind.Graphics => "\uE7F4",   
        _ => "\uE74D",                      
    };

    
    public static string ZoneLabelOf(AppDataZone zone) =>
        zone == AppDataZone.Roaming ? "漫游" : "本地";
}
