using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KaoyanPlanner.WPF.Controls;
using KaoyanPlanner.WPF.Services;
using RadioButton = System.Windows.Controls.RadioButton;

namespace KaoyanPlanner.WPF.Views.Dialogs;

/// <summary>
/// 添加提醒弹窗：步骤 1 小型日历选日期 → 步骤 2 时间段（开始/结束 + 间隔）+ 事务 + 紧急程度。
/// 选完日期后时间配置区丝滑淡入；添加后通过 CreatedReminder 返回新提醒对象（调用方落盘）。
/// </summary>
public partial class AddReminderDialog : Window
{
    private static readonly (string Text, long Value)[] IntervalPresets =
    {
        ("仅提醒一次", 0),
        ("每 5 分钟", 5),
        ("每 10 分钟", 10),
        ("每 15 分钟", 15),
        ("每 30 分钟", 30),
        ("每 45 分钟", 45),
        ("每 60 分钟", 60),
        ("每 90 分钟", 90),
        ("每 120 分钟", 120),
    };

    private readonly DataStore _store;
    private bool _timeShown;
    private bool _loading = true;

    /// <summary>添加成功后带出的新提醒 JsonObject（含 date/start/end/interval_min/label/priority/enabled）。</summary>
    public JsonObject? CreatedReminder { get; private set; }

    /// <summary>
    /// 创建弹窗。initialDate = 预选日期；presetStart / presetEnd（HH:mm，可空）=
    /// 从时间表格子进入时的时间预填；为空则默认「当前时间 ±1 小时」。
    /// </summary>
    public AddReminderDialog(DataStore store, DateTime initialDate, string? presetStart = null, string? presetEnd = null)
    {
        InitializeComponent();
        _store = store;

        calendar.DisplayedMonth = new DateTime(initialDate.Year, initialDate.Month, 1);
        calendar.SelectedDate = initialDate;

        foreach (var (text, value) in IntervalPresets)
        {
            var item = new ComboBoxItem { Content = text, Tag = value };
            intervalBox.Items.Add(item);
            if (value == 30) intervalBox.SelectedItem = item;
        }

        startBox.Reset();
        endBox.Reset();
        if (presetStart is not null && ReminderService.TryParseHm(presetStart, out _, out _))
            startBox.Value = presetStart;
        else
            startBox.Value = DateTime.Now.AddHours(-1).ToString("HH:mm", CultureInfo.InvariantCulture);
        if (presetEnd is not null && ReminderService.TryParseHm(presetEnd, out _, out _))
            endBox.Value = presetEnd;
        else
            endBox.Value = DateTime.Now.AddHours(1).ToString("HH:mm", CultureInfo.InvariantCulture);
        UpdateDateHint();
        _loading = false;

        calendar.DateSelected += Calendar_DateSelected;
        labelInput.KeyDown += LabelInput_KeyDown;
        ApplyPriorityVisual(priNormal);   // 初始「普通」即蓝底白字
        Loaded += (_, _) => UiMotion.FadeScaleIn(card, 0.95, 180);
    }

    private static readonly string[] WeekdayCn = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

    private void UpdateDateHint()
    {
        if (calendar.SelectedDate is DateTime d)
            dateHint.Text = $"{d.Month}月{d.Day}日 · {WeekdayCn[(int)d.DayOfWeek]}";
    }

    private void Calendar_DateSelected(DateTime date)
    {
        UpdateDateHint();
        if (!_timeShown)
        {
            _timeShown = true;
            timeSection.Visibility = Visibility.Visible;
            UiMotion.FadeSlideUp(timeSection, 10, 220);
        }
        // 日期变化时校验提示联动
        ValidateRange();
    }

    private void LabelInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Add(); e.Handled = true; }
    }

    private void Pri_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton rb) return;
        ApplyPriorityVisual(rb);
    }

    private void ApplyPriorityVisual(RadioButton selected)
    {
        // 选中态配色：普通=蓝 / 重要=橙 / 紧急=红（白字）
        Brush bg;
        switch (selected.Name)
        {
            case "priUrgent":
                bg = (Brush)FindResource("DangerBrush");
                break;
            case "priImportant":
                bg = (Brush)FindResource("WarnBrush");
                break;
            default:
                bg = (Brush)FindResource("AccentBrush");
                break;
        }
        selected.Background = bg;
        selected.BorderBrush = bg;
        selected.Foreground = Brushes.White;
        // 复位未选中的其他两项
        foreach (var other in new[] { priNormal, priImportant, priUrgent })
        {
            if (ReferenceEquals(other, selected)) continue;
            other.Background = (Brush)FindResource("InputBrush");
            other.BorderBrush = (Brush)FindResource("BorderBrush");
            other.Foreground = (Brush)FindResource("TextSecondaryBrush");
        }
    }

    private bool ValidateRange()
    {
        string? s = startBox.ValidTime();
        string? e = endBox.ValidTime();
        bool ok = s is not null && e is not null && string.CompareOrdinal(e, s) > 0;
        rangeError.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        return ok;
    }

    private void Add_Click(object sender, RoutedEventArgs e) => Add();

    private void Add()
    {
        if (calendar.SelectedDate is not DateTime date) return;
        if (!ValidateRange()) return;
        string? start = startBox.ValidTime();
        string? end = endBox.ValidTime();
        if (start is null || end is null) return;

        long interval = intervalBox.SelectedItem is ComboBoxItem { Tag: long v } ? v : 0;
        string label = labelInput.Text.Trim();
        if (label.Length == 0) label = "提醒";
        int priority = priUrgent.IsChecked == true ? 2 : priImportant.IsChecked == true ? 1 : 0;

        CreatedReminder = new JsonObject
        {
            ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["start"] = start,
            ["end"] = end,
            ["interval_min"] = interval,
            ["label"] = label,
            ["priority"] = priority,
            ["enabled"] = true,
        };
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }
}
