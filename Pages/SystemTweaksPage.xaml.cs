using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data.Tweaks;
using ClassSoftwareHub.Desktop.Services;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 系统优化（需求清单第 20 条，2026-10-31）。对齐 Dism++「系统优化」里能安全落地的部分。
///
/// <para>
/// ⭐ 界面**逐行照抄 `AutoShutdownPage` 的写法**：一行一个 `<c>controls:SettingsCard</c>` 直接写在
/// XAML 里，控件就是卡片内容区里的 <c>ToggleSwitch</c> 或 <c>ComboBox</c>。
/// ⛔ 不要改成 `ItemsControl` + `DataTemplate` 包 SettingsCard —— 2026-10-31 试过，
/// SettingsCard 在模板里排不出内容区，右侧开关整个不渲染（Nick：「能抄就抄，别猜」）。
/// </para>
/// <para>
/// ⚠️ 这些项都在当前用户（<c>HKCU</c>），改完即时生效，**不弹 UAC**：
/// 切换只排队，点「应用更改」时统一写入；需要外壳重读的项由 <see cref="TweakRefresh"/> 标注，
/// 写完后统一发一条通知（不再默认杀 explorer）。
/// （机器级项在「系统功能」页，那里才提权。）
/// </para>
/// <para>
/// 2026-10-31 第二轮：① 按系统 build 自动**停用**不适用项（整张卡片置灰、点不动，
/// 而不是藏起来 —— Win10 上看不到"任务栏靠左"这种 Win11 专有设置，但能知道有这一项）；
/// ② 本来就有多档的项改用下拉框（照 Dism++ 的 <c>Type="Combo"</c>）。
/// </para>
/// <para>
/// 2026-10-31 第三轮：应用入口搬到右下角的非目标式 <c>TeachingTip</c>。这一页是一长列卡片，
/// 用户拨完最下面那张要一路滚回顶部才能点「应用更改」，点完还得滚下来接着看 —— 提示条跟着走就没这问题。
/// </para>
/// <para>
/// 2026-10-31 第四轮（Nick 要求）：① 页面顶部那条常驻的应用横幅**整条删掉**，
/// 页面平时不出现任何"应用"字样；② 提示里加一份**任务清单**，逐项写明「项名 → 目标值」，可单项取消；
/// ③ 提示被用户收起来后，顶部冒出一个「待应用 N 项」小按钮，点它把提示叫回来。
/// </para>
/// </summary>
public sealed partial class SystemTweaksPage : Page
{
    /// <summary>待应用的改动：Id → **目标注册表值**（按 Id 去重，来回切换只留最后一次）。</summary>
    private readonly Dictionary<string, int> _pending = new(StringComparer.Ordinal);

    /// <summary>正在回填界面，此时控件的 Toggled / SelectionChanged 是程序触发的。</summary>
    private bool _loading;

    private bool _busy;

    /// <summary>确认框是否已经弹着 —— 同一时刻只允许一个 ContentDialog，第二个会抛异常。</summary>
    private bool _dialogOpen;

    /// <summary>
    /// 被停用的卡片在 XAML 里写的那份描述原文（引用形式）。
    /// 停用时会把描述换成"原文 + 停用原因"，留一份原文才能在卡片重新启用时还原。
    /// </summary>
    private readonly Dictionary<SettingsCard, object?> _originalDescription = new();

    /// <summary>底部提示当前在说哪件事 —— 决定它那个主按钮按下去是「应用」还是「重启资源管理器」。</summary>
    private TipMode _tipMode = TipMode.Pending;

    /// <summary>
    /// 用户点过提示上的「暂不应用」。
    /// 之后只更新顶部那条，不再每拨一下开关就弹一次 —— 否则连拨五项会被弹五次。
    /// 下一次拨开关（<see cref="Queue"/>）或队列清空时复位。
    /// </summary>
    private bool _tipDismissed;

    private enum TipMode
    {
        Pending,
        Applied,
        Failed,
    }

    public SystemTweaksPage()
    {
        InitializeComponent();

        // ⚠️ 必须等 Loaded 之后再读：`SettingsCard` 的内容要等模板套用才进视觉树，
        //    构造函数里找控件一个都找不到 —— 而 `ToggleSwitch` 默认就是关，
        //    于是"系统本来是深色模式"却显示成关（2026-10-31 Nick 指出的就是这个）。
        Loaded += OnLoaded;

        // AI 入口跟着「设置 → 启用 Hub AI」总闸走（与软件下载 / AppData 管理 / 应用卸载同一口径）
        HubAiEntry.Visibility = App.Settings.Current.HubAiEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 浮层每次提问都来取一份现场数据（见 BuildAiContext）
        AiFlyout.ContextProvider = BuildAiContext;
    }

