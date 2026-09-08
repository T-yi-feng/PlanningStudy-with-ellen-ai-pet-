using System;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KaoyanPlanner.WPF.Services;
using KaoyanPlanner.WPF.Views.Dialogs;
// UseWindowsForms 全局导入 System.Windows.Forms/System.Drawing → 文件级别名指向 WPF
using Cursors = System.Windows.Input.Cursors;
using Dock = System.Windows.Controls.Dock;

namespace KaoyanPlanner.WPF.Views.Tabs;

/// <summary>
/// 专注计时页：44px 大字正计时 + 开始/暂停 + 重置 + 喝水提醒 + 今日统计卡。
/// 接 FocusTimerService（纯 C# 计时引擎），250ms DispatcherTimer 轮询——即使页签隐藏也继续计时。
/// 配置读写 focus_reminder {enabled, interval_min}，镜像 widgets.py TimerTab。
/// </summary>
public partial class TimerTab : UserControl
{
    private readonly DataStore _store;
    private readonly FocusTimerService _focus;
    private readonly Action _openStats;
    private readonly DispatcherTimer _ui = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _loading = true;
    private string? _selectedPlan;   // null = 不指定计划

    public TimerTab(DataStore store, FocusTimerService focus, Action openStats)
    {
        InitializeComponent();
        _store = store;
        _focus = focus;
        _openStats = openStats;

        // 喝水提醒配置（Click/TextChanged 在 InitializeComponent 后挂，程序化赋值不重入）
        var cfg = DataStore.GetObj(_store.Data, "focus_reminder");
        remindCheck.IsChecked = DataStore.GetBool(cfg?["enabled"], true);
        remindInterval.Text = Math.Max(1, DataStore.GetInt(cfg?["interval_min"], 60))
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

        // 计划选择面板：专注目标 = 长期计划（固定任务）+「不分类」。默认「不分类」，由用户点选；
        // 固定任务增删/改名时重建列表（store.Changed 才触发）
        _selectedPlan = null;
        _focus.CurrentPlan = null;
        RebuildPlanList();
        _store.Changed += RebuildPlanList;

        _loading = false;

        startBtn.Click += StartBtn_Click;
        resetBtn.Click += ResetBtn_Click;
        remindCheck.Click += RemindCheck_Click;
        openStatsBtn.Click += OpenStats_Click;

        _focus.HistoryChanged += UpdateTodayStats;
        _focus.ReminderDue += ShowWaterReminder;

        _ui.Tick += (_, _) =>
        {
            _focus.Poll();
            UpdateTime();
        };
        _ui.Start();

        UpdateTime();
        UpdateTodayStats();
    }

    /// <summary>应用退出前刷掉未落盘的整秒。由 MainWindow.OnClosing 调用。</summary>
    public void Flush() => _focus.Flush();

    // ------------------------------------------------------------ 计时显示

    private void UpdateTime()
    {
        long total = (long)_focus.ElapsedSeconds;
        var ts = TimeSpan.FromSeconds(total);
        timeLbl.Text = $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
    }

