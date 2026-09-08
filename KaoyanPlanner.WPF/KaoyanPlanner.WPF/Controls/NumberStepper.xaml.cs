using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KaoyanPlanner.WPF.Controls;

/// <summary>
/// 数字步进器：− [输入] ＋。可编辑输入，回车/失焦提交，夹在两侧步进按钮中。
/// Value 为当前整数；步进受 Min/Max 约束，按钮按下有 0.9 缩放手感（继承 GhostIconButtonStyle）。
/// </summary>
public partial class NumberStepper : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(int), typeof(NumberStepper),
            new FrameworkPropertyMetadata(10, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnValueChanged, CoerceValue));

    public static readonly DependencyProperty MinProperty =
        DependencyProperty.Register(nameof(Min), typeof(int), typeof(NumberStepper),
            new FrameworkPropertyMetadata(1, OnBoundChanged));

    public static readonly DependencyProperty MaxProperty =
        DependencyProperty.Register(nameof(Max), typeof(int), typeof(NumberStepper),
            new FrameworkPropertyMetadata(600, OnBoundChanged));

    public static readonly DependencyProperty StepProperty =
        DependencyProperty.Register(nameof(Step), typeof(int), typeof(NumberStepper),
            new FrameworkPropertyMetadata(5));

    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Min { get => (int)GetValue(MinProperty); set => SetValue(MinProperty, value); }
    public int Max { get => (int)GetValue(MaxProperty); set => SetValue(MaxProperty, value); }
    public int Step { get => (int)GetValue(StepProperty); set => SetValue(StepProperty, value); }

    public event Action<int>? ValueChanged;

    private bool _syncing;

    public NumberStepper()
    {
        InitializeComponent();
    }

    private static object CoerceValue(DependencyObject d, object baseValue)
    {
        var s = (NumberStepper)d;
        int v = (int)baseValue;
        return Math.Clamp(v, s.Min, s.Max);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (NumberStepper)d;
        s.SyncBox();
        s.ValueChanged?.Invoke(s.Value);
    }

    private static void OnBoundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (NumberStepper)d;
        s.CoerceValue(ValueProperty);
    }

    private void SyncBox()
    {
        if (_syncing) return;
        _syncing = true;
        valBox.Text = Value.ToString(CultureInfo.InvariantCulture);
        _syncing = false;
    }

    private void Nudge(int delta)
    {
        int next = Math.Clamp(Value + delta, Min, Max);
        if (next != Value) Value = next;
    }

    private void CommitText()
    {
        if (_syncing) return;
        int parsed = int.TryParse(valBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v : Value;
        Value = Math.Clamp(parsed, Min, Max);   // 非法输入回退旧值
    }

    private void Minus_Click(object sender, RoutedEventArgs e) => Nudge(-Step);
    private void Plus_Click(object sender, RoutedEventArgs e) => Nudge(Step);

    private void Val_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        foreach (char c in e.Text)
            if (!char.IsDigit(c)) { e.Handled = true; return; }
    }

    private void Val_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitText();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Up) { Nudge(Step); e.Handled = true; }
        else if (e.Key == Key.Down) { Nudge(-Step); e.Handled = true; }
    }

    private void Val_LostFocus(object sender, RoutedEventArgs e) => CommitText();
}