    // ── Hub AI ───────────────────────────────────────────────────────────

    /// <summary>弹出提问浮层。浮层是公共控件，别在这一页另写一套动画与发送逻辑。</summary>
    private void HubAiEntry_Click(object sender, RoutedEventArgs e) => AiFlyout.Toggle();

    /// <summary>
    /// 给浮层准备这一页的现场数据（2026-10-31，2026-10-31 晚加强）。
    ///
    /// <para>
    /// Nick 对照「AppData 管理」页的提问效果提的要求：「他会提前把相关内容给 AI 看，
    /// 这样的话他就能非常的专注专精」。所以这份数据不只是"列清单"，而是让模型**不必再问、
    /// 也不必自己筛**：<list type="bullet">
    ///   <item>已开启 / 已停用 / 待应用 三份名单直接给出 —— 39 项让它自己从全表里筛，既慢又容易漏；</item>
    ///   <item>每项附「本机是否适用」，免得它在 Win10 上推荐一个 Win11 才有的开关；</item>
    ///   <item>「本页不做哪些」写明 —— 问「能不能关 UAC」时能直接答"这里没有"，而不是瞎猜。</item>
    /// </list>
    /// </para>
    /// </summary>
    private PageContextDoc BuildAiContext()
    {
        var build = SystemInfo.OsBuild();
        var all = TweakCatalog.All;

        var on = all.Where(TweakStore.IsOn).ToList();
        var unavailable = all.Where(t => !TweakRules.IsApplicable(t, build)).ToList();

        var doc = new PageContextDoc(
            "系统优化",
            "这一页把当前用户（HKCU）的界面与使用习惯设置收成一份清单，写入不需要管理员权限、不弹 UAC。" +
            "提问的人通常想知道：这一项具体改了什么、开了会不会影响上课 / 投影 / 打印、为什么拨了没反应。" +
            "回答时以「全部条目」里的项名、分组、注册表路径与当前值为准，直接点名是哪一项、在哪一组；" +
            "不要给「清理磁盘」「更新驱动」这类与本页无关的泛泛建议。" +
            "另有一条硬约束：你无法替用户改设置 —— 写入只能由他自己拨开关、再点右下角提示条的主按钮完成，" +
            "所以不要说你「已经帮你打开了」，要告诉他去点哪一项。" +
            "说话用大白话：UAC、CLSID 这类术语只供你自己判断，用户很可能不知道它们是什么；" +
            "被问到「为什么没有某项」时，用一句「会动到系统安全与系统服务，这一页不提供」说清即可，不要罗列术语。");

        doc.Add("状态", new
        {
            系统build = build,
            系统版本 = build >= TweakCatalog.Win11Build ? "Windows 11" : "Windows 10",
            本页条目总数 = all.Count,
            已启用的项数 = on.Count,
            本机不适用的项数 = unavailable.Count,
            待应用项数 = _pending.Count,
            正在应用 = _busy,
        });

        // 三份名单：模型一眼看到"现在开着什么""哪些点不动""哪些还没写进去"
        doc.Add("当前已启用", on.Select(t => new { t.Id, t.Header, 分组 = t.Group }).ToList());

        doc.Add("本机不适用（界面上整卡置灰、点不动）", unavailable.Select(t => new
        {
            t.Id,
            t.Header,
            原因 = t.MinBuild > 0
                ? $"需要 Windows 11 及以上（build ≥ {t.MinBuild}），本机 {build}"
                : $"仅 Windows 10 有效（build ≤ {t.MaxBuild}），本机 {build}",
        }).ToList());

        doc.Add("已排队待应用（拨了但还没写入注册表）", _pending.Select(kv => new
        {
            Id = kv.Key,
            项名 = all.FirstOrDefault(t => t.Id == kv.Key)?.Header ?? kv.Key,
            目标值 = kv.Value,
            写入后 = all.FirstOrDefault(t => t.Id == kv.Key) is { } d
                ? (kv.Value == d.OnValue ? "开" : kv.Value == d.OffValue ? "关" : $"第 {kv.Value} 档")
                : "",
        }).ToList());

        doc.Add("全部条目", all.Select(t => new
        {
            t.Id,
            t.Header,
            分组 = t.Group,
            说明 = t.Description,
            注册表 = t.FullPath,
            当前值 = TweakStore.EffectiveValue(t),
            当前是否开启 = TweakStore.IsOn(t),
            开 = t.OnValue,
            关 = t.OffValue,
            可选档位 = t.IsChoice
                ? t.Choices.Select(c => new { 文字 = c.Label, 值 = c.Value }).ToList()
                : null,
            待应用的目标值 = _pending.TryGetValue(t.Id, out var target) ? target : (int?)null,
            需要刷新 = t.Refresh switch
            {
                TweakRefresh.Tray => "任务栏（写完发 TraySettings 通知）",
                TweakRefresh.Desktop => "桌面（写完发刷新通知）",
                _ => "不需要",
            },
            改前需二次确认 = t.Risk == TweakRisk.Caution,
            本机是否适用 = TweakRules.IsApplicable(t, build),
        }).ToList());

        doc.Add("本页不做的事（被问到就这样答，不要自己推测能不能改）", new[]
        {
            "UAC、SmartScreen、Defender 等安全设置 —— 改错会让程序打不开，本页一律不提供",
            "系统服务（SysMain、搜索索引、打印后台）—— 停了机器变慢且很难联想到是优化工具所为",
            "Windows 更新策略 —— 教室机不更新比更新更麻烦",
            "右键菜单禁用项 —— 靠删 CLSID 实现，影响面大且难还原",
            "IE / Edge / WinRAR 等具体软件的设置 —— 装了才有意义，不属于系统层",
            "机器级设置（休眠、快速启动、分页文件）—— 在「系统功能」页，那里才提权",
        });

        doc.Add("界面怎么操作", new
        {
            拨开关或改下拉框 = "只排队，不立即写入注册表",
            应用更改 = "页面右下角提示条的主按钮，一次写入全部待应用项",
            单项取消 = "提示条的任务清单里，每行右侧有「取消」",
            提示被收起后 = "页面顶部出现「待应用 N 项」按钮，点它把提示叫回来",
            提示条的位置 = "右下角浮出，不随页面滚动",
        });

        return doc;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildChoices();    // 下拉框的选项由清单生成（XAML 里写 ItemTemplate + x:Bind 会崩，见 AppDataPage 文件头）
        ApplyOsFilter();   // 先按系统版本把不适用项停用，再回填状态
        LoadStates();
    }

