using System.Text.Json.Nodes;

namespace KaoyanPlanner.WPF.Services;

/// <summary>
/// 桌宠本地指令解析 + 执行：修改计划 / 专注控制，不依赖网络。
/// 移植 chat.py handle_command + widgets.py 的 pet_* 系列方法。
/// 数据一律走 DataStore（Save 触发 Changed → 主窗各面板自动刷新），专注走 IPetFocus。
/// HandleCommand 返回 (handled, reply)：handled=false 表示交给 AI 闲聊。
/// </summary>
public sealed class PetCommandService
{
    private readonly DataStore _store;
    private readonly IPetFocus _focus;

    public PetCommandService(DataStore store, IPetFocus focus)
    {
        _store = store;
        _focus = focus;
    }

    // ------------------------------------------------------------ 关键字表（对齐 chat.py）

    private static readonly string[] AddKeys =
        { "添加计划", "添加任务", "新增计划", "新增任务", "加计划", "加一个", "加一条", "帮我加", "加任务", "新增", "添加", "加个", "加入", "加上", "安排" };
    private static readonly string[] DelKeys = { "删除", "移除", "删掉", "去掉" };
    private static readonly string[] DoneKeys =
        { "划掉", "勾掉", "标记完成", "标记为完成", "做完了", "完成一下", "已完成", "完成", "搞定" };
    private static readonly string[] ListKeys =
        { "列出计划", "今日计划", "查看计划", "看看计划", "显示计划", "有哪些任务", "有什么任务", "现在有哪些任务", "任务列表", "计划清单", "还有任务", "还剩", "任务都有什么", "帮我看看计划" };
    private static readonly string[] AllDoneHints = { "全部完成", "全完成", "全部划掉", "全部勾掉", "都完成了", "都完成" };
    private static readonly string[] FocusStartKeys = { "开始专注", "开始计时", "进入专注", "开始番茄", "专注一下" };
    private static readonly string[] FocusPauseKeys = { "暂停专注", "暂停计时", "暂停" };
    private static readonly string[] FocusResumeKeys = { "继续专注", "继续计时", "继续" };
    private static readonly string[] FocusEndKeys = { "结束专注", "停止专注", "停止计时", "结束计时", "重置专注", "重置计时" };
    private static readonly string[] FreezeKeys =
        { "冻结任务", "任务冻结", "冻结计划", "先冻结", "暂时冻结", "暂停一下", "暂停计划", "暂停任务", "最近有事", "冻结" };
    private static readonly string[] UnfreezeKeys = { "解冻任务", "解冻", "解除冻结", "取消冻结", "恢复计划", "恢复任务", "继续任务" };
    private static readonly string[] DebtKeys =
        { "欠卡", "打卡情况", "长期任务进度", "固定任务进度", "长期任务情况", "固定任务情况", "我欠了多少", "欠了多少天", "打卡了吗", "打卡了没", "今天打卡", "要不要补卡", "补卡情况", "欠卡情况" };
    private static readonly string[] PunchKeys = { "打卡", "补卡" };
    private static readonly string[] AddFixedKeys = { "长期任务", "长期计划", "固定任务", "固定计划" };

    // 提醒（自然语言解析，见 ReminderTextParser）：列表优先于添加（「查看提醒」含「提醒」子串）
    private static readonly string[] ReminderListKeys =
        { "列出提醒", "查看提醒", "看看提醒", "提醒列表", "提醒清单", "有什么提醒", "有哪些提醒", "我的提醒" };
    private static readonly string[] ReminderKeys =
        { "提醒我一下", "提醒我", "提醒一下", "提醒", "设个提醒", "设提醒", "设置提醒", "添加提醒",
          "加个提醒", "加提醒", "帮我提醒", "帮我设个", "帮我定个", "定个提醒", "定提醒", "闹钟" };

    private static readonly string[] GenericWords =
        { "计划", "今日计划", "任务", "日程", "列表", "清单", "今天", "里", "现在", "一下", "点" };

    // ------------------------------------------------------------ 匹配工具（对齐 chat.py）

