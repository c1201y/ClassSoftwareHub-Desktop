using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Pages;













public sealed partial class AutoShutdownPage : Page
{
    
    private const string CancelModuleId = "cancelshutdown";

    private AutoShutdownConfig _config = new();

    
    private bool _loading;

    public AutoShutdownPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    

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

    
    
    
    
    
    
    
    private Button BuildCopyPrevButton(int dayIndex)
    {
        var prev = PreviousDay(dayIndex);

        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0),
            Width = 30,
            Height = 30,
            
            Content = new FontIcon { Glyph = "\uE8C8", FontSize = 12 },
        };
        ToolTipService.SetToolTip(button,
            $"把{AutoShutdownConfig.DayNames[prev]}的关机点套用到{AutoShutdownConfig.DayNames[dayIndex]}（本列原有内容会被替换）");

        var day = dayIndex;
        button.Click += async (_, _) => await CopyPreviousDayAsync(day);
        return button;
    }

    
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

    
    private void ShowScheduleHint(string text)
    {
        if (ScheduleHint is not null) ScheduleHint.Text = text;
    }

    

    private async Task AddPointAsync(int dayIndex)
    {
        
        
        var last = _config.LastPoint;
        var draft = last is null
            ? new ShutdownPoint { Time = "17:30", Method = ShutdownMethods.Shutdown, DelaySeconds = 60 }
            : new ShutdownPoint { Time = last.Time, Method = last.Method, DelaySeconds = last.DelaySeconds };

        if (await ShowPointDialogCoreAsync(draft, isNew: true) != PointDialogOutcome.Save) return;

        _config.Days[dayIndex].Add(draft);
        RememberLast(draft);
        SaveAndRefresh();
    }

    
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
                RememberLast(current);      
                SaveAndRefresh();
                break;

            case PointDialogOutcome.Delete:
                _config.Days[dayIndex].RemoveAt(pointIndex);
                SaveAndRefresh();
                break;

            
        }
    }

    private void SaveAndRefresh()
    {
        _config.Save();
        BuildSchedule();
        UpdateHints();
    }

    

    private enum PointDialogOutcome { Save, Delete, Cancel }

    
    private async Task<PointDialogOutcome> ShowPointDialogCoreAsync(ShutdownPoint draft, bool isNew)
    {
        var (hour, minute) = SplitTime(draft.Time);

        
        
        
        
        
        
        
        
        
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

        
        radioShutdown.Checked += (_, _) => SyncDelayVisibility();
        radioSlide.Checked += (_, _) => SyncDelayVisibility();
        SyncDelayVisibility();

        
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

    
    private static int ReadNumber(NumberBox box, int fallback, int min, int max)
    {
        var value = box.Value;
        if (double.IsNaN(value)) return fallback;
        return Math.Clamp((int)Math.Round(value), min, max);
    }

    
    private static (int Hour, int Minute) SplitTime(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text)
            && TimeSpan.TryParseExact(text.Trim(), @"hh\:mm", null, out var span))
            return (span.Hours, span.Minutes);

        return (17, 30);
    }

    

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
            ids.Add(CancelModuleId);                       
            App.Settings.Current.SidebarModuleIds = ids.ToArray();
            App.Settings.Save();
            Views.ToolSidebarWindow.ApplyModules();        
        }

        SidebarHint.Text = App.Settings.Current.SidebarEnabled
            ? "已添加到侧边栏末尾。可以在「侧边布局」里调整它的位置。"
            : "已添加到侧边栏末尾，但侧边栏当前是隐藏的 —— 去「侧边布局」把它显示出来。";
    }

    private void OpenSidebarLayout_Click(object sender, RoutedEventArgs e)
        => App.MainWindow?.Shell.NavigateTo("sidebar");
}