    /// <summary>按控件上的 <c>Tag</c>（项的 Id）反查定义。</summary>
    private static TweakDef? DefOf(object? tag) =>
        tag is string id ? TweakCatalog.All.FirstOrDefault(t => t.Id == id) : null;

    /// <summary>卡片内容区里那个控件携带的 Tag。</summary>
    private static string? TagOf(SettingsCard card) => card.Content switch
    {
        ToggleSwitch toggle => toggle.Tag as string,
        ComboBox combo => combo.Tag as string,
        _ => null,
    };

    // ── 构建与回填 ───────────────────────────────────────────────────────

    /// <summary>把清单里的选项灌进对应下拉框（只有一个来源，别在 XAML 里再抄一遍字）。</summary>
    private void BuildChoices()
    {
        // ⚠️ 灌选项期间必须压 _loading：往空 ComboBox 里加第一项时若自动选中，
        //    会触发 SelectionChanged 并排进一条"用户根本没选"的待应用项。
        _loading = true;
        try
        {
            foreach (var combo in Combos())
            {
                var def = DefOf(combo.Tag);
                if (def is null || !def.IsChoice) continue;

                combo.Items.Clear();
                foreach (var choice in def.Choices)
                    combo.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Value });
            }
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// 按当前系统的 build 号**停用**不适用的项（照 Dism++ 的 <c>&lt;Applicable&gt;</c>）。
    /// <para>
    /// 2026-10-31 第二轮改口径：原来是把卡片藏起来，现按 Nick 的要求改成 WinUI 的禁用样式 ——
    /// 卡片留在原位、整张置灰、点不动。这样用户看到的是"系统里有这么一项，但这台机器用不上"，
    /// 而不是页面上凭空少了两个开关、让人以为功能被人砍了。
    /// </para>
    /// <para>
    /// 灰得地道靠的是不自己调透明度：<c>SettingsCard</c> 模板自带 <c>CommonStates/Disabled</c> 视觉状态，
    /// 会把 Header、Description、图标的前景统一换成 <c>SettingsCardForegroundDisabled</c>；
    /// 内容区的开关与下拉框由控件自身的禁用态变灰。
    /// ⛔ 别再叠 <c>Opacity</c> —— 那是两套灰法，和同页其它禁用控件对不上。
    /// </para>
    /// </summary>
    private void ApplyOsFilter()
    {
        var build = SystemInfo.OsBuild();
        var disabled = new List<string>();

        foreach (var child in RowsPanel.Children)
        {
            if (child is not SettingsCard card) continue;

            var def = DefOf(TagOf(card));
            if (def is null) continue;

            var available = TweakRules.IsApplicable(def, build);

            card.IsEnabled = available;
            if (available) RestoreDescription(card);
            else
            {
                NoteUnsupported(card, def);
                disabled.Add(def.Id);
            }
        }

        if (disabled.Count == 0)
            AppLog.Step("tweaks", $"系统 build {build}：全部 {TweakCatalog.All.Count} 项均可用");
        else
            AppLog.Step("tweaks", $"系统 build {build}：停用 {disabled.Count} 项（{string.Join("、", disabled)}）");
    }

