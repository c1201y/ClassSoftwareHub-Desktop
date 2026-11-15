using System;
using Microsoft.UI.Xaml.Controls;

namespace ClassSoftwareHub.Desktop.Views.Submit;

/// <summary>
/// 步骤页的统一接口。导航壳在前进前问 <see cref="Validate"/>，页面自己负责提示。
///
/// <para>
/// ⛔ 刻意不用「校验不过就把按钮灰掉」：灰按钮不告诉用户缺什么。
/// 这里让按钮照常可点，点了由页面给出具体提示并聚焦到缺失项。
/// </para>
/// </summary>
public interface ISubmitStep
{
    /// <summary>本步能否继续；返回 null 表示通过。</summary>
    string? Validate();

    /// <summary>把错误显示在页面自己的提示条里。</summary>
    void ShowError(string message);
}

/// <summary>
/// 一次投稿向导运行的全部上下文：步骤表、当前位置、草稿。
///
/// <para>
/// 结构照 OOBE 的 <c>OobeSession</c>：窗口只放一个 Frame，页面类型排成一条线，
/// 前进/后退就是下标 ±1；每个页面标 <c>NavigationCacheMode="Required"</c>，
/// 往回翻时输入内容与滚动位置都还在。
/// </para>
///
/// <para>
/// ⛔ 与 OOBE 的三处语义差别（别照搬）：投稿可以随时关窗（自动存草稿）、
/// 成功后停在结果页不自动关窗、全过程不写任何设置。
/// </para>
/// </summary>
public sealed class SubmitSession
{
    private static readonly Type[] Steps =
    {
        typeof(Steps.SubmitStartPage),
        typeof(Steps.SubmitInfoPage),
        typeof(Steps.SubmitPackagePage),
        typeof(Steps.SubmitExtraPage),
        typeof(Steps.SubmitReviewPage),
    };

    private readonly SubmitWizardWindow _window;

    private SubmitSession(SubmitWizardWindow window, SubmitDraft draft)
    {
        _window = window;
        Draft = draft;
    }

    public static SubmitSession? Current { get; private set; }

    public SubmitDraft Draft { get; }

    public int Index { get; private set; }

    public int StepCount => Steps.Length;

    public bool IsLast => Index >= Steps.Length - 1;

    /// <summary>已经提交成功：底部按钮变成「关闭」，不再允许往前翻。</summary>
    public bool Completed { get; private set; }

    /// <summary>窗口句柄，选文件框要它做 owner。</summary>
    public IntPtr WindowHandle => _window.WindowHandle;

    /// <summary>第 <paramref name="index"/> 步对应的页面类型（窗口换页用）。</summary>
    public Type StepTypeAt(int index) => Steps[Math.Clamp(index, 0, Steps.Length - 1)];

    public static SubmitSession Attach(SubmitWizardWindow window, SubmitDraft draft)
        => Current = new SubmitSession(window, draft);

    public static void Detach() => Current = null;

    /// <summary>窗口构造时先导航到第一页，显示出来就有内容。</summary>
    public void Prepare() => _window.NavigateToStep(0, forward: true, animate: false);

    public void Next()
    {
        if (Completed || IsLast) return;
        GoTo(Index + 1, forward: true);
    }

    public void Back()
    {
        if (Completed || Index == 0) return;
        GoTo(Index - 1, forward: false);
    }

    public void GoTo(int index, bool forward)
    {
        index = Math.Clamp(index, 0, Steps.Length - 1);
        Index = index;
        SaveDraft();
        _window.NavigateToStep(index, forward, animate: true);
    }

    /// <summary>从核对页跳回某一步补内容。</summary>
    public void JumpTo(int index)
    {
        Completed = false;
        GoTo(index, forward: false);
    }

    /// <summary>提交成功：清草稿、停在结果页。</summary>
    public void MarkCompleted()
    {
        Completed = true;
        SubmitDraft.ClearDraft();
        _window.RefreshNav();
    }

    /// <summary>「再提交一个」：换一份空草稿，回到第一步。</summary>
    public void Restart()
    {
        Completed = false;
        Draft.Reset();
        Index = 0;
        SubmitDraft.ClearDraft();
        _window.NavigateToStep(0, forward: false, animate: true);
        _window.RefreshNav();
    }

    /// <summary>落草稿。翻页、关窗、提交失败都会调。</summary>
    public void SaveDraft()
    {
        if (Completed) return;
        Draft.Save();
    }

    /// <summary>页面里的状态变了（比如上传中），让底部按钮重读一次。</summary>
    public void RefreshChrome() => _window.RefreshNav();

    public void Close() => _window.CloseWizard();
}
