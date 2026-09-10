using System.Globalization;
using System.Text.RegularExpressions;

namespace KaoyanPlanner.WPF.Services;

/// <summary>
/// 自然语言提醒解析：把「提醒我 明天下午3点 喝水 每30分钟 重要」拆成
/// label / date / start / end / interval / priority。
/// 纯函数、无 IO、不落盘，可单测。时间未指定 → StartMinute=null（由调用方决定默认值）。
/// 支持：
///  ・日期：今天/今日/今晚/今早、明天/明日/明晚/明早、后天、大后天、N天后、
///          M月D日/号、纯 D日/号（跨月自动到下月）、默认今天
///  ・时间：X点 / X点半 / X点Y分 / HH:mm / 中文数字，前缀 凌晨/早上/上午/中午/下午/傍晚/晚上/夜里
///  ・时间段：A到B / A-B（end 不合法时调用方兜底）
///  ・间隔：每N分钟 / 每隔N分钟 / N分钟一次
///  ・优先级：紧急=2 / 重要=1
/// </summary>
public static class ReminderTextParser
{
    public sealed record Result(string Label, DateTime Date, int? StartMinute, int? EndMinute, long IntervalMin, int Priority);

    private static readonly string[] TriggerWords =
    {
        "提醒我一下", "提醒我", "提醒一下", "提醒", "设个提醒", "设提醒", "设置提醒",
        "添加提醒", "加个提醒", "加提醒", "帮我提醒", "帮我设个", "帮我定个", "定个提醒",
        "定提醒", "闹钟", "帮我", "帮我设", "设一个", "设个", "一下",
    };

    // 顺序敏感：长词在前（大后天 必须早于 后天；明天 早于 明早/明晚）
    private static readonly string[] DateWords =
        { "大后天", "后天", "明天", "明日", "明早", "明晚", "今天", "今日", "今早", "今晚" };

    // 时间段连接词（- 要求两侧都是时间点，避免误伤 label）
    private const string RangeSep = @"(?:到|至|—|~|～|-)";

    // 单个时间点：前缀 + 时 + (点|时|:) + 分
    private const string TimePoint =
        @"(?:(?<p>凌晨|早上|早晨|上午|中午|下午|傍晚|晚上|夜里|午夜)?(?<h>\d{1,2}|[零一二两三四五六七八九十]+)\s*(?:点|时|:)(?:(?<m>半|\d{1,2}|[零一二两三四五六七八九十]+)\s*分?)?)";

    // 时间段 = 时间点 + 连接词 + 时间点（组名前缀 p1/h1/m1、p2/h2/m2）
    private static readonly Regex TimeRangeRe = new(
        TimePoint.Replace("(?<p>", "(?<p1>").Replace("(?<h>", "(?<h1>").Replace("(?<m>", "(?<m1>")
        + RangeSep +
        TimePoint.Replace("(?<p>", "(?<p2>").Replace("(?<h>", "(?<h2>").Replace("(?<m>", "(?<m2>"),
        RegexOptions.Compiled);

    private static readonly Regex TimeSingleRe = new(TimePoint, RegexOptions.Compiled);

    private static readonly Dictionary<char, int> CnDigits = new()
    {
        ['零'] = 0, ['一'] = 1, ['二'] = 2, ['两'] = 2, ['三'] = 3, ['四'] = 4,
        ['五'] = 5, ['六'] = 6, ['七'] = 7, ['八'] = 8, ['九'] = 9,
    };

    /// <summary>解析提醒自然语言；label 为空（只有时间没内容）→ null。</summary>
    public static Result? Parse(string text, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = text;

        // 1) 优先级
        int priority = 0;
        if (t.Contains("紧急", StringComparison.Ordinal)) { priority = 2; t = t.Replace("紧急", " "); }
        else if (t.Contains("重要", StringComparison.Ordinal)) { priority = 1; t = t.Replace("重要", " "); }

        // 2) 间隔：每N分钟 / 每隔N分钟 / N分钟一次
        long interval = 0;
        Match m = Regex.Match(t, @"每\s*隔?\s*([0-9]+|[零一二两三四五六七八九十]+)\s*分钟");
        if (m.Success)
        {
            interval = Math.Max(0, ParseNumber(m.Groups[1].Value));
            t = t.Remove(m.Index, m.Length);
        }
        else
        {
            m = Regex.Match(t, @"([0-9]+|[零一二两三四五六七八九十]+)\s*分钟\s*(?:一次|提醒)");
            if (m.Success)
            {
                interval = Math.Max(0, ParseNumber(m.Groups[1].Value));
                t = t.Remove(m.Index, m.Length);
            }
        }

        // 3) 日期（相对词 → N天后 → M月D日 → 纯 D日）
        DateTime date = now.Date;
        bool dateFound = false;
        foreach (string w in DateWords)
        {
            if (!t.Contains(w, StringComparison.Ordinal)) continue;
            date = w switch
            {
                "大后天" => now.Date.AddDays(3),
                "后天" => now.Date.AddDays(2),
                "明天" or "明日" or "明早" or "明晚" => now.Date.AddDays(1),
                _ => now.Date,
            };
            t = t.Replace(w, " ");
            dateFound = true;
            break;
        }
        if (!dateFound)
        {
            m = Regex.Match(t, @"([0-9]+|[零一二两三四五六七八九十]+)\s*天\s*(?:后|以后)");
            if (m.Success)
            {
                date = now.Date.AddDays(Math.Max(1, ParseNumber(m.Groups[1].Value)));
                t = t.Remove(m.Index, m.Length);
                dateFound = true;
            }
        }
        if (!dateFound)
        {
            m = Regex.Match(t, @"([0-9]{1,2}|[零一二两三四五六七八九十]+)\s*月\s*([0-9]{1,2}|[零一二两三四五六七八九十]+)\s*[日号]");
            if (m.Success && TryAbsoluteDate(m.Groups[1].Value, m.Groups[2].Value, now, out DateTime d))
            {
                date = d;
                t = t.Remove(m.Index, m.Length);
                dateFound = true;
            }
        }
        if (!dateFound)
        {
            m = Regex.Match(t, @"([0-9]{1,2}|[零一二两三四五六七八九十]+)\s*[日号]");
            if (m.Success && TryMonthDay(m.Groups[1].Value, now, out DateTime d))
            {
                date = d;
                t = t.Remove(m.Index, m.Length);
                dateFound = true;
            }
        }

        // 4) 时间：先时间段（A到B），再单点
        int? start = null, end = null;
        if (TryParseRange(t, out int rs, out int re))
        {
            start = rs;
            end = re;
            t = TimeRangeRe.Replace(t, " ");
        }
        else if (TryParseSingle(t, out int s2))
        {
            start = s2;
            t = TimeSingleRe.Replace(t, " ");
        }

        // 5) 触发词 + 助词 → label
        string label = CleanLabel(t);
        if (label.Length == 0) return null;

        return new Result(label, date, start, end, interval, priority);
    }

