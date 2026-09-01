using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace KaoyanPlanner.WPF.Services;

/// <summary>
/// 1s 心跳（必须 UI 线程）：跨过午夜时结算欠卡（RolloverFixed）并归档旧日（EnsureToday）；
/// 每秒扫描定时提醒（同一分钟去重）；未完成任务提醒按配置间隔单独定时器。
/// 触发只发事件，弹窗/声音由 MainWindow 接 NotificationService 完成。
/// </summary>
public sealed class HeartbeatService
{
    private readonly DataStore _store;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _unfinishedTimer = new();
    private readonly DispatcherTimer _firstUnfinishedCheck = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly HashSet<string> _firedTimes = new();   // "日期|HH:mm" 已触发，防同分钟重复
    private string _lastDay = "";

    /// <summary>跨过午夜：欠卡已结算、旧日已归档，UI 据此重建面板。</summary>
    public event Action? DayChanged;

    /// <summary>每秒触发（供低频刷新；M4 计时页也会挂这）。</summary>
    public event Action? Tick;

    /// <summary>定时提醒到点：参数 (标题, 内容)。</summary>
    public event Action<string, string>? ReminderDue;

    /// <summary>未完成任务提醒：参数 (未完成总数, 前 3 条任务文本)。</summary>
    public event Action<int, List<string>>? UnfinishedDue;

    public HeartbeatService(DataStore store)
    {
        _store = store;
        _timer.Tick += OnTick;
        _unfinishedTimer.Tick += (_, _) => CheckUnfinished();
        // 启动 8s 首查（镜像 Python QTimer.singleShot(8000)）：让用户立刻看到未完成提醒是否生效
        _firstUnfinishedCheck.Tick += (_, _) =>
        {
            _firstUnfinishedCheck.Stop();
            CheckUnfinished();
        };
    }

    public void Start()
    {
        _lastDay = DataStore.TodayStr();
        _store.EnsureToday();
        _store.RolloverFixed();
        UpdateUnfinishedTimer();
        _timer.Start();
        _firstUnfinishedCheck.Start();
    }

    /// <summary>未完成任务提醒定时器按配置启停（提醒页改设置时调用）。</summary>
    public void UpdateUnfinishedTimer()
    {
        _unfinishedTimer.Stop();
        var cfg = DataStore.GetObj(_store.Data, "unfinished_reminder");
        if (!DataStore.GetBool(cfg?["enabled"], true)) return;
        long min = Math.Max(1, DataStore.GetInt(cfg?["interval_min"], 60));
        _unfinishedTimer.Interval = TimeSpan.FromMinutes(min);
        _unfinishedTimer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        string today = DataStore.TodayStr();
        if (today != _lastDay)
        {
            _lastDay = today;
            _store.RolloverFixed();   // 欠卡结算（有变化才落盘）
            _store.EnsureToday();     // 归档最近一天 + 建今日空列表
            _firedTimes.Clear();      // 跨日重置去重记录
            DayChanged?.Invoke();
        }
        CheckReminders();
        Tick?.Invoke();
    }

    private void CheckReminders()
    {
        string now = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (!_firedTimes.Add(_lastDay + "|" + now)) return;   // 同一分钟只处理一次
        foreach (var r in ReminderService.MatchingAt(_store.Data["reminders"] as JsonArray, now))
        {
            string label = DataStore.GetString(r["label"]);
            ReminderDue?.Invoke("时间到 ⏰", string.IsNullOrEmpty(label) ? "提醒" : label);
        }
    }

    private void CheckUnfinished()
    {
        var cfg = DataStore.GetObj(_store.Data, "unfinished_reminder");
        if (!DataStore.GetBool(cfg?["enabled"], true)) return;
        var (total, first3) = ReminderService.PendingToday(_store.Data["daily"] as JsonObject, DataStore.TodayStr());
        if (total == 0) return;
        UnfinishedDue?.Invoke(total, first3);
    }
}
