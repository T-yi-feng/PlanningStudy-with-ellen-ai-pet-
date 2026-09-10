namespace KaoyanPlanner.WPF.Services;

/// <summary>
/// 农历（阴历）换算：公历 → 农历，用于提醒界面日历格里的「廿九 / 八月 / 初二」小字标注。
/// 算法为通用查表法（1900–2100 农历数据表），纯静态可单测。
/// </summary>
public static class LunarCalendar
{
    // 农历 1900–2100 年数据：每年一个 4 位十六进制数——
    // 低 4 位 = 闰月（0 = 无闰月）；高 16 位按位表示各月大小（1 = 30 天大月），
    // bit16 也代表闰月是否为大月（与 LeapDays 配合）。标准通用表。
    private static readonly int[] LunarInfo =
    {
        0x04bd8, 0x04ae0, 0x0a570, 0x054d5, 0x0d260, 0x0d950, 0x16554, 0x056a0, 0x09ad0, 0x055d2,
        0x04ae0, 0x0a5b6, 0x0a4d0, 0x0d250, 0x1d255, 0x0b540, 0x0d6a0, 0x0ada2, 0x095b0, 0x14977,
        0x04970, 0x0a4b0, 0x0b4b5, 0x06a50, 0x06d40, 0x1ab54, 0x02b60, 0x09570, 0x052f2, 0x04970,
        0x06566, 0x0d4a0, 0x0ea50, 0x06e95, 0x05ad0, 0x02b60, 0x186e3, 0x092e0, 0x1c8d7, 0x0c950,
        0x0d4a0, 0x1d8a6, 0x0b550, 0x056a0, 0x1a5b4, 0x025d0, 0x092d0, 0x0d2b2, 0x0a950, 0x0b557,
        0x06ca0, 0x0b550, 0x15355, 0x04da0, 0x0a5b0, 0x14573, 0x052b0, 0x0a9a8, 0x0e950, 0x06aa0,
        0x0aea6, 0x0ab50, 0x04b60, 0x0aae4, 0x0a570, 0x05260, 0x0f263, 0x0d950, 0x05b57, 0x056a0,
        0x096d0, 0x04dd5, 0x04ad0, 0x0a4d0, 0x0d4d4, 0x0d250, 0x0d558, 0x0b540, 0x0b6a0, 0x195a6,
        0x095b0, 0x049b0, 0x0a974, 0x0a4b0, 0x0b27a, 0x06a50, 0x06d40, 0x0af46, 0x0ab60, 0x09570,
        0x04af5, 0x04970, 0x064b0, 0x074a3, 0x0ea50, 0x06b58, 0x05ac0, 0x0ab60, 0x096d5, 0x092e0,
        0x0c960, 0x0d954, 0x0d4a0, 0x0da50, 0x07552, 0x056a0, 0x0abb7, 0x025d0, 0x092d0, 0x0cab5,
        0x0a950, 0x0b4a0, 0x0baa4, 0x0ad50, 0x055d9, 0x04ba0, 0x0a5b0, 0x15176, 0x052b0, 0x0a930,
        0x07954, 0x06aa0, 0x0ad50, 0x05b52, 0x04b60, 0x0a6e6, 0x0a4e0, 0x0d260, 0x0ea65, 0x0d530,
        0x05aa0, 0x076a3, 0x096d0, 0x04afb, 0x04ad0, 0x0a4d0, 0x1d0b6, 0x0d250, 0x0d520, 0x0dd45,
        0x0b5a0, 0x056d0, 0x055b2, 0x049b0, 0x0a577, 0x0a4b0, 0x0aa50, 0x1b255, 0x06d20, 0x0ada0,
        0x14b63, 0x09370, 0x049f8, 0x04970, 0x064b0, 0x168a6, 0x0ea50, 0x06b20, 0x1a6c4, 0x0aae0,
        0x0a2e0, 0x0d2e3, 0x0c960, 0x0d557, 0x0d4a0, 0x0da50, 0x05d55, 0x056a0, 0x0a6d0, 0x055d4,
        0x052d0, 0x0a9b8, 0x0a950, 0x0b4a0, 0x0b6a6, 0x0ad50, 0x055a0, 0x0aba4, 0x0a5b0, 0x052b0,
        0x0b273, 0x06930, 0x07337, 0x06aa0, 0x0ad50, 0x14b55, 0x04b60, 0x0a570, 0x054e4, 0x0d160,
        0x0e968, 0x0d520, 0x0daa0, 0x16aa6, 0x056d0, 0x04ae0, 0x0a9d4, 0x0a2d0, 0x0d150, 0x0f252,
        0x0d520,
    };

