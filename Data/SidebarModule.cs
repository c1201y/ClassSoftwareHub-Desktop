using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassSoftwareHub.Desktop.Data;


public static class SidebarModuleKinds
{
    
    public const string Palette = "palette";
    
    public const string Page = "page";
    
    
    
    
    
    
    public const string Window = "window";
    
    
    
    
    
    
    
    
    
    public const string Action = "action";

    public const string Pinned = "pinned";

    
    
    
    
    
    
    
    
    
    
    public const string Panel = "panel";
}





public sealed class SidebarModule
{
    
    public string Id { get; init; } = "";

    
    public string Name { get; init; } = "";

    
    public string ShortName { get; init; } = "";

    
    
    
    
    
    
    
    
    
    
    public string LongName { get; init; } = "";

    
    public string Glyph { get; init; } = "";

    
    public string Kind { get; init; } = SidebarModuleKinds.Palette;

    
    public Type? Page { get; init; }

    
    
    
    
    public string? WindowTool { get; init; }

    
    
    
    
    
    
    
    
    public Type? WindowType { get; init; }

    
    public string? Note { get; init; }

    
    public string Hint => Note ?? Kind switch
    {
        SidebarModuleKinds.Page => "打开主窗口中的该页面",
        SidebarModuleKinds.Window => "在独立窗口中打开，不必拉出主界面",
        SidebarModuleKinds.Action => "单击即执行，不切换窗口、不抢占焦点",
        SidebarModuleKinds.Pinned => "显示已添加的快捷应用、快捷方式或文件",
        SidebarModuleKinds.Panel => "在侧边栏旁显示面板，不切换窗口、不占用主界面",
        _ => "显示浮窗，不占用主界面"
    };
}


