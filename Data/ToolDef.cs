using System;
using System.Collections.Generic;
using ClassSoftwareHub.Desktop.Pages.Tools;

namespace ClassSoftwareHub.Desktop.Data;



public enum ToolGroup
{
    
    Classroom,

    
    System,
}

public sealed class ToolDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    
    public ToolGroup Group { get; init; } = ToolGroup.Classroom;
    public string Desc { get; init; } = "";
    
    public string Glyph { get; init; } = "";
    
    public Type? Page { get; init; }

    
    
    
    
    
    
    
    public string? WebToolId { get; init; }

    
    
    
    
    
    
    
    
    
    
    
    
    public bool Standalone { get; init; }

    
    public override string ToString() => Name;
}







public static class ToolCatalog
{
    public static readonly List<ToolDef> All = new()
    {
        new ToolDef
        {
            Id = "image-color", Name = "图片取色",
            Group = ToolGroup.System,
            Desc = "导入图片后自动提取一组柔和的配色（种子色 + 7 档明暗变体），单击色块即可复制。",
            Glyph = "\uE790", Page = typeof(ImageColorToolPage)
        },
        new ToolDef
        {
            Id = "pick-number", Name = "随机抽号",
            Desc = "按学号范围或班级名单随机抽取，可一次抽多个、抽过不重复，也能一键随机分组。",
            Glyph = "\uE716", Page = typeof(PickNumberToolPage)
        },
        new ToolDef
        {
            Id = "timer", Name = "课堂计时器",
            Desc = "倒计时 / 秒表，大字号便于投影，到达设定时间响铃。",
            Glyph = "\uE916", Page = typeof(TimerToolPage)
        },
        new ToolDef
        {
            Id = "clock", Name = "全屏时钟",
            Desc = "将屏幕变为大型时钟：可设置背景图与蒙版，全屏显示查看时间。",
            Glyph = "\uE740", Page = typeof(ClockToolPage)
        },
        new ToolDef
        {
            Id = "encoding", Name = "编码 / 哈希工具",
            Group = ToolGroup.System,
            Desc = "校验文件：拖入即得 MD5 / SHA-1 / SHA-256 / SHA-512，可粘贴官方值自动核对；也可做 Base64、URL 编解码。",
            Glyph = "\uE943", Page = typeof(EncodingToolPage)
        },
        new ToolDef
        {
            Id = "qrcode", Name = "QR 生成器",
            Group = ToolGroup.System,
            Desc = "把文本、链接或文件生成二维码；文件通过本机临时地址分享，可设置有效期并保存 PNG。",
            Glyph = "\uE8FE", Page = typeof(QrCodeToolPage)
        },
        new ToolDef
        {
            Id = "autoshutdown", Name = "自动关机",
            Group = ToolGroup.System,
            Desc = "按星期设定关机时间点，到达时间后自动关机；可直接关机（可设延迟），也可用系统原生的滑动关机。",
            Glyph = "\uE787", Page = typeof(ClassSoftwareHub.Desktop.Pages.AutoShutdownPage)
        },
        
        
        
        
        new ToolDef
        {
            Id = "function-plot", Name = "函数图像",
            
            
            
            Desc = "绘制函数与几何图形：支持显函数、隐函数、不等式、极坐标与参数方程，"
                   + "可作点、线段、圆、椭圆、双曲线与多边形，并能绘制平行线、垂线与切线。",
            Glyph = "\uE9D2", Page = typeof(ClassSoftwareHub.Desktop.Pages.FunctionPlotPage),
            
            WebToolId = "function-plot",
            
            Standalone = true
        },
        new ToolDef
        {
            Id = "disk-map", Name = "磁盘空间",
            Group = ToolGroup.System,
            Desc = "按占用面积画出目录结构，一眼看出空间被谁吃掉了；单击方块可逐层下钻。",
            
            Glyph = "\uE7F1", Page = typeof(DiskMapPage)
        },
        new ToolDef
        {
            Id = "equation", Name = "方程求解",
            Desc = "一次方程组给出精确分数解；一元方程按方程本身求根，支持平方、三角函数、对数与指数。",
            
            Glyph = "\uE8EF", Page = typeof(EquationPage)
        },
        new ToolDef
        {
            Id = "chemistry", Name = "化学方程式配平",
            Desc = "输入 A + B = C 形式的方程式即可算出最小正整数系数，并逐种元素核对左右原子数。支持括号与结晶水。",
            
            Glyph = "\uE80A", Page = typeof(ChemistryPage)
        },
        
        
        

        
        
        
        
        
        
        new ToolDef
        {
            Id = "easiguard", Name = "白板专杀",
            Group = ToolGroup.System,
            Desc = "到点结束希沃白板5 后台滞留的残留进程。只认白板家族（EasiNote*），不碰集控端与更新组件。",
            Glyph = "\uEA99", Page = typeof(ClassSoftwareHub.Desktop.Pages.EasiNoteGuardPage)
        },
        new ToolDef
        {
            Id = "appdata", Name = "AppData 管理",
            Group = ToolGroup.System,
            Desc = "列出 AppData 下可安全清理的缓存目录，按占用排序，逐个确认后删除；禁碰区一律不列。",
            Glyph = "\uE8B7", Page = typeof(ClassSoftwareHub.Desktop.Pages.AppDataPage)
        },
        new ToolDef
        {
            Id = "repair", Name = "系统修复",
            Group = ToolGroup.System,
            Desc = "系统文件、组件存储、网络栈、系统服务的修复命令，按预设卡片执行，执行前原样列出命令。",
            Glyph = "\uE7EF", Page = typeof(ClassSoftwareHub.Desktop.Pages.SystemRepairPage)
        },
        
        

        
        
        
        
        
        new ToolDef
        {
            
            
            
            
            Id = "ppttouch", Name = "PowerPoint 触控优化",
            Group = ToolGroup.System,
            Desc = "放映时轻点屏幕翻到下一页，上下滑动切换页面，长按弹出放映菜单。",
            Glyph = "\uE786", Page = typeof(ClassSoftwareHub.Desktop.Pages.PptTouchPage)
        },
        new ToolDef
        {
            
            
            
            Id = "pagefile", Name = "虚拟内存",
            Group = ToolGroup.System,
            Desc = "逐个驱动器设置分页文件大小，重启后生效。",
            Glyph = "\uEDA2", Page = typeof(ClassSoftwareHub.Desktop.Pages.PageFilePage)
        },
        new ToolDef
        {
            
            Id = "ghostfile", Name = "幽灵文件清理",
            Group = ToolGroup.System,
            Desc = "清理删不掉、双击提示找不到路径的残留条目。",
            Glyph = "\uE7C4", Page = typeof(ClassSoftwareHub.Desktop.Pages.GhostFilePage)
        },
        new ToolDef
        {
            
            Id = "sysfeatures", Name = "系统功能",
            Group = ToolGroup.System,
            Desc = "休眠、快速启动、电源计划等系统开关，排队后一次授权写入。",
            Glyph = "\uE7E8", Page = typeof(ClassSoftwareHub.Desktop.Pages.SystemFeaturesPage)
        },
        new ToolDef
        {
            
            Id = "systemtweaks", Name = "系统优化",
            Group = ToolGroup.System,
            Desc = "任务栏、资源管理器这些整机设置，选中后一次授权统一应用。",
            Glyph = "\uE713", Page = typeof(ClassSoftwareHub.Desktop.Pages.SystemTweaksPage)
        },
    };
}
