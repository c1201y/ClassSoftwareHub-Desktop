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

    private void EnableToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        App.Settings.Current.HubAiEnabled = EnableToggle.IsOn;
        App.Settings.Save();
        AppLog.Step("hubai", EnableToggle.IsOn ? "总闸打开" : "总闸关闭");
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
                var use = new Button { Content = "使用" };
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

        var keyBox = new PasswordBox { Header = "API Key", PlaceholderText = "粘贴服务商提供的密钥" };

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
        panel.Children.Add(keyBox);
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
            keyBox.Visibility = isLocal ? Visibility.Collapsed : Visibility.Visible;
            urlBox.Visibility = isLocal || isCustom ? Visibility.Visible : Visibility.Collapsed;

            keyBox.PlaceholderText = isLocal ? "本地模型通常不需要密钥" : "粘贴服务商提供的密钥";

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

        // 新加的第一条自动设为当前
        if (!editing) AiModelStore.Activate(entry.Id);

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
