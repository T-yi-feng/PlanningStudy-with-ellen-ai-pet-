using System;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using KaoyanPlanner.WPF.Native;
using KaoyanPlanner.WPF.Services;
using KaoyanPlanner.WPF.Views.Pet;
using KaoyanPlanner.WPF.Views.Settings;
using KaoyanPlanner.WPF.Views.Tabs;

namespace KaoyanPlanner.WPF.Views;

/// <summary>
/// 主窗口外壳：大窗口工作台（52px rail | 272px 侧边栏 | 主区 | 状态栏）。
/// M3：接线四面板、标题倒计时/状态条、双击改标题、位置尺寸持久化、跨日心跳刷新。
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string[] WeekdayCn = { "星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日" };

    private readonly DataStore _store;
    private readonly FocusTimerService _focusTimer;
    private readonly NotificationService _notify = new();
    private readonly PlanTab _planTab;
    private readonly TimerTab _timerTab;
    private readonly ReminderTab _reminderTab;
    private readonly StatsTab _statsTab;
    private readonly PlanSidebar _planSidebar;

    private readonly DispatcherTimer _boundsTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _restoring = true;
    private bool _editingTitle;

    // 设置页导航：⚙ 进入的是主区里的一整页（非浮窗），记住来源页签以便「← 返回」
    private string _currentTag = "plan";
    private string _settingsPrevTag = "plan";
    private SettingsPage? _settingsPage;

    // Ctrl+K 命令面板：命令集（重建于每次打开，标题随状态变化）
    private readonly List<(string Key, string Desc, Action Run)> _cmdItems = new();

    /// <summary>托盘「退出」时置 true 放行 OnClosing；否则关闭只隐藏到托盘。</summary>
    public bool AllowClose { get; set; }

    /// <summary>桌宠窗口引用（App 组合根接线）：设置页换皮肤/字幕设置后要实时通知它。</summary>
    public PetWindow? PetWindow { get; set; }

    /// <summary>提醒消息 → 桌宠说的人话（去掉 HH:mm 前缀，只留事务内容）。</summary>
    private static string MakePetReminderLine(string message)
    {
        int i = message.IndexOf(" · ", StringComparison.Ordinal);
        string content = i >= 0 ? message[(i + 3)..] : message;
        return $"到点啦：{content}";
    }

    public MainWindow(DataStore store, HeartbeatService heartbeat, FocusTimerService? focusTimer = null)
    {
        InitializeComponent();
        DwmInterop.ApplyRoundedCorners(this);

        _store = store;

        // P1 桌宠状态条要共享同一个专注计时器（App 创建并传入；缺省自建保持兼容）
        _focusTimer = focusTimer ?? new FocusTimerService(store);
        _planTab = new PlanTab(store);
        _timerTab = new TimerTab(store, _focusTimer, () => SelectTab("stats"));
        _reminderTab = new ReminderTab(store, heartbeat);
        _statsTab = new StatsTab(store, _focusTimer);
        _planSidebar = new PlanSidebar(store);
        sidebarHost.Child = _planSidebar;
        mainHost.Content = _planTab;

        // M5 提醒触发：定时提醒 / 未完成任务提醒 → 声音 + 右下角弹窗；同时让桌宠开口（语音可用时）
        heartbeat.ReminderDue += (title, message) =>
        {
            _notify.Notify(title, message);
            PetWindow?.AnnounceReminder(MakePetReminderLine(message));
        };
        heartbeat.UnfinishedDue += (total, lines) =>
        {
            _notify.Notify($"今日还有 {total} 项待办未完成", string.Join("\n", lines));
            PetWindow?.AnnounceReminder($"还有 {total} 项待办没完成，记得去打卡哦");
        };

        railPlan.Click += (_, _) => SelectTab("plan");
        railTimer.Click += (_, _) => SelectTab("timer");
        railReminder.Click += (_, _) => SelectTab("reminder");
        railStats.Click += (_, _) => SelectTab("stats");

        // 数据变更 → 头部/侧边栏进度；跨日 → 欠卡结算后重算并刷新面板
        store.Changed += UpdateHeader;
        heartbeat.DayChanged += OnDayChanged;

        LocationChanged += (_, _) => SchedulePersist();
        SizeChanged += (_, _) => SchedulePersist();
        _boundsTimer.Tick += (_, _) => PersistBounds();

        RestoreWindowBounds();
        _restoring = false;
        ApplyTopmost(DataStore.GetBool(_store.Data["window_topmost"], true));
        UpdateHeader();

        // Ctrl+K 命令面板
        cmdPopup.PlacementTarget = this;
        PreviewKeyDown += Main_PreviewKeyDown;
        cmdInput.TextChanged += (_, _) => FilterCommands();
        cmdList.SelectionChanged += (_, _) => cmdList.ScrollIntoView(cmdList.SelectedItem);
    }

    // ------------------------------------------------------------ Ctrl+K 命令面板

    private void Main_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ToggleCommandPalette();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && cmdPopup.IsOpen)
        {
            cmdPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void ToggleCommandPalette()
    {
        if (cmdPopup.IsOpen) { cmdPopup.IsOpen = false; return; }
        OpenCommandPalette();
    }

    private void OpenCommandPalette()
    {
        BuildCommands();
        cmdInput.Clear();
        FilterCommands();
        cmdPopup.IsOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            cmdInput.Focus();
            cmdInput.CaretIndex = cmdInput.Text.Length;
        }));
    }

    private void BuildCommands()
    {
        _cmdItems.Clear();
        bool frozen = DataStore.GetBool(_store.Data["frozen"]);

        _cmdItems.Add(("今日计划", "切换到今日计划页", () => SelectTab("plan")));
        _cmdItems.Add(("专注计时", "切换到专注计时页", () => SelectTab("timer")));
        _cmdItems.Add(("提醒", "切换到提醒页", () => SelectTab("reminder")));
        _cmdItems.Add(("专注统计", "切换到统计页", () => SelectTab("stats")));
        _cmdItems.Add(("设置", "打开设置", () => SelectTab("settings")));
        _cmdItems.Add(("新建任务", "切到今日计划并开始输入", () => { SelectTab("plan"); _planTab.FocusNewTask(); }));
        _cmdItems.Add((frozen ? "解冻任务" : "冻结任务", frozen ? "恢复固定任务打卡" : "暂停固定任务打卡", () => _planTab.ToggleFrozen()));
        _cmdItems.Add(("清理已完成", "删除当前计划已完成的临时与固定任务", () => _planTab.ClearDoneNow()));
        _cmdItems.Add(("桌宠聊天", "打开与艾莲的聊天窗口", () => PetWindow?.OpenChat()));
        _cmdItems.Add((PetWindow is { IsVisible: true } ? "隐藏桌宠" : "显示桌宠", "显示或隐藏桌宠窗", () =>
        {
            if (PetWindow is { IsVisible: true }) PetWindow.HidePet();
            else PetWindow?.ShowPet();
        }));
        _cmdItems.Add(("通用设置", "打开设置 · 通用", () => NavigateToSettings("general")));
        _cmdItems.Add(("AI 聊天设置", "打开设置 · AI 聊天", () => NavigateToSettings("chat")));
        _cmdItems.Add(("语音设置", "打开设置 · 语音播报", () => NavigateToSettings("tts")));
        _cmdItems.Add(("桌宠设置", "打开设置 · 桌宠", () => NavigateToSettings("pet")));
    }

    private void FilterCommands()
    {
        string q = cmdInput.Text.Trim();
        cmdList.Items.Clear();
        foreach (var (key, desc, run) in _cmdItems)
        {
            if (q.Length == 0 || key.Contains(q, StringComparison.OrdinalIgnoreCase)
                || desc.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                var item = new ListBoxItem
                {
                    Content = $"{key}  —  {desc}",
                    Tag = run,
                    FontSize = 13,
                    Padding = new Thickness(8, 5, 8, 5),
                };
                cmdList.Items.Add(item);
            }
        }
        if (cmdList.Items.Count > 0) cmdList.SelectedIndex = 0;
    }

    private void RunSelectedCommand()
    {
        if (cmdList.SelectedItem is ListBoxItem { Tag: Action run })
        {
            cmdPopup.IsOpen = false;
            run();
        }
    }

    private void CmdInput_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                RunSelectedCommand();
                e.Handled = true;
                break;
            case Key.Down:
                if (cmdList.Items.Count > 0)
                    cmdList.SelectedIndex = (cmdList.SelectedIndex + 1) % cmdList.Items.Count;
                e.Handled = true;
                break;
            case Key.Up:
                if (cmdList.Items.Count > 0)
                    cmdList.SelectedIndex = (cmdList.SelectedIndex - 1 + cmdList.Items.Count) % cmdList.Items.Count;
                e.Handled = true;
                break;
        }
    }

    /// <summary>置顶开关（设置窗口调用）；默认开启，贴合旧版浮窗习惯。</summary>
    public void ApplyTopmost(bool top) => Topmost = top;

    /// <summary>降低动效（M7）：关掉面板切换动画，避免晕动/省资源。</summary>
    private bool ReduceMotion => DataStore.GetBool(_store.Data["reduce_motion"]);

    // ------------------------------------------------------------ 页签切换

    private void SelectTab(string tag)
    {
        _currentTag = tag;
        UserControl page = tag switch
        {
            "plan" => _planTab,
            "timer" => _timerTab,
            "reminder" => _reminderTab,
            "stats" => _statsTab,
            "settings" => SettingsPageInstance,
            _ => _statsTab,
        };
        SyncRail(tag);

        // 切到统计页总是取最新专注数据
        if (ReferenceEquals(page, _statsTab)) _statsTab.Refresh();

        if (ReferenceEquals(mainHost.Content, page))
        {
            // 同页再点：兜底恢复完全不透明（防止中断动画残留的半透明）
            page.Opacity = 1;
            if (page.RenderTransform is TranslateTransform tt0) tt0.Y = 0;
            return;
        }

        if (page.RenderTransform is not TranslateTransform tt)
        {
            tt = new TranslateTransform();
            page.RenderTransform = tt;
        }
        page.Opacity = 0;
        tt.Y = 8;
        mainHost.Content = page;

        if (SystemParameters.ClientAreaAnimation && !ReduceMotion)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            page.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        }
        else
        {
            page.Opacity = 1;
            tt.Y = 0;
        }
    }

    private void SyncRail(string tag)
    {
        railPlan.IsChecked = tag == "plan";
        railTimer.IsChecked = tag == "timer";
        railReminder.IsChecked = tag == "reminder";
        railStats.IsChecked = tag == "stats";
        railSettings.IsChecked = tag == "settings";
    }

    /// <summary>设置页（惰性单例）：首次进入时构造并默认显示「通用」。</summary>
    private SettingsPage SettingsPageInstance
    {
        get
        {
            if (_settingsPage is null)
            {
                _settingsPage = new SettingsPage(_store, this);
                _settingsPage.ShowSection("general");
            }
            return _settingsPage;
        }
    }

    /// <summary>进入设置页（丝滑转场，非浮窗）。section 指定要停在哪个分栏（桌宠右键跳转用）。</summary>
    public void NavigateToSettings(string? section = null)
    {
        SettingsPage settings = SettingsPageInstance;   // 先构造，避免 SelectTab 空引用
        if (_currentTag != "settings")
            _settingsPrevTag = _currentTag;              // 已在设置页再点 ⚙ 不覆盖返回目标
        SelectTab("settings");
        settings.ShowSection(section ?? "general");
    }

    /// <summary>设置页「← 返回」→ 回到进入前的页签。</summary>
    public void ReturnFromSettings() => SelectTab(_settingsPrevTag);

    private void OnDayChanged()
    {
        _planTab.Refresh();   // EnsureToday 不触发 Changed，需手动刷新
        _planSidebar.Refresh();
        UpdateHeader();
    }

    // ------------------------------------------------------------ 头部信息

    private void UpdateHeader()
    {
        string title = DataStore.GetString(_store.Data["title"]);
        if (string.IsNullOrEmpty(title)) title = "考研复习";
        titleText.Text = title;

        var today = DateTime.Today;
        string cd;
        string exam = DataStore.GetString(_store.Data["exam_date"]);
        if (!string.IsNullOrEmpty(exam) &&
            DateTime.TryParseExact(exam, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ed))
        {
            int delta = (ed - today).Days;
            cd = delta > 0 ? $"距考研还有 {delta} 天"
                : delta == 0 ? "今天就是考研日，冲！"
                : "考研已开始";
        }
        else
        {
            cd = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + WeekdayCn[((int)today.DayOfWeek + 6) % 7];
        }
        countdownText.Text = cd;

        // 状态条：今日 X/Y · ❄ 已冻结 · ⚠ 欠 N 天（镜像 widgets.py update_header）
        string day = DataStore.TodayStr();
        var daily = _store.Data["daily"] as JsonObject;
        var tasks = daily is not null ? daily[day] as JsonArray : null;
        int total = tasks?.Count ?? 0;
        int done = 0;
        if (tasks is not null)
            foreach (var n in tasks)
                if (n is JsonObject t && DataStore.GetBool(t["done"])) done++;

        var fixedArr = _store.Data["tasks"] as JsonArray;
        int fTotal = fixedArr?.Count ?? 0;
        int fDone = 0;
        long owe = 0;
        if (fixedArr is not null)
        {
            foreach (var n in fixedArr)
            {
                if (n is not JsonObject ft) continue;
                // 与 FixedPunchedTodayTask 同口径：新建当天 progress=0 只豁免欠卡，不算「今日已打卡」
                if (DataStore.GetBool(ft["done"]) ||
                    (DataStore.GetString(ft["last_done_date"]) == day && DataStore.GetInt(ft["progress"]) > 0))
                    fDone++;
                else if (DataStore.GetInt(ft["owed"]) > 0)
                    owe += DataStore.GetInt(ft["owed"]);
            }
        }

        var segs = new List<string> { $"今日 {done + fDone}/{total + fTotal}" };
        if (DataStore.GetBool(_store.Data["frozen"])) segs.Add("❄ 已冻结");
        if (owe > 0) segs.Add($"⚠ 欠 {owe} 天");
        statusText.Text = string.Join("  ·  ", segs);
    }

    // ------------------------------------------------------------ 双击改标题

    private void TitleText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && !_editingTitle)
            BeginTitleEdit();
    }

    private void BeginTitleEdit()
    {
        _editingTitle = true;
        titleEdit.Text = titleText.Text;
        titleEdit.Visibility = Visibility.Visible;
        titleText.Visibility = Visibility.Collapsed;
        titleEdit.Focus();
        titleEdit.SelectAll();
    }

    private void TitleEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitTitleEdit();
        else if (e.Key == Key.Escape) CancelTitleEdit();
    }

    private void TitleEdit_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_editingTitle) CommitTitleEdit();
    }

    private void CommitTitleEdit()
    {
        if (!_editingTitle) return;
        _editingTitle = false;
        string t = titleEdit.Text.Trim();
        if (t.Length > 0)
        {
            _store.Data["title"] = t;
            _store.Save();   // Changed → UpdateHeader 刷新标题
        }
        titleEdit.Visibility = Visibility.Collapsed;
        titleText.Visibility = Visibility.Visible;
    }

    private void CancelTitleEdit()
    {
        _editingTitle = false;
        titleEdit.Visibility = Visibility.Collapsed;
        titleText.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------ 位置/尺寸持久化

    private void RestoreWindowBounds()
    {
        // window_pos 语义扩展为 [left, top, width, height]（旧 2 元素 [x, y] 也兼容）
        if (_store.Data["window_pos"] is not JsonArray wp || wp.Count < 2) return;
        if (!TryNum(wp[0], out double left) || !TryNum(wp[1], out double top)) return;
        double w = Width, h = Height;
        if (wp.Count >= 4 && TryNum(wp[2], out double d2) && TryNum(wp[3], out double d3))
        {
            w = d2;
            h = d3;
        }
        if (w < MinWidth) w = Width;
        if (h < MinHeight) h = Height;
        Left = left;
        Top = top;
        Width = w;
        Height = h;
    }

    private static bool TryNum(JsonNode? n, out double v)
    {
        v = 0;
        if (n is JsonValue val)
        {
            if (val.TryGetValue<long>(out var l)) { v = l; return true; }
            if (val.TryGetValue<int>(out var i)) { v = i; return true; }
            if (val.TryGetValue<double>(out var d)) { v = d; return true; }
        }
        return false;
    }

    private void SchedulePersist()
    {
        if (_restoring) return;
        _boundsTimer.Stop();
        _boundsTimer.Start();
    }

    private void PersistBounds()
    {
        if (WindowState == WindowState.Maximized) return;
        _store.Data["window_pos"] = new JsonArray { (long)Left, (long)Top, (long)Width, (long)Height };
        _store.SaveQuiet();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _boundsTimer.Stop();
        _timerTab.Flush();   // 刷掉未落盘的专注整秒，别丢最后几秒
        if (WindowState != WindowState.Maximized)
        {
            _store.Data["window_pos"] = new JsonArray { (long)Left, (long)Top, (long)Width, (long)Height };
            _store.SaveQuiet();
        }
        if (!AllowClose)
        {
            // M6 退出语义：右上 ✕ 只隐藏到托盘，进程继续跑（提醒/计时照常）；托盘「退出」才真正退出
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    // ------------------------------------------------------------ 窗口按钮

    private void Window_StateChanged(object sender, EventArgs e)
    {
        // 最大化时修正非客户区溢出
        rootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => SystemCommands.MinimizeWindow(this);

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // M6 退出语义：隐藏到托盘（OnClosing 里 Cancel+Hide 兜底）
        Hide();
    }

    /// <summary>从托盘/单实例唤醒还原窗口。</summary>
    public void ShowFromTray()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => NavigateToSettings();
}
