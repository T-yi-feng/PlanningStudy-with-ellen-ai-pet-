using System.Text.Json.Nodes;
using KaoyanPlanner.WPF.Services;
using Xunit;

namespace KaoyanPlanner.WPF.Tests;

/// <summary>
/// ReminderService 纯逻辑测试：旧版每日时刻 / 新版「日期+时间段+间隔」触发、
/// 下一次提醒、时间解析、待办收集。全部在内存 JsonObject 上操作，不碰真实 data.json。
/// </summary>
public class ReminderServiceTests
{
    private static JsonArray Reminders(params JsonObject[] items)
    {
        var arr = new JsonArray();
        foreach (var o in items) arr.Add(o);
        return arr;
    }

    private static JsonObject Legacy(string time, string label, bool enabled)
        => new() { ["time"] = time, ["label"] = label, ["enabled"] = enabled };

    private static JsonObject DayReminder(string date, string start, string end, long interval,
        string label = "提醒", int priority = 0, bool enabled = true)
        => new()
        {
            ["date"] = date,
            ["start"] = start,
            ["end"] = end,
            ["interval_min"] = interval,
            ["label"] = label,
            ["priority"] = priority,
            ["enabled"] = enabled,
        };

    private static DateTime Dt(int y, int mo, int d, int h = 0, int mi = 0)
        => new(y, mo, d, h, mi, 0);

    // ------------------------------------------------------------ 旧版（每日时刻）

    [Fact]
    public void MatchingAt_Legacy_FiresEnabledAtExactTime_IgnoresOthers()
    {
        var arr = Reminders(Legacy("19:00", "复盘", true), Legacy("20:30", "背词", true), Legacy("19:00", "已关闭", false));
        var matches = ReminderService.MatchingAt(arr, Dt(2026, 9, 10, 19, 0)).ToList();

        Assert.Single(matches);
        Assert.Equal("复盘", matches[0]["label"]!.ToString());
        Assert.Empty(ReminderService.MatchingAt(arr, Dt(2026, 9, 10, 18, 59)));
    }

    [Fact]
    public void MatchingAt_Null_ReturnsEmpty()
        => Assert.Empty(ReminderService.MatchingAt(null, Dt(2026, 9, 10, 19, 0)));

    // ------------------------------------------------------------ 新版（日期 + 时间段 + 间隔）

    [Fact]
    public void ScheduleTimes_IntervalGrid_InclusiveEnds()
    {
        var times = ReminderService.ScheduleTimes("2026-09-15", "09:00", "12:00", 30);
        Assert.Equal(new[] { "09:00", "09:30", "10:00", "10:30", "11:00", "11:30", "12:00" }, times);
    }

    [Fact]
    public void ScheduleTimes_OnceWhenIntervalZero()
    {
        var times = ReminderService.ScheduleTimes("2026-09-15", "09:00", "12:00", 0);
        Assert.Equal(new[] { "09:00" }, times);
    }

    [Fact]
    public void ScheduleTimes_InvalidInput_ReturnsEmpty()
    {
        Assert.Empty(ReminderService.ScheduleTimes("2026-13-40", "09:00", "12:00", 30));
        Assert.Empty(ReminderService.ScheduleTimes("2026-09-15", "25:00", "12:00", 30));
        Assert.Empty(ReminderService.ScheduleTimes("2026-09-15", "12:00", "09:00", 30));   // end < start
    }

