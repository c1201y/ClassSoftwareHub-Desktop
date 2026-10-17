using System;
using System.Collections.Generic;
using System.Linq;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// 化学方程式配平（第二版）。
///
/// <para>
/// ⛔ 交互原则（Nick 2026-10-17）：**不让用户手打整条方程式**。
/// 左边一行行加反应物、右边一行行加生成物，化学式从元素周期表点出来，
/// 反应条件从下拉多选 —— 用户只需要知道"有哪些物质"，不用关心
/// 哪里要打括号、下标怎么敲、箭头怎么画。
/// </para>
/// <para>判定逻辑仍然全在 <see cref="ChemistryBalancer"/>（纯计算，可脱离界面断言）。</para>
/// </summary>
public sealed partial class ChemistryPage : Page
{
    /// <summary>可选的反应条件。⚠️ 只是书写样式，不参与配平计算。</summary>
    private static readonly string[] ConditionNames =
        { "点燃", "加热", "高温", "催化剂", "通电", "光照", "加压" };

    private readonly List<string> _reactants = new();
    private readonly List<string> _products = new();
    private readonly HashSet<string> _conditions = new(StringComparer.Ordinal);
    private readonly List<CheckBox> _conditionChecks = new();

    public ChemistryPage()
    {
        InitializeComponent();
        BuildConditionFlyout();
        Refresh();
    }

    private Brush Ink => Services.ThemeBrush.Get(this, "TextFillColorPrimaryBrush");

    // ══════════════════════════════════════════════════════
    //  物质列表
    // ══════════════════════════════════════════════════════

    private void AddReactant_Click(object sender, RoutedEventArgs e) => _ = AddSpeciesAsync(true);

    private void AddProduct_Click(object sender, RoutedEventArgs e) => _ = AddSpeciesAsync(false);

    private async System.Threading.Tasks.Task AddSpeciesAsync(bool isReactant)
    {
        var formula = await EditFormulaAsync("");
        if (formula is null || formula.Length == 0) return;

        (isReactant ? _reactants : _products).Add(formula);
        Refresh();
    }

    private async System.Threading.Tasks.Task EditSpeciesAsync(bool isReactant, int index)
    {
        var list = isReactant ? _reactants : _products;
        if (index < 0 || index >= list.Count) return;

        var formula = await EditFormulaAsync(list[index]);
        if (formula is null) return;

        if (formula.Length == 0) list.RemoveAt(index);
        else list[index] = formula;

        Refresh();
    }

    // ── 化学式编辑器（页面内浮层，见 XAML 注释为什么不用 ContentDialog） ──

    private FormulaEditor? _editor;
    private System.Threading.Tasks.TaskCompletionSource<string?>? _editorResult;

    /// <summary>弹出化学式编辑器。返回 null 表示用户取消。</summary>
    private System.Threading.Tasks.Task<string?> EditFormulaAsync(string initial)
    {
        _editor = new FormulaEditor(initial, Ink);
        _editor.Changed += valid => EditorOk.IsEnabled = valid;
        EditorOk.IsEnabled = _editor.IsValid;

        EditorHost.Child = _editor.View;
        EditorTitle.Text = initial.Length == 0 ? "添加物质" : "修改物质";

        // 尺寸全由页面算：留一圈边距，窗口再小也不会被切
        var width = ActualWidth > 0 ? ActualWidth : 1000;
        var height = ActualHeight > 0 ? ActualHeight : 700;

        // Canvas 里的元素不吃布局，尺寸全靠这里给
        EditorOverlay.Width = width;
        EditorOverlay.Height = height;

        // 周期表 18 列，每格放大到 38 还要带中文名，右栏还要放常用项 ⇒ 默认给到 1200
        EditorShell.Width = Math.Min(1200, Math.Max(680, width - 48));
        EditorShell.Height = Math.Min(760, Math.Max(440, height - 56));

        // ⛔ 别给 View 写死宽高：它比外壳的可用高度高一点点时，
        //    内层滚动区的视口就比看得见的地方高，滚到底也有一截永远露不出来
        //    （Nick 2026-10-17：离子根滑到下面还是被截断）。交给布局自己撑满。

        EditorOverlay.Visibility = Visibility.Visible;

        _editorResult = new System.Threading.Tasks.TaskCompletionSource<string?>();
        return _editorResult.Task;
    }

