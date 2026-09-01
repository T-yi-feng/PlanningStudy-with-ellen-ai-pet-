using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace KaoyanPlanner.WPF.Views.Dialogs;

/// <summary>
/// 轻量提醒弹窗（M4 先服务喝水提醒；M5 扩展成提醒栈/音效/托盘气泡）。
/// 右下角定位、WS_EX_NOACTIVATE 防抢焦、180ms 展开、10s 自动关、点击即关。
/// </summary>
public partial class ReminderPopupWindow : Window
{
    private const int GwlExstyle = -20;
    private const long WsExNoactivate = 0x08000000L;

    /// <summary>当前在屏弹窗栈（右下角向上堆叠，镜像 widgets.py ReminderPopup._stack）。</summary>
    private static readonly List<ReminderPopupWindow> Stack = new();

    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    public ReminderPopupWindow(string title, string message)
    {
        InitializeComponent();
        titleText.Text = title;
        messageText.Text = message;

        _closeTimer.Tick += (_, _) => Close();
        _closeTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 不抢占焦点：弹窗出现时用户正在专注/打字
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GwlExstyle);
        _ = SetWindowLong(hwnd, GwlExstyle, (int)(ex | WsExNoactivate));
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // 右下角（避开任务栏），已有弹窗向上堆叠 8px
        Stack.Add(this);
        var wa = SystemParameters.WorkArea;
        double offset = 0;
        foreach (var p in Stack)
            if (!ReferenceEquals(p, this) && p.IsVisible)
                offset += p.ActualHeight + 8;
        Left = wa.Right - ActualWidth - 16;
        Top = wa.Bottom - ActualHeight - 16 - offset;
        if (Top < wa.Top) Top = wa.Top;   // 堆叠超出屏幕顶部时钳制

        // 180ms 展开（PanelOpen 语义：淡入 + 8px 上移）。
        // 坑：动画必须打在内容根元素 card 上，绝不能打 Window 本身——
        // WPF 强制 Window.RenderTransform 只能为 Identity（Window.CoerceRenderTransform
        // 对可见窗口赋非 Identity 变换会抛 InvalidOperationException「转换器 Window 无效」），
        // 一赋就崩溃、整个进程（含主窗+桌宠）消失。历史事故：2026-08-18 提醒弹窗崩进程。
        card.RenderTransform = new TranslateTransform(0, 8);
        card.Opacity = 0;   // 动画未起效的首帧先置透明，防闪一下
        var sb = new Storyboard();
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
        Storyboard.SetTarget(fade, card);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        sb.Children.Add(fade);
        var slide = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(slide, card);
        Storyboard.SetTargetProperty(slide, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
        sb.Children.Add(slide);
        sb.Begin(card);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        Stack.Remove(this);
        base.OnClosed(e);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