    /// <summary>停用的卡片在描述后面补一句原因 —— 灰着不说话，用户只能猜。</summary>
    private void NoteUnsupported(SettingsCard card, TweakDef def)
    {
        // 记原文用引用而不是字符串：重复调用时不会把上一条说明再套一层
        if (!_originalDescription.TryGetValue(card, out var original))
        {
            original = card.Description;
            _originalDescription[card] = original;
        }

        var text = original switch
        {
            TextBlock block => block.Text,
            string plain => plain,
            _ => string.Empty,
        };

        var note = $"此项需要 {RequiredSystemName(def)}，当前系统不支持，已停用。";

        // ⚠️ Description 给字符串不会换行，长句会把卡片顶宽（`SettingsCard` 的老毛病），必须包 TextBlock
        card.Description = new TextBlock
        {
            Text = text.Length == 0 ? note : text + "\n" + note,
            TextWrapping = TextWrapping.Wrap,
        };
    }

    /// <summary>把描述还原成 XAML 里写的那一份（只在系统版本判断变化、卡片重新启用时用到）。</summary>
    private void RestoreDescription(SettingsCard card)
    {
        if (_originalDescription.TryGetValue(card, out var original) && original is not null)
            card.Description = original;
    }

    /// <summary>门槛对成人话（目前只有 Win11 门槛的项，这里留全是为了将来加 Win10 专有项）。</summary>
    private static string RequiredSystemName(TweakDef def)
    {
        if (def.MinBuild >= TweakCatalog.Win11Build) return "Windows 11 及以上系统";
        if (def.MaxBuild > 0 && def.MaxBuild < TweakCatalog.Win11Build) return "Windows 10";
        return "更高的系统版本";
    }

    /// <summary>把界面上所有控件按注册表当前值摆一遍。</summary>
    private void LoadStates()
    {
        _loading = true;
        try
        {
            var seen = new List<string>();

            foreach (var toggle in Toggles())
            {
                var def = DefOf(toggle.Tag);
                if (def is null) continue;

                toggle.IsOn = TweakStore.EffectiveValue(def) == def.OnValue;
                seen.Add($"{def.Id}={(toggle.IsOn ? "开" : "关")}");
            }

            foreach (var combo in Combos())
            {
                var def = DefOf(combo.Tag);
                if (def is null || !def.IsChoice) continue;

                var index = IndexOfValue(def, TweakStore.EffectiveValue(def));
                combo.SelectedIndex = index;
                seen.Add($"{def.Id}={(index >= 0 ? def.Choices[index].Label : "未识别")}");
            }

            // 逐项打出来：这台机器上的截图工具会抽掉窗口右侧，靠日志核对初始态最可靠
            AppLog.Step("tweaks", $"按当前系统设置回填 {seen.Count} 项；当前状态：{string.Join("、", seen)}");
        }
        finally
        {
            _loading = false;
        }
    }

    private static int IndexOfValue(TweakDef def, int value)
    {
        for (var i = 0; i < def.Choices.Count; i++)
            if (def.Choices[i].Value == value) return i;

        return -1;   // 注册表里的值不在选项表里（系统版本差异或手工改过）：不假装选中某一档
    }

