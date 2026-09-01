using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KaoyanPlanner.WPF.Services;

namespace KaoyanPlanner.WPF.Views.Pet;

/// <summary>
/// 宠物头顶的对话气泡（可含图片缩略图）：短暂显示后自动消失。
/// 独立透明置顶窗口，位置在桌宠上方；出现动效 = 从 0.15 快速放大，BackEase 轻微过冲回弹（Q 弹，镜像 pet.py OutBack）。
/// </summary>
public partial class PetBubbleWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly Action? _onClosed;
    private readonly double _petLeft;
    private readonly double _petTop;
    private readonly double _petWidth;

    /// <param name="pet">桌宠窗口（用于定位）。</param>
    /// <param name="text">气泡文本。</param>
    /// <param name="image">可选图片缩略图。</param>
    /// <param name="timeoutMs">自动消失毫秒数。</param>
    public PetBubbleWindow(PetWindow pet, string text, BitmapSource? image = null, int timeoutMs = 10000, Action? onClosed = null)
    {
        InitializeComponent();
        _onClosed = onClosed;
        _petLeft = pet.Left;
        _petTop = pet.StatusBarTop();
        _petWidth = pet.ActualWidth;
        bubbleText.Text = text;
        if (image is not null)
        {
            imgPreview.Source = image;
            imgPreview.Visibility = Visibility.Visible;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(timeoutMs) };
        _timer.Tick += (_, _) => { _timer.Stop(); AutoClose(); };
        _timer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UpdateLayout();   // SizeToContent 窗口此刻才有实际尺寸

        // 定位到桌宠上方（状态条上方，避免遮挡）
        double refTop = _petTop;
        double x = _petLeft + _petWidth / 2 - ActualWidth / 2;
        double y = refTop - ActualHeight - 10;
        var wa = SystemParameters.WorkArea;
        x = Math.Max(wa.Left + 4, Math.Min(x, wa.Right - ActualWidth - 4));
        y = Math.Max(wa.Top + 4, y);
        Left = x;
        Top = y;

        // Q 弹出现：0.15 → 1.0，200ms，BackEase 轻微过冲再回落 + 淡入。
        // 坑：动画必须打在 ScaleTransform 实例上（card.BeginAnimation(ScaleTransform.ScaleXProperty,…)
        // 动的是 Border 上一个不参与渲染的值，卡片会永远卡在 0.15 缩放 → 气泡看不见、也没动效）。
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        var st = new ScaleTransform(0.15, 0.15);
        card.RenderTransform = st;
        var grow = new DoubleAnimation(0.15, 1.0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 1.7 },
        };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        card.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(150)));
    }

    private void AutoClose()
    {
        _onClosed?.Invoke();
        Close();
    }
}
