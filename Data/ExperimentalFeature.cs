namespace ClassSoftwareHub.Desktop.Data;

/// <summary>
/// 一条「实验性功能」的定义（实验性功能总览页用）。
///
/// ⚠️ 为什么是 <see cref="Tag"/> 而不是 <c>Type Page</c>（对比 <see cref="ToolDef"/>）：
///    实验性功能挂在左侧导航的「实验性功能」分组下，点卡片必须走
///    <c>ShellPage.NavigateTo(tag)</c> 才能把左侧高亮一起带走；直接 <c>Frame.Navigate</c> 会跳页但高亮留在原地。
/// </summary>
public sealed class ExperimentalFeature
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>一句话说明（卡片第二行，别写长）。</summary>
    public string Desc { get; init; } = "";

    /// <summary>SEGOEICONS 字形码。</summary>
    public string Glyph { get; init; } = "";

    /// <summary>导航 tag（<c>ShellPage.NavigateTagCore</c> 里要有对应的 case）。</summary>
    public string Tag { get; init; } = "";

    /// <summary>无障碍 / 调试用。</summary>
    public override string ToString() => Name;
}
