using System;
using System.Runtime.InteropServices;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Views.Submit.Steps;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassSoftwareHub.Desktop.Views.Submit;














public sealed partial class SubmitWizardWindow : Window
{
    private const int WidthDip = 880;
    private const int HeightDip = 720;

    private static SubmitWizardWindow? _instance;

    private SubmitSession? _session;
    private AppWindow? _appWindow;
    private bool _closed;

    private SubmitWizardWindow()
    {
        InitializeComponent();
        Configure();

        _session = SubmitSession.Attach(this, SubmitDraft.Load() ?? SubmitDraft.CreateEmpty());

        
        StepFrame.Navigated += (_, _) => RefreshNav();
        _session.Prepare();
    }

    
    public static void Show()
    {
        if (_instance is not null)
        {
            _instance.Activate();
            return;
        }

        try
        {
            _instance = new SubmitWizardWindow();
            _instance.Activate();
            AppLog.Info("submit", "投稿向导已打开");
        }
        catch (Exception ex)
        {
            AppLog.Exception("submit", ex, "投稿向导窗口创建失败");
            _instance = null;
        }
    }

    internal IntPtr WindowHandle => WindowNative.GetWindowHandle(this);

    private void Configure()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.Title = "提交软件";

            ModernWindow.ApplyTitleBar(this, TitleBarHost, Root);
            BackdropHost.Apply(this, Root, App.Settings.Current.Backdrop);
            ThemeHost.Apply(Root);

            ApplySize();
            CenterOnScreen();

            Closed += (_, _) =>
            {
                _closed = true;
                SaveAndDetach();
            };
        }
        catch (Exception ex)
        {
            AppLog.Exception("submit", ex, "投稿向导窗口初始化");
        }
    }

    private void ApplySize()
    {
        if (_appWindow is null) return;

        try
        {
            var scale = Scale();
            var work = DisplayArea.Primary.WorkArea;

            
            var maxW = Math.Max(760, work.Width / scale - 40);
            var maxH = Math.Max(520, work.Height / scale - 40);

            _appWindow.ResizeClient(new SizeInt32(
                (int)Math.Round(Math.Clamp(WidthDip, 760, maxW) * scale),
                (int)Math.Round(Math.Clamp(HeightDip, 520, maxH) * scale)));
        }
        catch (Exception ex)
        {
            AppLog.Warn("submit", "投稿向导尺寸设置失败：" + ex.Message);
        }
    }

    private void CenterOnScreen()
    {
        if (_appWindow is null) return;

        try
        {
            var work = DisplayArea.Primary.WorkArea;
            var size = _appWindow.Size;
            _appWindow.Move(new PointInt32(
                work.X + Math.Max(0, (work.Width - size.Width) / 2),
                work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
        }
        catch (Exception ex)
        {
            AppLog.Warn("submit", "投稿向导定位失败：" + ex.Message);
        }
    }

    

    internal void NavigateToStep(int index, bool forward, bool animate)
    {
        if (_closed || _session is null) return;

        try
        {
            NavigationTransitionInfo transition = animate
                ? new SlideNavigationTransitionInfo
                {
                    Effect = forward
                        ? SlideNavigationTransitionEffect.FromRight
                        : SlideNavigationTransitionEffect.FromLeft,
                }
                : new SuppressNavigationTransitionInfo();

            StepFrame.Navigate(_session.StepTypeAt(index), null, transition);
        }
        catch (Exception ex)
        {
            AppLog.Exception("submit", ex, "投稿向导弹换页失败：" + index);
        }
    }

    
    internal void RefreshNav()
    {
        if (_session is null) return;

        var index = _session.Index;
        StepText.Text = $"第 {index + 1} 步，共 {_session.StepCount} 步";

        BackNavButton.Visibility = index > 0 && !_session.Completed
            ? Visibility.Visible
            : Visibility.Collapsed;

        NextNavButton.Content = _session.Completed
            ? "关闭"
            : _session.IsLast ? "提交" : index == 0 ? "开始填写" : "下一步";

        
        FooterHint.Text = _session.Completed ? "" : "关闭窗口会自动保存草稿";
    }

    private void NavBack_Click(object sender, RoutedEventArgs e) => _session?.Back();

    private async void NavNext_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;

        if (_session.Completed)
        {
            CloseWizard();
            return;
        }

        
        if (StepFrame.Content is SubmitReviewPage review)
        {
            NextNavButton.IsEnabled = false;
            try { await review.SubmitAsync(); }
            finally
            {
                NextNavButton.IsEnabled = true;
                RefreshNav();
            }
            return;
        }

        
        if (StepFrame.Content is ISubmitStep step)
        {
            var error = step.Validate();
            if (error is not null)
            {
                step.ShowError(error);
                return;
            }
        }

        _session.Next();
    }

    internal void CloseWizard()
    {
        try { Close(); }
        catch (Exception ex) { AppLog.Warn("submit", "投稿向导关闭失败：" + ex.Message); }
    }

    
    private void SaveAndDetach()
    {
        try { _session?.SaveDraft(); } catch {  }
        SubmitSession.Detach();
        _session = null;
        _instance = null;
    }

    private double Scale()
    {
        try
        {
            var dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch { return 1.0; }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
