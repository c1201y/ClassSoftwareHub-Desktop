using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Services.HubAi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// Hub AI 的二级设置页（2026-10-18）。
///
/// <para>
/// 版式原则：**页面上只放"有哪些模型"，配置细节全部收进对话框**。
/// 上一版把服务商/地址/模型名/密钥/思考/上下文六项平铺在页面上，用户分不清哪块属于哪个模型，
/// 配一个已知厂商的模型要填五项（Nick：「恶心，不如人家一步搞定」）。
/// </para>
/// <para>
/// ⛔ 密钥只在对话框里输入，保存后立刻清空；列表上只显示「未配置密钥」这种状态。
/// **每条模型一个密钥文件**，删模型不会误伤别的。
/// </para>
/// </summary>
public sealed partial class HubAiSettingsPage : Page
{
    private bool _loading;

    public HubAiSettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _loading = true;
        try
        {
            AiModelStore.EnsureMigrated();

            EnableToggle.IsOn = App.Settings.Current.HubAiEnabled;
            RenderModelList();

            UserNameBox.Text = App.Settings.Current.UserDisplayName;

            ToolsToggle.IsOn = App.Settings.Current.HubAiToolsEnabled;
            RefreshToolState();

#if DEBUG
            if (!SecretStore.SelfTest())
                AppLog.Warn("hubai", "DPAPI 自检未通过，保存密钥可能不可用");
#endif
        }
        catch (Exception ex)
        {
            AppLog.Exception("hubai", ex, "初始化 Hub AI 设置页");
        }
        finally
        {
            _loading = false;
        }
    }

    // ══════════════════════════════════════════════════════
    //  总闸
    // ══════════════════════════════════════════════════════

    private async void EnableToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var enabled = EnableToggle.IsOn;

        App.Settings.Current.HubAiEnabled = enabled;
        App.Settings.Save();
        AppLog.Step("hubai", enabled ? "总闸打开" : "总闸关闭");

        // 打开与关闭都给一个「立即重启」的出口（2026-10-28 Nick：关掉之后前台功能还能用）。
        // ⚠️ 说清楚哪些是即时生效的、哪些要重新进入页面才刷新 —— 别一句"请重启"糊过去：
        //    软件下载页的入口卡片、对话页的状态都是"进入页面时重算"，
        //    加上 HubAiPage 是 NavigationCacheMode=Enabled，停在原地确实看不到变化。
        // ⛔ 导航栏的 Hub AI 项**不受总闸影响**，永远在那儿（Nick 要留作宣传位），文案里别把它算进去。
        var dialog = new ContentDialog
        {
            Title = enabled ? "Hub AI 已启用" : "Hub AI 已停用",
            Content = enabled
                ? "设置已保存。软件下载页的入口会在下次进入时出现，导航栏的 Hub AI 随时可用；"
                  + "如果对话页仍然停在引导界面，重新进入一次即可，或立即重启应用。"
                : "设置已保存。软件下载页的入口会收起，对话页会在下次进入时切回启用引导；"
                  + "导航栏的 Hub AI 保留，方便随时重新开启。如需立刻清掉残留，可以重启应用。",
            PrimaryButtonText = "立即重启",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Close,   // 默认不是重启：多数情况重进一次页面就够
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            App.MainWindow?.RestartApp();
    }

    // ══════════════════════════════════════════════════════
    //  模型列表
    // ══════════════════════════════════════════════════════

    private void RenderModelList()
    {
        ModelListPanel.Children.Clear();

        var active = AiModelStore.Active;
        var cardBg = ThemeBrush.Get(this, "CardBackgroundFillColorDefault");
        var cardBorder = ThemeBrush.Get(this, "CardStrokeColorDefault");
        var textPrimary = ThemeBrush.Get(this, "TextFillColorPrimary");
        var textSecondary = ThemeBrush.Get(this, "TextFillColorSecondary");

        if (AiModelStore.All.Count == 0)
        {
            ModelListPanel.Children.Add(new TextBlock
            {
                Text = "还没有配置任何模型。点击下方「添加模型」开始。",
                FontSize = 12.5,
                Foreground = textSecondary,
                Margin = new Thickness(2, 4, 0, 4),
            });
            return;
        }

        foreach (var entry in AiModelStore.All)
        {
            var isActive = entry.Id == active?.Id;
            var entryId = entry.Id;

            var title = new TextBlock
            {
                Text = entry.Name,
                FontSize = 13.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = textPrimary,
            };

            var hasKey = SecretStore.Has(entry.EffectiveSecretId);
            var subtitleText = entry.Subtitle
                               + (isActive ? " · 使用中" : "")
                               + (hasKey ? "" : " · 未配置密钥");

            var subtitle = new TextBlock
            {
                Text = subtitleText,
                FontSize = 11.5,
                Foreground = textSecondary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            };

            var textColumn = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            textColumn.Children.Add(title);
            textColumn.Children.Add(subtitle);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (!isActive)
            {
                // 没密钥的模型不给「使用」：点了也用不了，还会让人以为已经在用它
                var use = new Button { Content = "使用", IsEnabled = hasKey };
                if (!hasKey) ToolTipService.SetToolTip(use, "这条模型还没有配置密钥，暂时无法使用");
                use.Click += (_, _) =>
                {
                    AiModelStore.Activate(entryId);
                    RenderModelList();
                };
                actions.Children.Add(use);
            }

            var edit = new Button { Content = "编辑" };
            edit.Click += (_, _) => _ = ShowModelDialogAsync(entry);
            actions.Children.Add(edit);

            var remove = new Button { Content = "删除" };
            remove.Click += (_, _) =>
            {
                AiModelStore.Remove(entryId);
                RenderModelList();
            };
            actions.Children.Add(remove);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(textColumn);
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);

            ModelListPanel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 12),
                Background = cardBg,
                BorderBrush = cardBorder,
                BorderThickness = new Thickness(1),
                Child = row,
            });
        }
    }

    private void AddModel_Click(object sender, RoutedEventArgs e) => _ = ShowModelDialogAsync(null);

    /// <summary>上下文档位。⚠️ 存的是**近似字符数**（0 = 不限制），不是 token 数。</summary>
    private static readonly (string Label, int Chars)[] ContextOptions =
    {
        ("不限制", 0),
        ("16K", 64_000),
        ("32K", 128_000),
        ("128K", 512_000),
        ("1M", 4_000_000),
    };

    private static int ContextIndexOf(int chars)
    {
        for (var i = 0; i < ContextOptions.Length; i++)
            if (ContextOptions[i].Chars == chars) return i;

        return 0;
    }

    /// <summary>
    /// 添加 / 编辑对话框。已知厂商只需要三步：**选供应商 → 填密钥 → 选模型**。
    /// 地址由预设自动带出，只有「自定义」和「本地模型」才露出来。
    /// </summary>
    private async Task ShowModelDialogAsync(AiModelEntry? existing)
    {
        var editing = existing is not null;

        var providerCombo = new ComboBox { Header = "供应商", HorizontalAlignment = HorizontalAlignment.Stretch };

        // 云端厂商在前；本地与自定义单独放最后 —— 前者跑在自己机器上（不要密钥），
        // 后者要手填地址，和"选一家直营厂商"是完全不同的心智
        foreach (var provider in AiProviders.All.Where(p => p.Id is not ("local" or "custom")))
            providerCombo.Items.Add(new ComboBoxItem { Content = provider.Name, Tag = provider.Id });

        providerCombo.Items.Add(new ComboBoxItem { Content = "本地模型（Ollama / LM Studio）", Tag = "local" });
        providerCombo.Items.Add(new ComboBoxItem { Content = "自定义（兼容 OpenAI 协议）", Tag = "custom" });

        // ⛔ 已配置过密钥的模型，不能再摆一个空白的密码框：密钥用 DPAPI 加密存在本机、
        //    本来就不可查看，空白框只会让用户以为「这条模型没配密钥」
        //    （2026-11-07 Nick 反馈的原话就是这个）。改成显式写「已配置」，另给一个「更换密钥」入口。
        var hasSavedKey = existing is not null && SecretStore.Has(existing.EffectiveSecretId);
        var keyEditing = false;

        var keyTitle = new TextBlock { Text = "API Key", FontSize = 12, Opacity = 0.8 };

        var keyBox = new PasswordBox { PlaceholderText = "粘贴服务商提供的密钥" };

        var keyStateText = new TextBlock
        {
            Text = "已配置。密钥加密保存在本机，不可查看。",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.75,
        };

        var changeKeyButton = new Button { Content = "更换密钥" };

        var keyConfiguredRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        keyConfiguredRow.Children.Add(changeKeyButton);
        keyConfiguredRow.Children.Add(keyStateText);

        var keyPanel = new StackPanel { Spacing = 6 };
        keyPanel.Children.Add(keyTitle);
        keyPanel.Children.Add(keyConfiguredRow);
        keyPanel.Children.Add(keyBox);

        var modelCombo = new ComboBox
        {
            Header = "模型名称",
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var urlBox = new TextBox
        {
            Header = "服务地址",
            PlaceholderText = "https://api.example.com/v1",
            Visibility = Visibility.Collapsed,
        };

        // 高级参数默认折叠：绝大多数人只需要"选厂商 → 填密钥 → 选模型"
        var thinkToggle = new ToggleSwitch { Header = "思考模式", IsOn = true };

        var effortCombo = new ComboBox { Header = "思考强度", HorizontalAlignment = HorizontalAlignment.Stretch };
        effortCombo.Items.Add(new ComboBoxItem { Content = "轻度", Tag = "low" });
        effortCombo.Items.Add(new ComboBoxItem { Content = "标准", Tag = "high" });
        effortCombo.Items.Add(new ComboBoxItem { Content = "最深", Tag = "max" });
        effortCombo.SelectedIndex = 1;

        var contextCombo = new ComboBox { Header = "上下文上限", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (label, chars) in ContextOptions)
            contextCombo.Items.Add(new ComboBoxItem { Content = label, Tag = chars });
        contextCombo.SelectedIndex = 0;

        var advancedPanel = new StackPanel { Spacing = 12 };
        advancedPanel.Children.Add(thinkToggle);
        advancedPanel.Children.Add(effortCombo);
        advancedPanel.Children.Add(contextCombo);

        var advanced = new Expander
        {
            Header = "高级选项",
            Content = advancedPanel,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        var testButton = new Button { Content = "测试连接", VerticalAlignment = VerticalAlignment.Bottom };
        var statusText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var testRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        testRow.Children.Add(testButton);
        testRow.Children.Add(statusText);

        var panel = new StackPanel { Spacing = 14, MinWidth = 400 };
        panel.Children.Add(providerCombo);
        panel.Children.Add(keyPanel);
        panel.Children.Add(modelCombo);
        panel.Children.Add(urlBox);
        panel.Children.Add(advanced);
        panel.Children.Add(testRow);

        var dialog = new ContentDialog
        {
                RequestedTheme = (App.MainWindow?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualTheme ?? Microsoft.UI.Xaml.ElementTheme.Default,
            Title = editing ? "编辑模型" : "添加模型",
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = panel,
            XamlRoot = XamlRoot,
        };

        var filling = true;

        void SyncProvider()
        {
            if (providerCombo.SelectedItem is not ComboBoxItem { Tag: string providerId }) return;

            var provider = AiProviders.ById(providerId);
            var isLocal = providerId == "local";
            var isCustom = providerId == "custom";

            // 本地模型不校验密钥；自定义才需要手填地址
            keyPanel.Visibility = isLocal ? Visibility.Collapsed : Visibility.Visible;
            urlBox.Visibility = isLocal || isCustom ? Visibility.Visible : Visibility.Collapsed;

            // 同一家供应商、密钥已经存过、用户也没点「更换密钥」→ 显示「已配置」而不是空白框。
            // 换了供应商就必须重填：密钥是按厂商签发的，不能拿 A 家的去测 B 家。
            var configured = !isLocal
                             && existing is not null
                             && existing.ProviderId == providerId
                             && hasSavedKey
                             && !keyEditing;

            keyConfiguredRow.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
            keyBox.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;

            modelCombo.Items.Clear();
            foreach (var model in provider.Models)
            {
                // 显示名给人看，Tag 存真正的模型 ID —— 两者不一样（deepseek-flash ↔ DeepSeek-V4.1-Flash）
                modelCombo.Items.Add(new ComboBoxItem
                {
                    Content = AiProviders.ModelLabel(provider.Id, model),
                    Tag = model,
                });
            }

            if (filling) return;

            if (provider.BaseUrl.Length > 0) urlBox.Text = provider.BaseUrl;

            if (provider.Models.Count > 0) modelCombo.SelectedIndex = 0;
            else modelCombo.Text = "";
        }

        providerCombo.SelectionChanged += (_, _) => SyncProvider();

        changeKeyButton.Click += (_, _) =>
        {
            keyEditing = true;
            SyncProvider();
            keyBox.Focus(FocusState.Programmatic);
        };

        // 回填：先把选项摆好，再让 filling=false，避免回填过程触发"切换供应商"的副作用
        var initialProvider = existing?.ProviderId ?? "deepseek";
        providerCombo.SelectedItem = providerCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string?)i.Tag == initialProvider) ?? providerCombo.Items[0];

        SyncProvider();

        if (existing is not null)
        {
            var hit = modelCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (string?)i.Tag == existing.Model);

            if (hit is not null) modelCombo.SelectedItem = hit;
            else modelCombo.Text = existing.Model;

            if (existing.BaseUrl.Length > 0) urlBox.Text = existing.BaseUrl;

            thinkToggle.IsOn = existing.Thinking;
            effortCombo.SelectedIndex = existing.Effort switch { "low" => 0, "max" => 2, _ => 1 };
            contextCombo.SelectedIndex = ContextIndexOf(existing.ContextLimit);
        }

        filling = false;

        testButton.Click += async (_, _) =>
        {
            if (providerCombo.SelectedItem is not ComboBoxItem { Tag: string providerId }) return;

            var provider = AiProviders.ById(providerId);
            var baseUrl = urlBox.Text.Trim().Length > 0 ? urlBox.Text.Trim() : provider.BaseUrl;

            // 下拉选中项给的是 Tag（真 ID）；手填时 Text 就是 ID
            var model = modelCombo.SelectedItem is ComboBoxItem { Tag: string pickedModel }
                ? pickedModel
                : (modelCombo.Text ?? "").Trim();

            var key = keyBox.Password?.Trim() ?? "";

            // 「已配置」状态下输入框是空的，测试时要用本机存着的那把，而不是拿空串去撞
            if (key.Length == 0 && existing is not null && existing.ProviderId == providerId)
            {
                try { key = SecretStore.Load(existing.EffectiveSecretId); }
                catch { key = ""; }
            }

            if (baseUrl.Length == 0 || model.Length == 0)
            {
                statusText.Text = "请先填写服务地址与模型名称。";
                return;
            }

            testButton.IsEnabled = false;
            statusText.Text = "正在测试";

            try
            {
                var result = await AiClient.TestAsync(new AiConfig(providerId, baseUrl, model, key));
                statusText.Text = result.Message;
            }
            catch (Exception ex)
            {
                AppLog.Exception("hubai", ex, "对话框连通性测试");
                statusText.Text = "测试失败，详情见日志。";
            }
            finally
            {
                testButton.IsEnabled = true;
            }
        };

        // ⛔ 没填密钥不许保存。以前允许留空，于是列表里能躺着一条「看着配好了、实际用不了」的模型，
        //    用户会以为那是应用自带的（2026-11-07 Nick 反馈）。拦在对话框里面，
        //    不要等对话框关掉再拿 InfoBar 报错 —— 那样用户还得重新打开填一遍。
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (providerCombo.SelectedItem is not ComboBoxItem { Tag: string providerId })
            {
                args.Cancel = true;
                return;
            }

            if (providerId == "local") return;   // 本地模型不要密钥

            var typed = keyBox.Password?.Trim() ?? "";
            var reuse = existing is not null
                        && existing.ProviderId == providerId
                        && SecretStore.Has(existing.EffectiveSecretId);

            if (typed.Length > 0 || reuse) return;

            args.Cancel = true;
            statusText.Text = "还差一步：填写 API Key，否则这条模型保存后无法使用。";
            keyBox.Focus(FocusState.Programmatic);
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (providerCombo.SelectedItem is not ComboBoxItem { Tag: string saveProviderId }) return;

        var saveProvider = AiProviders.ById(saveProviderId);

        // 下拉选中项给的是 Tag（真 ID）；手填时 Text 就是 ID
        var saveModel = modelCombo.SelectedItem is ComboBoxItem { Tag: string savedModelId }
            ? savedModelId
            : (modelCombo.Text ?? "").Trim();

        if (saveModel.Length == 0)
        {
            ShowResult(false, "模型名称不能为空。");
            return;
        }

        var entry = existing ?? new AiModelEntry { Id = Guid.NewGuid().ToString("N") };

        entry.ProviderId = saveProviderId;
        entry.BaseUrl = urlBox.Text.Trim().Length > 0 ? urlBox.Text.Trim() : saveProvider.BaseUrl;
        entry.Model = saveModel;

        if (!editing || string.IsNullOrWhiteSpace(entry.Name))
            entry.Name = $"{saveProvider.Name} · {AiProviders.ModelLabel(saveProviderId, saveModel)}";

        if (entry.Effort is not ("low" or "high" or "max")) entry.Effort = "high";

        // 高级选项
        entry.Thinking = thinkToggle.IsOn;
        if (effortCombo.SelectedItem is ComboBoxItem { Tag: string effort }) entry.Effort = effort;
        if (contextCombo.SelectedItem is ComboBoxItem { Tag: int charCount }) entry.ContextLimit = charCount;

        AiModelStore.SaveEntry(entry);

        var key = keyBox.Password?.Trim() ?? "";
        if (key.Length > 0) SecretStore.Save(key, entry.EffectiveSecretId);

        // 这条保存之后到底能不能用？本地模型不看密钥，其余要有「这次填的」或「原本存着的」
        var usable = saveProviderId == "local"
                     || key.Length > 0
                     || (existing is not null && existing.ProviderId == saveProviderId
                         && SecretStore.Has(existing.EffectiveSecretId));

        // 新加的第一条自动设为当前；没有密钥的**不抢**这个位置，否则用户以为它已经在用了
        if (!editing && usable) AiModelStore.Activate(entry.Id);

        RenderModelList();
        ShowResult(true, editing ? "模型已更新。" : "模型已添加。");
    }

    private void ShowResult(bool ok, string message)
    {
        TestBar.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        TestBar.Title = ok ? "操作成功" : "操作失败";
        TestBar.Message = message;
        TestBar.IsOpen = true;
    }

    // ══════════════════════════════════════════════════════
    //  个人资料
    // ══════════════════════════════════════════════════════

    private void UserNameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var name = UserNameBox.Text.Trim();
        if (name == App.Settings.Current.UserDisplayName) return;

        App.Settings.Current.UserDisplayName = name;
        App.Settings.Save();
    }

    // ══════════════════════════════════════════════════════
    //  工具包（可选安装）
    // ══════════════════════════════════════════════════════

    private bool _installingToolPack;

    private void RefreshToolState()
    {
        var pack = ToolPackStore.Load();

        if (pack is not null)
        {
            ToolPackState.Text = $"已安装 v{pack.Version} · {pack.Tools.Count} 个工具";
            ToolPackRemoveBtn.IsEnabled = true;
            return;
        }

        ToolPackState.Text = ToolPackStore.IsInstalled ? "工具包已损坏，请重新下载" : "未安装";
        ToolPackRemoveBtn.IsEnabled = ToolPackStore.IsInstalled;
    }

    private async void ToolsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _installingToolPack) return;

        if (!ToolsToggle.IsOn)
        {
            App.Settings.Current.HubAiToolsEnabled = false;
            App.Settings.Save();
            AppLog.Step("hubai", "已关闭 AI 工具");
            return;
        }

        if (ToolPackStore.Load() is not null)
        {
            App.Settings.Current.HubAiToolsEnabled = true;
            App.Settings.Save();
            return;
        }

        await InstallToolPackAsync(fromToggle: true);
    }

    private async void UpdateToolPack_Click(object sender, RoutedEventArgs e)
        => await InstallToolPackAsync(fromToggle: false);

    private void RemoveToolPack_Click(object sender, RoutedEventArgs e)
    {
        ToolPackStore.Remove();

        App.Settings.Current.HubAiToolsEnabled = false;
        App.Settings.Save();

        _loading = true;
        try { ToolsToggle.IsOn = false; }
        finally { _loading = false; }

        RefreshToolState();
        ShowToolResult(true, "工具包已移除，相应能力不再可用。");
    }

    private async Task InstallToolPackAsync(bool fromToggle)
    {
        if (_installingToolPack) return;
        _installingToolPack = true;

        ToolPackRing.IsActive = true;
        ToolPackUpdateBtn.IsEnabled = false;
        ToolPackState.Text = "正在下载";

        try
        {
            var result = await ToolPackInstaller.InstallAsync();
            ShowToolResult(result.Ok, result.Message);
            RefreshToolState();

            if (result.Ok)
            {
                App.Settings.Current.HubAiToolsEnabled = true;
                App.Settings.Save();

                _loading = true;
                try { ToolsToggle.IsOn = true; }
                finally { _loading = false; }
            }
            else if (fromToggle)
            {
                _loading = true;
                try { ToolsToggle.IsOn = false; }
                finally { _loading = false; }

                App.Settings.Current.HubAiToolsEnabled = false;
                App.Settings.Save();
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("hubai", ex, "下载工具包（界面层）");
            ShowToolResult(false, "下载失败，详情见日志。");

            if (fromToggle)
            {
                _loading = true;
                try { ToolsToggle.IsOn = false; }
                finally { _loading = false; }
            }
        }
        finally
        {
            _installingToolPack = false;
            ToolPackRing.IsActive = false;
            ToolPackUpdateBtn.IsEnabled = true;
            RefreshToolState();
        }
    }

    private void ShowToolResult(bool ok, string message)
    {
        ToolBar.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        ToolBar.Title = ok ? "工具包已就绪" : "工具包获取失败";
        ToolBar.Message = message;
        ToolBar.IsOpen = true;
    }
}
