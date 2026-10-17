using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// 化学式编辑器。
///
/// <para>
/// 站在**学生 / 老师**的角度排的（Nick 2026-10-17）：
/// 左边先用**常用元素**的大按钮（带中文名，一眼能找到"铁、氧、钠"），
/// 需要别的元素再展开完整周期表；右边是括号/数字与常用物质、离子根。
/// </para>
/// <para>
/// ⛔ 一上来就摆 118 个 24px 的小格子是开发者思维：中学生配平翻来覆去就那十几种元素，
/// 满屏小方块只会让人找不着。
/// ⛔ 不让用户手敲 "H2O" —— 键盘上打出来的 2 是正文，看着像 H 乘以 2。
/// </para>
/// </summary>
internal sealed class FormulaEditor
{
    private List<string> _substances;
    private List<string> _ions;

    private readonly StringBuilder _raw;
    private readonly Brush _brush;
    private readonly StackPanel _preview;
    private readonly TextBlock _error;
    private readonly StackPanel _fullTableHost;
    private readonly Border _fullTableWrap;

    public Grid View { get; }

    /// <summary>内容变了，参数 = 当前是否合法（用来控制「确定」能不能点）。</summary>
    public event Action<bool>? Changed;

    public bool IsValid { get; private set; }

    public FormulaEditor(string initial, Brush brush)
    {
        _raw = new StringBuilder(initial ?? "");
        _brush = brush;

        var favorites = ChemicalFavorites.Load();
        _substances = favorites.Substances;
        _ions = favorites.Ions;

        _preview = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            MinHeight = 46,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _error = new TextBlock
        {
            FontSize = 12,
            Foreground = Services.ThemeBrush.Get(null, "SystemFillColorCriticalBrush"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        _fullTableHost = new StackPanel();
        _fullTableWrap = new Border
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0),
            Child = _fullTableHost,
        };

        var root = new Grid { RowSpacing = 12, ColumnSpacing = 14, MinWidth = 620 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var preview = BuildPreviewCard();
        Grid.SetColumnSpan(preview, 2);
        root.Children.Add(preview);

        var left = BuildLeftPanel();
        Grid.SetRow(left, 1);
        root.Children.Add(left);

        var right = BuildRightPanel();
        Grid.SetRow(right, 1);
        Grid.SetColumn(right, 1);
        root.Children.Add(right);

        View = root;
        Refresh();
    }

    public string Value => _raw.ToString();

    // ── 预览 ──
    private Border BuildPreviewCard()
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(_preview);
        stack.Children.Add(_error);

        // ⛔ 删除键必须贴在输入内容旁边：放到下面的小键盘里，用户得满处找（Nick 点名）
        var backspace = new Button
        {
            Content = new FontIcon { Glyph = "\uE750", FontSize = 15 },
            Width = 48,
            Height = 44,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(backspace, "删除最后一个字符");
        AttachPressGradient(backspace);
        backspace.Click += (_, _) => Backspace();

        var clear = new Button
        {
            Content = "清空",
            Width = 64,
            Height = 44,
            FontSize = 13,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AttachPressGradient(clear);
        clear.Click += (_, _) => { _raw.Clear(); Refresh(); };

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(stack);
        Grid.SetColumn(backspace, 1);
        grid.Children.Add(backspace);
        Grid.SetColumn(clear, 2);
        grid.Children.Add(clear);

        return new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 8, 12, 8),
            Background = Services.ThemeBrush.Get(null, "ControlFillColorSecondaryBrush"),
            Child = grid,
        };
    }

    private void Refresh()
    {
        _preview.Children.Clear();

        if (_raw.Length == 0)
        {
            _preview.Children.Add(new TextBlock
            {
                Text = "点左边的元素开始输入",
                FontSize = 13,
                Opacity = 0.4,
                VerticalAlignment = VerticalAlignment.Center,
            });

            _error.Visibility = Visibility.Collapsed;
            IsValid = false;
            Changed?.Invoke(false);
            return;
        }

        var rendered = ChemicalNotation.Formula(_raw.ToString(), 30, _brush);
        rendered.VerticalAlignment = VerticalAlignment.Center;
        _preview.Children.Add(rendered);

        IsValid = ChemistryBalancer.ValidateFormula(_raw.ToString(), out var error);

        if (!IsValid && error is { Length: > 0 })
        {
            _error.Text = error;
            _error.Visibility = Visibility.Visible;
        }
        else
        {
            _error.Visibility = Visibility.Collapsed;
        }

        Changed?.Invoke(IsValid);
    }

