using System;
using ClassSoftwareHub.Desktop.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Controls;

/// <summary>
/// 页面标题旁的「实验性功能」标记。
///
/// <para>
/// 显示与否**不写死在页面里**：页面只声明自己是谁（<see cref="FeatureId"/>），
/// 控件去 <see cref="ExperimentalFeatureCatalog"/> 里查 —— 查得到就显示，查不到就整块收起。
/// 这样功能转正时**只要从清单里删掉那一条**，标志自动消失，不会留下"已经转正了还挂着实验性"
/// （2026-11-03 之前就是这么乱的：四个页面各抄一份 Border，后来新加的五个页面全忘了加）。
/// </para>
///
/// <para>
/// ⛔ 加新的实验性页面时：标题横向 StackPanel 里放一个
/// <c>&lt;controls:ExperimentalBadge FeatureId="导航 tag" /&gt;</c>，FeatureId 必须和
/// <c>ExperimentalFeatureCatalog</c> 里的 Tag 一字不差。
/// </para>
/// </summary>
public sealed partial class ExperimentalBadge : UserControl
{
    public static readonly DependencyProperty FeatureIdProperty = DependencyProperty.Register(
        nameof(FeatureId), typeof(string), typeof(ExperimentalBadge),
        new PropertyMetadata("", OnFeatureIdChanged));

    /// <summary>这一页在 <see cref="ExperimentalFeatureCatalog"/> 里的导航 tag。</summary>
    public string FeatureId
    {
        get => (string)GetValue(FeatureIdProperty);
        set => SetValue(FeatureIdProperty, value);
    }

    public ExperimentalBadge()
    {
        InitializeComponent();
        Loaded += (_, _) => Update();
    }

    private static void OnFeatureIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // XAML 里设属性时控件还没进视觉树，但改 Visibility 本身是安全的
        if (d is ExperimentalBadge badge) badge.Update();
    }

    private void Update()
    {
        Visibility = ExperimentalFeatureCatalog.Find(FeatureId) is null
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
