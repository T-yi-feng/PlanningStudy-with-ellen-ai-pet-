using System.Text.Json.Nodes;
using KaoyanPlanner.WPF.Services;
using Xunit;

namespace KaoyanPlanner.WPF.Tests;

/// <summary>
/// ReminderService 纯逻辑测试：时刻匹配、下一次提醒（今天/明天/默认标签）、时间解析、待办收集。
/// 全部在内存 JsonObject 上操作，不碰真实 data.json。
/// </summary>
public class ReminderServiceTests
{
    private static JsonArray Reminders(params (string time, string label, bool enabled)[] items)
    {
        var arr = new JsonArray();
        foreach (var (t, l, e) in items)
            arr.Add(new JsonObject { ["time"] = t, ["label"] = l, ["enabled"] = e });
        return arr;
    }

    [Fact]
    public void MatchingAt_FiresEnabledAtExactTime_IgnoresOthers()
    {
        var arr = Reminders(("19:00", "复盘", true), ("20:30", "背词", true), ("19:00", "已关闭", false));
        var matches = ReminderService.MatchingAt(arr, "19:00").ToList();

        Assert.Single(matches);
        Assert.Equal("复盘", matches[0]["label"]!.ToString());
        Assert.Empty(ReminderService.MatchingAt(arr, "18:59"));
    }

    [Fact]
    public void MatchingAt_Null_ReturnsEmpty()
        => Assert.Empty(ReminderService.MatchingAt(null, "19:00"));

    [Fact]
    public void NextEnabled_TodayIfStillAhead_ElseTomorrow()
    {
        var now = new DateTime(2026, 8, 17, 18, 0, 0);
        var arr = Reminders(("19:00", "晚间复盘", true), ("17:30", "背词", true));

        var next = ReminderService.NextEnabled(arr, now);
        Assert.NotNull(next);
        Assert.Equal("今天", next.Value.day);
        Assert.Equal("19:00", next.Value.hhmm);

        var next2 = ReminderService.NextEnabled(arr, new DateTime(2026, 8, 17, 19, 30, 0));
        Assert.NotNull(next2);
        Assert.Equal("明天", next2.Value.day);
        Assert.Equal("17:30", next2.Value.hhmm);   // 过去的时间滚到明天
    }

    [Fact]
    public void NextEnabled_SkipsDisabledAndInvalid()
    {
        var now = new DateTime(2026, 8, 17, 12, 0, 0);
        var arr = Reminders(("25:00", "非法时间", true), ("19:00", "已关闭", false), ("20:00", "", true));

        var next = ReminderService.NextEnabled(arr, now);
        Assert.NotNull(next);
        Assert.Equal("20:00", next.Value.hhmm);
        Assert.Equal("提醒", next.Value.label);   // 空标签回退默认
    }

    [Fact]
    public void NextEnabled_NoEnabled_ReturnsNull()
        => Assert.Null(ReminderService.NextEnabled(Reminders(("19:00", "关", false)), new DateTime(2026, 8, 17, 12, 0, 0)));

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