    // ── 控件枚举 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 页面里那批开关。
    /// <para>
    /// 先走 <c>RowsPanel.Children</c> 里每张 <c>SettingsCard</c> 的 <c>Content</c> —— 内容在 XAML 解析后
    /// 就挂在属性上，不依赖模板是否已经套用；视觉树那段留作兜底（将来卡片嵌进别的容器时仍找得到）。
    /// </para>
    /// </summary>
    private IEnumerable<ToggleSwitch> Toggles()
    {
        var seen = new HashSet<ToggleSwitch>();

        foreach (var child in RowsPanel.Children)
            if (child is SettingsCard { Content: ToggleSwitch toggle }
                && toggle.Tag is string
                && seen.Add(toggle))
                yield return toggle;

        foreach (var toggle in VisualDescendants<ToggleSwitch>(this))
            if (toggle.Tag is string && seen.Add(toggle))
                yield return toggle;
    }

    /// <summary>页面里那批下拉框（枚举方式与 <see cref="Toggles"/> 一致）。</summary>
    private IEnumerable<ComboBox> Combos()
    {
        var seen = new HashSet<ComboBox>();

        foreach (var child in RowsPanel.Children)
            if (child is SettingsCard { Content: ComboBox combo }
                && combo.Tag is string
                && seen.Add(combo))
                yield return combo;

        foreach (var combo in VisualDescendants<ComboBox>(this))
            if (combo.Tag is string && seen.Add(combo))
                yield return combo;
    }

