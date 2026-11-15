using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 「自动关机」页（内置工具，tag = <c>autoshutdown</c>）。
///
/// ⚠️ 2026-10-12 从「实验性功能」转正，搬进「内置工具」分组
///    （入口 = <c>Data/ToolDef.cs</c> 的 ToolCatalog；页头那颗「实验性功能」徽标已去掉）。
///
/// 上半是总开关，中间是按星期排的时间表（周一~周日一列一天），
/// 下半是「把取消关机加到侧边栏」的快捷入口 + 限制说明。
///
/// ⚠️ 页面只负责**改配置**，真正到点执行在 <see cref="Data.AutoShutdown"/> 的定时巡检里；
///    这里绝不自己发关机命令（改完存盘即可，引擎每次 Tick 都重新读配置）。
/// </summary>
public sealed partial class AutoShutdownPage : Page
{
    /// <summary>侧边栏那颗「取消关机」的模块 id（与 Data/SidebarModule.cs 一致）。</summary>
    private const string CancelModuleId = "cancelshutdown";

    private AutoShutdownConfig _config = new();

    /// <summary>程序化改控件值时用它挡住 Toggled，别把"回显"当成"用户改的"。</summary>
    private bool _loading;

    public AutoShutdownPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    // ── 载入 / 回显 ─────────────────────────────────────────

    private void Reload()
    {
        _config = AutoShutdownConfig.Load();

        _loading = true;
        EnableSwitch.IsOn = _config.Enabled;
        _loading = false;

        ShowScheduleHint("");
        BuildSchedule();
        UpdateHints();
    }

    private void UpdateHints()
    {
        var today = AutoShutdownConfig.DayIndexOf(DateTime.Now);
        var count = _config.Days[today].Count;

        SwitchHint.Text = _config.Enabled
            ? $"已启用。今天（{AutoShutdownConfig.DayNames[today]}）有 {count} 个关机点，到点会按设定执行。"
            : "未启用 —— 打开后才会按下面的时间表执行。";

        SidebarHint.Text = HasCancelModule()
            ? "「取消关机」已在侧边栏上，可以在「侧边布局」里调整它的位置。"
            : "还没把「取消关机」加到侧边栏。";
    }

    private static bool HasCancelModule() =>
        (App.Settings.Current.SidebarModuleIds ?? Array.Empty<string>()).Contains(CancelModuleId);

    // ── 时间表 ──────────────────────────────────────────────

    private void BuildSchedule()
    {
        ScheduleGrid.Children.Clear();
        ScheduleGrid.ColumnDefinitions.Clear();

        for (var day = 0; day < 7; day++)
        {
            ScheduleGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var column = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Top };

            column.Children.Add(new TextBlock
            {
                Text = AutoShutdownConfig.DayNames[day],
                FontSize = 12,
                Opacity = 0.7,
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            var points = _config.Days[day];
            for (var index = 0; index < points.Count; index++)
                column.Children.Add(BuildPointButton(day, index, points[index]));

            // 列底：加号 + 它右边那颗「套用前一天」。
            // ⚠️ 两颗并排放在一个横向 StackPanel 里（Nick 2026-10-12 指明"在加号右边再加一个"）。
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            actions.Children.Add(BuildAddButton(day));
            actions.Children.Add(BuildCopyPrevButton(day));
            column.Children.Add(actions);

            Grid.SetColumn(column, day);
            ScheduleGrid.Children.Add(column);
        }
    }

    /// <summary>一颗关机点按钮：上行是时间，下行是方式（+延迟）。</summary>
    private Button BuildPointButton(int dayIndex, int pointIndex, ShutdownPoint point)
    {
        var caption = AutoShutdownConfig.NormalizeMethod(point.Method) == ShutdownMethods.Slide
            ? "滑动关机"
            : $"直接关机 · {point.DelaySeconds}s";

        var content = new StackPanel { Spacing = 1 };
        content.Children.Add(new TextBlock { Text = point.Time, FontSize = 13 });
        content.Children.Add(new TextBlock { Text = caption, FontSize = 11, Opacity = 0.6 });

        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 6, 10, 6),
        };
        ToolTipService.SetToolTip(button, "点一下修改这个关机点，或者删掉它");