    private static bool AnyIn(string text, string[] keys)
    {
        foreach (string k in keys) if (text.Contains(k, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string After(string text, string key)
    {
        int i = text.IndexOf(key, StringComparison.Ordinal);
        return text[(i + key.Length)..].Trim(" ：:，,。.!！?？～~、\"'「」()（）".ToCharArray());
    }

    private static string Before(string text, string key)
    {
        int i = text.IndexOf(key, StringComparison.Ordinal);
        return text[..i].Trim(" ：:，,。.!！?？～~、\"'「」()（）".ToCharArray());
    }

    private static string CleanTask(string s)
    {
        foreach (string w in new[] { "一个新的", "一条", "一个新", "一个", "个", "条", "项" })
            if (s.StartsWith(w, StringComparison.Ordinal)) { s = s[w.Length..]; break; }
        return s.Trim(" ，,。！!".ToCharArray());
    }

    private static string ExtractTask(string text, string[] keys)
    {
        foreach (string key in keys)
        {
            if (!text.Contains(key, StringComparison.Ordinal)) continue;
            string after = After(text, key);
            if (after.Length > 0 && !ArrayContains(GenericWords, after) && after.Length >= 2)
                return CleanTask(after);
            string before = Before(text, key);
            if (before.Length > 0)
                return CleanTask(before.TrimStart("把，将，请，帮我，给".ToCharArray()));
        }
        return "";
    }

    private static bool ArrayContains(string[] arr, string s)
    {
        foreach (string a in arr) if (s == a) return true;
        return false;
    }

    /// <summary>解析「添加长期任务 名 N天 简介：…」→ (name, desc, days) 或 null。</summary>
    private static (string Name, string Desc, int Days)? ExtractFixed(string text, string[] keys)
    {
        foreach (string key in keys)
        {
            if (!text.Contains(key, StringComparison.Ordinal)) continue;
            string rest = After(text, key);
            if (rest.Length == 0 || ArrayContains(GenericWords, rest))
                rest = Before(text, key);
            rest = (rest ?? "").Trim(" ：:，,。.！!？?～~、\"'".ToCharArray());
            if (rest.Length == 0) continue;

            string desc = "";
            foreach (string tag in new[] { "简介", "说明", "备注" })
            {
                int ti = rest.IndexOf(tag, StringComparison.Ordinal);
                if (ti >= 0)
                {
                    desc = rest[(ti + tag.Length)..].Trim(" ：:，,。.！!？?～~、\"'（）()".ToCharArray());
                    rest = rest[..ti].Trim(" ：:，,。.！!～~、\"'".ToCharArray());
                    break;
                }
            }

            int days = 1;
            System.Text.RegularExpressions.Regex m = new(@"(\d+)\s*天");
            var match = m.Match(rest);
            if (match.Success)
            {
                days = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                rest = rest[..match.Index].Trim(" ：:，,。.！!～~、\"'".ToCharArray());
            }
            string name = CleanTask(rest.Trim(" ：:，,。.！!～~、\"'".ToCharArray()));
            if (name.Length > 0) return (name, desc, days);
        }
        return null;
    }

    /// <summary>模糊匹配：先精确/包含，再按字符重合度（镜像 chat.py 两段式）。</summary>
    private static List<JsonObject> MatchTasks(List<JsonObject> cands, string name)
    {
        var m = new List<JsonObject>();
        foreach (JsonObject t in cands)
            if (name == DataStore.GetString(t["text"]) || (name.Length >= 2 && name.Contains(DataStore.GetString(t["text"]), StringComparison.Ordinal)))
                m.Add(t);
        if (m.Count == 0 && name.Length >= 2)
        {
            int threshold = Math.Max(2, name.Length / 2);
            foreach (JsonObject t in cands)
            {
                int hit = 0;
                foreach (char ch in name) if (DataStore.GetString(t["text"]).Contains(ch)) hit++;
                if (hit >= threshold) m.Add(t);
            }
        }
        return m;
    }

    // ------------------------------------------------------------ 指令解析入口（对齐 chat.py handle_command）

    public (bool Handled, string Reply) HandleCommand(string text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return (true, "");
        if (t is "帮助" or "help" or "你能做什么" or "你会什么" or "你都能干嘛" or "怎么用" or "怎么用你")
            return (true, HelpText());

        if (AnyIn(t, FreezeKeys)) return (true, FreezeTasks(true));
        if (AnyIn(t, UnfreezeKeys)) return (true, FreezeTasks(false));

        if (AnyIn(t, FocusStartKeys)) return (true, StartFocus());
        if (AnyIn(t, FocusPauseKeys)) return (true, PauseFocus());
        if (AnyIn(t, FocusResumeKeys)) return (true, ResumeFocus());
        if (AnyIn(t, FocusEndKeys)) return (true, ResetFocus());

        if (AnyIn(t, DebtKeys)) return (true, DebtReport());

        // 提醒：先「列出」，再「添加」（自然语言：今天/几月几号/几点/间隔/紧急程度）
        if (AnyIn(t, ReminderListKeys)) return (true, ListReminders());

        if (AnyIn(t, ReminderKeys))
        {
            var parsed = ReminderTextParser.Parse(t, DateTime.Now);
            if (parsed is null)
                return (true, "想提醒你什么？说「提醒我 明天下午3点 喝水」这样～");
            return (true, AddReminder(parsed));
        }

        if (AnyIn(t, PunchKeys) && !AnyIn(t, new[] { "？", "?", "吗" }))
        {
            string task = ExtractTask(t, PunchKeys);
            if (task.Length > 0) return (true, PunchTask(task));
            return (true, "要打卡哪个长期任务？说「打卡 背单词」～");
        }

        if (AnyIn(t, AllDoneHints)) return (true, CompleteAll());

        if (AnyIn(t, ListKeys)) return (true, ListTasks());

        if (AnyIn(t, DelKeys))
        {
            string task = ExtractTask(t, DelKeys);
            if (task.Length > 0) return (true, DeleteTask(task));
        }

        if (AnyIn(t, DoneKeys))
        {
            string task = ExtractTask(t, DoneKeys);
            if (task.Length > 0) return (true, CompleteTask(task));
        }

        if (AnyIn(t, AddFixedKeys) && AnyIn(t, AddKeys))
        {
            var parsed = ExtractFixed(t, AddFixedKeys);
            if (parsed is not null)
                return (true, AddFixedTask(parsed.Value.Name, parsed.Value.Desc, parsed.Value.Days));
        }

        if (AnyIn(t, AddKeys))
        {
            string task = ExtractTask(t, AddKeys);
            if (task.Length > 0) return (true, AddTask(task));
        }

        return (false, "");
    }

    // ------------------------------------------------------------ 一次性任务

    public string AddTask(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0)
            return "嗯？想加什么计划呀？直接说「添加 背单词」就好～";
        string day = _store.EnsureToday();
        var daily = DataStore.GetOrCreateObj(_store.Data, "daily");
        if (daily[day] is not JsonArray arr) arr = (JsonArray)(daily[day] = new JsonArray());
        arr.Add(new JsonObject { ["text"] = text, ["done"] = false });
        _store.Save();
        return $"好，已经把「{text}」加进今天啦 ✅";
    }

    public string CompleteAll()
    {
        string day = DataStore.TodayStr();
        var tasks = DailyTasks(day);
        var pending = tasks.Where(t => !DataStore.GetBool(t["done"])).ToList();
        if (pending.Count == 0) return "今天已经全部完成啦，太棒了！🏆";
        foreach (var t in pending) t["done"] = true;
        _store.Save();
        return $"好耶，一次划掉 {pending.Count} 项！今天超高效 💪";
    }

    public string CompleteTask(string name)
    {
        name = (name ?? "").Trim();
        string day = DataStore.TodayStr();
        var pending = DailyTasks(day).Where(t => !DataStore.GetBool(t["done"])).ToList();
        var fixedPending = ActiveFixedTasks();
        if (pending.Count == 0 && fixedPending.Count == 0)
            return "今天没有未完成的任务啦，很棒！";

        var matched = MatchTasks(pending, name);
        if (matched.Count > 0)
        {
            if (matched.Count > 1) return Ambiguous(name, matched);
            var t = matched[0];
            t["done"] = true;
            _store.Save();
            return $"搞定！「{DataStore.GetString(t["text"])}」已划掉 ✅";
        }
        var matchedF = MatchTasks(fixedPending, name);
        if (matchedF.Count > 0)
        {
            if (matchedF.Count > 1) return Ambiguous(name, matchedF);
            var t = matchedF[0];
            t["done"] = true;
            t["owed"] = 0L;
            _store.Save();
            return $"搞定！长期任务「{DataStore.GetString(t["text"])}」提前划去完成啦 🎉";
        }
        return $"没找到「{name}」。可以说「列出计划」看看有哪些～";
    }

    public string DeleteTask(string name)
    {
        name = (name ?? "").Trim();
        string day = DataStore.TodayStr();
        var tasks = DailyTasks(day);
        var matched = MatchTasks(tasks, name);
        if (matched.Count == 0) return $"没找到「{name}」，可以说「列出计划」看看～";
        if (matched.Count > 1) return Ambiguous(name, matched);
        string text = DataStore.GetString(matched[0]["text"]);
        // 必须改真实数组（DailyTasks 是副本）：镜像 chat.py 的 tasks.remove(matched[0])
        var arr = DataStore.GetObj(_store.Data, "daily")?[day] as JsonArray;
        arr?.Remove(matched[0]);
        _store.Save();
        return $"已删除「{text}」";
    }

    public string ListTasks()
    {
        string day = DataStore.TodayStr();
        var tasks = DailyTasks(day);
        var fixedTasks = ActiveFixedTasks();
        if (tasks.Count == 0 && fixedTasks.Count == 0)
            return "今天还没有计划，要不要加一条？跟我说「添加 背单词」就行～";
        var pending = tasks.Where(t => !DataStore.GetBool(t["done"])).ToList();
        var done = tasks.Where(t => DataStore.GetBool(t["done"])).ToList();
        var lines = new List<string> { $"今日共 {tasks.Count} 项一次性任务，已完成 {done.Count} 项：" };
        if (pending.Count > 0)
        {
            lines.Add("未完成：");
            for (int i = 0; i < pending.Count; i++)
                lines.Add($"{i + 1}. {DataStore.GetString(pending[i]["text"])}");
        }
        if (done.Count > 0)
        {
            var names = string.Join("、", done.Take(5).Select(t => DataStore.GetString(t["text"])));
            lines.Add($"已完成：{names}{(done.Count > 5 ? "…" : "")}");
        }
        if (fixedTasks.Count > 0)
        {
            lines.Add($"长期任务 {fixedTasks.Count} 项：");
            for (int i = 0; i < fixedTasks.Count; i++)
            {
                var t = fixedTasks[i];
                long owe = DataStore.GetInt(t["owed"]);
                string tail = owe > 0 ? $"（欠 {owe} 天）" : "";
                lines.Add($"  {i + 1}. {DataStore.GetString(t["text"])} {DataStore.GetInt(t["progress"])}/{DataStore.GetInt(t["target_days"], 1)}{tail}");
            }
        }
        return string.Join("\n", lines);
    }

    // ------------------------------------------------------------ 固定任务

    public string AddFixedTask(string text, string desc, int days)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0)
            return "固定任务内容不能为空，说「添加长期任务 背单词 30天」试试～";
        if (days < 1) days = 1;
        _store.AddFixed(text, desc, days);
        return $"好，长期任务「{text}」建好啦（每天打卡，{days} 天完成）✅";
    }

