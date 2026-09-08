using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace KaoyanPlanner.WPF.Controls;

/// <summary>
/// 统一 Toast：主窗右下角轻量提示卡片，支持可选「撤销」按钮。
/// 单个 Popup 复用，新消息替换旧消息；动效遵循全站约定（130ms 淡入 / 200ms 淡出，ReduceMotion 时直出直隐）。
/// </summary>
public static class ToastHost
{
    private static Popup? _popup;
    private static Border? _card;
    private static TextBlock? _msg;
    private static Button? _actionBtn;
    private static Action? _lastAction;
    private static DispatcherTimer? _timer;

    public static void Show(Window? owner, string message, string actionText = "", Action? action = null)
    {
        if (owner == null || !owner.IsLoaded) return;
        EnsurePopup(owner);
        _msg!.Text = message;

        if (action != null && actionText.Length > 0)
        {
            _actionBtn!.Content = actionText;
            _actionBtn.Visibility = Visibility.Visible;
            _lastAction = action;
        }
        else
        {
            _actionBtn!.Visibility = Visibility.Collapsed;
            _lastAction = null;
        }

        // 右下角：owner 视口右下 - 卡片宽(约 300) - 24 边距
        double w = owner.ActualWidth > 0 ? owner.ActualWidth : 1200;
        double h = owner.ActualHeight > 0 ? owner.ActualHeight : 800;
        _popup!.HorizontalOffset = w - 312;
        _popup.VerticalOffset = h - 64;
        _popup.IsOpen = true;

        bool reduced = !SystemParameters.ClientAreaAnimation;
        _card!.Opacity = 0;
        if (reduced)
        {
            _card.Opacity = 1;
            ScheduleClose(2600);
            return;
        }
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        _card.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        ScheduleClose(2600);
    }

    private static void ScheduleClose(int stayMs)
    {
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(stayMs) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            if (!SystemParameters.ClientAreaAnimation) { _popup!.IsOpen = false; return; }
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            fadeOut.Completed += (_, _) => _popup!.IsOpen = false;
            _card!.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        };
        _timer.Start();
    }

    private static void EnsurePopup(Window owner)
    {
        if (_popup != null) return;
        _popup = new Popup
        {
            AllowsTransparency = true,
            Placement = PlacementMode.Relative,
            PlacementTarget = owner,
            StaysOpen = true,
            IsHitTestVisible = true,
        };
        _card = new Border
        {
            Background = (Brush)owner.FindResource("ElevatedBrush"),
            BorderBrush = (Brush)owner.FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8, 12, 8),
            Effect = (System.Windows.Media.Effects.Effect)owner.FindResource("CardShadowEffect"),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _msg = new TextBlock
        {
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 240,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)owner.FindResource("TextPrimaryBrush"),
        };
        Grid.SetColumn(_msg, 0);
        _actionBtn = new Button
        {
            Style = (Style)owner.FindResource("GhostIconButtonStyle"),
            Content = "撤销",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)owner.FindResource("AccentBrush"),
        };
        _actionBtn.Click += (_, _) => _lastAction?.Invoke();
        Grid.SetColumn(_actionBtn, 1);
        grid.Children.Add(_msg);
        grid.Children.Add(_actionBtn);
        _card.Child = grid;
        _popup.Child = _card;
    }
}