    // ------------------------------------------------------------ 日期

    private static bool TryAbsoluteDate(string moStr, string dayStr, DateTime now, out DateTime date)
    {
        date = default;
        int mo = ParseNumber(moStr), d = ParseNumber(dayStr);
        if (mo is < 1 or > 12 || d is < 1 or > 31) return false;
        try
        {
            date = new DateTime(now.Year, mo, d);
            if (date < now.Date) date = date.AddYears(1);   // 今年已过 → 明年
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryMonthDay(string dayStr, DateTime now, out DateTime date)
    {
        date = default;
        int d = ParseNumber(dayStr);
        if (d is < 1 or > 31) return false;
        try
        {
            date = new DateTime(now.Year, now.Month, d);
            if (date < now.Date) date = date.AddMonths(1);   // 本月已过 → 下月
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------ 时间

    private static bool TryParseRange(string t, out int start, out int end)
    {
        start = end = 0;
        Match m = TimeRangeRe.Match(t);
        if (!m.Success) return false;
        string p1 = m.Groups["p1"].Value, p2 = m.Groups["p2"].Value;
        if (!TryToMinutes(p1, m.Groups["h1"].Value, m.Groups["m1"].Value, out start)) return false;
        if (!TryToMinutes(p2, m.Groups["h2"].Value, m.Groups["m2"].Value, out end)) return false;
        // 「晚上8点到10点」：后半段没前缀且数值比前半段小 → 继承前段前缀重算（8点→10点=22:00）
        if (p2.Length == 0 && p1.Length > 0 && end <= start)
            if (!TryToMinutes(p1, m.Groups["h2"].Value, m.Groups["m2"].Value, out end)) return false;
        if (end <= start) end = Math.Min(1439, start + 60);   // 非法区间兜底：起点后 1 小时
        return true;
    }

    private static bool TryParseSingle(string t, out int start)
    {
        start = 0;
        Match m = TimeSingleRe.Match(t);
        if (!m.Success) return false;
        return TryToMinutes(m.Groups["p"].Value, m.Groups["h"].Value, m.Groups["m"].Value, out start);
    }

    /// <summary>「下午3点半」→ 15:30；「晚上12点」→ 0:00；无前缀按字面。</summary>
    private static bool TryToMinutes(string pre, string hStr, string mStr, out int minutes)
    {
        minutes = 0;
        int hh = ParseNumber(hStr);
        if (hh < 0) return false;
        int mm = 0;
        if (!string.IsNullOrEmpty(mStr))
        {
            if (mStr == "半") mm = 30;
            else
            {
                mm = ParseNumber(mStr);
                if (mm < 0) return false;
            }
        }
        if (pre.Length > 0)
        {
            if (pre is "下午" or "傍晚" or "晚上" or "夜里" or "午夜")
            {
                if (hh < 12) hh += 12;
                else if (hh == 12 && pre is "晚上" or "夜里" or "午夜") hh = 0;
            }
            // 凌晨/早上/上午/中午 按字面（中午12点 = 12:00）
        }
        if (hh is < 0 or > 23 || mm is < 0 or > 59) return false;
        minutes = hh * 60 + mm;
        return true;
    }

    // ------------------------------------------------------------ 数字 / 清理

    /// <summary>阿拉伯数字或中文数字 → int；非法返回 -1。</summary>
    private static int ParseNumber(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return -1;
        if (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int v)) return v;
        if (s.Length == 1) return CnDigit(s[0]);
        if (s == "十") return 10;
        if (s.StartsWith("十", StringComparison.Ordinal)) return 10 + CnDigit(s[1]);   // 十一~十九
        if (s.EndsWith("十", StringComparison.Ordinal)) return CnDigit(s[0]) * 10;     // 二十~九十
        return CnDigit(s[0]) * 10 + CnDigit(s[1]);                                     // 二十一~九十九
    }

    private static int CnDigit(char c) => CnDigits.TryGetValue(c, out int v) ? v : -1;

    private static string CleanLabel(string t)
    {
        foreach (string w in TriggerWords)
            t = t.Replace(w, " ", StringComparison.Ordinal);
        t = Regex.Replace(t, @"\s+", " ");
        return t.Trim(" ，,。.！!？?～~、:：;；\"'「」()（）【】《》".ToCharArray());
    }
}
