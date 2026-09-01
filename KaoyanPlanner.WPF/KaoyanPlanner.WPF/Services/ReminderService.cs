using System.Globalization;
using System.Text.Json.Nodes;

namespace KaoyanPlanner.WPF.Services;

/// <summary>
/// 提醒纯逻辑（无 UI / 无定时器依赖，可单测）：此刻该触发的提醒、下一次提醒、待办收集。
/// 供 HeartbeatService 触发、ReminderTab 摘要共用；镜像 widgets.py ReminderTab/_check_reminders/_check_unfinished。
/// </summary>
public static class ReminderService
{
    /// <summary>返回 HH:mm 此刻该触发的已启用提醒（time == hhmm）。</summary>
    public static IEnumerable<JsonObject> MatchingAt(JsonArray? reminders, string hhmm)
    {
        if (reminders is null) yield break;
        foreach (var n in reminders)
        {
            if (n is not JsonObject r) continue;
            if (!DataStore.GetBool(r["enabled"], true)) continue;
            if (DataStore.GetString(r["time"]) == hhmm)
                yield return r;
        }
    }

    /// <summary>
    /// 下一次触发：今天未到点则今天，否则明天。无启用提醒 / 全部非法 → null。
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
            string time = DataStore.GetString(r["time"]);
            if (!TryParseHm(time, out int h, out int m)) continue;
            var due = now.Date.AddHours(h).AddMinutes(m);
            if (due <= now) due = due.AddDays(1);
            if (best is null || due < best)
            {
                best = due;
                string l = DataStore.GetString(r["label"]);
                label = string.IsNullOrEmpty(l) ? "提醒" : l;
            }
        }
        if (best is null) return null;
        return (best.Value.Date == now.Date ? "今天" : "明天",
                best.Value.ToString("HH:mm", CultureInfo.InvariantCulture), label);
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
