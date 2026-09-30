using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;   // FlyoutPlacementMode
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.UI;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// 随机抽号（对齐网页版 tools/PickNumberTool.vue）：
/// 加密随机抽号（Fisher–Yates）+ 滚动动画 + 不重复记录（本地存档）+ 公平性自检（2 万次直方图 + 卡方）+ 随机分组。
/// 2026-09-27（Nick）：抽取对象可以是号码范围，也可以是导入的班级名单（Excel / txt）。
/// 2026-09-29（Nick）：名单要能预览、能就地改名、能导出回 Excel / txt —— 见 Views\RosterEditorDialog。
/// </summary>
public sealed partial class PickNumberToolPage : Page
{
    private const int RollTicks = 16;
    private const int FairTotal = 20000;

    private static readonly string[] GroupModeItems = { "按组数（分成 N 组）", "按人数（每组 N 人）" };

    private readonly DispatcherQueueTimer _rollTimer;
    private readonly List<int> _used = new();
    private List<int> _pending = new();
    private int _ticks;
    private bool _ready;

    // ── 名单模式（2026-09-27 Nick 提）──
    private readonly List<string> _roster = new();
    private readonly List<string> _usedNames = new();
    private List<string> _pendingNames = new();
    private string _rosterSource = "";   // 导入时的文件名，只用来显示
    private int _savedMode;              // 从存档读出来的模式，铺完控件才生效
    private string _groupText = "";      // 最近一次分组结果的纯文本（给「复制结果」用）

    public PickNumberToolPage()
    {
        InitializeComponent();

        _rollTimer = DispatcherQueue.CreateTimer();
        _rollTimer.Interval = TimeSpan.FromMilliseconds(65);
        _rollTimer.IsRepeating = true;
        _rollTimer.Tick += (_, _) => RollTick();

        // 离开页面把抽号滚动停掉
        Unloaded += (_, _) => _rollTimer.Stop();

        foreach (var item in GroupModeItems) GroupModeBox.Items.Add(item);
        GroupModeBox.SelectedIndex = 0;

        LoadConfig();

        // 抽取对象：两条 Checked 已经挂在 XAML 上（PoolMode_Changed），这里只按存档恢复选中。
        // ⚠️ IsChecked 不能在 XAML 里写 True —— 见 App.xaml 里 CshModeCardStyle 的注释。
        if (_savedMode == 1) ModeRosterRadio.IsChecked = true;
        else ModeRangeRadio.IsChecked = true;

        FromBox.ValueChanged += (_, _) => OnSettingChanged();
        ToBox.ValueChanged += (_, _) => OnSettingChanged();
        CountBox.ValueChanged += (_, _) => OnSettingChanged();
        NoRepeatBox.Checked += (_, _) => OnSettingChanged();
        NoRepeatBox.Unchecked += (_, _) => OnSettingChanged();

        _ready = true;
        SyncPoolMode();
        RefreshHints();

        // 功能：抽号 / 随机分组 —— 一次只面对一件事（两件事共用上面的抽取对象设置）
        TaskDrawRadio.IsChecked = true;
        SyncTask();

        // 分组方式一换，「组数」这个标题就得跟着换（按人数时它其实叫「每组人数」）
        GroupModeBox.SelectionChanged += (_, _) => SyncGroupModeHeader();
        SyncGroupModeHeader();
    }

    /// <summary>「按组数」/「按人数」切换时，同步旁边那个数字框的标题与提示。</summary>
    private void SyncGroupModeHeader()
    {
        GroupValueBox.Header = GroupModeBox.SelectedIndex <= 0 ? "组数" : "每组人数";
        RefreshGroupRangeHint();
    }

    /// <summary>分组页那一行小字：说清"要分的到底是哪个池子、有多少人/号"。</summary>
    private void RefreshGroupRangeHint()
    {
        if (UseRoster)
        {
            var total = _roster.Count;
            GroupRangeHint.Text = total > 0
                ? $"抽取池：导入的名单，共 {total} 人。"
                : "抽取池：尚未导入名单，请在上方导入。";
            return;
        }

        GroupRangeHint.Text = PoolSize > 0
            ? $"抽取池：{Lo} ~ {Hi}，共 {PoolSize} 个号码。"
            : "抽取池：请先在上方填写有效的号码范围。";
    }

