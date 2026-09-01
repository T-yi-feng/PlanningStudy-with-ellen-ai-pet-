using System.Collections.Generic;
using KaoyanPlanner.WPF.Services;
using Xunit;

namespace KaoyanPlanner.WPF.Tests;

/// <summary>
/// PlanPalette：计划名 → ChartNBrush 资源键的纯函数映射测试。
/// 颜色随 plans 顺序稳定（按 index % 6）；null / 空串 / 已删除计划（不在列表）→ 灰（TextMutedBrush）。
/// </summary>
public class PlanPaletteTests
{
    private static readonly List<string> Plans = new() { "考研计划", "健身计划", "阅读计划", "外语计划", "饮食计划", "睡眠计划", "第七计划" };

    [Theory]
    [InlineData("考研计划", "Chart1Brush")]
    [InlineData("健身计划", "Chart2Brush")]
    [InlineData("阅读计划", "Chart3Brush")]
    [InlineData("外语计划", "Chart4Brush")]
    [InlineData("饮食计划", "Chart5Brush")]
    [InlineData("睡眠计划", "Chart6Brush")]
    [InlineData("第七计划", "Chart1Brush")]   // 超出 6 → 回绕 index % 6
    public void BrushKeyFor_IndexedPlan_MapsToChartBrush(string plan, string expected)
        => Assert.Equal(expected, PlanPalette.BrushKeyFor(plan, Plans));

    [Fact]
    public void BrushKeyFor_NullOrEmpty_FallsBackToMuted()
    {
        Assert.Equal("TextMutedBrush", PlanPalette.BrushKeyFor(null, Plans));
        Assert.Equal("TextMutedBrush", PlanPalette.BrushKeyFor("", Plans));
    }

    [Fact]
    public void BrushKeyFor_DeletedPlan_FallsBackToMuted()
    {
        Assert.Equal("TextMutedBrush", PlanPalette.BrushKeyFor("已删除计划", Plans));   // 不在 plans → 灰
    }
}
