using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KaoyanPlanner.WPF.Services;
// UseWindowsForms 全局导入 System.Windows.Forms/System.Drawing → 文件级别名指向 WPF
using Cursors = System.Windows.Input.Cursors;
using Dock = System.Windows.Controls.Dock;

namespace KaoyanPlanner.WPF.Views.Tabs;

/// <summary>
/// 提醒页签：定时提醒（HH:mm + 标签，开/关、删除、按时间排序）+ 未完成任务提醒配置。
/// 直接读写 data.json["reminders"] / ["unfinished_reminder"]，镜像 widgets.py ReminderTab。
/// 触发由 HeartbeatService 驱动，这里只管编辑与摘要。
/// </summary>
public partial class ReminderTab : UserControl
{
    private readonly DataStore _store;
    private readonly HeartbeatService _heartbeat;
    private bool _loading = true;

    public ReminderTab(DataStore store, HeartbeatService heartbeat)
    {
        InitializeComponent();
        _store = store;
        _heartbeat = heartbeat;

        var cfg = DataStore.GetObj(_store.Data, "unfinished_reminder");
        unfinishedCheck.IsChecked = DataStore.GetBool(cfg?["enabled"], true);
        intervalStepper.Value = (int)Math.Clamp(DataStore.GetInt(cfg?["interval_min"], 60), 10, 180);
        _loading = false;

        // Click（非 Checked）防重入；程序化赋值不触发
        unfinishedCheck.Click += UnfinishedCheck_Click;
        timeBox.EnterPressed += Add;
        labelInput.KeyDown += LabelInput_KeyDown;

        _heartbeat.DayChanged += Refresh;   // 跨日：下次提醒摘要的「今天/明天」重算
        Refresh();
    }

    // ------------------------------------------------------------ 添加

    private void Add_Click(object sender, RoutedEventArgs e) => Add();

    private void LabelInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Add(); e.Handled = true; }
    }

    private void Add()
    {
        string? timeStr = timeBox.ValidTime();
        if (timeStr is null)
        {
            timeBox.Reset();   // 非法时间 → 回退默认，不添加
            return;
        }
        string label = labelInput.Text.Trim();
        if (label.Length == 0) label = "提醒";

        var reminders = GetReminders();
        foreach (var n in reminders)
            if (n is JsonObject r &&
                DataStore.GetString(r["time"]) == timeStr &&
                DataStore.GetString(r["label"]) == label)
            {
                MessageBox.Show("这个提醒已经存在。", "添加提醒",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

        reminders.Add(new JsonObject
        {
            ["time"] = timeStr,
            ["label"] = label,
            ["enabled"] = true,
        });
        SortReminders();
        _store.Save();
        labelInput.Clear();
        timeBox.Reset();
        Refresh();
    }

    // ------------------------------------------------------------ 列表

    private JsonArray GetReminders()
    {
        if (_store.Data["reminders"] is JsonArray a) return a;
        var created = new JsonArray();
        _store.Data["reminders"] = created;
        return created;
    }

    private void SortReminders()
    {
        var arr = GetReminders();
        var sorted = arr
            .Select(n => n as JsonObject)
            .Where(n => n is not null)
            .OrderBy(n => DataStore.GetString(n!["time"])).ThenBy(n => DataStore.GetString(n!["label"]))
            .Cast<JsonObject>()
            .ToList();
        arr.Clear();
        foreach (var n in sorted) arr.Add(n);
    }

    public void Refresh()
    {
        listPanel.Children.Clear();
        var reminders = GetReminders();
        bool hasAny = reminders.Count > 0;
        listPanel.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;
        emptyLbl.Visibility = hasAny ? Visibility.Collapsed : Visibility.Visible;

        for (int i = 0; i < reminders.Count; i++)
            if (reminders[i] is JsonObject r)
            {
                int idx = i;
                listPanel.Children.Add(BuildRow(r, idx));
            }
        UpdateSummary();
    }

    private Border BuildRow(JsonObject r, int index)
    {
        bool on = DataStore.GetBool(r["enabled"], true);

        var dock = new DockPanel { LastChildFill = true };
        var card = new Border
        {
            Style = (Style)FindResource("CardStyle"),
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(10, 6, 6, 6),
            Child = dock,
        };

        // 删除 ✕（右）
        var del = new Button
        {
            Style = (Style)FindResource("DangerIconButtonStyle"),
            Content = "✕",
            ToolTip = "删除此提醒",
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(del, Dock.Right);
        int delIdx = index;
        del.Click += (_, _) =>
        {
            GetReminders().RemoveAt(delIdx);
            _store.Save();
            Refresh();
        };
        dock.Children.Add(del);

        // 时间（accent）
        var time = new TextBlock
        {
            Text = DataStore.GetString(r["time"]),
            Style = (Style)FindResource("MetadataText"),
            Foreground = (Brush)FindResource("AccentStrongBrush"),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        DockPanel.SetDock(time, Dock.Left);
        dock.Children.Add(time);

        // 开/关 36×24
        var toggle = new Button
        {
            Width = 36,
            Height = 24,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Content = on ? "开" : "关",
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        DockPanel.SetDock(toggle, Dock.Left);
        if (on)
        {
            toggle.Background = (Brush)FindResource("AccentBrush");
            toggle.BorderBrush = (Brush)FindResource("AccentBrush");
            toggle.Foreground = Brushes.White;
        }
        else
        {
            toggle.Background = (Brush)FindResource("ElevatedBrush");
            toggle.BorderBrush = (Brush)FindResource("BorderBrush");
            toggle.Foreground = (Brush)FindResource("TextSecondaryBrush");
        }
        int tIdx = index;
        toggle.Click += (_, _) =>
        {
            if (GetReminders()[tIdx] is JsonObject rem)
            {
                rem["enabled"] = !DataStore.GetBool(rem["enabled"], true);
                _store.Save();
            }
            Refresh();
        };
        dock.Children.Add(toggle);

        // 标签（填满）
        dock.Children.Add(new TextBlock
        {
            Text = DataStore.GetString(r["label"]),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });

        return card;
    }

    private void UpdateSummary()
    {
        var reminders = GetReminders();
        bool anyEnabled = reminders.Any(n => n is JsonObject r && DataStore.GetBool(r["enabled"], true));
        if (!anyEnabled)
        {
            nextLbl.Text = "没有开启中的定时提醒";
            return;
        }
        var next = ReminderService.NextEnabled(reminders, DateTime.Now);
        if (next is null)
        {
            nextLbl.Text = "没有可用的定时提醒";
            return;
        }
        nextLbl.Text = $"下次提醒：{next.Value.day} {next.Value.hhmm} · {next.Value.label}";
    }

    // ------------------------------------------------------------ 未完成任务提醒配置

    private void UnfinishedCheck_Click(object sender, RoutedEventArgs e) => SaveUnfinishedConfig();

    private void IntervalStepper_Changed(int value) => SaveUnfinishedConfig();

    private void SaveUnfinishedConfig()
    {
        if (_loading) return;
        var cfg = DataStore.GetObj(_store.Data, "unfinished_reminder");
        if (cfg is null)
        {
            cfg = new JsonObject();
            _store.Data["unfinished_reminder"] = cfg;
        }
        cfg["enabled"] = unfinishedCheck.IsChecked == true;
        cfg["interval_min"] = Math.Clamp(intervalStepper.Value, 10, 180);
        _store.Save();
        _heartbeat.UpdateUnfinishedTimer();
    }
}
