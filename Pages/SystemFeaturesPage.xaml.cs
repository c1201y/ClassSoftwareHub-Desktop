using System;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 系统功能（需求清单第 20 条，2026-10-30 第一版）：休眠、快速启动的开关，分页文件的只读体检。
///
/// <para>
/// ⚠️ 开关行的状态**只从服务层读**，不在页面里缓存：这两项的值用户可能在系统设置里改过、
/// 也可能被组策略改回，页面每次进来重读一遍最省事（读注册表是微秒级）。
/// </para>
/// <para>
/// ⛔ 回填开关会触发 <c>Toggled</c>，所以整段回填必须压在 <c>_loading</c> 里 ——
/// 这个坑本项目踩过不止一次（AppData 卡的开关、侧边栏布局页）。
/// </para>
/// </summary>
public sealed partial class SystemFeaturesPage : Page
{
    /// <summary>正在回填界面：此时 Toggled 是程序自己触发的，不能当作用户操作。</summary>
    private bool _loading;

    /// <summary>有一次改动正在等 UAC 返回。</summary>
    private bool _busy;

    public SystemFeaturesPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Load();
    }

    // ── 读取 ─────────────────────────────────────────────────────────────

    private void Load()
    {
        _loading = true;
        try
        {
            var hibernate = SystemFeatures.Hibernate();
            var fast = SystemFeatures.FastStartup();

            HibernateSwitch.IsOn = hibernate == FeatureState.On;
            HibernateSwitch.IsEnabled = hibernate != FeatureState.Unknown && !_busy;

            FastStartupSwitch.IsOn = fast == FeatureState.On;

            // ⚠️ 休眠关着的时候快速启动不可能生效（它靠休眠文件工作），所以直接把开关禁掉，
            //    比让用户点了没反应好 —— 说明文字里也写了这一层关系。
            FastStartupSwitch.IsEnabled = fast != FeatureState.Unknown && hibernate == FeatureState.On && !_busy;

            if (hibernate == FeatureState.Unknown)
                ShowNotice("读不到休眠状态，可能是注册表访问被安全软件拦截。", InfoBarSeverity.Warning);

            var bytes = SystemFeatures.HibernateFileBytes();
            HibernateFileText.Text = bytes > 0 ? AppLog.Size(bytes) : "不存在（休眠已关闭）";

            PageFileList.ItemsSource = SystemFeatures.PageFiles();
        }
        finally
        {
            _loading = false;
        }
    }

    // ── 开关 ─────────────────────────────────────────────────────────────

    private async void Hibernate_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _busy) return;

        var want = HibernateSwitch.IsOn;
        var current = SystemFeatures.HibernateFileBytes();

        var confirmed = await ConfirmAsync(
            want ? "开启休眠" : "关闭休眠",
            want
                ? "开启后系统会在系统盘生成与内存容量相当的休眠文件，用于休眠与快速启动。继续吗？"
                : $"关闭后系统会删除休眠文件（当前占用 {AppLog.Size(current)}），快速启动随之失效。继续吗？");

        if (!confirmed)
        {
            Revert(HibernateSwitch, !want);
            return;
        }

        await ApplyAsync("休眠", () => SystemFeatures.SetHibernateAsync(want));
    }

    private async void FastStartup_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _busy) return;

        var want = FastStartupSwitch.IsOn;
        var confirmed = await ConfirmAsync(
            want ? "开启快速启动" : "关闭快速启动",
            want
                ? "开启后关机时会保存系统状态，下次开机更快；装有双系统的机器可能因此无法正常进入另一个系统。继续吗？"
                : "关闭后每次关机都是完整关机，开机速度会变慢，但更接近「彻底重启」。继续吗？");

        if (!confirmed)
        {
            Revert(FastStartupSwitch, !want);
            return;
        }

        await ApplyAsync("快速启动", () => SystemFeatures.SetFastStartupAsync(want));
    }

    /// <summary>取消时把开关拨回去。⚠️ 必须压在 _loading 里，否则会再触发一次 Toggled。</summary>
    private void Revert(ToggleSwitch control, bool value)
    {
        _loading = true;
        control.IsOn = value;
        _loading = false;
    }

    /// <summary>
    /// 执行一次改动，然后**按实际状态**回填并给一句结果。
    /// ⛔ 不写"已执行命令"，只写读回来的状态 —— 用户拒绝 UAC 时命令根本没跑。
    /// </summary>
    private async Task ApplyAsync(string name, Func<Task<FeatureState>> action)
    {
        _busy = true;
        HibernateSwitch.IsEnabled = false;
        FastStartupSwitch.IsEnabled = false;

        try
        {
            var state = await action();
            Load();

            ShowNotice(
                state == FeatureState.Unknown
                    ? $"{name}的设置没有生效。可能是未授予管理员权限，或该设置被组策略限定。"
                    : $"{name}已{(state == FeatureState.On ? "开启" : "关闭")}。",
                state == FeatureState.Unknown ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Exception("sysfeatures", ex, $"设置{name}");
            ShowNotice($"{name}设置失败，详情见日志。", InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            HibernateSwitch.IsEnabled = true;
            FastStartupSwitch.IsEnabled = true;
            Load();   // 重新按真实状态摆一次开关
        }
    }

    // ── 界面小工具 ────────────────────────────────────────────────────────

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        NoticeBar.Severity = severity;
        NoticeBar.Message = message;
        NoticeBar.IsOpen = true;
    }
}