    public string FreezeTasks(bool on)
    {
        bool frozen = DataStore.GetBool(_store.Data["frozen"]);
        if (frozen == on)
            return on ? "任务已经在冻结状态啦，说「解冻任务」就能恢复～" : "任务没有冻结哦～";
        _store.SetFrozen(on);
        return on
            ? "长期任务已冻结 ❄ 进度和欠卡都先冻住，解冻后也不补欠卡。忙你的～"
            : "解冻啦！长期任务恢复正常打卡 📅";
    }

    public string PunchTask(string name)
    {
        name = (name ?? "").Trim();
        if (DataStore.GetBool(_store.Data["frozen"]))
            return "任务已冻结，暂时不能打卡哦～";
        var active = ActiveFixedTasks();
        if (active.Count == 0)
            return "现在没有进行中的长期任务。说「添加长期任务 背单词 30天」建一个～";
        var matched = MatchTasks(active, name);
        if (matched.Count == 0)
            return $"没找到「{name}」这个长期任务。可以说「长期任务进度」看看有哪些～";
        if (matched.Count > 1)
        {
            var names = string.Join("、", matched.Select(t => DataStore.GetString(t["text"])));
            return $"「{name}」匹配到好几个：{names}，说具体一点～";
        }
        var t = matched[0];
        if (DataStore.FixedPunchedTodayTask(t))
            return $"「{DataStore.GetString(t["text"])}」今天已经打过卡啦，别再打啦 ✅";
        bool backfill = DataStore.GetInt(t["owed"]) > 0;   // 有欠卡 = 补卡（打一次抵消一天）
        _store.PunchFixed(DataStore.GetString(t["id"]));
        string action = backfill ? "补卡" : "打卡";
        return $"「{DataStore.GetString(t["text"])}」{action}成功！进度 {DataStore.GetInt(t["progress"])}/{DataStore.GetInt(t["target_days"], 1)}";
    }

