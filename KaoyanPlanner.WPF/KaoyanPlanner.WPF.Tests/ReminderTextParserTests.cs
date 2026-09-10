using KaoyanPlanner.WPF.Services;
using Xunit;

namespace KaoyanPlanner.WPF.Tests;

/// <summary>
/// 自然语言提醒解析测试：日期（今天/明天/几月几号/几号/N天后）、
/// 时间（几点/半点/X点Y分/24h制/时段前缀）、时间段、间隔、紧急程度。
/// 固定基准时间 2026-09-10 10:00（周四）。
/// </summary>
public class ReminderTextParserTests
{
    private static readonly DateTime Now = new(2026, 9, 10, 10, 0, 0);

    private static ReminderTextParser.Result Parse(string text) => ReminderTextParser.Parse(text, Now)!;

    private static ReminderTextParser.Result? TryParse(string text) => ReminderTextParser.Parse(text, Now);

    // ------------------------------------------------------------ 日期

    [Fact]
    public void Parse_明天下午3点_DateIsTomorrowStart1500()
    {
        var r = Parse("提醒我 明天下午3点 喝水");
        Assert.Equal(new DateTime(2026, 9, 11), r.Date);
        Assert.Equal(15 * 60, r.StartMinute);
        Assert.Equal("喝水", r.Label);
    }

    [Fact]
    public void Parse_今天_DateIsToday()
    {
        var r = Parse("提醒我 今天 19:30 复盘");
        Assert.Equal(new DateTime(2026, 9, 10), r.Date);
        Assert.Equal(19 * 60 + 30, r.StartMinute);
        Assert.Equal("复盘", r.Label);
    }

    [Fact]
    public void Parse_几月几号_AbsoluteDate()
    {
        var r = Parse("提醒我 9月12号 早上8点 背单词");
        Assert.Equal(new DateTime(2026, 9, 12), r.Date);
        Assert.Equal(8 * 60, r.StartMinute);
        Assert.Equal("背单词", r.Label);
    }

    [Fact]
    public void Parse_中文月日_AbsoluteDate()
    {
        var r = Parse("提醒我 九月十五号 上午10点 交作业");
        Assert.Equal(new DateTime(2026, 9, 15), r.Date);
        Assert.Equal(10 * 60, r.StartMinute);
        Assert.Equal("交作业", r.Label);
    }

    [Fact]
    public void Parse_已过月份_AbsoluteDateRollsToNextYear()
    {
        // 9/10 说「3月5号」→ 今年已过 → 明年 3/5
        var r = Parse("提醒我 3月5号 下午2点 体检");
        Assert.Equal(new DateTime(2027, 3, 5), r.Date);
    }

    [Fact]
    public void Parse_纯几号_MonthDay()
    {
        // 9/10 说「15号」→ 本月 9/15；「25号」→ 本月 9/25
        Assert.Equal(new DateTime(2026, 9, 15), Parse("提醒我 15号 下午2点 交作业").Date);
        Assert.Equal(new DateTime(2026, 9, 25), Parse("提醒我 25号 下午2点 交作业").Date);
    }

    [Fact]
    public void Parse_纯几号_已过RollsToNextMonth()
    {
        // 9/10 说「5号」→ 已过 → 下月 10/5
        var r = Parse("提醒我 5号 下午2点 交作业");
        Assert.Equal(new DateTime(2026, 10, 5), r.Date);
    }

    [Fact]
    public void Parse_后天_DayAfterTomorrow()
    {
        var r = Parse("提醒我 后天 晚上8点 看书");
        Assert.Equal(new DateTime(2026, 9, 12), r.Date);
    }

    [Fact]
    public void Parse_NDaysLater()
    {
        var r = Parse("提醒我 3天后 下午3点 吃药");
        Assert.Equal(new DateTime(2026, 9, 13), r.Date);
        Assert.Equal("吃药", r.Label);
    }

    [Fact]
    public void Parse_无日期_DefaultsToday()
    {
        var r = Parse("提醒我 下午3点 喝水");
        Assert.Equal(new DateTime(2026, 9, 10), r.Date);
    }

    // ------------------------------------------------------------ 时间

    [Fact]
    public void Parse_下午前缀_Adds12Hours()
    {
        Assert.Equal(15 * 60, Parse("提醒我 下午3点 喝水").StartMinute);
        Assert.Equal(20 * 60, Parse("提醒我 晚上8点 跑步").StartMinute);
        Assert.Equal(7 * 60, Parse("提醒我 早上7点 起床").StartMinute);
        Assert.Equal(12 * 60, Parse("提醒我 中午12点 吃饭").StartMinute);
        Assert.Equal(1 * 60, Parse("提醒我 凌晨1点 夜巡").StartMinute);
    }

    [Fact]
    public void Parse_晚上12点_Midnight()
    {
        Assert.Equal(0, Parse("提醒我 晚上12点 睡觉").StartMinute);
    }

    [Fact]
    public void Parse_中文数字_半点()
    {
        Assert.Equal(3 * 60 + 30, Parse("提醒我 三点半 喝水").StartMinute);
        Assert.Equal(2 * 60, Parse("提醒我 两点 喝水").StartMinute);
    }

    [Fact]
    public void Parse_点分_And_24h()
    {
        Assert.Equal(3 * 60 + 15, Parse("提醒我 3点15分 喝水").StartMinute);
        Assert.Equal(15 * 60 + 5, Parse("提醒我 15:05 喝水").StartMinute);
    }

    [Fact]
    public void Parse_时间段_StartAndEnd()
    {
        var r = Parse("提醒我 后天 晚上8点到10点 看书 每15分钟");
        Assert.Equal(20 * 60, r.StartMinute);
        Assert.Equal(22 * 60, r.EndMinute);
        Assert.Equal(15, r.IntervalMin);
        Assert.Equal("看书", r.Label);
    }

    // ------------------------------------------------------------ 间隔 / 优先级

    [Fact]
    public void Parse_间隔_EveryMinutes()
    {
        Assert.Equal(30, Parse("提醒我 明天 早上8点 背单词 每30分钟").IntervalMin);
        Assert.Equal(10, Parse("提醒我 明天 早上8点 背单词 每隔10分钟").IntervalMin);
        Assert.Equal(0, Parse("提醒我 明天 早上8点 背单词").IntervalMin);
    }

    [Fact]
    public void Parse_优先级_UrgentImportant()
    {
        Assert.Equal(2, Parse("提醒我 今天 19:30 复盘 紧急").Priority);
        Assert.Equal(1, Parse("提醒我 明天 早上8点 背单词 重要").Priority);
        Assert.Equal(0, Parse("提醒我 明天 早上8点 背单词").Priority);
    }

    [Fact]
    public void Parse_闹钟触发词_WorksAsReminder()
    {
        var r = Parse("闹钟 明天 7点 起床");
        Assert.Equal("起床", r.Label);
        Assert.Equal(new DateTime(2026, 9, 11), r.Date);
        Assert.Equal(7 * 60, r.StartMinute);
    }

    // ------------------------------------------------------------ 边界

    [Fact]
    public void Parse_只有触发词_ReturnsNull()
    {
        Assert.Null(TryParse("提醒"));
        Assert.Null(TryParse("提醒我"));
    }

    [Fact]
    public void Parse_只有时间无内容_ReturnsNull()
    {
        Assert.Null(TryParse("提醒我 明天下午3点"));
    }

    [Fact]
    public void Parse_空文本_ReturnsNull()
    {
        Assert.Null(TryParse(""));
        Assert.Null(TryParse("   "));
    }
}
