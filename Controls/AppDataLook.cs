using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Controls;

/// <summary>
/// AppData 管理页的「分类 / 归属」文字与字形（下拉、列表行、说明弹窗共用一处）。
/// 与 <see cref="AppCategoryLook"/> 是同一个角色：把枚举 ↔ 界面文字的对应关系收口，别散在页面里。
///
/// ⚠️ 字形只用 **E7 / E8 / E9 段**的老码位。ED 段属新版图标字体，Win10 上是**空框**，
///    而且 cmap 查得到也没用 —— 必须用 glyphs 脚本渲图看过才算数（见 MEMORY §6）。
/// </summary>
public static class AppDataLook
{
    /// <summary>下拉里「可清理」各分类的固定顺序。</summary>
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
        AppDataKind.Browser => "\uE774",    // 地球
        AppDataKind.Store => "\uE719",      // 购物袋
        AppDataKind.Dev => "\uE8F1",        // 书架
        AppDataKind.Graphics => "\uE7F4",   // 显示器
        _ => "\uE74D",                      // 垃圾桶
    };

    /// <summary>归属列的短文字（列表行、表格副标题用）。</summary>
    public static string ZoneLabelOf(AppDataZone zone) =>
        zone == AppDataZone.Roaming ? "漫游" : "本地";
}