    /// <summary>把这个工具丢到工具浮窗里跑（浮窗和这一页共用同一个抽号存档）。</summary>
    private void OpenPalette_Click(object sender, RoutedEventArgs e)
        => Views.ToolPaletteWindow.ShowTool("pick-number");

    // ══════════ 功能切换：抽号 / 随机分组 ══════════
    private void TaskMode_Changed(object sender, RoutedEventArgs e) => SyncTask();

    /// <summary>按「功能」卡片切左右两块视图。两块共用上面的「抽取对象」设置。</summary>
    private void SyncTask()
    {
        var draw = TaskDrawRadio.IsChecked == true;
        DrawView.Visibility = draw ? Visibility.Visible : Visibility.Collapsed;
        GroupView.Visibility = draw ? Visibility.Collapsed : Visibility.Visible;

        // ⚠️ 只在已经挂进视觉树之后才 ChangeView：构造函数里控件还没进树，
        //    那时调等于"在布局过程中请求滚动"，WinUI 会判成 Layout cycle。
        var view = draw ? DrawView : GroupView;
        if (view.IsLoaded) view.ChangeView(0, 0, null);
    }

    // ══════════ 抽取对象：号码范围 / 名单 ══════════
    private bool UseRoster => ModeRosterRadio.IsChecked == true;

    private void PoolMode_Changed(object sender, RoutedEventArgs e) => OnPoolModeChanged();

    private void OnPoolModeChanged()
    {
        if (!_ready) return;
        SyncPoolMode();
        SaveConfig();
        RefreshHints();
    }

    /// <summary>按当前模式收起 / 放出参数行里的那一组输入控件。</summary>
    private void SyncPoolMode()
    {
        var roster = UseRoster;
        RangePanel.Visibility = roster ? Visibility.Collapsed : Visibility.Visible;
        RosterPanel.Visibility = roster ? Visibility.Visible : Visibility.Collapsed;

        // 2026-09-29：勾选框挤在控制行里，文案只能短 —— "抽过的名字不再出现"这种补充说明挪到悬停提示
        ToolTipService.SetToolTip(NoRepeatBox, roster ? "抽过的名字不再出现" : "抽过的不再出现");
        RefreshRosterText();
    }

    /// <summary>名单相关按钮的可用性（名单为空时"查看 / 编辑""清空""导出"都没意义）。</summary>
    private void RefreshRosterText()
    {
        var has = _roster.Count > 0;
        ClearRosterButton.IsEnabled = has;
        OpenRosterButton.IsEnabled = has;
    }

    /// <summary>
    /// 打开「查看 / 编辑名单」弹窗，回来时把改动收下。
    ///
    /// 弹窗里是"当场改、当场删"的语义，所以这里只做两件事：把结果写回抽取池、把界面刷一遍。
    /// ⚠️ 名单变了要顺手清掉「抽号记录」—— 记录里存的是**旧名字**，
    ///    留着会让"不重复"认不出改名后的人（同一个人被再抽一次）。
    /// </summary>
    private async void OpenRoster_Click(object sender, RoutedEventArgs e)
    {
        HideError();
        var dialog = new Views.RosterEditorDialog(_roster, _rosterSource) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();

        var updated = dialog.Result;
        var same = updated.Count == _roster.Count && !updated.Where((n, i) => n != _roster[i]).Any();
        if (same) return;

        _roster.Clear();
        _roster.AddRange(updated);
        _usedNames.Clear();

        SaveConfig();
        RefreshRosterText();
        RefreshHints();
        Toast.Text = $"名单已更新，共 {_roster.Count} 人（抽号记录已重置）。";
    }

    // ══════════ 设置 ══════════
    private int From => double.IsNaN(FromBox.Value) ? 0 : (int)Math.Floor(FromBox.Value);
    private int To => double.IsNaN(ToBox.Value) ? 0 : (int)Math.Floor(ToBox.Value);
    private int Lo => Math.Min(From, To);
    private int Hi => Math.Max(From, To);
    /// <summary>号码范围上限（跟浮窗版一致）。一万个号足够任何班级场景，再大就是误输入了。</summary>
    private const int MaxRange = 9999;