    public string DebtReport()
    {
        var data = _store.Data;
        if (DataStore.GetBool(data["frozen"]))
        {
            string since = DataStore.GetString(data["frozen_since"]);
            return "任务已冻结，欠卡暂不计时。" + (since.Length > 0 ? $"（自 {since} 起冻结）" : "");
        }
        var active = ActiveFixedTasks();
        var debts = active.Where(t => DataStore.GetInt(t["owed"]) > 0).ToList();
        if (active.Count == 0) return "现在没有进行中的长期任务～";
        var lines = new List<string>();
        if (debts.Count > 0)
        {
            long totalOwed = debts.Sum(t => DataStore.GetInt(t["owed"]));
            lines.Add($"⚠ 有 {debts.Count} 个长期任务欠卡共 {totalOwed} 天：");
            foreach (var t in debts)
                lines.Add($"  {DataStore.GetString(t["text"])}：欠 {DataStore.GetInt(t["owed"])} 天，进度 {DataStore.GetInt(t["progress"])}/{DataStore.GetInt(t["target_days"], 1)}");
            lines.Add("欠卡任务按钮会变成「补卡」（蓝底），今天打一次就抵消一天欠卡。");
        }
        else
        {
            lines.Add("当前没有欠卡，全部按时打卡 👍");
        }
        lines.Add("进行中：");
        foreach (var t in active)
        {
            long owe = DataStore.GetInt(t["owed"]);
            lines.Add($"  {DataStore.GetString(t["text"])} {DataStore.GetInt(t["progress"])}/{DataStore.GetInt(t["target_days"], 1)}" + (owe > 0 ? $"（欠 {owe} 天）" : ""));
        }
        return string.Join("\n", lines);
    }