    [Fact]
    public void MatchingAt_DateBased_FiresOnIntervalPointsOnly()
    {
        var r = DayReminder("2026-09-15", "09:00", "10:30", 30, "背单词", 1);
        var arr = Reminders(r);

        Assert.Single(ReminderService.MatchingAt(arr, Dt(2026, 9, 15, 9, 0)));
        Assert.Single(ReminderService.MatchingAt(arr, Dt(2026, 9, 15, 9, 30)));
        Assert.Single(ReminderService.MatchingAt(arr, Dt(2026, 9, 15, 10, 0)));
        Assert.Single(ReminderService.MatchingAt(arr, Dt(2026, 9, 15, 10, 30)));
        Assert.Empty(ReminderService.MatchingAt(arr, Dt(2026, 9, 15, 9, 15)));   // 不在间隔点上
        Assert.Empty(ReminderService.MatchingAt(arr, Dt(2026, 9, 15, 10, 45)));  // 超出终点
        Assert.Empty(ReminderService.MatchingAt(arr, Dt(2026, 9, 16, 9, 0)));    // 非当天
        Assert.Empty(ReminderService.MatchingAt(arr, Dt(2026, 9, 14, 9, 0)));    // 非当天
    }

    [Fact]
    public void MatchingAt_DateBased_Disabled_DoesNotFire()
    {
        var r = DayReminder("2026-09-15", "09:00", "10:00", 30, enabled: false);
        Assert.Empty(ReminderService.MatchingAt(Reminders(r), Dt(2026, 9, 15, 9, 0)));
    }

    [Fact]
    public void Priority_ClampedAndText()
    {
        Assert.Equal(0, ReminderService.PriorityOf(DayReminder("2026-09-15", "09:00", "10:00", 0)));
        Assert.Equal(1, ReminderService.PriorityOf(DayReminder("2026-09-15", "09:00", "10:00", 0, priority: 1)));
        Assert.Equal(2, ReminderService.PriorityOf(DayReminder("2026-09-15", "09:00", "10:00", 0, priority: 9)));
        Assert.Equal("普通", ReminderService.PriorityText(0));
        Assert.Equal("重要", ReminderService.PriorityText(1));
        Assert.Equal("紧急", ReminderService.PriorityText(2));
    }

    // ------------------------------------------------------------ 过期清理

    [Fact]
    public void Expired_RemovesPastDateOnly_KeepsTodayLegacyAndInvalid()
    {
        var today = new DateTime(2026, 9, 10);
        var arr = Reminders(
            DayReminder("2026-09-09", "09:00", "10:00", 30, "昨天"),      // 已过 → 删
            DayReminder("2026-09-08", "09:00", "10:00", 30, "大前天"),    // 已过 → 删
            DayReminder("2026-09-10", "09:00", "10:00", 30, "今天"),      // 保留
            DayReminder("2026-09-11", "09:00", "10:00", 30, "明天"),      // 保留
            DayReminder("bad-date", "09:00", "10:00", 30, "非法日期"),     // 保留
            Legacy("19:00", "每日", true));                               // 每日 → 保留

        var expired = ReminderService.Expired(arr, today);

        Assert.Equal(2, expired.Count);
        Assert.Contains(expired, r => DataStore.GetString(r["label"]) == "昨天");
        Assert.Contains(expired, r => DataStore.GetString(r["label"]) == "大前天");
    }

    [Fact]
    public void Expired_Null_ReturnsEmpty()
        => Assert.Empty(ReminderService.Expired(null, new DateTime(2026, 9, 10)));

    // ------------------------------------------------------------ 下一次提醒

    [Fact]
    public void NextEnabled_Legacy_TodayIfStillAhead_ElseTomorrow()
    {
        var now = Dt(2026, 8, 17, 18, 0);
        var arr = Reminders(Legacy("19:00", "晚间复盘", true), Legacy("17:30", "背词", true));

        var next = ReminderService.NextEnabled(arr, now);
        Assert.NotNull(next);
        Assert.Equal("今天", next.Value.day);
        Assert.Equal("19:00", next.Value.hhmm);

        var next2 = ReminderService.NextEnabled(arr, Dt(2026, 8, 17, 19, 30));
        Assert.NotNull(next2);
        Assert.Equal("明天", next2.Value.day);
        Assert.Equal("17:30", next2.Value.hhmm);   // 过去的时间滚到明天
    }

