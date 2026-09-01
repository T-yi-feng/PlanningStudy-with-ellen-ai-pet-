using System;
using System.Text.Json.Nodes;

namespace KaoyanPlanner.WPF.Services;

/// <summary>
/// 专注计时引擎：纯 C#，无 WPF 依赖（可单测）。镜像 widgets.py TimerTab 的正计时：
/// - Environment.TickCount64 单调时钟（= time.monotonic），250ms 轮询 Poll()
/// - 忽略 delta&lt;0 或 &gt;2000ms 的异常空隙（休眠/卡顿不计时）
/// - 整秒落盘 focus_history[today][hour] += sec/60（3 位小数）
/// - ≥15s 写节流 → SaveQuiet（不触发 Changed 全面板重建），只发 HistoryChanged 给计时/统计页
/// - 喝水提醒：idx=int(已专注秒/(interval*60)) 变化 → ReminderDue
/// 计时本身跨重启不持久化（与 Python 一致）。
/// </summary>
public sealed class FocusTimerService : IPetFocus
{
    private readonly DataStore _store;
    private readonly Func<long> _clock;
    private long _lastTicks;        // 上次 Poll 的单调时钟值
    private double _accSec;         // 尚未落盘的整秒累积
    private double _lastSavedSec;   // 上次 SaveQuiet 时的已专注秒数
    private int _lastReminderIdx;
    private bool _running;

    public bool Running => _running;

    /// <summary>本次会话累计专注秒数（跨重启不保留）。</summary>
    public double ElapsedSeconds { get; private set; }

    /// <summary>当前专注归属的计划名；null = 不指定计划（只计入总时长 focus_history）。</summary>
    public string? CurrentPlan { get; set; }

    /// <summary>喝水提醒到达（每 interval 分钟一次）。</summary>
    public event Action? ReminderDue;

    /// <summary>专注数据已写盘（15s 节流 / 暂停 / 重置），计时与统计页据此刷新。</summary>
    public event Action? HistoryChanged;

    /// <param name="clock">单调毫秒时钟源；测试注入假时钟，生产默认 Environment.TickCount64。</param>
    public FocusTimerService(DataStore store, Func<long>? clock = null)
    {
        _store = store;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    public void Start()
    {
        if (_running) return;
        _lastTicks = _clock();
        _running = true;
    }

    public void Pause()
    {
        if (!_running) return;
        _running = false;
        FlushPending();
    }

    public void Reset()
    {
        _running = false;
        ElapsedSeconds = 0;
        _accSec = 0;
        _lastSavedSec = 0;
        _lastReminderIdx = 0;
        FlushPending();
    }

    /// <summary>250ms 心跳调用；未开始则空转。</summary>
    public void Poll()
    {
        if (!_running) return;
        long now = _clock();
        long deltaMs = now - _lastTicks;
        _lastTicks = now;
        if (deltaMs < 0 || deltaMs > 2000) return;   // 休眠/卡顿：空隙不计时

        double delta = deltaMs / 1000.0;
        ElapsedSeconds += delta;
        _accSec += delta;

        long whole = (long)_accSec;
        if (whole >= 1)
        {
            _accSec -= whole;
            _store.AddFocusSeconds(DateTime.Now.Hour, (int)whole, CurrentPlan);
        }
        if (ElapsedSeconds - _lastSavedSec >= 15.0)
        {
            _lastSavedSec = ElapsedSeconds;
            _store.SaveQuiet();
            HistoryChanged?.Invoke();
        }
        CheckReminder();
    }

    /// <summary>应用退出/切页前调用：把未落盘的整秒刷下去并写盘。</summary>
    public void Flush() => FlushPending();

    private void FlushPending()
    {
        long whole = (long)_accSec;
        if (whole < 1) return;
        _accSec -= whole;
        _store.AddFocusSeconds(DateTime.Now.Hour, (int)whole);
        _store.SaveQuiet();
        HistoryChanged?.Invoke();
    }

    private void CheckReminder()
    {
        var cfg = DataStore.GetObj(_store.Data, "focus_reminder");
        if (!DataStore.GetBool(cfg?["enabled"], true)) return;
        long intervalMin = Math.Max(1, DataStore.GetInt(cfg?["interval_min"], 60));
        int idx = (int)(ElapsedSeconds / (intervalMin * 60.0));
        if (idx > _lastReminderIdx)
        {
            _lastReminderIdx = idx;
            ReminderDue?.Invoke();
        }
    }
}