    // ------------------------------------------------------------ 提醒（自然语言添加 / 列出）

    /// <summary>
    /// 把解析结果落盘为提醒（date/start/end/interval_min/label/priority/enabled）。
    /// 未指定时间 → 现在 +1 分钟开始，持续 1 小时仅一次；今天已过的时间自动顺延明天并告知。
    /// </summary>
    public string AddReminder(ReminderTextParser.Result r, DateTime? now = null)
    {
        now ??= DateTime.Now;
        DateTime date = r.Date;
        int startMin = r.StartMinute ?? (now.Value.AddMinutes(1).Hour * 60 + now.Value.AddMinutes(1).Minute);
        int endMin = r.EndMinute ?? Math.Min(1439, startMin + 60);
        long interval = Math.Max(0, r.IntervalMin);

        // 今天、且用户显式给的时间已过 → 顺延明天（不静默，回复里说明）
        bool rolled = r.StartMinute is not null && date == now.Value.Date && startMin <= now.Value.Hour * 60 + now.Value.Minute;
        if (rolled) date = date.AddDays(1);

        string start = $"{startMin / 60:00}:{startMin % 60:00}";
        string end = $"{endMin / 60:00}:{endMin % 60:00}";

        var reminders = GetReminders();
        reminders.Add(new JsonObject
        {
            ["date"] = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["start"] = start,
            ["end"] = end,
            ["interval_min"] = interval,
            ["label"] = r.Label,
            ["priority"] = (long)r.Priority,
            ["enabled"] = true,
        });
        _store.Save();

        string when = date == now.Value.Date ? "今天" : date == now.Value.Date.AddDays(1) ? "明天" : $"{date.Month}月{date.Day}日";
        string timePart = interval > 0 ? $"{start} 到 {end}（每 {interval} 分钟一次）" : start;
        string priTxt = r.Priority == 2 ? "，紧急" : r.Priority == 1 ? "，重要" : "";
        string rolledNote = rolled ? $"\n（你定的今天 {start} 已经过了，我改到明天了）" : "";
        return $"好，已设提醒：{when} {timePart}「{r.Label}」{priTxt} ✅ 现在共 {reminders.Count} 条提醒。{rolledNote}";
    }

