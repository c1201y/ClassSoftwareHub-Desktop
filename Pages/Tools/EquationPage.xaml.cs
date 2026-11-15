using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// 方程求解页（第二版）。
///
/// <para>
/// 与化学配平页同一套交互（Nick 2026-10-17）：**一条方程一张卡片**，
/// 点开是按键式编辑器（数字 / 未知数 / 运算 / 函数 / 常用式子），
/// 化学式那样"点出来"而不是"敲出来"；改完自动求解，不需要再按一次求解。
/// </para>
/// <para>求解全在 <see cref="EquationSolver"/>（纯计算，可脱离界面断言）。</para>
/// </summary>
public sealed partial class EquationPage : Page
{
    private readonly List<string> _equations = new();

    private string _lastPlain = "";

    public EquationPage()
    {
        InitializeComponent();
        Refresh();
    }

    private Brush Ink => Services.ThemeBrush.Get(this, "TextFillColorPrimaryBrush");

    // ══════════════════════════════════════════════════════
    //  方程列表
    // ══════════════════════════════════════════════════════

    private void AddEquation_Click(object sender, RoutedEventArgs e) => _ = AddEquationAsync();

    private async Task AddEquationAsync()
    {
        var equation = await EditEquationAsync("");
        if (equation is null || equation.Length == 0) return;

        _equations.Add(equation);
        Refresh();
    }

    private async Task EditEquationAsync(int index)
    {
        if (index < 0 || index >= _equations.Count) return;

        var equation = await EditEquationAsync(_equations[index]);
        if (equation is null) return;

        if (equation.Length == 0) _equations.RemoveAt(index);
        else _equations[index] = equation;

        Refresh();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (_equations.Count == 0) return;

        _equations.Clear();
        Refresh();
    }

    // ── 编辑器浮层（见 XAML 注释为什么不用 ContentDialog） ──

    private EquationEditor? _editor;
    private TaskCompletionSource<string?>? _editorResult;

    /// <summary>弹出方程编辑器。返回 null 表示取消。</summary>
    private Task<string?> EditEquationAsync(string initial)
    {
        _editor = new EquationEditor(initial, Ink);
        _editor.Changed += valid => EditorOk.IsEnabled = valid;
        EditorOk.IsEnabled = _editor.IsValid;

        EditorHost.Child = _editor.View;
        EditorTitle.Text = initial.Length == 0 ? "添加方程" : "修改方程";

        // 尺寸全由页面算；⛔ 不给内容写死宽高，否则内层滚动区的视口会比看得见的地方高（见化学页教训）
        var width = ActualWidth > 0 ? ActualWidth : 1000;
        var height = ActualHeight > 0 ? ActualHeight : 700;

        EditorOverlay.Width = width;
        EditorOverlay.Height = height;

        EditorShell.Width = Math.Min(1000, Math.Max(640, width - 64));
        EditorShell.Height = Math.Min(740, Math.Max(420, height - 16));

        EditorOverlay.Visibility = Visibility.Visible;

        _editorResult = new TaskCompletionSource<string?>();
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

    // ══════════════════════════════════════════════════════
    //  刷新与求解
    // ══════════════════════════════════════════════════════

    private void Refresh()
    {
        RebuildList();

        EquationCount.Text = $"{_equations.Count} 条";
        EmptyHint.Visibility = _equations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearAllButton.Visibility = _equations.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        Solve();
    }

    private void RebuildList()
    {
        EquationList.Children.Clear();

        for (var i = 0; i < _equations.Count; i++)
            EquationList.Children.Add(EquationCard(_equations[i], i));
    }

    /// <summary>一条方程：数学排版（真分数、真上标）+ 删除按钮。点整块可修改。</summary>
    private Border EquationCard(string equation, int index)
    {
        var math = MathText.Render(equation, 20, Ink);
        math.VerticalAlignment = VerticalAlignment.Center;

        // ⛔ 排版结果自带文本选中，会把点击吃掉；卡片上的点击用途是「修改」，
        //    所以让它不参与命中测试 —— 要看、要复制的结果在下面结果区。
        math.IsHitTestVisible = false;

        var host = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Padding = new Thickness(2, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = math,
        };

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
            _equations.RemoveAt(index);
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

        card.Tapped += (_, _) => _ = EditEquationAsync(index);
        ToolTipService.SetToolTip(card, "单击可修改这条方程");
        return card;
    }

    private void Solve()
    {
        if (_equations.Count == 0)
        {
            ShowHint("添加方程后自动求解。");
            return;
        }

        EquationSolver.Result result;
        try
        {
            result = EquationSolver.Solve(string.Join("\n", _equations));
        }
        catch (Exception ex)
        {
            AppLog.Exception("equation", ex, "求解方程");
            ShowHint("求解过程中出现异常，请检查式子的写法。");
            return;
        }

        if (!result.Ok)
        {
            ResultPanel.Children.Clear();
            ResultPanel.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Warning,
                Title = "无法求解",
                Message = result.Error!,
                IsOpen = true,
                IsClosable = false,
            });
            _lastPlain = "";
            return;
        }

        Render(result);
    }

    private void ShowHint(string text)
    {
        HintText.Text = text;
        ResultPanel.Children.Clear();
        ResultPanel.Children.Add(HintText);
        _lastPlain = "";
    }

    private void Render(EquationSolver.Result result)
    {
        ResultPanel.Children.Clear();

        ResultPanel.Children.Add(new TextBlock
        {
            Text = result.Kind,
            FontSize = 12.5,
            Opacity = 0.5,
        });

        if (result.Solutions.Count == 0)
        {
            ResultPanel.Children.Add(new TextBlock
            {
                Text = "无解",
                FontSize = 24,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
        }
        else
        {
            foreach (var line in result.Solutions)
            {
                var math = MathText.Render(line, 24, Ink);
                math.VerticalAlignment = VerticalAlignment.Center;
                ResultPanel.Children.Add(math);
            }
        }

        if (result.Note is { Length: > 0 } note)
        {
            ResultPanel.Children.Add(new TextBlock
            {
                Text = note.Replace("**", ""),
                FontSize = 12.5,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        _lastPlain = result.Solutions.Count > 0
            ? string.Join("\n", result.Solutions)
            : "无解";

        var copy = new Button { Content = "复制结果", Padding = new Thickness(10, 4, 10, 4) };
        copy.Click += (_, _) => Copy(copy);
        ResultPanel.Children.Add(copy);
    }

    private void Copy(Button source)
    {
        if (_lastPlain.Length == 0) return;

        try
        {
            var package = new DataPackage();
            package.SetText(_lastPlain);
            Clipboard.SetContent(package);

            source.Content = "已复制";
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(1600);
                source.Content = "复制结果";
            });
        }
        catch (Exception ex)
        {
            AppLog.Exception("equation", ex, "复制结果");
        }
    }

    // ══════════════════════════════════════════════════════
    //  示例
    // ══════════════════════════════════════════════════════

    private void ExampleQuadratic_Click(object sender, RoutedEventArgs e) =>
        LoadExample("x^2 - 5x + 6 = 0");

    private void ExampleLinear_Click(object sender, RoutedEventArgs e) =>
        LoadExample("2x + 3y = 8", "3x - y = 1");

    private void ExampleTrig_Click(object sender, RoutedEventArgs e) =>
        LoadExample("sin(x) = 0.5");

    private void ExampleExp_Click(object sender, RoutedEventArgs e) =>
        LoadExample("2^x = 8");

    private void LoadExample(params string[] equations)
    {
        _equations.Clear();
        _equations.AddRange(equations.Where(e => e.Length > 0));
        Refresh();
    }
}
