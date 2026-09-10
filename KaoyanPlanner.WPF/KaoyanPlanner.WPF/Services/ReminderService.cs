using System.Globalization;
using System.Text.Json.Nodes;

namespace KaoyanPlanner.WPF.Services;

/// <summary>
/// 提醒纯逻辑（无 UI / 无定时器依赖，可单测）：
/// 两种提醒模型——
/// ・旧版（有 time 键）：每天 HH:mm 触发一次（历史数据兼容）。
/// ・新版（有 date 键）：指定日期的 [start, end] 时间段内，按 interval_min 间隔触发；
///   interval_min &lt;= 0 表示仅开始时间触发一次；priority = 0 普通 / 1 重要 / 2 紧急。
/// 供 HeartbeatService 触发、ReminderTab 摘要共用。
/// </summary>
public static class ReminderService
{
    // ------------------------------------------------------------ 模型判别

    /// <summary>旧版每日提醒（有 time、无 date）。</summary>
    public static bool IsLegacy(JsonObject r)
        => !string.IsNullOrEmpty(DataStore.GetString(r["time"]));

    /// <summary>提醒紧急程度：0 普通 / 1 重要 / 2 紧急（非法值归 0）。</summary>
    public static int PriorityOf(JsonObject r)
        => (int)Math.Clamp(DataStore.GetInt(r["priority"], 0), 0, 2);

    public static string PriorityText(int priority) => priority switch
    {
        1 => "重要",
        2 => "紧急",
        _ => "普通",
    };

    /// <summary>标签兜底：空标签显示「提醒」。</summary>
    public static string LabelOf(JsonObject r)
    {
        string l = DataStore.GetString(r["label"]);
        return string.IsNullOrEmpty(l) ? "提醒" : l;
    }

    /// <summary>
    /// 过期提醒（日期已过，如已过一天）：返回应删除的条目，释放空间。
    /// 仅清理日期型提醒（date &lt; today），每日型（time 键）与非法日期保留。
    /// </summary>
    public static List<JsonObject> Expired(JsonArray? reminders, DateTime today)
    {
        var expired = new List<JsonObject>();
        if (reminders is null) return expired;
        foreach (var n in reminders)
        {
            if (n is not JsonObject r || IsLegacy(r)) continue;
            string date = DataStore.GetString(r["date"]);
            if (DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                && d.Date < today.Date)
                expired.Add(r);
        }
        return expired;
    }

    // ------------------------------------------------------------ 触发点计算

    /// <summary>
    /// 新版提醒在该日期的全部触发时刻（HH:mm，含起点与终点）。
    /// interval_min &lt;= 0 → 仅起点一次。日期/时间非法 → 空。
    /// </summary>
    public static List<string> ScheduleTimes(string date, string start, string end, long intervalMin)
    {
        var result = new List<string>();
        if (!TryParseHm(start, out int sh, out int sm) || !TryParseHm(end, out int eh, out int em)) return result;
        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return result;
        int startM = sh * 60 + sm;
        int endM = eh * 60 + em;
        if (endM < startM) return result;

        if (intervalMin <= 0)
        {
            result.Add($"{sh:00}:{sm:00}");
            return result;
        }
        for (int t = startM; t <= endM; t += (int)intervalMin)
            result.Add($"{t / 60:00}:{t % 60:00}");
        return result;
    }

    /// <summary>该提醒此刻（now）是否应触发。旧版：时刻相等即触发；新版：日期命中且在间隔点上。</summary>
    public static bool IsFireTime(JsonObject r, DateTime now)
    {
        if (!DataStore.GetBool(r["enabled"], true)) return false;
        string nowHm = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (IsLegacy(r))
            return DataStore.GetString(r["time"]) == nowHm;

        string date = DataStore.GetString(r["date"]);
        if (date != now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) return false;
        long interval = DataStore.GetInt(r["interval_min"], 0);
        return ScheduleTimes(date, DataStore.GetString(r["start"]), DataStore.GetString(r["end"]), interval)
            .Contains(nowHm);
    }

    /// <summary>此刻该触发的全部已启用提醒（now 为完整时间，含日期）。</summary>
    public static IEnumerable<JsonObject> MatchingAt(JsonArray? reminders, DateTime now)
    {
        if (reminders is null) yield break;
        foreach (var n in reminders)
        {
            if (n is not JsonObject r) continue;
            if (IsFireTime(r, now))
                yield return r;
        }
    }

    /// <summary>
    /// 下一次触发：(day, hhmm, label)。旧版今天未到点则今天、否则明天；
    /// 新版取日期段内 ≥ now 的下一个触发点。全部禁用/非法 → null。
    /// </summary>
    public static (string day, string hhmm, string label)? NextEnabled(JsonArray? reminders, DateTime now)
    {
        DateTime? best = null;
        string label = "提醒";
        if (reminders is null) return null;

        foreach (var n in reminders)
        {
            if (n is not JsonObject r) continue;
            if (!DataStore.GetBool(r["enabled"], true)) continue;
            DateTime? due = NextFireTime(r, now);
            if (due is null) continue;
            if (best is null || due < best)
            {
                best = due;
                label = LabelOf(r);
            }
        }
        if (best is null) return null;
        return (best.Value.Date == now.Date ? "今天" : "明天",
                best.Value.ToString("HH:mm", CultureInfo.InvariantCulture), label);
    }

    /// <summary>单条提醒下一次触发时刻；无（已过期/禁用/非法）→ null。</summary>
    public static DateTime? NextFireTime(JsonObject r, DateTime now)
    {
        if (IsLegacy(r))
        {
            if (!TryParseHm(DataStore.GetString(r["time"]), out int h, out int m)) return null;
            var due = now.Date.AddHours(h).AddMinutes(m);
            if (due <= now) due = due.AddDays(1);
            return due;
        }

        string dateStr = DataStore.GetString(r["date"]);
        if (!DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return null;
        var times = ScheduleTimes(dateStr, DataStore.GetString(r["start"]), DataStore.GetString(r["end"]),
            DataStore.GetInt(r["interval_min"], 0));
        DateTime? best = null;
        foreach (string t in times)
        {
            if (!TryParseHm(t, out int h, out int m)) continue;
            var due = date.Date.AddHours(h).AddMinutes(m);
            if (due < now) continue;
            if (best is null || due < best) best = due;
        }
        return best;
    }

    /// <summary>解析 "HH:mm" → 时/分；格式或范围非法返回 false。</summary>
    public static bool TryParseHm(string time, out int h, out int m)
    {
        h = m = 0;
        var parts = time.Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out h) || !int.TryParse(parts[1], out m)) return false;
        return h is >= 0 and <= 23 && m is >= 0 and <= 59;
    }

    /// <summary>今天待办中未完成的数量 + 前 3 条文本。</summary>
    public static (int total, List<string> first3) PendingToday(JsonObject? daily, string day)
    {
        var tasks = daily is not null ? daily[day] as JsonArray : null;
        var first3 = new List<string>();
        int total = 0;
        if (tasks is not null)
        {
            foreach (var n in tasks)
            {
                if (n is not JsonObject t) continue;
                if (DataStore.GetBool(t["done"])) continue;
                total++;
                if (first3.Count < 3)
                    first3.Add(DataStore.GetString(t["text"]));
            }
        }
        return (total, first3);
    }
}