    private static readonly string[] MonthNames = { "正", "二", "三", "四", "五", "六", "七", "八", "九", "十", "冬", "腊" };
    private static readonly string[] DayNames = { "初", "十", "廿", "三" };

    private const int BaseYear = 1900;
    private static readonly DateTime BaseDate = new(1900, 1, 31);   // 农历 1900 年正月初一

    private static int LeapMonth(int year) => LunarInfo[year - BaseYear] & 0xf;

    private static int LeapDays(int year)
        => LeapMonth(year) > 0 ? ((LunarInfo[year - BaseYear] & 0x10000) != 0 ? 30 : 29) : 0;

    private static int MonthDays(int year, int month)
        => (LunarInfo[year - BaseYear] & (0x10000 >> month)) != 0 ? 30 : 29;

    private static int YearDays(int year)
    {
        int sum = 348;
        for (int i = 0x8000; i > 0x8; i >>= 1)
            if ((LunarInfo[year - BaseYear] & i) != 0) sum++;
        return sum + LeapDays(year);
    }

    /// <summary>公历 → 农历（年/月/日/是否闰月）。超出 1900–2100 范围返回 null。</summary>
    public static (int Year, int Month, int Day, bool Leap)? FromSolar(DateTime solar)
        => SolarToLunarCore(solar, out var r) ? r : null;

    private static bool SolarToLunarCore(DateTime solar, out (int Year, int Month, int Day, bool Leap) result)
    {
        result = default;
        int offset = (solar.Date - BaseDate).Days;
        if (offset < 0) return false;

        int temp = 0;
        int i;
        for (i = BaseYear; i < 2101 && offset > 0; i++)
        {
            temp = YearDays(i);
            offset -= temp;
        }
        if (offset < 0)
        {
            offset += temp;
            i--;
        }
        if (i > 2100) return false;

        int year = i;
        int leap = LeapMonth(year);
        bool isLeap = false;
        for (i = 1; i < 13 && offset > 0; i++)
        {
            // 闰月夹在正常月份之间：先走闰月，再走本月
            if (leap > 0 && i == leap + 1 && !isLeap)
            {
                i--;
                isLeap = true;
                temp = LeapDays(year);
            }
            else
            {
                temp = MonthDays(year, i);
            }
            if (isLeap && i == leap + 1) isLeap = false;
            offset -= temp;
        }

        if (offset == 0 && leap > 0 && i == leap + 1)
        {
            if (isLeap) isLeap = false;
            else
            {
                isLeap = true;
                i--;
            }
        }
        if (offset < 0)
        {
            offset += temp;
            i--;
        }

        result = (year, i, offset + 1, isLeap);
        return true;
    }

    /// <summary>公历 → 农历「月·日」中文小字（如 七月廿九 / 八月 / 闰二月十五），供日历格标注。</summary>
    public static string MonthDayCn(DateTime solar)
    {
        var r = FromSolar(solar);
        if (r is null) return "";
        var (year, month, day, leap) = r.Value;
        string monthName = (leap ? "闰" : "") + MonthNames[month - 1] + "月";
        return day == 1 ? monthName : monthName + DayCn(day);
    }

    /// <summary>农历日中文：初一…初十…廿九…三十。</summary>
    public static string DayCn(int day)
    {
        if (day <= 0 || day > 30) return "";
        if (day == 10) return "初十";
        if (day == 20) return "二十";
        if (day == 30) return "三十";
        return DayNames[(day - 1) / 10] + new[] { "一", "二", "三", "四", "五", "六", "七", "八", "九" }[(day - 1) % 10];
    }

    /// <summary>公历日所在农历月的第一天（用于判断某日是否为农历初一）。</summary>
    public static bool IsLunarFirstDay(DateTime solar)
        => FromSolar(solar) is { } r && r.Day == 1;
}