    [Fact]
    public void NextEnabled_DateBased_FutureDateStartsAtStart()
    {
        var now = Dt(2026, 9, 10, 12, 0);
        var r = DayReminder("2026-09-15", "09:00", "12:00", 60, "考研数学");
        var next = ReminderService.NextEnabled(Reminders(r), now);
        Assert.NotNull(next);
        Assert.Equal("09:00", next.Value.hhmm);   // 未来日期 → 那天 09:00 首响
        Assert.Equal("明天", next.Value.day);
        Assert.Equal("考研数学", next.Value.label);
    }

    [Fact]
    public void NextEnabled_DateBased_TodayReturnsNextIntervalPoint()
    {
        var now = Dt(2026, 9, 15, 9, 20);
        var r = DayReminder("2026-09-15", "09:00", "12:00", 30, "考研数学");
        var next = ReminderService.NextEnabled(Reminders(r), now);
        Assert.NotNull(next);
        Assert.Equal("09:30", next.Value.hhmm);
        Assert.Equal("今天", next.Value.day);
    }

    [Fact]
    public void NextEnabled_DateBased_AfterEnd_ReturnsNull()
    {
        var now = Dt(2026, 9, 15, 12, 30);
        var r = DayReminder("2026-09-15", "09:00", "12:00", 30, "考研数学");
        Assert.Null(ReminderService.NextEnabled(Reminders(r), now));
    }

    [Fact]
    public void NextEnabled_SkipsDisabledAndInvalid()
    {
        var now = Dt(2026, 8, 17, 12, 0);
        var arr = Reminders(Legacy("25:00", "非法时间", true), Legacy("19:00", "已关闭", false),
            Legacy("20:00", "", true), DayReminder("2026-08-18", "09:00", "10:00", 60, enabled: false));

        var next = ReminderService.NextEnabled(arr, now);
        Assert.NotNull(next);
        Assert.Equal("20:00", next.Value.hhmm);
        Assert.Equal("提醒", next.Value.label);   // 空标签回退默认
    }

    [Fact]
    public void NextEnabled_NoEnabled_ReturnsNull()
        => Assert.Null(ReminderService.NextEnabled(Reminders(Legacy("19:00", "关", false)), Dt(2026, 8, 17, 12, 0)));

    [Theory]
    [InlineData("19:00", true)]
    [InlineData("00:00", true)]
    [InlineData("23:59", true)]
    [InlineData("24:00", false)]
    [InlineData("9:00", true)]    // 宽松解析（镜像 Python int(part)）
    [InlineData("19:60", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    public void TryParseHm_Validates(string time, bool expected)
        => Assert.Equal(expected, ReminderService.TryParseHm(time, out _, out _));

    // ------------------------------------------------------------ 待办收集

    [Fact]
    public void PendingToday_CountsUndone_ReturnsFirst3()
    {
        var daily = new JsonObject
        {
            ["2026-08-17"] = new JsonArray
            {
                new JsonObject { ["text"] = "任务A", ["done"] = true },
                new JsonObject { ["text"] = "任务B", ["done"] = false },
                new JsonObject { ["text"] = "任务C", ["done"] = false },
                new JsonObject { ["text"] = "任务D", ["done"] = false },
                new JsonObject { ["text"] = "任务E", ["done"] = false },
            },
        };

        var (total, first3) = ReminderService.PendingToday(daily, "2026-08-17");
        Assert.Equal(4, total);
        Assert.Equal(3, first3.Count);
        Assert.Equal("任务B", first3[0]);
        Assert.Equal("任务D", first3[2]);
    }

    [Fact]
    public void PendingToday_NoPending_ReturnsZero()
    {
        var daily = new JsonObject
        {
            ["2026-08-17"] = new JsonArray { new JsonObject { ["text"] = "A", ["done"] = true } },
        };
        var (total, first3) = ReminderService.PendingToday(daily, "2026-08-17");
        Assert.Equal(0, total);
        Assert.Empty(first3);
    }
}
