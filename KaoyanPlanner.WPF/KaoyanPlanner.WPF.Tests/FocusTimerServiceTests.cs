using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using KaoyanPlanner.WPF.Services;
using Xunit;

namespace KaoyanPlanner.WPF.Tests;

/// <summary>
/// FocusTimerService 纯 C# 计时引擎测试。注入假时钟驱动 Poll，验证：
/// 整秒落盘、>2000ms 空隙忽略、≥15s 写节流、暂停/重置刷 pending、喝水提醒触发。
/// 全部在临时目录副本上操作，不碰真实 data.json。
/// </summary>
public class FocusTimerServiceTests
{
    private static string NewTempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "kp_timer_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static DataStore NewStore(string dir)
    {
        string path = Path.Combine(dir, "data.json");
        File.WriteAllText(path, "{\"title\":\"t\",\"daily\":{},\"focus_history\":{},\"focus_reminder\":{\"enabled\":true,\"interval_min\":60}}",
            new UTF8Encoding(false));
        var store = new DataStore(path);
        store.Load();
        return store;
    }

    private static double DayTotal(DataStore store)
    {
        var hist = store.Data["focus_history"] as JsonObject;
        var day = hist?[DataStore.TodayStr()] as JsonObject;
        double t = 0;
        if (day is not null)
            foreach (var kv in day)
                if (kv.Value is JsonValue v && v.TryGetValue<double>(out double d))
                    t += d;
        return t;
    }

    private static double PlanDay(DataStore store, string plan)
    {
        var fp = store.Data["focus_plan"] as JsonObject;
        var day = fp?[DataStore.TodayStr()] as JsonObject;
        return day?[plan] is JsonValue v && v.TryGetValue<double>(out double d) ? d : 0;
    }