    /// <summary>
    /// 池子大小 —— **已经夹到合法区间**，所以任何调用点都拿不到一个荒谬的数。
    ///
    /// ⚠️ 为什么必须夹：原来这里直接写 `Hi - Lo + 1`。
    ///   · 极端输入下（如 To = 2000000000）这个减法会在 int 上溢出变负；
    ///   · 老师在数字框里粘一个 100000000，`Enumerable.Range(Lo, size).ToList()` 会立刻申请
    ///     数亿个 int（几 GB）→ 先是界面冻死，再大一点直接 OutOfMemoryException。
    /// 这里用 long 算再夹到 MaxRange，从根上保证"绝不分配巨型列表"。
    /// 至于"要告诉老师范围填太大了"，由 <see cref="TryUseRange"/> 负责，不靠这里静默夹取。
    /// </summary>
    private int PoolSize
    {
        get
        {
            var raw = (long)Hi - Lo + 1;
            if (raw <= 0) return 0;
            return (int)Math.Min(raw, MaxRange);
        }
    }

    /// <summary>范围是否合法可用；不合法时给出能直接显示给老师的原因。</summary>
    private bool TryUseRange(out int size, out string error)
    {
        var raw = (long)Hi - Lo + 1;
        if (raw <= 1)
        {
            size = 0;
            error = "请填写有效的号码范围（如 1 ~ 50）";
            return false;
        }

        if (raw > MaxRange)
        {
            size = 0;
            error = $"号码范围过大（{raw:N0} 个），本工具上限为 {MaxRange}。请检查起止数字是否输入有误。";
            return false;
        }

        size = (int)raw;
        error = "";
        return true;
    }

    private int WantCount => double.IsNaN(CountBox.Value) ? 1 : Math.Max(1, (int)Math.Floor(CountBox.Value));
    private bool NoRepeat => NoRepeatBox.IsChecked == true;

    private void OnSettingChanged()
    {
        if (!_ready) return;
        SaveConfig();
        RefreshHints();
    }

    private int UsedInRange => _used.Where(n => n >= Lo && n <= Hi).Distinct().Count();

    private void RefreshHints()
    {
        if (UseRoster) { RefreshRosterHints(); return; }

        SummaryText.Text = $"本次设置：从 {Lo} ~ {Hi} 中抽取 {WantCount} 个号" + (NoRepeat ? "，抽过的不再出现" : "");

        // 参数行右侧那句状态 —— 就在起止框旁边，不用去别处找
        PoolStatusText.Text = PoolSize > 0 ? PoolStatus(PoolSize, UsedInRange, "个号") : "请填写有效的号码范围（如 1 ~ 50）";

        UsedRow.Visibility = _used.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UsedText.Text = _used.Count > 0 ? $"已抽取 {_used.Count} 个：{Head(_used.Select(n => n.ToString()))}" : "";
        RefreshGroupRangeHint();
    }

    private void RefreshRosterHints()
    {
        var total = _roster.Count;
        var used = _usedNames.Count;

        SummaryText.Text = $"本次设置：从名单中抽取 {WantCount} 人" + (NoRepeat ? "，抽过的不再出现" : "");
        PoolStatusText.Text = total == 0
            ? "名单是空的 · 可先单击「示例名单」看一份现成的"
            : PoolStatus(total, used, "人");

        UsedRow.Visibility = used > 0 ? Visibility.Visible : Visibility.Collapsed;
        UsedText.Text = used > 0 ? $"已抽取 {used} 人：{Head(_usedNames)}" : "";
        RefreshGroupRangeHint();
    }

    /// <summary>
    /// 参数行右侧那句状态：池子多大、抽掉多少、还剩多少。
    /// 抽完时直接把"怎么重来"写在这儿 —— 用户看到"剩 0 个"的第一反应就是找按钮。
    /// </summary>
    private string PoolStatus(int size, int used, string unit)
    {
        if (!NoRepeat) return $"共 {size} {unit}";
        var remain = Math.Max(0, size - used);
        return remain == 0
            ? $"共 {size} {unit} · 已抽完，如需重新抽取请单击「重置记录」"
            : $"共 {size} {unit} · 已抽 {used} · 剩 {remain}";
    }

    /// <summary>记录行只列前 12 个，剩下的用省略号 —— 五十几个名字铺满两行反而看不清。</summary>
    private static string Head(IEnumerable<string> items)
    {
        var list = items.ToList();
        var shown = string.Join("、", list.Take(12));
        return list.Count > 12 ? shown + $" 等 {list.Count} 个" : shown;
    }

    // ══════════ 抽号 ══════════
    private static int RandInt(int n) => n <= 1 ? 0 : RandomNumberGenerator.GetInt32(n);