    public string ListReminders()
    {
        var reminders = GetReminders();
        if (reminders.Count == 0)
            return "还没有提醒。说「提醒我 明天下午3点 喝水」就能加一条～";
        var lines = new List<string> { $"当前共 {reminders.Count} 条提醒：" };
        foreach (var n in reminders)
        {
            if (n is not JsonObject r) continue;
            string label = ReminderService.LabelOf(r);
            string priTxt = ReminderService.PriorityOf(r) switch { 2 => "（紧急）", 1 => "（重要）", _ => "" };
            string when;
            if (ReminderService.IsLegacy(r))
            {
                when = "每天 " + DataStore.GetString(r["time"]);
            }
            else
            {
                string d = DataStore.GetString(r["date"]);
                long interval = DataStore.GetInt(r["interval_min"], 0);
                string dayCn = d;
                if (DateTime.TryParseExact(d, "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out DateTime dt))
                    dayCn = dt == DateTime.Today ? "今天" : dt == DateTime.Today.AddDays(1) ? "明天" : $"{dt.Month}月{dt.Day}日";
                when = interval > 0
                    ? $"{dayCn} {DataStore.GetString(r["start"])}~{DataStore.GetString(r["end"])}（每{interval}分钟）"
                    : $"{dayCn} {DataStore.GetString(r["start"])}";
            }
            lines.Add($"· {when} {label}{priTxt}");
        }
        return string.Join("\n", lines);
    }

    private JsonArray GetReminders()
    {
        if (_store.Data["reminders"] is JsonArray a) return a;
        var created = new JsonArray();
        _store.Data["reminders"] = created;
        return created;
    }

    // ------------------------------------------------------------ 专注计时控制

    public string StartFocus()
    {
        if (_focus.Running) return "已经在专注啦，继续保持！🎯";
        _focus.Start();
        return "好，开始专注！时间从现在开始累计 🎯";
    }

    public string PauseFocus()
    {
        if (!_focus.Running) return "现在没有在专注哦～";
        _focus.Pause();
        return $"暂停啦，已专注 {(int)_focus.ElapsedSeconds / 60} 分钟，想继续就说「继续专注」";
    }

    public string ResumeFocus()
    {
        if (_focus.Running) return "正在专注中呀～";
        _focus.Start();
        return _focus.ElapsedSeconds > 0
            ? $"继续专注，已累计 {(int)_focus.ElapsedSeconds / 60} 分钟，加油！🎯"
            : "好，继续专注！🎯";
    }

    public string ResetFocus()
    {
        double was = _focus.ElapsedSeconds;
        _focus.Reset();
        return was > 0 ? $"结束专注，本次共专注 {(int)was / 60} 分钟，辛苦啦！" : "计时已清零，说「开始专注」随时再战～";
    }

    // ------------------------------------------------------------ 辅助

    public static string HelpText() =>
        "我可以帮你管计划和提醒哦～试试：\n" +
        "· 添加 背单词\n" +
        "· 添加长期任务 背单词 30天 简介：每天50个\n" +
        "· 打卡 背单词\n" +
        "· 划掉 背单词\n" +
        "· 删除 背单词\n" +
        "· 列出计划\n" +
        "· 长期任务进度\n" +
        "· 冻结任务 / 解冻任务\n" +
        "· 全部完成\n" +
        "· 提醒我 明天下午3点 喝水\n" +
        "· 提醒我 9月12号 晚上8点 背单词 每30分钟 重要\n" +
        "· 列出提醒\n" +
        "也可以随便跟我聊天～";

    private string Ambiguous(string name, List<JsonObject> matched)
    {
        var names = string.Join("、", matched.Select(t => DataStore.GetString(t["text"])));
        return $"「{name}」匹配到好几项：{names}，能说得再具体点吗？";
    }

    private List<JsonObject> DailyTasks(string day)
    {
        var daily = DataStore.GetObj(_store.Data, "daily");
        if (daily is null) return new List<JsonObject>();
        var arr = daily[day];
        var list = new List<JsonObject>();
        if (arr is JsonArray ja)
            foreach (var n in ja) if (n is JsonObject t) list.Add(t);
        return list;
    }

    private List<JsonObject> ActiveFixedTasks()
    {
        var list = new List<JsonObject>();
        if (_store.Data["tasks"] is JsonArray tasks)
            foreach (var n in tasks)
                if (n is JsonObject t && !DataStore.GetBool(t["done"]))
                    list.Add(t);
        return list;
    }
}
