using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KaoyanPlanner.WPF.Controls;

/// <summary>
/// 统一的界面动效助手（Codex 风：短、柔、可打断，不弹跳过头）。
/// 所有窗口/面板入场都走这里，保证时长与缓动手感全局一致。
/// </summary>
public static class UiMotion
{
    /// <summary>标准控件/面板入场：透明度 0→1 + 从 96% 轻微放大落位（160ms CubicEase EaseOut）。</summary>
    public static void FadeScaleIn(FrameworkElement element, double fromScale = 0.96, int ms = 160)
    {
        if (element is null) return;
        var st = new ScaleTransform(fromScale, fromScale);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = st;
        element.Opacity = 0;

        void Run()
        {
            // 每次呈现都从起点开始（窗口二次 Show 也正确）
            st.ScaleX = st.ScaleY = fromScale;
            element.Opacity = 0;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, DoubleTo(1, ms, ease));
            st.BeginAnimation(ScaleTransform.ScaleYProperty, DoubleTo(1, ms, ease));
            element.BeginAnimation(UIElement.OpacityProperty, DoubleTo(1, ms, ease));
        }

        if (element.IsLoaded) Run();
        else element.Loaded += (_, _) => Run();
    }

    /// <summary>浮层上移淡入（提醒气泡等）：从 dy 像素下方淡入归位。</summary>
    public static void FadeSlideUp(FrameworkElement element, double dy = 8, int ms = 180)
    {
        if (element is null) return;
        var tt = new TranslateTransform(0, dy);
        element.RenderTransform = tt;
        element.Opacity = 0;

        void Run()
        {
            tt.Y = dy;
            element.Opacity = 0;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            tt.BeginAnimation(TranslateTransform.YProperty, DoubleTo(0, ms, ease));
            element.BeginAnimation(UIElement.OpacityProperty, DoubleTo(1, ms, ease));
        }

        if (element.IsLoaded) Run();
        else element.Loaded += (_, _) => Run();
    }

    private static DoubleAnimation DoubleTo(double to, int ms, IEasingFunction? ease = null)
        => new()
        {
            To = to,
            Duration = System.TimeSpan.FromMilliseconds(ms),
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        };

    /// <summary>把一个 SolidColorBrush 的颜色平滑补间到目标色（120ms）——代码动态建列表行的悬停用。
    /// 注意：必须传入自有（非冻结、非共享资源）画刷，否则无法动画。</summary>
    public static void TweenColor(SolidColorBrush brush, Color to, int ms = 120)
    {
        if (brush is null) return;
        var anim = new ColorAnimation
        {
            To = to,
            Duration = System.TimeSpan.FromMilliseconds(ms),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }
}