    private List<int>? PickOnce(int k)
    {
        var size = PoolSize;
        if (size <= 0 || k > size) return null;

        List<int> available;
        if (NoRepeat)
        {
            var usedSet = new HashSet<int>(_used);
            available = Enumerable.Range(Lo, size).Where(n => !usedSet.Contains(n)).ToList();
            if (available.Count < k) return null;
        }
        else
        {
            available = Enumerable.Range(Lo, size).ToList();
        }

        for (var i = available.Count - 1; i > 0; i--)
        {
            var j = RandInt(i + 1);
            (available[i], available[j]) = (available[j], available[i]);
        }
        return available.Take(k).ToList();
    }

    private void Draw_Click(object sender, RoutedEventArgs e)
    {
        HideError();

        if (UseRoster) { DrawFromRoster(); return; }

        var k = WantCount;
        if (!TryUseRange(out var size, out var rangeError)) { ShowError(rangeError); return; }
        if (k > size) { ShowError($"单次最多抽取 {size} 个号"); return; }
        if (NoRepeat && UsedInRange + k > size)
        {
            ShowError("范围内号码不足，请单击「重置记录」后重新抽取");
            return;
        }

        var final = PickOnce(k);
        if (final is null) { ShowError("抽号失败，请检查范围与数量"); return; }

        _pending = final;
        _ticks = 0;
        DrawButton.IsEnabled = false;
        DrawButton.Content = "抽号中";
        _rollTimer.Start();
    }

    private void DrawFromRoster()
    {
        var k = WantCount;
        var total = _roster.Count;
        if (total == 0) { ShowError("名单是空的，请单击上方的「导入名单」；不清楚格式可先单击「示例名单」。"); return; }
        if (k > total) { ShowError($"名单共 {total} 人，单次最多抽取 {total} 个"); return; }

        List<string> available;
        if (NoRepeat)
        {
            var usedSet = new HashSet<string>(_usedNames, StringComparer.OrdinalIgnoreCase);
            available = _roster.Where(n => !usedSet.Contains(n)).ToList();
            if (available.Count < k)
            {
                ShowError("名单中未抽取的人数不足，请单击「重置记录」后重新抽取");
                return;
            }
        }
        else
        {
            available = _roster.ToList();
        }

        Shuffle(available);
        _pendingNames = available.Take(k).ToList();
        _ticks = 0;
        DrawButton.IsEnabled = false;
        DrawButton.Content = "抽号中";
        _rollTimer.Start();
    }

