using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Oobe;

/// <summary>
/// 第 6 步：侧边栏位置。放置模式决定「贴哪条边」的可选项（贴靠：左右/左右两条；
/// 自由：左上右下），两套边分开存在草稿里，来回切模式不会把对方的选择冲掉。
/// </summary>
public sealed partial class OobeSidebarPositionPage : Page
{
    private bool _loading;
    private bool _edgeRebuild;

    public OobeSidebarPositionPage()
    {
        InitializeComponent();

        _loading = true;
        var draft = OobeSession.Current!.Draft;
        SelectTag(ModeCombo, draft.SidebarMode == "free" ? "free" : "dock");
        RebuildEdgeCombo();
        SelectTag(GapCombo, new[] { 0, 2, 4, 6, 10, 16 }.Contains(draft.SidebarCollapsedGap)
            ? draft.SidebarCollapsedGap.ToString()
            : "6");
        _loading = false;

        UpdateHints();
        UpdateEnabledState();

        // 页面被缓存：从上一页改完开关再翻回来时，置灰状态要重算
        Loaded += (_, _) => UpdateEnabledState();
    }

    private OobeDraft? Draft => OobeSession.Current?.Draft;

    private static void SelectTag(ComboBox combo, string tag)
    {
        var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == tag);
        combo.SelectedItem = item ?? combo.Items.OfType<ComboBoxItem>().First();
    }

    /// <summary>按当前放置模式重建「贴在哪条边」：贴靠 = 左 / 右 / 左右两条，自由 = 左 / 右 / 上 / 下。</summary>
    private void RebuildEdgeCombo()
    {
        var draft = Draft;
        if (draft is null) return;

        var free = draft.SidebarMode == "free";
        var current = free ? draft.SidebarFreeEdge : draft.SidebarEdge;

        _edgeRebuild = true;
        EdgeCombo.Items.Clear();
        AddEdge("左侧", "left");
        AddEdge("右侧", "right");
        if (free)
        {
            AddEdge("上方", "top");
            AddEdge("下方", "bottom");
        }
        else
        {
            AddEdge("左右两条", "both");
        }

        var item = EdgeCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == current);
        EdgeCombo.SelectedItem = item ?? EdgeCombo.Items.OfType<ComboBoxItem>().First();
        _edgeRebuild = false;
    }

    private void AddEdge(string text, string tag) =>
        EdgeCombo.Items.Add(new ComboBoxItem { Content = text, Tag = tag });

    private void Mode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ModeCombo.SelectedItem is not ComboBoxItem { Tag: string mode }) return;

        var draft = Draft;
        if (draft is null) return;

        draft.SidebarMode = mode;
        RebuildEdgeCombo();
        UpdateHints();
    }

    private void Edge_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _edgeRebuild) return;
        if (EdgeCombo.SelectedItem is not ComboBoxItem { Tag: string edge }) return;

        var draft = Draft;
        if (draft is null) return;

        if (draft.SidebarMode == "free") draft.SidebarFreeEdge = edge;
        else draft.SidebarEdge = edge;
    }

    private void Gap_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (GapCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;

        var draft = Draft;
        if (draft is null) return;

        draft.SidebarCollapsedGap = int.TryParse(tag, out var gap) ? gap : 6;
    }

    private void UpdateHints()
    {
        var free = Draft?.SidebarMode == "free";
        ModeHint.Text = free
            ? "侧边栏可吸附屏幕任意一条边；贴上边或下边时呈横条。"
            : "侧边栏只吸附屏幕左右两条边，可选左右同时显示。";
        EdgeHint.Text = free
            ? "四条边均可吸附；贴上边或下边时侧边栏为横条。拖动收起状态的抓手也可改边。"
            : "选「左右两边」时两侧同时显示，上下位置保持一致，拖动其中一条另一条同步移动。拖动收起状态的抓手也可改边。";
    }

    private void UpdateEnabledState()
    {
        var on = Draft?.SidebarEnabled != false;
        ModeCard.IsEnabled = on;
        EdgeCard.IsEnabled = on;
        GapCard.IsEnabled = on;
        OffHint.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }
}
