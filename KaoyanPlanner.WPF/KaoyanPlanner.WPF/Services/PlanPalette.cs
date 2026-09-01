using System.Collections.Generic;

namespace KaoyanPlanner.WPF.Services;

/// <summary>
/// 按计划取色板：纯 C#，无 WPF 依赖（可单测）。返回主题资源键，UI 侧 FindResource 转 Brush。
/// 当前计划按在 plans 列表中的序号取 Chart1..6Brush（循环）；null/空/不在列表（已删除计划、
/// 未分类）→ TextMutedBrush（灰）。
/// </summary>
public static class PlanPalette
{
    public const int ColorCount = 6;

    public static string BrushKeyFor(string? plan, List<string> plans)
    {
        if (string.IsNullOrEmpty(plan)) return "TextMutedBrush";
        int idx = plans.IndexOf(plan);
        if (idx < 0) return "TextMutedBrush";
        return "Chart" + ((idx % ColorCount) + 1) + "Brush";
    }
}