    /// <summary>Fisher–Yates（和号码模式同一套洗牌，保证公平性一致）。</summary>
    private static void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = RandInt(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private void RollTick()
    {
        _ticks++;
        var roster = UseRoster;

        if (_ticks >= RollTicks)
        {
            _rollTimer.Stop();
            DrawButton.IsEnabled = true;
            DrawButton.Content = "开始抽号";

            if (roster) ShowNames(_pendingNames, rolling: false);
            else ShowResult(_pending, rolling: false);

            if (NoRepeat)
            {
                if (roster) _usedNames.AddRange(_pendingNames);
                else _used.AddRange(_pending);
                SaveConfig();
                RefreshHints();
            }
            return;
        }

        // 滚动中：号码模式刷随机号，名单模式刷随机名字
        if (roster)
        {
            var total = _roster.Count;
            ShowNames(Enumerable.Range(0, _pendingNames.Count).Select(_ => _roster[RandInt(total)]).ToList(), rolling: true);
        }
        else
        {
            ShowResult(Enumerable.Range(0, _pending.Count).Select(_ => Lo + RandInt(PoolSize)).ToList(), rolling: true);
        }
    }

    private void ShowResult(IReadOnlyList<int> numbers, bool rolling)
    {
        ResultHost.Children.Clear();
        foreach (var n in numbers)
        {
            ResultHost.Children.Add(new TextBlock
            {
                Text = n.ToString(),
                // 2026-09-29：右栏成了结果专用空间，字号跟着放大一档 —— 抽出来的号是这一页的主角
                FontSize = 52,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = rolling ? 0.72 : 1,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        ResultHint.Visibility = numbers.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>名单模式的结果：名字比号码长，字号收一档，太长了就折行。</summary>
    private void ShowNames(IReadOnlyList<string> names, bool rolling)
    {
        ResultHost.Children.Clear();
        foreach (var name in names)
        {
            ResultHost.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 42,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = rolling ? 0.72 : 1,
                MaxWidth = 138,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        ResultHint.Visibility = names.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ResetUsed_Click(object sender, RoutedEventArgs e)
    {
        _used.Clear();
        _usedNames.Clear();
        SaveConfig();
        RefreshHints();
        Toast.Text = "已重置抽号记录";
    }

    // ══════════ 公平性自检 ══════════
    /// <summary>
    /// 在本机实抽 2 万次，画一张分布直方图 + 卡方结论。
    ///
    /// 2026-09-29：从"设置栏里的折叠区"改成"链接 + 浮出面板"。
    /// 折叠区那一版实测会被挤到可视区下面（AutomationId 还在，但高度被裁成 1px）——
    /// 对一个从没看过说明书的用户来说，等于这个功能不存在。浮出面板贴在链接上弹，
    /// 点哪儿看哪儿，也不占设置栏的高度。
    /// </summary>
    private void FairCheck_Click(object sender, RoutedEventArgs e)
    {
        var size = PoolSize;
        if (size <= 1) { Toast.Text = "请先填写有效的号码范围"; return; }

        var buckets = Math.Min(10, size);
        var counts = new int[buckets];
        for (var i = 0; i < FairTotal; i++)
        {
            var v = Lo + RandInt(size);
            var bi = Math.Min(buckets - 1, (int)Math.Floor((v - Lo) / (double)size * buckets));
            counts[bi]++;
        }

        var expect = FairTotal / (double)buckets;
        double chi = 0;
        foreach (var c in counts) chi += Math.Pow(c - expect, 2) / expect;
        var max = counts.Max();

        // 图是每次现搭的：桶数跟着号码范围走，没法预先钉在 XAML 上
        var chart = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
        for (var i = 0; i < buckets; i++)
            chart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var i = 0; i < buckets; i++)
        {
            var start = Lo + (int)Math.Round((double)i * size / buckets);
            var end = Lo + (int)Math.Round((double)(i + 1) * size / buckets) - 1;
            var label = start == end ? start.ToString() : $"{start}-{end}";
            var pct = max > 0 ? counts[i] / (double)max : 0;

            var col = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Bottom };
            col.Children.Add(new Rectangle
            {
                Height = Math.Max(2, pct * 78),
                RadiusX = 3,
                RadiusY = 3,
                Fill = AccentBrush(),
            });
            col.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 10,
                Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            ToolTipService.SetToolTip(col, $"{label}：{counts[i]} 次");
            Grid.SetColumn(col, i);
            chart.Children.Add(col);
        }

        var normal = chi < 27.9;
        var note = new TextBlock
        {
            Text = $"实抽 {FairTotal:N0} 次，分成 {buckets} 格，每格理论约 {Math.Round(expect)} 次；" +
                   $"实际 {counts.Min()} ~ {counts.Max()} 次（卡方 {chi:0.0}，" +
                   (normal ? "分布正常" : "本次分布略有偏差，可再次抽取") + "）",
            FontSize = 12.5,
            Opacity = 0.75,
            MaxWidth = 420,
            TextWrapping = TextWrapping.Wrap,
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "在本机实抽 2 万次，检验号码分布是否均匀。",
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 4),
            Height = 124,
            Background = Res("ControlFillColorDefaultBrush"),
            Child = chart,
        });
        panel.Children.Add(note);

        new Flyout { Content = panel, Placement = FlyoutPlacementMode.Top }.ShowAt(FairLink);
    }

    // ══════════ 随机分组 ══════════
    /// <summary>
    /// 随机分组：号码池与名单池共用上面的抽取池设置。
    ///
    /// ⚠️ 分组**不受**「不重复」记录影响，也不消耗它 —— 每次单击都是对整池独立洗牌，
    ///    所以可以反复分组、没有次数限制。用户最常问的就是"能分几次"，这句话直接写在页面上了。
    /// </summary>
    private void Group_Click(object sender, RoutedEventArgs e)
    {
        Toast.Text = "";
        if (UseRoster) { GroupRoster(); return; }

        if (!TryUseRange(out var size, out var rangeError)) { Toast.Text = rangeError; return; }
        if (size < 2) { Toast.Text = "至少要 2 个号码才能分组。"; return; }

        var byGroups = GroupModeBox.SelectedIndex <= 0;
        var value = GroupValue;
        var nums = Enumerable.Range(Lo, size).ToList();
        Shuffle(nums);

        var groupCount = Math.Max(1, byGroups ? Math.Min(value, size) : (int)Math.Ceiling(size / (double)value));
        var groups = new List<List<string>>();
        for (var i = 0; i < groupCount; i++) groups.Add(new List<string>());
        for (var i = 0; i < nums.Count; i++) groups[i % groupCount].Add(nums[i].ToString());
        // 号是整数，得按数值排（直接按字符串排会把 "10" 排到 "9" 前面）
        foreach (var g in groups) g.Sort((a, b) => int.Parse(a).CompareTo(int.Parse(b)));

        RenderGroups(groups, "个号");
    }

    /// <summary>名单分组：把导入的名字随机分成 N 组（或每组 N 人）。组内按姓名排序，看起来才整齐。</summary>
    private void GroupRoster()
    {
        var total = _roster.Count;
        if (total == 0) { Toast.Text = "尚未导入名单，请单击「导入名单」。"; return; }
        if (total < 2) { Toast.Text = "名单里至少要有 2 个人才能分组。"; return; }

        var byGroups = GroupModeBox.SelectedIndex <= 0;
        var value = GroupValue;
        var names = _roster.ToList();
        Shuffle(names);

        var groupCount = Math.Max(1, byGroups ? Math.Min(value, total) : (int)Math.Ceiling(total / (double)value));

        var groups = new List<List<string>>();
        for (var g = 0; g < groupCount; g++)
        {
            // 轮流发牌：第 g 组拿 names[g]、names[g+groupCount]、…（与号码模式同一种发法）
            var members = new List<string>();
            for (var i = g; i < names.Count; i += groupCount) members.Add(names[i]);
            members.Sort(StringComparer.CurrentCulture);
            groups.Add(members);
        }

        RenderGroups(groups, "人");
    }

    /// <summary>「组数 / 每组人数」框里的那个数（夹到合法区间，免得在 double 上溢出）。</summary>
    private int GroupValue
        => double.IsNaN(GroupValueBox.Value) ? 1 : Math.Max(1, Math.Min(9999, (int)Math.Floor(GroupValueBox.Value)));

    /// <summary>
    /// 画分组结果，同时攒一份纯文本给「复制结果」——
    /// 分完组通常要发到班级群里，复制出来的就是「第 1 组（6 人）：张三、李四…」这种能直接粘贴的格式。
    /// </summary>
    private void RenderGroups(List<List<string>> groups, string unit)
    {
        GroupHint.Visibility = Visibility.Collapsed;
        GroupsHost.Children.Clear();

        var text = new System.Text.StringBuilder();
        var chipWidth = unit == "人" ? 92 : 46;

        for (var i = 0; i < groups.Count; i++)
        {
            var members = groups[i];
            text.Append($"第 {i + 1} 组（{members.Count} {unit}）：")
                .Append(string.Join("、", members))
                .Append('\n');

            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            row.Children.Add(new TextBlock
            {
                Text = $"第 {i + 1} 组 · {members.Count} {unit}",
                FontSize = 12.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var chips = new VariableSizedWrapGrid
            {
                Orientation = Orientation.Horizontal,
                ItemWidth = chipWidth,
                ItemHeight = 30,
            };
            foreach (var m in members)
            {
                chips.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 2, 8, 2),
                    Background = Res("ControlFillColorSecondaryBrush"),
                    Child = new TextBlock
                    {
                        Text = m,
                        FontSize = 13,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                });
            }
            Grid.SetColumn(chips, 1);
            row.Children.Add(chips);
            GroupsHost.Children.Add(row);
        }

        _groupText = text.ToString().TrimEnd();
        CopyGroupsButton.IsEnabled = groups.Count > 0;
        Toast.Text = $"已分成 {groups.Count} 组，可反复分组。";
    }

    /// <summary>把分组结果按「第 N 组（x 人）：a、b…」复制到剪贴板，方便直接发群里。</summary>
    private void CopyGroups_Click(object sender, RoutedEventArgs e)
    {
        if (_groupText.Length == 0) return;
        try
        {
            var dp = new DataPackage();
            dp.SetText(_groupText);
            Clipboard.SetContent(dp);
            Toast.Text = "已复制分组结果";
        }
        catch { Toast.Text = "复制失败"; }
    }

    // ══════════ 本地存档 + 小工具 ══════════
    // 存档类型挪到 Data\PickNumberConfig.cs —— 工具页与置顶浮窗共用一份，字段不会再各写各的

    private void LoadConfig()
    {
        var cfg = PickNumberConfig.Load();

        FromBox.Value = cfg.From;
        ToBox.Value = cfg.To;
        CountBox.Value = cfg.Count;
        NoRepeatBox.IsChecked = cfg.NoRepeat;
        _used.Clear();
        _used.AddRange(cfg.Used);

        _roster.Clear();
        _roster.AddRange(cfg.Roster ?? new List<string>());
        _usedNames.Clear();
        _usedNames.AddRange(cfg.UsedNames ?? new List<string>());
        _rosterSource = cfg.RosterSource ?? "";
        _savedMode = cfg.Mode;
    }

    private void SaveConfig()
    {
        // 两个界面（工具页 / 置顶浮窗）共用同一个存档类型，字段不会再各写各的
        new PickNumberConfig
        {
            From = From,
            To = To,
            Count = WantCount,
            NoRepeat = NoRepeat,
            Used = _used,
            Mode = UseRoster ? 1 : 0,
            Roster = _roster,
            UsedNames = _usedNames,
            RosterSource = _rosterSource,
        }.Save();
    }

    // ══════════ 示例名单 ══════════
    //
    // 2026-09-29（Nick 提）：静态示例名单 —— 和「示例名单」对话框里那两张图、两份示例文件是同一批姓名。
    /// <summary>示例名单里的姓名（10 人）。与 Assets/roster 下的示例文件保持一致。</summary>
    private static readonly string[] SampleNames =
    {
        "张伟", "王伟", "王芳", "李伟", "王秀英", "李秀英", "李娜", "张秀英", "刘伟", "张敏",
    };

    private async void SampleRoster_Click(object sender, RoutedEventArgs e)
    {
        HideError();
        var dialog = new Views.RosterSampleDialog(_roster.Count > 0) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();

        if (dialog.UseSampleRequested) UseSampleRoster();
    }

    /// <summary>把示例的 10 个姓名装进抽取池，并切到名单模式 —— 让用户立刻能试一次。</summary>
    private void UseSampleRoster()
    {
        _roster.Clear();
        _roster.AddRange(SampleNames);
        _usedNames.Clear();          // 名单换了，上一份名单的抽号记录就没意义了（与导入名单同一处理）
        _rosterSource = "示例名单";

        if (!UseRoster) ModeRosterRadio.IsChecked = true;

        SaveConfig();
        SyncPoolMode();
        RefreshHints();
        Toast.Text = $"已载入示例名单（{SampleNames.Length} 人）。可单击「开始抽号」，或切到「随机分组」试一次。";
    }

    // ══════════ 名单导入 / 清空 ══════════

    private async void ImportRoster_Click(object sender, RoutedEventArgs e)
    {
        HideError();
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            foreach (var ext in NameRoster.TextExts) picker.FileTypeFilter.Add(ext);
            foreach (var ext in NameRoster.ExcelExts) picker.FileTypeFilter.Add(ext);

            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var names = NameRoster.Read(file.Path);
            if (names.Count == 0)
            {
                ShowError("该文件中未读取到姓名。txt / csv 请每行一个（或用逗号分隔）；"
                          + "Excel 请将姓名放在同一列。需要对照可单击「示例名单」。");
                return;
            }

            _roster.Clear();
            _roster.AddRange(names);
            _usedNames.Clear();          // 名单换了，上一份名单的抽号记录就没意义了
            _rosterSource = file.Name;

            // 导完直接切到名单模式，省得再点一次
            if (!UseRoster) ModeRosterRadio.IsChecked = true;

            SaveConfig();
                SyncPoolMode();
            RefreshHints();
            Toast.Text = $"已导入 {names.Count} 个姓名。";
        }
        catch (Exception ex)
        {
            ShowError("读取名单失败：" + ex.Message);
        }
    }

    private void ClearRoster_Click(object sender, RoutedEventArgs e)
    {
        _roster.Clear();
        _usedNames.Clear();
        _rosterSource = "";
        SaveConfig();
        SyncPoolMode();
        RefreshHints();
        Toast.Text = "已清空名单";
    }

    private Brush Res(string key, Color? fallback = null) => Services.ThemeBrush.Get(this, key);

    private Brush AccentBrush() => Services.ThemeBrush.Get(this, "AccentFillColorDefaultBrush");

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
}