    private void EditorOk_Click(object sender, RoutedEventArgs e) => CloseEditor(_editor?.Value);

    private void EditorCancel_Click(object sender, RoutedEventArgs e) => CloseEditor(null);

    private void EditorOverlay_Tapped(object sender, TappedRoutedEventArgs e) => CloseEditor(null);

    private void CloseEditor(string? value)
    {
        EditorOverlay.Visibility = Visibility.Collapsed;
        EditorHost.Child = null;
        _editor = null;

        var result = _editorResult;
        _editorResult = null;
        result?.TrySetResult(value);
    }

    private void Refresh()
    {
        RebuildList(ReactantList, _reactants, true);
        RebuildList(ProductList, _products, false);
        UpdateConditionLabel();

        ReactantCount.Text = $"{_reactants.Count} 种";
        ProductCount.Text = $"{_products.Count} 种";
        ReactantEmpty.Visibility = _reactants.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProductEmpty.Visibility = _products.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        Balance();
    }

    private void RebuildList(StackPanel host, List<string> list, bool isReactant)
    {
        host.Children.Clear();

        for (var i = 0; i < list.Count; i++)
            host.Children.Add(SpeciesCard(list[i], isReactant, i));
    }

    /// <summary>一种物质：化学式（真下标）+ 删除按钮。单击化学式可修改。</summary>
    private Border SpeciesCard(string formula, bool isReactant, int index)
    {
        var host = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Padding = new Thickness(2, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = ChemicalNotation.Formula(formula, 17, Ink),
        };
        host.Tapped += (_, _) => _ = EditSpeciesAsync(isReactant, index);

        var remove = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 9 },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(7, 3, 7, 3),
            MinWidth = 0,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        remove.Click += (_, _) =>
        {
            (isReactant ? _reactants : _products).RemoveAt(index);
            Refresh();
        };