    [Fact]
    public void Start_PollWholeSeconds_WriteFocusMinutes()
    {
        string dir = NewTempDir();
        try
        {
            var store = NewStore(dir);
            long now = 0;
            var svc = new FocusTimerService(store, () => now);
            svc.Start();

            for (int i = 0; i < 8; i++)
            {
                now += 250;
                svc.Poll();
            }
            // 2 整秒：逐秒 AddFocusSeconds 各自 round 3（镜像 Python）→ 0.017+0.0167→0.034
            Assert.Equal(2.0, svc.ElapsedSeconds, 3);
            Assert.Equal(0.034, DayTotal(store), 3);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Poll_IgnoresGapOver2000ms()
    {
        string dir = NewTempDir();
        try
        {
            var store = NewStore(dir);
            long now = 0;
            var svc = new FocusTimerService(store, () => now);
            svc.Start();

            now += 250; svc.Poll();          // 0.25s
            now += 5000; svc.Poll();         // 4750ms 空隙 → 忽略
            Assert.Equal(0.25, svc.ElapsedSeconds, 3);

            now += 250; svc.Poll();          // 恢复：仍按 250ms 计
            Assert.Equal(0.50, svc.ElapsedSeconds, 3);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Pause_FlushesPending_AndStops()
    {
        string dir = NewTempDir();
        try
        {
            var store = NewStore(dir);
            long now = 0;
            var svc = new FocusTimerService(store, () => now);
            svc.Start();

            now += 2000; svc.Poll();         // 2s（一次 Poll delta 2000ms 恰好 ≤2000）
            svc.Pause();
            Assert.False(svc.Running);
            Assert.Equal(0.033, DayTotal(store), 3);   // 2 整秒一次性 AddFocusSeconds(2) → 0.033；暂停即刷 pending

            double before = DayTotal(store);
            now += 1000; svc.Poll();         // 已暂停 → 空转
            Assert.Equal(before, DayTotal(store), 3);   // 暂停后不再落盘
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Reset_ClearsElapsed_AndFlushesPending()
    {
        string dir = NewTempDir();
        try
        {
            var store = NewStore(dir);
            long now = 0;
            var svc = new FocusTimerService(store, () => now);
            svc.Start();

            for (int i = 0; i < 12; i++)     // 12×250ms = 3 整秒（逐秒 round 3 → 0.051）
            {
                now += 250;
                svc.Poll();
            }
            svc.Reset();
            Assert.Equal(0, svc.ElapsedSeconds, 3);
            Assert.Equal(0.051, DayTotal(store), 3);    // 3 整秒增量累加（0.017→0.034→0.051）
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ThrottleSave_WritesFileAfter15s_AndFiresHistoryChanged()
    {
        string dir = NewTempDir();
        try
        {
            var store = NewStore(dir);
            long now = 0;
            var svc = new FocusTimerService(store, () => now);
            int changed = 0;
            svc.HistoryChanged += () => changed++;
            svc.Start();

            // 每 250ms 轮询，跑满 16s
            for (int i = 0; i < 64; i++)
            {
                now += 250;
                svc.Poll();
            }
            Assert.True(changed >= 1);
            Assert.True(File.Exists(Path.Combine(dir, "data.json")));
            string text = File.ReadAllText(Path.Combine(dir, "data.json"));
            Assert.Contains("focus_history", text);
            // 16 整秒逐秒 round 3 累加 → 0.272（Python 同样增量舍入）
            Assert.Equal(0.272, DayTotal(store), 3);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Reminder_FiresEveryIntervalMinutes()
    {
        string dir = NewTempDir();
        try
        {
            var store = NewStore(dir);
            ((JsonObject)store.Data["focus_reminder"]!)["interval_min"] = 1L;   // 每 1 分钟
            long now = 0;
            var svc = new FocusTimerService(store, () => now);
            int fired = 0;
            svc.ReminderDue += () => fired++;
            svc.Start();

            for (int i = 1; i <= 480; i++)   // 120s
            {
                now += 250;
                svc.Poll();
            }
            Assert.Equal(2, fired);   // 60s、120s 各一次
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Reminder_Disabled_DoesNotFire()
    {
        string dir = NewTempDir();
        try
        {
            var store = NewStore(dir);
            ((JsonObject)store.Data["focus_reminder"]!)["enabled"] = false;
            long now = 0;
            var svc = new FocusTimerService(store, () => now);
            int fired = 0;
            svc.ReminderDue += () => fired++;
            svc.Start();

            for (int i = 1; i <= 480; i++)
            {
                now += 250;
                svc.Poll();
            }
            Assert.Equal(0, fired);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------ 按计划（CurrentPlan）

    [Fact]
    public void CurrentPlan_PropagatesToFocusPlan()
    {
        string dir = NewTempDir();
        try
        {
            var store = NewStore(dir);
            long now = 0;
            var svc = new FocusTimerService(store, () => now) { CurrentPlan = "健身计划" };
            svc.Start();

            for (int i = 0; i < 8; i++) { now += 250; svc.Poll(); }
            Assert.Equal(0.034, DayTotal(store), 3);
            Assert.Equal(0.034, PlanDay(store, "健身计划"), 3);   // 总时长与计划时长同步
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CurrentPlan_Null_WritesOnlyHistory()
    {
        string dir = NewTempDir();
        try
        {
            var store = NewStore(dir);
            long now = 0;
            var svc = new FocusTimerService(store, () => now);   // CurrentPlan 默认 null = 不指定
            svc.Start();

            for (int i = 0; i < 8; i++) { now += 250; svc.Poll(); }
            Assert.Equal(0.034, DayTotal(store), 3);
            Assert.False(store.Data.ContainsKey("focus_plan"));   // 惰性：未指定计划不落盘
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CurrentPlan_SwitchMidSession_Separates()
    {
        string dir = NewTempDir();
        try
        {
            var store = NewStore(dir);
            long now = 0;
            var svc = new FocusTimerService(store, () => now) { CurrentPlan = "A计划" };
            svc.Start();

            for (int i = 0; i < 4; i++) { now += 250; svc.Poll(); }   // 1 整秒 → A
            svc.CurrentPlan = "B计划";
            for (int i = 0; i < 4; i++) { now += 250; svc.Poll(); }   // 1 整秒 → B

            Assert.Equal(0.017, PlanDay(store, "A计划"), 3);
            Assert.Equal(0.017, PlanDay(store, "B计划"), 3);
            Assert.Equal(0.034, DayTotal(store), 3);
        }
        finally { Directory.Delete(dir, true); }
    }
}