    private void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_focus.Running)
        {
            _focus.Pause();
            startBtn.Content = "继续";
            statusLbl.Text = $"已暂停 · 已专注 {Fmt.Minutes(_focus.ElapsedSeconds / 60.0)}";
        }
        else
        {
            _focus.Start();
            startBtn.Content = "暂停";
            statusLbl.Text = "专注中 · 按小时自动统计分布";
        }
    }

    private void ResetBtn_Click(object sender, RoutedEventArgs e)
    {
        _focus.Reset();
        startBtn.Content = "开始";
        statusLbl.Text = "未开始 · 点击开始进入专注";
        UpdateTime();
        UpdateTodayStats();
    }

    // ------------------------------------------------------------ 喝水提醒配置

    private void RemindCheck_Click(object sender, RoutedEventArgs e)
    {
        var cfg = DataStore.GetObj(_store.Data, "focus_reminder")!;
        cfg["enabled"] = remindCheck.IsChecked == true;
        _store.Save();
    }

    private void Interval_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        foreach (char c in e.Text)
            if (!char.IsDigit(c))
            {
                e.Handled = true;
                return;
            }
    }

    private void Interval_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Keyboard.ClearFocus();   // 触发 LostFocus → 提交
    }

    private void Interval_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (!int.TryParse(remindInterval.Text, out int v)) v = 60;
        v = Math.Clamp(v, 10, 240);
        remindInterval.Text = v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var cfg = DataStore.GetObj(_store.Data, "focus_reminder")!;
        cfg["interval_min"] = (long)v;
        _store.Save();
    }

    // ------------------------------------------------------------ 今日统计 + 切页

    private void UpdateTodayStats()
    {
        var hist = DataStore.GetObj(_store.Data, "focus_history");
        var day = hist is not null
            ? hist[DataStore.TodayStr()] as JsonObject
            : null;
        double total = 0;
        int active = 0;
        if (day is not null)
        {
            foreach (var kv in day)
            {
                double v = DataStore.GetDouble(kv.Value);
                if (v > 0)
                {
                    total += v;
                    active++;
                }
            }
        }
        todayStatsLbl.Text = $"{Fmt.Minutes(total)} · {active} 个活跃时段";
    }

    private void OpenStats_Click(object sender, RoutedEventArgs e) => _openStats();

    // ------------------------------------------------------------ 计划选择面板

    /// <summary>重建右侧计划列表（由 ctor / 固定任务增删改名触发）。当前目标被删/改名 → 回退「不分类」。</summary>
    private void RebuildPlanList()
    {
        _loading = true;
        var plans = _store.GetFixedTaskNames();
        if (_selectedPlan is not null && !plans.Contains(_selectedPlan))
        {
            _selectedPlan = null;              // 当前目标被删/改名 → 回退「不分类」
            _focus.CurrentPlan = null;
        }
        planList.Children.Clear();
        planList.Children.Add(BuildPlanRow(null));   // 「不分类」置顶
        foreach (string p in plans)
            planList.Children.Add(BuildPlanRow(p));
        currentPlanLbl.Text = _selectedPlan ?? "不分类";
        _loading = false;
    }

    private UIElement BuildPlanRow(string? plan)
    {
        string name = plan ?? "不分类";
        bool sel = (_selectedPlan ?? "") == (plan ?? "");

        // 左 accent 竖条（选中才显示）
        var accent = new Border
        {
            Width = 2,
            Background = sel ? (Brush)FindResource("AccentBrush") : Brushes.Transparent,
            CornerRadius = new CornerRadius(1),
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 2, 0, 2),
        };

        var nameTxt = new TextBlock
        {
            Text = name,
            FontFamily = (FontFamily)FindResource("UIFont"),
            FontSize = 13,
            FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = sel ? (Brush)FindResource("AccentStrongBrush") : (Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // 右侧显示当日该计划已专注分钟（不分类不显示）
        var minTxt = new TextBlock
        {
            Text = PlanDayMinutes(plan),
            Style = (Style)FindResource("MutedText"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 2, 0),
        };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(accent, Dock.Left);
        DockPanel.SetDock(minTxt, Dock.Right);
        dock.Children.Add(accent);
        dock.Children.Add(minTxt);
        dock.Children.Add(nameTxt);

        // 用 Border + MouseLeftButtonDown（用户输入才触发，天然防程序化重建重入）
        var rowBg = new SolidColorBrush(sel
            ? (Color)FindResource("AccentSoftFillColor")
            : Colors.Transparent);
        var row = new Border
        {
            Background = rowBg,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(6, 7, 6, 7),
            Cursor = Cursors.Hand,
            Tag = plan ?? "",
            Child = dock,
        };
        row.MouseEnter += (_, _) =>
        {
            if (!sel) Controls.UiMotion.TweenColor(rowBg, (Color)FindResource("ElevatedColor"));
        };
        row.MouseLeave += (_, _) =>
        {
            if (!sel) Controls.UiMotion.TweenColor(rowBg, Colors.Transparent);
        };
        row.MouseLeftButtonDown += PlanRow_Click;
        return row;
    }

    private string PlanDayMinutes(string? plan)
    {
        if (string.IsNullOrEmpty(plan)) return "";
        var fp = DataStore.GetObj(_store.Data, "focus_plan");
        var fd = fp?[DataStore.TodayStr()] as JsonObject;
        double m = DataStore.GetDouble(fd?[plan]);
        return m > 0 ? Fmt.Minutes(m) : "";
    }

    private void PlanRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (_loading) return;
        var row = (Border)sender;
        string tag = (string)row.Tag;
        _selectedPlan = tag.Length == 0 ? null : tag;
        _focus.CurrentPlan = _selectedPlan;
        RebuildPlanList();
    }

    private void ShowWaterReminder()
    {
        int min = (int)(_focus.ElapsedSeconds / 60.0);
        var popup = new ReminderPopupWindow(
            "该休息一下了 ☕",
            $"已连续专注 {min} 分钟，喝口水、活动一下再继续，计时不会停")
        {
            Owner = Window.GetWindow(this),
        };
        popup.Show();
    }
}