        var grid = new Grid { ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(host);
        Grid.SetColumn(remove, 1);
        grid.Children.Add(remove);

        var card = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 6, 6),
            Background = Services.ThemeBrush.Get(this, "ControlFillColorSecondaryBrush"),
            Child = grid,
        };

        ToolTipService.SetToolTip(card, "单击化学式可修改");
        return card;
    }

    // ══════════════════════════════════════════════════════
    //  反应条件（多选）
    // ══════════════════════════════════════════════════════

    private void BuildConditionFlyout()
    {
        var panel = new StackPanel { Spacing = 2, MinWidth = 150 };

        foreach (var name in ConditionNames)
        {
            var check = new CheckBox { Content = name, MinWidth = 0 };
            check.Checked += (_, _) => { _conditions.Add(name); Refresh(); };
            check.Unchecked += (_, _) => { _conditions.Remove(name); Refresh(); };

            _conditionChecks.Add(check);
            panel.Children.Add(check);
        }

        ConditionButton.Flyout = new Flyout { Content = panel };
    }

    private void UpdateConditionLabel()
    {
        ConditionButton.Content = _conditions.Count == 0
            ? "反应条件"
            : string.Join("、", ConditionNames.Where(_conditions.Contains));
    }

    // ══════════════════════════════════════════════════════
    //  配平与结果
    // ══════════════════════════════════════════════════════

    private void Balance()
    {
        ResultPanel.Children.Clear();

        if (_reactants.Count == 0 || _products.Count == 0)
        {
            HintText.Text = _reactants.Count == 0
                ? "先在左边添加反应物。"
                : "再在右边添加生成物。";
            ResultPanel.Children.Add(HintText);
            return;
        }

        var input = string.Join(" + ", _reactants) + " = " + string.Join(" + ", _products);

        ChemistryBalancer.Result result;
        try
        {
            result = ChemistryBalancer.Balance(input);
        }
        catch (Exception ex)
        {
            AppLog.Exception("chem", ex, "配平方程");
            ShowError("配平过程中出现异常，请检查物质的写法。");
            return;
        }

        if (!result.Ok)
        {
            ShowError(result.Error!);
            return;
        }

        Render(result);
    }

    private void ShowError(string message)
    {
        ResultPanel.Children.Clear();
        ResultPanel.Children.Add(new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            Title = "无法配平",
            Message = message,
            IsOpen = true,
            IsClosable = false,
        });
    }

    private void Render(ChemistryBalancer.Result result)
    {
        var left = result.Terms.Take(_reactants.Count).ToList();
        var right = result.Terms.Skip(_reactants.Count).ToList();

        var conditions = ConditionNames.Where(_conditions.Contains).ToList();

        ResultPanel.Children.Add(ChemicalNotation.Equation(left, right, conditions, 26, Ink));

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var copy = new Button { Content = "复制方程式", Padding = new Thickness(10, 4, 10, 4), FontSize = 12.5 };
        copy.Click += (_, _) => Copy(result.Equation, copy);
        actions.Children.Add(copy);

        actions.Children.Add(new TextBlock
        {
            Text = $"{result.Terms.Count} 种物质",
            FontSize = 12,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
        });

        ResultPanel.Children.Add(actions);

        if (result.Note is { Length: > 0 } note)
        {
            ResultPanel.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Informational,
                Message = note,
                IsOpen = true,
                IsClosable = false,
            });
        }

        // ── 元素守恒核对 ──
        ResultPanel.Children.Add(new TextBlock
        {
            Text = "元素守恒核对",
            FontSize = 13,
            Opacity = 0.55,
            Margin = new Thickness(0, 6, 0, 0),
        });

        var header = NewCheckGrid();
        header.Children.Add(Head("元素", 0));
        header.Children.Add(Head("左边", 1));
        header.Children.Add(Head("右边", 2));
        ResultPanel.Children.Add(header);

        foreach (var (element, leftCount, rightCount) in result.ElementCheck)
        {
            var row = NewCheckGrid();
            row.Padding = new Thickness(0, 5, 0, 5);

            row.Children.Add(new TextBlock
            {
                Text = element,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            row.Children.Add(Cell(leftCount.ToString(), 1));
            row.Children.Add(Cell(rightCount.ToString(), 2));

            var mark = new TextBlock
            {
                Text = leftCount == rightCount ? "✓" : "✗",
                FontSize = 13,
                Opacity = leftCount == rightCount ? 0.5 : 1,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(mark, 3);
            row.Children.Add(mark);

            ResultPanel.Children.Add(row);
        }
    }

    /// <summary>核对表的一行：元素 / 左边 / 右边 / 打勾，四列宽度固定，行与行才对得齐。</summary>
    private static Grid NewCheckGrid()
    {
        var grid = new Grid { ColumnSpacing = 18 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return grid;
    }

    private static TextBlock Head(string text, int column = 0)
    {
        var block = new TextBlock { Text = text, FontSize = 12, Opacity = 0.45 };
        Grid.SetColumn(block, column);
        return block;
    }

    private static TextBlock Cell(string text, int column)
    {
        var block = new TextBlock { Text = text, FontSize = 13 };
        Grid.SetColumn(block, column);
        return block;
    }

    // ══════════════════════════════════════════════════════
    //  其它
    // ══════════════════════════════════════════════════════

    private void Example_Click(object sender, RoutedEventArgs e)
    {
        _reactants.Clear();
        _products.Clear();
        _conditions.Clear();

        _reactants.Add("Fe");
        _reactants.Add("O2");
        _products.Add("Fe2O3");

        _conditions.Add("点燃");
        foreach (var check in _conditionChecks)
            check.IsChecked = (check.Content as string) == "点燃";

        Refresh();
    }

    private void Copy(string text, Button source)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);

            source.Content = "已复制";
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                await System.Threading.Tasks.Task.Delay(1600);
                source.Content = "复制方程式";
            });
        }
        catch (Exception ex)
        {
            AppLog.Exception("chem", ex, "复制方程式");
        }
    }
}