    // ── 左：常用元素（大）+ 完整周期表（折叠） ──
    private FrameworkElement BuildLeftPanel()
    {
        var panel = new StackPanel { Spacing = 8 };

        // 标题跟着内容走：展开周期表后还写「常用元素」会让人以为下面还是常用那几个
        var sectionTitle = SectionTitle("常用元素");
        panel.Children.Add(sectionTitle);

        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        const int columns = 7;

        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var rows = (PeriodicTable.Common.Count + columns - 1) / columns;
        for (var r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(62) });

        for (var i = 0; i < PeriodicTable.Common.Count; i++)
        {
            var (symbol, name) = PeriodicTable.Common[i];
            var cell = BuildCommonElement(symbol, name);
            Grid.SetRow(cell, i / columns);
            Grid.SetColumn(cell, i % columns);
            grid.Children.Add(cell);
        }

        panel.Children.Add(grid);

        // 完整周期表：默认收起，需要时再展开。
        // ⛔ 展开时把「常用元素」整块收起来：两块都摆着，左栏一定比可视区高，
        //    周期表得滚半天才看得见（Nick 2026-10-17）。
        ScrollViewer? scroller = null;

        var toggle = new HyperlinkButton
        {
            Content = "找不到？展开完整元素周期表",
            FontSize = 12,
            Padding = new Thickness(0, 2, 0, 2),
        };
        toggle.Click += (_, _) =>
        {
            if (_fullTableWrap.Visibility == Visibility.Visible)
            {
                _fullTableWrap.Visibility = Visibility.Collapsed;
                grid.Visibility = Visibility.Visible;
                sectionTitle.Text = "常用元素";
                toggle.Content = "找不到？展开完整元素周期表";
                return;
            }

            if (_fullTableHost.Children.Count == 0)
            {
                var width = scroller is { ActualWidth: > 0 } s ? s.ActualWidth : 800;
                _fullTableHost.Children.Add(BuildPeriodicTable(width));
            }

            grid.Visibility = Visibility.Collapsed;
            sectionTitle.Text = "元素周期表";
            _fullTableWrap.Visibility = Visibility.Visible;
            toggle.Content = "收起完整元素周期表";
        };

        panel.Children.Add(toggle);
        panel.Children.Add(_fullTableWrap);

