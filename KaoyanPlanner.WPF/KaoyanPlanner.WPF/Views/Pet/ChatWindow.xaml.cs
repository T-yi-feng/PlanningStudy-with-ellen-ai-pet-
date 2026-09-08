using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaoyanPlanner.WPF.Native;
// UseWindowsForms 会隐式导入 System.Windows.Forms + System.Drawing → 命名冲突全局别名
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace KaoyanPlanner.WPF.Views.Pet;

/// <summary>
/// 完整聊天记录窗：气泡消息 + 输入框（可指令改计划 / AI 闲聊 / 发图）。
/// 消息只在这里渲染；发送/回复处理全部交给 PetWindow（唯一拥有对话管线）。
/// 可拖动（头部）、发送时不抢焦点到桌面宠下方输入框。
/// </summary>
public partial class ChatWindow : Window
{
    private readonly PetWindow _pet;
    private Vector _dragOffset;   // Point - Point → Vector（WPF）

    public ChatWindow(PetWindow pet)
    {
        InitializeComponent();
        _pet = pet;
        DwmInterop.ApplyRoundedCorners(this);
        // 每次呼出聊天窗都重播一次柔弹入场（该窗是隐藏而非关闭）
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Controls.UiMotion.FadeScaleIn(rootCard, fromScale: 0.98, ms: 140);
        };
        PositionNearPet();

        AppendMessage("pet", Brand.PetGreeting);
    }

    private void PositionNearPet()
    {
        var wa = SystemParameters.WorkArea;
        Left = Math.Max(wa.Left + 8, Math.Min(_pet.Left - (Width - _pet.ActualWidth) / 2, wa.Right - Width - 8));
        Top = Math.Max(wa.Top + 8, Math.Min(_pet.Top - Height - 12, wa.Bottom - Height - 8));
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 镜像 pet.py：防止关闭聊天窗时把程序退掉，只隐藏
        e.Cancel = true;
        Hide();
    }

    // ------------------------------------------------------------ 拖动

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 坑：必须用屏幕坐标（PointToScreen），不能把窗口相对坐标与 Left/Top 混算——
        // 拖拽时窗口移动 → 相对坐标跟着变 → 反馈循环抖动（与黑板同款 bug）。
        _dragOffset = PointToScreen(e.GetPosition(this)) - new Point(Left, Top);
    }

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && _dragOffset != default)
        {
            var g = PointToScreen(e.GetPosition(this));
            Left = g.X - _dragOffset.X;
            Top = g.Y - _dragOffset.Y;
        }
    }

    private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _dragOffset = default;

    // ------------------------------------------------------------ 消息

    public void AppendMessage(string role, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        AppendBubble(role, text, null);
    }

    public void AppendImage(string role, string b64)
    {
        if (string.IsNullOrEmpty(b64)) return;
        try
        {
            var src = PngFromB64(b64);
            if (src is not null) AppendBubble(role, null, src);
        }
        catch
        {
            // 图片解码失败就忽略
        }
    }

    private void AppendBubble(string role, string? text, BitmapSource? img)
    {
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 7, 10, 7),
            MaxWidth = 230,
        };
        if (role == "me")
        {
            bubble.Background = (Brush)FindResource("AccentBrush");
            var sp = new StackPanel();
            if (img is not null)
                sp.Children.Add(new Image { Source = img, Width = 140, Stretch = Stretch.Uniform });
            if (!string.IsNullOrEmpty(text))
                sp.Children.Add(new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.White,
                });
            bubble.Child = sp;
        }
        else
        {
            bubble.Background = (Brush)FindResource("InputBrush");
            var sp = new StackPanel();
            if (img is not null)
                sp.Children.Add(new Image { Source = img, Width = 140, Stretch = Stretch.Uniform });
            if (!string.IsNullOrEmpty(text))
                sp.Children.Add(new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)FindResource("TextPrimaryBrush"),
                });
            bubble.Child = sp;
        }

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = role == "me" ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 4),
        };
        row.Children.Add(bubble);
        msgHost.Children.Add(row);
        scroll.ScrollToEnd();
    }

    private static BitmapSource? PngFromB64(string b64)
    {
        byte[] bytes = Convert.FromBase64String(b64);
        using var ms = new MemoryStream(bytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    // ------------------------------------------------------------ 输入

    public void FocusInput()
    {
        Show();
        Activate();
        chatInput.Focus();
    }

    private void ChatInput_TextChanged(object sender, TextChangedEventArgs e)
        => chatPlaceholder.Visibility = chatInput.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;

    private void ChatInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Send_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Hide();
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        string text = chatInput.Text.Trim();
        chatInput.Clear();
        if (text.Length == 0) return;
        _pet.SendChat(text, null);
    }

    public void SetBusy(bool busy)
    {
        busyLbl.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        chatInput.IsEnabled = !busy;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var win = _pet.MainWindow;
        if (win is not null)
        {
            // 设置不再是浮窗：回主窗口 → 就地转场进设置页「通用」分栏
            win.ShowFromTray();
            win.NavigateToSettings("general");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
}
