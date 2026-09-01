using System;
using System.Globalization;

namespace KaoyanPlanner.WPF.Services;

/// <summary>widgets.py fmt_minutes 移植：把专注分钟数显示得更友好。</summary>
public static class Fmt
{
    /// <summary>≤100 分钟显示「N 分钟」；超过转「X 小时（Y 分钟）」。整时省略余数。</summary>
    public static string Minutes(double minutes)
    {
        int m = (int)Math.Round(Math.Max(0, minutes), MidpointRounding.AwayFromZero);
        if (m <= 100) return $"{m} 分钟";
        int h = m / 60;
        int rem = m % 60;
        return rem == 0 ? $"{h} 小时" : $"{h} 小时 {rem} 分钟";
    }
}