        // ⛔ 整条左栏也要能滚：展开完整周期表后，内容一定比可视区高，
        //    没有滚动区就只能被裁掉（Nick 2026-10-17：滑到底还是被截断）。
        scroller = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        return scroller;
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Opacity = 0.55,
    };

    /// <summary>常用元素：符号大、中文名在下，按钮本身就是"看得清、点得中"的尺寸。</summary>
    private Button BuildCommonElement(string symbol, string name)
    {
        var content = new StackPanel { Spacing = 0, HorizontalAlignment = HorizontalAlignment.Center };

        content.Children.Add(new TextBlock
        {
            Text = symbol,
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        content.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 10,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var button = new Button
        {
            Content = content,
            Tag = symbol,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Services.ThemeBrush.Get(null, "ControlFillColorDefaultBrush"),
        };

        ToolTipService.SetToolTip(button, $"{symbol}　{name}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{symbol} {name}");
        AttachPressGradient(button);
        button.Click += (sender, _) => Append(((Button)sender).Tag as string ?? symbol);

        return button;
    }

    /// <summary>
    /// 完整周期表（18 族 × 7 周期 + 镧系锕系两行）。
    ///
    /// <para>
    /// 格子尺寸按左栏的实际宽度算：窗口窄的时候缩到 30，宽的时候给到 44，
    /// 这样绝大多数情况下 18 列正好摆下、不用横向拖。
    /// </para>
    /// </summary>
    private FrameworkElement BuildPeriodicTable(double availableWidth)
    {
        // 18 列 + 17 个 2px 间隙。⛔ 上限别贴着算：展开表格后纵向滚动条会占掉十几像素，
        // 按满宽算会让第 18 族（He/Ne/Ar…）正好被裁掉一截（Nick 2026-10-17 实测）。
        var cellWidth = Math.Clamp((availableWidth - 48) / 18, 30, 42);
        var cellHeight = Math.Round(cellWidth * 1.19);

        var grid = new Grid { ColumnSpacing = 2, RowSpacing = 2, HorizontalAlignment = HorizontalAlignment.Left };

        for (var c = 0; c < 18; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cellWidth) });

        for (var r = 0; r < 10; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(cellHeight) });

        foreach (var element in PeriodicTable.All)
        {
            var cell = BuildElementCell(element, cellWidth, cellHeight);
            Grid.SetRow(cell, element.Row - 1);
            Grid.SetColumn(cell, element.Column - 1);
            grid.Children.Add(cell);
        }

        grid.Children.Add(GroupLabel("镧系", 8));
        grid.Children.Add(GroupLabel("锕系", 9));

        // ⛔ 只给横向滚动：竖向交给外层那条滚动区，
        //    两层都能竖向滚的话滚轮会互相抢，手感很怪。
        return new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    private static TextBlock GroupLabel(string text, int row)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 9,
            Opacity = 0.4,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetRow(label, row - 1);
        Grid.SetColumn(label, 0);
        Grid.SetColumnSpan(label, 2);
        return label;
    }

    private Button BuildElementCell(Element element, double cellWidth, double cellHeight)
    {
        var name = PeriodicTable.ChineseName(element.Number);

        // 三行各占各的位置（⛔ 不能都叠在中间：符号和中文名会糊成一团）：
        // 上面留原子序数，中间符号，下面中文名。
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Round(cellHeight * 0.21)) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Round(cellHeight * 0.42)) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var number = new TextBlock
        {
            Text = element.Number.ToString(),
            FontSize = 7,
            Opacity = 0.38,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(4, 1, 0, 0),
        };
        content.Children.Add(number);

        var symbol = new TextBlock
        {
            Text = element.Symbol,
            FontSize = Math.Max(11, Math.Round(cellWidth * 0.31)),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(symbol, 1);
        content.Children.Add(symbol);

        var chinese = new TextBlock
        {
            Text = name,
            FontSize = Math.Max(8.5, Math.Round(cellWidth * 0.23)),
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(chinese, 2);
        content.Children.Add(chinese);

        var button = new Button
        {
            Content = content,
            Tag = element.Symbol,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            Width = cellWidth,
            Height = cellHeight,
            CornerRadius = new CornerRadius(4),
            Background = Services.ThemeBrush.Get(null, "ControlFillColorDefaultBrush"),
        };

        ToolTipService.SetToolTip(button,
            name.Length > 0 ? $"{element.Symbol}　{name}　原子序数 {element.Number}"
                            : $"{element.Symbol}　原子序数 {element.Number}");

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,
            name.Length > 0 ? $"{element.Symbol} {name}" : element.Symbol);

        AttachPressGradient(button);
        button.Click += (sender, _) => Append(((Button)sender).Tag as string ?? element.Symbol);

        return button;
    }

    // ── 右：括号 / 数字 + 常用物质 / 离子根 ──
    private FrameworkElement BuildRightPanel()
    {
        // ⛔ 整条右栏都进滚动区：窗口矮的时候 数字键 / 常用物质 / 离子根 必须都够得着，
        //    固定住键盘只让下面滚，离子根必然被截断（Nick 两次点名）。
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(BuildKeypad());
        content.Children.Add(BuildFavoriteSection("常用物质", false));
        content.Children.Add(BuildFavoriteSection("常用离子根", true));

        return new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
    }

    private FrameworkElement BuildFavoriteSection(string title, bool isIons)
    {
        var section = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        var body = new StackPanel();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var edit = new Button
        {
            Content = "编辑",
            FontSize = 11.5,
            Padding = new Thickness(10, 3, 10, 3),
            MinWidth = 0,
            MinHeight = 0,
        };
        Grid.SetColumn(edit, 1);
        header.Children.Add(edit);

        void ShowChips()
        {
            body.Children.Clear();

            var items = isIons ? _ions : _substances;
            if (items.Count == 0)
            {
                body.Children.Add(new TextBlock { Text = "（空）点「编辑」添加", FontSize = 11.5, Opacity = 0.4 });
                return;
            }

            body.Children.Add(BuildChips(items));
        }

        edit.Click += (_, _) =>
        {
            body.Children.Clear();

            var box = new TextBox
            {
                Text = string.Join("\n", isIons ? _ions : _substances),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Height = isIons ? 120 : 160,
                PlaceholderText = "一行一个",
            };
            body.Children.Add(box);

            body.Children.Add(new TextBlock
            {
                Text = isIons
                    ? "一行一个。下标直接写数字，电荷写在 ^ 之后，例如 SO4^2-、NH4^+。"
                    : "一行一个。下标直接写数字，例如 H2O、Ca(OH)2。",
                FontSize = 11,
                Opacity = 0.45,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 6, 0, 0),
            };

            var save = new Button { Content = "保存", FontSize = 12, Padding = new Thickness(12, 4, 12, 4), MinWidth = 0 };
            save.Click += (_, _) =>
            {
                var list = box.Text
                    .Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .Take(40)
                    .ToList();

                if (isIons) _ions = list; else _substances = list;
                ChemicalFavorites.Save(_substances, _ions);
                ShowChips();
            };
            actions.Children.Add(save);

            var reset = new Button { Content = "恢复默认", FontSize = 12, Padding = new Thickness(12, 4, 12, 4), MinWidth = 0 };
            reset.Click += (_, _) =>
            {
                if (isIons) _ions = ChemicalFavorites.DefaultIons.ToList();
                else _substances = ChemicalFavorites.DefaultSubstances.ToList();

                ChemicalFavorites.Save(_substances, _ions);
                ShowChips();
            };
            actions.Children.Add(reset);

            var cancel = new Button { Content = "取消", FontSize = 12, Padding = new Thickness(12, 4, 12, 4), MinWidth = 0 };
            cancel.Click += (_, _) => ShowChips();
            actions.Children.Add(cancel);

            body.Children.Add(actions);
        };

        section.Children.Add(header);
        section.Children.Add(body);
        ShowChips();

        return section;
    }

    /// <summary>常用项：两列，按钮 40 高 —— 鼠标好点、投影上也看得清。</summary>
    private FrameworkElement BuildChips(IReadOnlyList<string> items)
    {
        var stack = new StackPanel { Spacing = 5 };

        for (var i = 0; i < items.Count; i += 2)
        {
            var row = new Grid { ColumnSpacing = 5 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (var c = 0; c < 2 && i + c < items.Count; c++)
            {
                var text = items[i + c];

                // ⛔ 按钮上不能出 "SO4^2-" 这种计算机写法：照化学式排成 SO₄²⁻（Nick 2026-10-17）
                var chip = new Button
                {
                    Content = ChemicalNotation.Formula(text, 14, _brush),
                    Tag = text,
                    Padding = new Thickness(2),
                    MinWidth = 0,
                    MinHeight = 0,
                    Height = 40,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                };

                ToolTipService.SetToolTip(chip, $"追加 {ChemicalNotation.Plain(text)}");

                // 内容是排版出来的控件树，读屏/自动化拿不到文字 ⇒ 单独补一个名字
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                    chip, ChemicalNotation.Plain(text));

                AttachPressGradient(chip);
                chip.Click += (sender, _) => Append(((Button)sender).Tag as string ?? text);

                Grid.SetColumn(chip, c);
                row.Children.Add(chip);
            }

            stack.Children.Add(row);
        }

        return stack;
    }

    // ── 括号 / 结晶水 / 数字 ──
    private FrameworkElement BuildKeypad()
    {
        var stack = new StackPanel { Spacing = 5 };

        var symbols = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        symbols.Children.Add(Key("(", "左括号", width: 60));
        symbols.Children.Add(Key(")", "右括号", width: 60));
        symbols.Children.Add(Key("·", "结晶水（如 CuSO4·5H2O）", width: 60));
        stack.Children.Add(symbols);

        var digits = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        for (var d = 1; d <= 9; d++)
        {
            var text = d.ToString();
            digits.Children.Add(Key(text, "下标", () => Append(text), width: 30));
        }

        digits.Children.Add(Key("0", "下标", () => Append("0"), width: 30));
        stack.Children.Add(digits);

        return stack;
    }

    private Button Key(string text, string tip, Action? onClick = null, double width = 26)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 15,
            Width = width,
            Height = 40,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            CornerRadius = new CornerRadius(4),
        };

        ToolTipService.SetToolTip(button, tip);
        AttachPressGradient(button);
        button.Click += (_, _) =>
        {
            if (onClick is not null) onClick();
            else Append(text);
        };

        return button;
    }

    /// <summary>按下去要有反馈：底色换成**主题色渐变**（与方程编辑器共用 <see cref="ButtonFeel"/>）。</summary>
    private static void AttachPressGradient(Button button) => ButtonFeel.ApplyPressGradient(button);

    private void Append(string text)
    {
        _raw.Append(text);
        Refresh();
    }

    private void Backspace()
    {
        if (_raw.Length > 0) _raw.Remove(_raw.Length - 1, 1);
        Refresh();
    }
}
