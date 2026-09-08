using KaoyanPlanner.WPF.Controls;
using Xunit;

namespace KaoyanPlanner.WPF.Tests;

/// <summary>
/// HistogramControl 纯静态逻辑（2 点日界 2h 分桶 + 区间标签）：不触碰 WPF 资源，可直接测试。
/// 窗口日 D 的日历小时键时序 = D 当天 02:00–24:00（键 2..23）+ 次日 00:00–02:00（键 0..1），
/// 合并成 12 根「每根 2h」的柱：键 2,3→桶0 … 键 22,23→桶10，键 0,1→桶11。
/// </summary>
public class HistogramControlTests
{
    [Fact]
    public void TwoHourBuckets_AllZero_Returns12Zeros()
    {
        var b = HistogramControl.TwoHourBuckets(new double[24]);
        Assert.Equal(12, b.Length);
        Assert.All(b, v => Assert.Equal(0, v));
    }

    [Fact]
    public void TwoHourBuckets_Hours23_GoToBucket0()
    {
        var m = new double[24];
        m[2] = 30;   // 02:00–03:00
        m[3] = 20;   // 03:00–04:00
        var b = HistogramControl.TwoHourBuckets(m);
        Assert.Equal(50, b[0]);
        Assert.Equal(0, b[11]);   // 次日凌晨桶不受 2-4 点影响
    }

    [Fact]
    public void TwoHourBuckets_LateEvening_GoToBucket10()
    {
        var m = new double[24];
        m[22] = 40;   // 22:00–23:00
        m[23] = 10;   // 23:00–24:00
        var b = HistogramControl.TwoHourBuckets(m);
        Assert.Equal(50, b[10]);
    }

    [Fact]
    public void TwoHourBuckets_NextDayEarlyMorning_GoToLastBucket11()
    {
        var m = new double[24];
        m[0] = 15;   // 次日 00:00–01:00
        m[1] = 25;   // 次日 01:00–02:00
        var b = HistogramControl.TwoHourBuckets(m);
        Assert.Equal(40, b[11]);   // 次日凌晨补昨晚 → 收在横轴末尾
        Assert.Equal(0, b[0]);
    }

    [Fact]
    public void TwoHourBuckets_WrongLength_Returns12Zeros()
    {
        var b = HistogramControl.TwoHourBuckets(new double[10]);
        Assert.Equal(12, b.Length);
        Assert.All(b, v => Assert.Equal(0, v));
    }

    [Fact]
    public void TwoHourLabels_Starts2Ends2_WithMidnightRange()
    {
        string[] labels = HistogramControl.TwoHourLabels();
        Assert.Equal(12, labels.Length);
        Assert.Equal("2-4", labels[0]);
        Assert.Equal("4-6", labels[1]);
        Assert.Equal("20-22", labels[10 - 1]);   // 桶9 = 20-22
        Assert.Equal("22-24", labels[10]);        // 22:00–24:00 → 22-24（0 点显示为 24，语义更清晰）
        Assert.Equal("0-2", labels[11]);         // 次日 0-2 → 收在 2 点
    }
}