    /// <summary>递归找视觉树里的某类控件（兜底用）。</summary>
    private static List<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is T match) found.Add(match);
                queue.Enqueue(child);
            }
        }

        return found;
    }

    // ── 切换 ─────────────────────────────────────────────────────────────

    private async void Row_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _busy) return;
        if (sender is not ToggleSwitch toggle) return;

        var def = DefOf(toggle.Tag);
        if (def is null) return;

        var want = toggle.IsOn;
        var target = want ? def.OnValue : def.OffValue;

        // 高危项：先问一句。取消就把开关拨回去（拨回时同样要压 _loading，否则会再弹一次）
        if (def.Risk == TweakRisk.Caution)
        {
            // ⚠️ 两个边界，都是 async void 里会直接崩掉进程的情形：
            //   ① 同屏已经在弹一个 ContentDialog —— 再 ShowAsync 会抛「只能同时打开一个」；
            //   ② 等待期间页面被切走（XamlRoot 变 null）—— 再弹会抛「找不到 XamlRoot」。
            if (_dialogOpen || XamlRoot is null)
            {
                Revert(toggle, want);
                return;
            }

            bool ok;
            _dialogOpen = true;
            try
            {
                ok = await ConfirmAsync(def.Header,
                    $"将要修改系统设置：{def.Description}\n\n应用后若要还原，把这一项再改回去并重新应用。");
            }
            catch (Exception ex)
            {
                // 弹不出来就别改：宁可这一下不生效，也不能带着"没确认过"的状态往下走
                AppLog.Warn("tweaks", $"确认框未显示（{def.Id}）：{ex.Message}");
                ok = false;
            }
            finally
            {
                _dialogOpen = false;
            }

            if (!ok)
            {
                Revert(toggle, want);
                return;
            }
        }

        Queue(def, target, want ? "开" : "关");
    }

    /// <summary>
    /// 下拉框选了别的档（2026-10-31 加）。
    /// <para>
    /// ⚠️ 下拉框**不弹二次确认**：三档之间切换本身不是危险操作，而每选一档都弹一次
    /// 会让这一行没法用。所以标了 <see cref="TweakRisk.Caution"/> 的项都保留成开关。
    /// </para>
    /// </summary>
    private void Row_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _busy) return;
        if (sender is not ComboBox combo) return;

        var def = DefOf(combo.Tag);
        if (def is null || !def.IsChoice) return;

        var index = combo.SelectedIndex;
        if (index < 0 || index >= def.Choices.Count) return;

        var choice = def.Choices[index];
        Queue(def, choice.Value, choice.Label);
    }

    /// <summary>
    /// 记一笔待应用。
    /// <para>
    /// ⚠️ 判据用 <see cref="TweakStore.ReadValue"/>（注册表里的**实际值**），不用 <c>EffectiveValue</c>：
    /// 值缺失时后者会假装成默认档，那样"选了默认档"就会被当成"没变化"而永远写不进去。
    /// </para>
    /// </summary>
    private void Queue(TweakDef def, int target, string label)
    {
        if (TweakStore.ReadValue(def) == target) _pending.Remove(def.Id);
        else _pending[def.Id] = target;

        AppLog.Step("tweaks", _pending.ContainsKey(def.Id)
            ? $"排队：{def.Id} = {target}（{label}）"
            : $"取消排队：{def.Id}（与当前系统状态一致）");

        // 用户又动了一下开关 ⇒ 他认为还在改，可以再提示一次
        _tipDismissed = false;
        _tipMode = TipMode.Pending;

        RefreshPendingUi();
    }

    /// <summary>把开关拨回原位，且不让这次回拨再触发一次排队。</summary>
    private void Revert(ToggleSwitch toggle, bool want)
    {
        _loading = true;
        try { toggle.IsOn = !want; }
        finally { _loading = false; }
    }

    /// <summary>
    /// 待应用状态变了：重建提示里的任务清单、刷新底部提示、决定顶部入口按钮的显隐。
    /// <para>
    /// 2026-10-31 起：原来刷新的是页面顶部那条横幅。Nick 要求把横幅整条取消 —— 应用只从底部提示发起，
    /// 提示被收起来之后，由顶部一个小按钮把用户送回提示。
    /// </para>
    /// </summary>
    private void RefreshPendingUi()
    {
        if (_pending.Count == 0)
        {
            ApplyTip.IsOpen = false;
            _tipMode = TipMode.Pending;
            _tipDismissed = false;
            PendingButton.Visibility = Visibility.Collapsed;
            return;
        }

        RebuildPendingList();

        var needsRefresh = _pending.Keys.Any(id =>
            TweakCatalog.All.FirstOrDefault(t => t.Id == id)?.Refresh != TweakRefresh.None);

        // 只有处于「待应用」状态时才刷新提示；应用之后那个「已应用 / 重启」的提示不能被这里盖掉
        if (_tipMode == TipMode.Pending) ShowTipPending(needsRefresh);

        RefreshPendingButton();
    }

    /// <summary>顶部入口按钮：有改动、但提示已经被收起来时才出现（提示开着时它是多余的）。</summary>
    private void RefreshPendingButton()
    {
        PendingButton.Content = $"待应用 {_pending.Count} 项";
        PendingButton.Visibility = _pending.Count > 0 && !ApplyTip.IsOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// 把队列摊成一份可逐项取消的清单：每行是「项名 → 目标值」加一个取消按钮。
    /// <para>
    /// ⚠️ 逐项用代码构造，不用模板（理由见 XAML 里那段说明）。
    /// </para>
    /// </summary>
    private void RebuildPendingList()
    {
        PendingList.Items.Clear();

        foreach (var (id, target) in _pending)
        {
            var def = TweakCatalog.All.FirstOrDefault(t => t.Id == id);
            if (def is null) continue;

            var label = new TextBlock
            {
                Text = $"{def.Header} → {DescribeTarget(def, target)}",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var cancel = new Button
            {
                Content = "取消",
                Tag = def.Id,
                MinWidth = 0,                                        // 默认按钮有最小宽度，会把行挤满
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            cancel.Click += CancelPending_Click;

            var row = new Grid { ColumnSpacing = 16, Margin = new Thickness(0, 7, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(label);
            Grid.SetColumn(cancel, 1);
            row.Children.Add(cancel);

            PendingList.Items.Add(row);
        }
    }

    /// <summary>目标值怎么念：开关报开 / 关，下拉框报它自己那一档的名字。</summary>
    private static string DescribeTarget(TweakDef def, int value)
    {
        if (def.IsChoice)
            foreach (var choice in def.Choices)
                if (choice.Value == value) return choice.Label;

        return value == def.OnValue ? "开" : "关";
    }

    /// <summary>
    /// 清单里点「取消」：只把这一项从队列里撤掉，然后把界面拨回注册表里的实际值。
    /// ⚠️ 必须回填，否则那个开关会停在一个"看起来改了、其实不会写入"的位置。
    /// </summary>
    private void CancelPending_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;

        if (_pending.Remove(id)) AppLog.Step("tweaks", $"从任务清单取消：{id}");

        _tipMode = TipMode.Pending;
        LoadStates();
        RefreshPendingUi();
    }

    /// <summary>顶部那个「待应用 N 项」按钮：把底部提示重新叫出来。</summary>
    private void PendingButton_Click(object sender, RoutedEventArgs e)
    {
        _tipDismissed = false;
        _tipMode = TipMode.Pending;
        RefreshPendingUi();
    }

    /// <summary>提示收起来了（点「暂不应用」、或应用完自动收）：该不该在顶部放回入口。</summary>
    private void ApplyTip_Closed(TeachingTip sender, object args)
    {
        RefreshPendingButton();
    }

    /// <summary>
    /// 每次弹出前按窗口宽度算一次上限 —— 宽度不写死：窄窗口收窄免得贴边溢出，宽窗口最多 520（再宽不好读）。
    /// <para>
    /// ⚠️ 这里只管上限，实际宽度仍由内容撑开、ScrollViewer 吃满可用空间，所以不会出现
    /// "写死 340 结果把「取消」裁掉"那种事（2026-10-31 连错两次的教训）。
    /// 调它而不是挂 <c>TeachingTip.Opened</c>：这个控件没有 Opened 事件（只有 Closed）。
    /// </para>
    /// </summary>
    private void FitTipToWindow()
    {
        var window = XamlRoot?.Size.Width ?? 1024;
        ApplyTip.MaxWidth = Math.Clamp(window - 120, 320, 520);
    }

    /// <summary>
    /// 底部提示摆成「待应用」：主按钮是应用，关闭按钮是暂不应用。
    /// 已经开着就只换文案，不关掉重开（否则连拨五项会闪五次）。
    /// </summary>
    private void ShowTipPending(bool needsRefresh)
    {
        FitTipToWindow();

        ApplyTip.Title = $"有 {_pending.Count} 项待应用";

        // 副标题压成一行：原来那句两行，紧贴标题和清单，整块看着很挤
        ApplyTip.Subtitle = needsRefresh
            ? "应用后自动刷新任务栏与桌面。"
            : "只写当前用户设置，无需管理员权限。";
        ApplyTip.ActionButtonContent = "应用更改";
        ApplyTip.CloseButtonContent = "暂不应用";

        if (!_tipDismissed && !ApplyTip.IsOpen) ApplyTip.IsOpen = true;
    }

    // ── 应用 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 真正干活的那段（现在只有底部提示的主按钮会调它；2026-10-31 第四轮起页面顶部不再有应用按钮）。
    /// </summary>
    private async Task ApplyPendingAsync()
    {
        if (_busy || _pending.Count == 0) return;

        var changes = _pending
            .Select(kv => (Def: TweakCatalog.All.First(t => t.Id == kv.Key), Value: kv.Value))
            .ToList();

        _busy = true;

        // 应用期间把提示整条禁用：主按钮点第二次没有意义，而且这时候不该让用户再去改清单
        ApplyTip.IsEnabled = false;
        ApplyTip.Title = "正在应用…";
        ApplyTip.Subtitle = "正在写入当前用户设置，并按实际值核对结果。";

        TweakApplyResult result;
        try
        {
            result = await TweakStore.ApplyAsync(changes);
        }
        catch (Exception ex)
        {
            // ApplyAsync 自己吞了大部分异常，但 async void 里任何漏网的都会直接终止进程 —— 兜一层
            AppLog.Exception("tweaks", ex, "应用更改");
            ShowNotice("应用失败：" + ex.Message, InfoBarSeverity.Error);
            RefreshPendingUi();
            return;
        }
        finally
        {
            _busy = false;              // 放在 finally：return 与正常结束都会经过这里
            ApplyTip.IsEnabled = true;
        }

        // 只有**确认没生效**的项留在队列里，方便直接重试；生效的全部出队。
        // ⚠️ 原来是无条件 _pending.Clear()，于是「10 项里 3 项失败」之后用户既不知道是哪 3 项、
        //    也没法只重试它们（2026-10-31 自检发现）。规则本体在 TweakRules.RemainingAfterApply，有测试。
        _pending.Clear();
        foreach (var (id, target) in TweakRules.RemainingAfterApply(changes, result.Failed))
            _pending[id] = target;

        LoadStates();   // 界面一律按注册表实际值重摆，不信"我以为写进去了"

        if (result.Matched == result.Requested)
        {
            // 状态全对、但写入过程报过错：既要说"生效了"，也不能把那条错误吞掉
            ShowNotice(
                result.Ok
                    ? $"已应用 {result.Matched} 项。"
                    : $"已应用 {result.Matched} 项，当前状态与目标一致，但写入过程有报错：{result.Error}",
                result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning);

            // ⚠️ 这步必须在 finally 之后：原来写在 try 里，紧接着 finally 的收尾动作看到队列已空，
            //    会把刚亮起来的"重启资源管理器"又收掉，于是需要重启才生效的项永远等不到那个按钮
            //    （2026-10-31 自检发现，必现）。
            if (changes.Any(c => c.Def.Refresh != TweakRefresh.None)) ShowTipApplied(result.Matched);
            else RefreshPendingUi();
        }
        else
        {
            ShowNotice(
                $"已应用 {result.Matched}/{result.Requested} 项；未生效：{DescribeFailed(result.Failed)}。"
                + "可能被组策略或其他安全软件固定，剩下的项已留在任务列表里可重试。",
                InfoBarSeverity.Warning);

            // 先定状态再刷界面，否则会被"待应用"的文案盖回去
            _tipMode = TipMode.Failed;
            RefreshPendingUi();
            ShowTipFailed();
        }
    }

    /// <summary>应用成功后提示一次刷新兜底（通知已经发过了，这里只是"还没刷新出来"时的手动出口）。</summary>
    private void ShowTipApplied(int applied)
    {
        FitTipToWindow();

        // 用户此刻多半停在页面底部，把出口摆到他眼前，省得再滚回顶部
        _tipMode = TipMode.Applied;
        ApplyTip.Title = $"已应用 {applied} 项";
        ApplyTip.Subtitle = "已通知系统刷新。若任务栏或桌面没有变化，重启资源管理器即可看到效果。";
        ApplyTip.ActionButtonContent = "重启资源管理器";
        ApplyTip.CloseButtonContent = "关闭";
        ApplyTip.IsOpen = true;

        // 点主按钮时提示会先自动收起（Closed 把顶部按钮亮出来），这里打开后要再收一次，
        // 否则界面上会同时出现"提示"和"待应用 N 项"两个入口
        RefreshPendingButton();
    }

    /// <summary>失败后：队列里只剩没生效的项，提示改成「可重试」。</summary>
    private void ShowTipFailed()
    {
        FitTipToWindow();

        ApplyTip.Title = $"有 {_pending.Count} 项未生效";
        ApplyTip.Subtitle = "可能被组策略或其他安全软件固定。可以再试一次，或重启系统后再看。";
        ApplyTip.ActionButtonContent = "重试";
        ApplyTip.CloseButtonContent = "关闭";
        ApplyTip.IsOpen = true;
        RefreshPendingButton();
    }

    // ── 底部提示上的两个按钮 ─────────────────────────────────────────────

    private async void ApplyTip_ActionClick(TeachingTip sender, object args)
    {
        if (_tipMode == TipMode.Applied)
        {
            RestartExplorer_Click(sender, new RoutedEventArgs());
            return;
        }

        try
        {
            await ApplyPendingAsync();
        }
        catch (Exception ex)
        {
            AppLog.Exception("tweaks", ex, "应用更改（底部提示）");
            ShowNotice("应用失败：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ApplyTip_CloseClick(TeachingTip sender, object args)
    {
        // 只是收起来，队列原样保留 —— 顶部那个「待应用 N 项」按钮会冒出来，点它就能回到这里
        _tipDismissed = true;
    }

    /// <summary>把失败项的名称拼出来（拿不到名字就退回 Id，总比只说"有 3 项"强）。</summary>
    private static string DescribeFailed(IReadOnlyList<string> failed)
    {
        if (failed.Count == 0) return "无";
        return string.Join("、", failed.Select(id =>
            TweakCatalog.All.FirstOrDefault(t => t.Id == id)?.Header ?? id));
    }

    private void RestartExplorer_Click(object sender, RoutedEventArgs e)
    {
        TweakStore.RestartExplorer();

        // ⚠️ 不能直接收掉提示：队列里可能还有没应用的改动，那会被一起藏起来 —— 交给 RefreshPendingUi 统一判定
        _tipMode = TipMode.Pending;
        RefreshPendingUi();

        ShowNotice("资源管理器已重启。若任务栏没有回来，用任务管理器重新运行 explorer.exe。", InfoBarSeverity.Success);
    }

    // ── 界面小工具 ────────────────────────────────────────────────────────

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "应用这项",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        // NoticeBar 平时是 Collapsed（避免那条 8px 外边距在页头下面撑出空档），用之前先放出来
        NoticeBar.Visibility = Visibility.Visible;
        NoticeBar.Severity = severity;
        NoticeBar.Message = message;
        NoticeBar.IsOpen = true;
    }

    /// <summary>用户点了 InfoBar 的关闭叉：连控件一起收起，别让它留 8px 外边距在那撑空档。</summary>
    private void NoticeBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        sender.Visibility = Visibility.Collapsed;
    }
}