public static class SidebarModules
{
    public static readonly IReadOnlyList<SidebarModule> All = new List<SidebarModule>
    {
        new() { Id = "pinned-apps", Name = "快捷应用", ShortName = "快捷", LongName = "快捷应用", Glyph = "\uE731", Kind = SidebarModuleKinds.Pinned, Note = "在「侧边布局」里添加应用、快捷方式或文件，按侧边栏模块按钮的统一尺寸显示" },
        new() { Id = "pick-number", Name = "随机抽号", ShortName = "抽号", LongName = "随机抽号", Glyph = "\uE716", Kind = SidebarModuleKinds.Palette },
        new() { Id = "timer", Name = "课堂计时", ShortName = "计时", LongName = "课堂计时", Glyph = "\uE81C", Kind = SidebarModuleKinds.Palette },
        new() { Id = "stopwatch", Name = "秒表计时", ShortName = "秒表", LongName = "秒表计时", Glyph = "\uE916", Kind = SidebarModuleKinds.Palette },
        new() { Id = "clock", Name = "全屏时钟", ShortName = "时钟", LongName = "全屏时钟", Glyph = "\uE740", Kind = SidebarModuleKinds.Palette },
        
        
        
        
        new() { Id = "image-color", Name = "图片取色", ShortName = "取色", LongName = "图片取色", Glyph = "\uE790", Kind = SidebarModuleKinds.Window, WindowType = typeof(Views.ImageColorWindow), Note = "弹出小浮窗做取色：拖入图片即提取柔和配色（种子色 + 7 档明暗），单击色块复制色值" },
        
        
        
        
        
        
        
        
        new() { Id = "dictionary", Name = "查阅单词", ShortName = "查阅", LongName = "查阅单词", Glyph = "\uE8F1", Kind = SidebarModuleKinds.Window, WindowType = typeof(Views.QuickLookupWindow), Note = "弹出小浮窗查阅单词：英译中、中译英都支持，先查本地词库，未收录的再联网" },
        
        
        new() { Id = "encoding", Name = "编码 / 哈希转换", ShortName = "编码", LongName = "编码校验", Glyph = "\uE943", Kind = SidebarModuleKinds.Window, WindowType = typeof(Views.HashCheckWindow), Note = "弹出小浮窗做哈希校验：拖入文件即出 MD5 / SHA-1 / SHA-256 / SHA-512，粘贴官方校验值自动比对" },
        
        
        
        
        
        new() { Id = "subject-function", Name = "函数图像", ShortName = "函数", LongName = "函数图像", Glyph = "\uE9D2", Kind = SidebarModuleKinds.Window, WindowTool = "function-plot", Note = "在独立窗口中打开函数图像，不必打开主界面" },

        // 2026-11-21：写作与分享都做成独立窗口，侧边栏点一下直接进，不经过主界面。
        new() { Id = "tool-markdown", Name = "Markdown 工具", ShortName = "Markdown", LongName = "Markdown 写作", Glyph = "\uE70F", Kind = SidebarModuleKinds.Window, WindowType = typeof(Views.Markdown.MarkdownWindow), Note = "独立窗口打开 Markdown 写作，可导入文件，写完用沉浸模式全屏展示" },
        new() { Id = "tool-qrcode", Name = "二维码分享", ShortName = "二维码", LongName = "二维码分享", Glyph = "\uE72D", Kind = SidebarModuleKinds.Window, WindowType = typeof(Views.Share.QrShareWindow), Note = "独立窗口把链接或文件生成二维码，手机扫码即可打开或下载" },

        // 2026-11-20 暂时隐藏：方程求解与化学方程式配平不在侧边栏提供入口，页面与 ToolCatalog 定义保留。
        
        

        
        
        
        new() { Id = "volume", Name = "音量调节", ShortName = "音量", LongName = "音量调节", Glyph = "\uE767", Kind = SidebarModuleKinds.Panel, Note = "单击后在侧边栏旁显示面板，调节系统主音量（带刻度的垂直滑块）。面板中的「展开」可显示音量合成器，为各发声应用单独调节音量" },
        
        
        new() { Id = "brightness", Name = "屏幕亮度", ShortName = "亮度", LongName = "屏幕亮度", Glyph = "\uE706", Kind = SidebarModuleKinds.Panel, Note = "单击后在侧边栏旁显示面板，调节屏幕亮度（笔记本内屏可用，外接显示器通常不可用）。面板中的「自动亮度」按钮需设备具备环境光传感器才能使用" },

        

        
        
        
        
        new() { Id = "explorer", Name = "文件资源管理器", ShortName = "文件", LongName = "资源管理", Glyph = "\uE8B7", Kind = SidebarModuleKinds.Action, Note = "打开文件资源管理器，等同按 Win+E" },
        new() { Id = "run", Name = "运行", ShortName = "运行", LongName = "运行命令", Glyph = "\uE756", Kind = SidebarModuleKinds.Action, Note = "打开「运行」对话框，等同按 Win+R；可输入 ms-settings:、cmd、devmgmt.msc 等" },
        
        
        new() { Id = "osk", Name = "屏幕键盘", ShortName = "键盘", LongName = "屏幕键盘", Glyph = "\uE765", Kind = SidebarModuleKinds.Action, Note = "打开 Windows 自带的屏幕键盘（触摸设备上替代实体键盘输入）" },
        new() { Id = "lock", Name = "锁定电脑", ShortName = "锁屏", LongName = "锁定电脑", Glyph = "\uE72E", Kind = SidebarModuleKinds.Action, Note = "立即锁定屏幕，等同按 Win+L；解锁需输入当前账户的密码或 PIN" },

        
        
        
        
        new() { Id = "taskmgr", Name = "任务管理器", ShortName = "管理器", LongName = "任务管理", Glyph = "\uE9D9", Kind = SidebarModuleKinds.Action, Note = "打开 Windows 任务管理器，查看内存 / CPU 占用、结束没有响应的程序（等同 Ctrl+Shift+Esc）" },
        
        
        
        new() { Id = "security", Name = "安全选项", ShortName = "安全项", LongName = "安全选项", Glyph = "\uEA18", Kind = SidebarModuleKinds.Action, Note = "等同 Ctrl+Alt+Del 那个界面上能做的事：锁定电脑 / 切换用户 / 注销（该界面的本体属系统安全桌面，普通程序无法直接调起，这里给的是其中的各个操作）" },
        new() { Id = "clipboard", Name = "剪贴板历史", ShortName = "剪贴板", LongName = "剪贴板", Glyph = "\uE8C8", Kind = SidebarModuleKinds.Action, Note = "等同 Win+V：打开剪贴板历史，挑一条粘到刚才那个窗口里（需先在系统「设置 → 系统 → 剪贴板」里打开剪贴板历史）" },

        new() { Id = "mag", Name = "放大镜", ShortName = "放大", LongName = "放大镜", Glyph = "\uE71E", Kind = SidebarModuleKinds.Action, Note = "调用系统放大镜（跟随鼠标区域）；再次单击关闭" },
        
        
        new() { Id = "screenshot", Name = "截屏贴图", ShortName = "截屏", LongName = "截屏贴图", Glyph = "\uE722", Kind = SidebarModuleKinds.Action, Note = "框选区域后在编辑窗中标注：画笔 / 荧光笔 / 箭头 / 矩形 / 椭圆 / 文字 / 马赛克，并支持撤销与重做；随后可复制到剪贴板 / 保存到本地 / 钉图（钉图按 1:1 显示，拖动边框可缩放，双击或右键关闭）" },
        new() { Id = "taskview", Name = "任务视图", ShortName = "任务", LongName = "任务视图", Glyph = "\uE7C4", Kind = SidebarModuleKinds.Action, Note = "等同 Win+Tab" },
        new() { Id = "showdesktop", Name = "回到桌面", ShortName = "桌面", LongName = "回到桌面", Glyph = "\uE7F4", Kind = SidebarModuleKinds.Action, Note = "最小化所有窗口以显示桌面；再次单击即可还原（不关闭任何程序）" },
        new() { Id = "closefg", Name = "关闭前台应用", ShortName = "关前台", LongName = "关闭前台", Glyph = "\uE8BB", Kind = SidebarModuleKinds.Action, Note = "关闭当前活动窗口（等同单击标题栏 ×，会提示是否保存，不强制结束）" },
        new() { Id = "closeall", Name = "关闭全部窗口", ShortName = "关全部", LongName = "关闭全部", Glyph = "\uE74D", Kind = SidebarModuleKinds.Action, Note = "关闭任务栏中的所有窗口（含最小化窗口）。防误触：首次单击仅计数，再次单击才会执行；均为正常关闭，不强制结束" },
        
        
        
        
        
        
        
        new() { Id = "shutdown", Name = "关机", ShortName = "关机", Glyph = "\uE7E8", Kind = SidebarModuleKinds.Action, Note = "关闭计算机：先弹窗确认；按设置中的「关机方式」执行（手动关机＝系统滑动关机界面，需下滑确认；直接关机＝立即关机）" },

        
        
        
        new() { Id = "cancelshutdown", Name = "取消关机", ShortName = "撤关机", LongName = "取消关机", Glyph = "\uE7E8", Kind = SidebarModuleKinds.Action, Note = "撤回还没执行的自动关机（等同系统的取消关机命令）。当前没有待执行的关机时，只会提示一句" },

        
        
        
        
        
        new() { Id = "killseewo", Name = "结束希沃白板", ShortName = "杀希沃", LongName = "结束希沃", Glyph = "\uEA99", Kind = SidebarModuleKinds.Action, Note = "立刻结束希沃白板5 后台滞留的残留进程（等同「白板专杀」页上的「立即结束」）。有可见窗口的一律放过，不会打断正在讲课的白板。按时间点自动清理那套在「实验性功能 → 白板专杀」里设置" },

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        new() { Id = "ppttouch", Name = "PowerPoint 触控", ShortName = "PPT", LongName = "PPT 触控", Glyph = "\uE786", Kind = SidebarModuleKinds.Action, Note = "单击弹出菜单，开启或关闭放映时的触控手势与长按菜单（两者都关掉就不会再干扰 PowerPoint 本身）；同一组开关也在「PowerPoint 触控优化」页里" },

        
        
    };

    
    public static readonly string[] DefaultIds = { "pick-number", "timer", "stopwatch", "clock" };

    
    public static SidebarModule? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(m => m.Id == id);
}


public sealed class SidebarModuleRow
{
    public SidebarModuleRow(SidebarModule m, bool enabled)
    {
        Id = m.Id;
        Name = m.Name;
        Hint = m.Hint;
        Glyph = m.Glyph;
        Enabled = enabled;
    }

    public string Id { get; }
    public string Name { get; }
    public string Hint { get; }
    public string Glyph { get; }

    
    public bool Enabled { get; set; }
}