        var day = dayIndex;
        var index = pointIndex;
        button.Click += async (_, _) => await EditPointAsync(day, index);
        return button;
    }

    /// <summary>那一列的「+」。</summary>
    private Button BuildAddButton(int dayIndex)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0),
            Width = 30,
            Height = 30,
            Content = new FontIcon { Glyph = "\uE710", FontSize = 12 },
        };
        ToolTipService.SetToolTip(button, $"给{AutoShutdownConfig.DayNames[dayIndex]}加一个关机点");

        var day = dayIndex;
        button.Click += async (_, _) => await AddPointAsync(day);
        return button;
    }

    /// <summary>
    /// 加号右边那颗「套用前一天」：把**前一天**那一列整列拷到本列（周一取周日）。
    ///
    /// ⚠️ 语义是**覆盖**不是追加 —— "套用"就是把这一天排成前一天的样子；
    ///    本列已有关机点时先弹一句确认，不做无声覆盖。
    /// ⚠️ 前一天是空的就只提示、不动手 —— 否则"套用"会退化成"把本列清空"，那不是用户要的。
    /// </summary>
    private Button BuildCopyPrevButton(int dayIndex)
    {
        var prev = PreviousDay(dayIndex);

        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0),
            Width = 30,
            Height = 30,
            // E8C8（Copy）是 MDL2 里的老码位，Win10 也有字形
            Content = new FontIcon { Glyph = "\uE8C8", FontSize = 12 },
        };
        ToolTipService.SetToolTip(button,
            $"把{AutoShutdownConfig.DayNames[prev]}的关机点套用到{AutoShutdownConfig.DayNames[dayIndex]}（本列原有内容会被替换）");

        var day = dayIndex;
        button.Click += async (_, _) => await CopyPreviousDayAsync(day);
        return button;
    }

    /// <summary>本列的"前一天"（周一取周日）。</summary>
    private static int PreviousDay(int dayIndex) => (dayIndex + 6) % 7;

    private async Task CopyPreviousDayAsync(int dayIndex)
    {
        var prev = PreviousDay(dayIndex);
        var source = _config.Days[prev];
        var target = _config.Days[dayIndex];

        if (source.Count == 0)
        {
            ShowScheduleHint($"{AutoShutdownConfig.DayNames[prev]}还没有关机点，没有可以套用的内容。");
            return;
        }

        if (target.Count > 0)
        {
            var confirm = new ContentDialog
            {
                        RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
                XamlRoot = XamlRoot,
                Title = "套用前一天的关机点",
                Content = $"{AutoShutdownConfig.DayNames[dayIndex]}现在有 {target.Count} 个关机点，"
                          + $"套用{AutoShutdownConfig.DayNames[prev]}的 {source.Count} 个会把它们全部替换掉。",
                PrimaryButtonText = "替换",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }

        // ⚠️ 必须**深拷**：直接把同一批对象塞过去，之后改其中一天会连带改到另一天
        target.Clear();
        foreach (var p in source)
            target.Add(new ShutdownPoint
            {
                Time = p.Time,
                Method = p.Method,
                DelaySeconds = p.DelaySeconds,
            });

        SaveAndRefresh();
        ShowScheduleHint(
            $"已把{AutoShutdownConfig.DayNames[prev]}的 {source.Count} 个关机点套用到{AutoShutdownConfig.DayNames[dayIndex]}。");
    }

    /// <summary>时间表那块自己的提示行（套用结果 / 没什么可套用）。</summary>
    private void ShowScheduleHint(string text)
    {
        if (ScheduleHint is not null) ScheduleHint.Text = text;
    }

    // ── 增 / 改 / 删 ────────────────────────────────────────

    private async Task AddPointAsync(int dayIndex)
    {
        // 默认值：**沿用上次保存过的那个点**（时间 / 方式 / 延迟），没存过才退回内置默认
        // （放学时段 + 直接关机 + 一分钟延迟）。Nick 2026-10-12：一个一个配太麻烦。
        var last = _config.LastPoint;
        var draft = last is null
            ? new ShutdownPoint { Time = "17:30", Method = ShutdownMethods.Shutdown, DelaySeconds = 60 }
            : new ShutdownPoint { Time = last.Time, Method = last.Method, DelaySeconds = last.DelaySeconds };

        if (await ShowPointDialogCoreAsync(draft, isNew: true) != PointDialogOutcome.Save) return;

        _config.Days[dayIndex].Add(draft);
        RememberLast(draft);
        SaveAndRefresh();
    }

    /// <summary>把"用户刚填好的这一份"记进存档，供下次新增关机点时当默认值。</summary>
    private void RememberLast(ShutdownPoint point) =>
        _config.LastPoint = new ShutdownPoint
        {
            Time = point.Time,
            Method = point.Method,
            DelaySeconds = point.DelaySeconds,
        };

    private async Task EditPointAsync(int dayIndex, int pointIndex)
    {
        var current = _config.Days[dayIndex][pointIndex];

        // ⚠️ 先拷一份草稿进去编辑，用户点「取消」时原对象一个字段都不该被改过
        var draft = new ShutdownPoint
        {
            Time = current.Time,
            Method = current.Method,
            DelaySeconds = current.DelaySeconds,
        };

        var outcome = await ShowPointDialogCoreAsync(draft, isNew: false);

        switch (outcome)
        {
            case PointDialogOutcome.Save:
                current.Time = draft.Time;
                current.Method = draft.Method;
                current.DelaySeconds = draft.DelaySeconds;
                RememberLast(current);      // 改过的这份也算"上次填的"，下次新增照它来
                SaveAndRefresh();
                break;

            case PointDialogOutcome.Delete:
                _config.Days[dayIndex].RemoveAt(pointIndex);
                SaveAndRefresh();
                break;

            // Cancel：草稿丢掉，原样不动
        }
    }

    private void SaveAndRefresh()
    {
        _config.Save();
        BuildSchedule();
        UpdateHints();
    }

    // ── 关机点配置弹层 ──────────────────────────────────────

    private enum PointDialogOutcome { Save, Delete, Cancel }

    /// <summary>弹配置框。返回值 =「保存 / 删除 / 取消」（删除只在编辑既有时间点时才有这个键）。</summary>
    private async Task<PointDialogOutcome> ShowPointDialogCoreAsync(ShutdownPoint draft, bool isNew)
    {
        var (hour, minute) = SplitTime(draft.Time);

        // ⚠️ 时间用两个 NumberBox（时 / 分），不用 TimePicker：
        //    TimePicker 三列默认宽度大（塞进对话框会挤），弹出列表还会朝上盖住上面的内容 ——
        //    计时器页已经因为这个把 TimePicker 撤过一回（见 Pages/Tools/TimerToolPage.xaml 的注释）。
        //    NumberBox 用 Inline 步进：Compact 的浮层在触屏上取消不掉，也点不动（同 TimerToolPage）。
        // ⚠️ 宽度不能照抄计时器页的 124：NumberBox **获得焦点时会插进一颗「清除 ×」按钮**，
        //    而 Inline 步进的两个箭头是常驻的 —— 124 宽下这三颗按钮刚好把文字区挤成 0 宽，
        //    于是「17」这种两位数会整片看不见（看着像空框，其实值一直在；失焦立刻又出现）。
        //    2026-10-10 实拍确认：同一时刻「时」框失焦显示 17、「分」框一获焦它的 30 就消失。
        //    ⇒ 160 是让"焦点态下仍留得下三位数"的最小值；动它之前先截图复核。
        var hourBox = new NumberBox
        {
            Value = hour, Minimum = 0, Maximum = 23, SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 160, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center,
        };
        var minuteBox = new NumberBox
        {
            Value = minute, Minimum = 0, Maximum = 59, SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 160, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center,
        };

        var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        timeRow.Children.Add(hourBox);
        timeRow.Children.Add(new TextBlock { Text = "时", FontSize = 12, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center });
        timeRow.Children.Add(minuteBox);
        timeRow.Children.Add(new TextBlock { Text = "分", FontSize = 12, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center });

        // 方式：两颗原生 RadioButton（⚠️ MinWidth 必须清零，默认 120 会把这一行撑开）
        var radioShutdown = new RadioButton
        {
            Content = "直接关机", GroupName = "ShutdownMethod", MinWidth = 0,
            IsChecked = AutoShutdownConfig.NormalizeMethod(draft.Method) == ShutdownMethods.Shutdown,
        };
        var radioSlide = new RadioButton
        {
            Content = "滑动关机", GroupName = "ShutdownMethod", MinWidth = 0,
            IsChecked = AutoShutdownConfig.NormalizeMethod(draft.Method) == ShutdownMethods.Slide,
        };

        var methodRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        methodRow.Children.Add(radioShutdown);
        methodRow.Children.Add(radioSlide);

        // 延迟：只有「直接关机」用得上
        var delayBox = new NumberBox
        {
            Value = draft.DelaySeconds, Minimum = 0, Maximum = AutoShutdownConfig.MaxDelaySeconds, SmallChange = 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 170, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center,
        };
        var delayRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        delayRow.Children.Add(delayBox);
        delayRow.Children.Add(new TextBlock
        {
            Text = "秒后关机（0 = 立刻）", FontSize = 12, Opacity = 0.8,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var delaySection = new StackPanel { Spacing = 6, Children = { delayRow } };
        var delayLabel = new TextBlock { Text = "延迟", FontSize = 12, Opacity = 0.6 };
        delaySection.Children.Insert(0, delayLabel);

        void SyncDelayVisibility() =>
            delaySection.Visibility = radioShutdown.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        // ⚠️ 事件必须在控件都建好之后再挂 —— 初始化 IsChecked 时 Checked 就会触发一次
        radioShutdown.Checked += (_, _) => SyncDelayVisibility();
        radioSlide.Checked += (_, _) => SyncDelayVisibility();
        SyncDelayVisibility();

        // 内容宽 380：时间行 = 160 + 8 +「时」+ 8 + 160 + 8 +「分」≈ 369，留一点余量给字体度量差异
        var root = new StackPanel { Spacing = 16, Width = 380 };
        root.Children.Add(Section("时间", timeRow));
        root.Children.Add(Section("方式", methodRow));
        root.Children.Add(delaySection);

        var dialog = new ContentDialog
        {
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = XamlRoot,
            Title = isNew ? "添加关机点" : "关机点",
            Content = root,
            PrimaryButtonText = "保存",
            SecondaryButtonText = isNew ? "" : "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return PointDialogOutcome.Cancel;

        if (result == ContentDialogResult.Secondary) return PointDialogOutcome.Delete;

        // 保存：把控件里的值写回草稿（用 Clamp 挡掉 NaN / 越界）
        draft.Time = $"{ReadNumber(hourBox, hour, 0, 23):00}:{ReadNumber(minuteBox, minute, 0, 59):00}";
        draft.Method = radioSlide.IsChecked == true ? ShutdownMethods.Slide : ShutdownMethods.Shutdown;
        draft.DelaySeconds = ReadNumber(delayBox, draft.DelaySeconds, 0, AutoShutdownConfig.MaxDelaySeconds);
        return PointDialogOutcome.Save;
    }

    private static StackPanel Section(string label, UIElement control)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.6 });
        panel.Children.Add(control);
        return panel;
    }

    /// <summary>NumberBox 取整数：NaN（用户清空过）和越界都退回兜底值。</summary>
    private static int ReadNumber(NumberBox box, int fallback, int min, int max)
    {
        var value = box.Value;
        if (double.IsNaN(value)) return fallback;
        return Math.Clamp((int)Math.Round(value), min, max);
    }

    /// <summary>"HH:mm" → (时, 分)；认不出就给 17:30。</summary>
    private static (int Hour, int Minute) SplitTime(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text)
            && TimeSpan.TryParseExact(text.Trim(), @"hh\:mm", null, out var span))
            return (span.Hours, span.Minutes);

        return (17, 30);
    }

    // ── 顶部开关 / 侧边栏 ───────────────────────────────────

    private void EnableSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _config.Enabled = EnableSwitch.IsOn;
        _config.Save();
        UpdateHints();
    }

    private void AddToSidebar_Click(object sender, RoutedEventArgs e)
    {
        var ids = (App.Settings.Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();

        if (!ids.Contains(CancelModuleId))
        {
            ids.Add(CancelModuleId);                       // 追加到末尾
            App.Settings.Current.SidebarModuleIds = ids.ToArray();
            App.Settings.Save();
            Views.ToolSidebarWindow.ApplyModules();        // 真侧边栏立刻重建
        }

        SidebarHint.Text = App.Settings.Current.SidebarEnabled
            ? "已添加到侧边栏末尾。可以在「侧边布局」里调整它的位置。"
            : "已添加到侧边栏末尾，但侧边栏当前是隐藏的 —— 去「侧边布局」把它显示出来。";
    }

    private void OpenSidebarLayout_Click(object sender, RoutedEventArgs e)
        => App.MainWindow?.Shell.NavigateTo("sidebar");
}
