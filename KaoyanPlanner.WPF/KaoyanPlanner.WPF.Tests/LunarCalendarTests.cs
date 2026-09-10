using KaoyanPlanner.WPF.Services;
using Xunit;

namespace KaoyanPlanner.WPF.Tests;

/// <summary>
/// 农历换算测试：以 iPhone 日历截图（2026-09-10 七月廿九 / 09-11 八月初一 / 09-12 八月初二）
/// 作为真实校验向量，另补常见边界（闰月年、正月初一、腊月三十）。
/// </summary>
public class LunarCalendarTests
{
    [Fact]
    public void FromSolar_September2026_MatchesScreenshot()
    {
        var d10 = LunarCalendar.FromSolar(new DateTime(2026, 9, 10));
        Assert.NotNull(d10);
        Assert.Equal((2026, 7, 29, false), d10.Value);

        var d11 = LunarCalendar.FromSolar(new DateTime(2026, 9, 11));
        Assert.NotNull(d11);
        Assert.Equal((2026, 8, 1, false), d11.Value);

        var d12 = LunarCalendar.FromSolar(new DateTime(2026, 9, 12));
        Assert.NotNull(d12);
        Assert.Equal((2026, 8, 2, false), d12.Value);
    }

    [Fact]
    public void MonthDayCn_FormatsLikeScreenshot()
    {
        Assert.Equal("七月廿九", LunarCalendar.MonthDayCn(new DateTime(2026, 9, 10)));
        Assert.Equal("八月", LunarCalendar.MonthDayCn(new DateTime(2026, 9, 11)));     // 初一 → 只显示月名
        Assert.Equal("八月初二", LunarCalendar.MonthDayCn(new DateTime(2026, 9, 12)));
    }

    [Fact]
    public void MonthDayCn_DayNameEdges()
    {
        Assert.Equal("初十", LunarCalendar.DayCn(10));
        Assert.Equal("二十", LunarCalendar.DayCn(20));
        Assert.Equal("廿一", LunarCalendar.DayCn(21));
        Assert.Equal("三十", LunarCalendar.DayCn(30));
        Assert.Equal("", LunarCalendar.DayCn(31));
    }

    [Fact]
    public void FromSolar_LeapMonthYear()
    {
        // 2023 年有闰二月：公历 2023-03-22 是闰二月初一
        var r = LunarCalendar.FromSolar(new DateTime(2023, 3, 22));
        Assert.NotNull(r);
        Assert.True(r.Value.Leap);
        Assert.Equal(2, r.Value.Month);
        Assert.Equal(1, r.Value.Day);
        Assert.Equal("闰二月", LunarCalendar.MonthDayCn(new DateTime(2023, 3, 22)));
    }

    [Fact]
    public void FromSolar_SpringFestivalBoundary()
    {
        // 2026 年春节 = 2026-02-17（正月初一）
        var r = LunarCalendar.FromSolar(new DateTime(2026, 2, 17));
        Assert.NotNull(r);
        Assert.Equal((2026, 1, 1, false), r.Value);
        Assert.Equal("正月", LunarCalendar.MonthDayCn(new DateTime(2026, 2, 17)));

        // 春节前一天 = 2025 年腊月廿九（乙巳年腊月为小月 29 天）
        var prev = LunarCalendar.FromSolar(new DateTime(2026, 2, 16));
        Assert.NotNull(prev);
        Assert.Equal((2025, 12, 29, false), prev.Value);
        Assert.Equal("腊月廿九", LunarCalendar.MonthDayCn(new DateTime(2026, 2, 16)));
    }
}
