using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KaoyanPlanner.WPF.Services;

/// <summary>
/// storage.py 的 C# 移植。主对象是 JsonObject：保持键序、未知键、嵌套配置（pet_chat/caption/voice/tts）
/// 原样存活；原地改 dict 语义 = 改 JsonObject 属性。Save 用 PythonJson 逐字节还原 json.dump 输出。
/// </summary>
public sealed class DataStore
{
    public string DataFile { get; }
    public JsonObject Data { get; private set; } = new();

    /// <summary>Save() 成功后触发，UI 据此重建面板（镜像 Python 的 refresh()）。</summary>
    public event Action? Changed;

    /// <summary>
    /// 当天打卡的内存标记：taskId → 打卡日期。只存内存、绝不落盘（保持 data.json 字节契约）；
    /// 用于区分「当天刚打卡完成」与「之前就已完成」的任务，决定按钮可否撤销。
    /// 跨重启丢失后：未完成任务的撤销仍由数据本身（last_done_date/progress）推导。
    /// 补卡不打今天的卡、不提供当天撤销，故不记这里。
    /// </summary>
    private readonly Dictionary<string, string> _todayPunch = new();   // taskId → 打卡日期

    public DataStore(string? dataFile = null)
    {
        DataFile = dataFile ?? AppPaths.DataFile;
    }

    // ------------------------------------------------------------ 基础 / 默认 / 读写

    /// <summary>窗口日：当天 02:00 起算，00:00–02:00 归入前一天（凌晨补昨晚的卡）。可注入 now 单测。</summary>
    internal static DateTime WindowToday(DateTime now)
        => now.Hour < 2 ? now.Date.AddDays(-1) : now.Date;

    /// <summary>当前窗口日（凌晨 2 点日界的「今天」）。</summary>
    public static DateTime Today() => WindowToday(DateTime.Now);

    public static string TodayStr()
        => Today().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>默认结构，键序与 storage.py 完全一致；不含 archive（由 EnsureToday 惰性创建）。</summary>
    public static JsonObject CreateDefaults() => new()
    {
        ["title"] = "考研复习",
        ["exam_date"] = "",
        ["window_pos"] = null,
        ["collapsed"] = false,
        ["daily"] = new JsonObject(),
        ["tasks"] = new JsonArray(),
        ["frozen"] = false,
        ["frozen_since"] = "",
        ["reminders"] = new JsonArray(),
        ["timer"] = new JsonObject { ["duration_min"] = 25L },
        ["focus_sessions"] = new JsonArray(),
        ["focus_history"] = new JsonObject(),
        ["focus_reminder"] = new JsonObject { ["enabled"] = true, ["interval_min"] = 60L },
        ["unfinished_reminder"] = new JsonObject { ["enabled"] = true, ["interval_min"] = 60L },
        ["pet_chat"] = new JsonObject
        {
            ["enabled"] = true,
            ["base_url"] = "https://open.bigmodel.cn/api/paas/v4/chat/completions",
            ["model"] = "glm-4.7",   // 2026-08-31 实测该账号可用（glm-4.6v-flash 已下线）
            ["api_key"] = "",
        },
        ["pet_idle"] = new JsonObject { ["enabled"] = true, ["interval_min"] = 8L },
        ["caption"] = new JsonObject
        {
            ["enabled"] = false,
            ["model"] = "FunAudioLLM/SenseVoiceSmall",
            ["language"] = "auto",
            ["font_size"] = 18L,
            ["color"] = "#F5F0E6",
        },
        ["voice"] = new JsonObject { ["enabled"] = false },
        ["tts"] = new JsonObject
        {
            ["enabled"] = false,
            ["url"] = "http://127.0.0.1:9880",
            ["server_cmd"] = "",
            ["ref_audio_path"] = "",
            ["prompt_text"] = "",
            ["prompt_lang"] = "zh",
        },
    };

    /// <summary>
    /// 读取 data.json：缺省字段用默认补齐（浅合并，嵌套 dict 整体替换不深合并）；
    /// 损坏/IO 异常 → 原文件改名 .bak 后返回默认。
    /// </summary>
    public void Load()
    {
        if (File.Exists(DataFile))
        {
            try
            {
                string text = File.ReadAllText(DataFile, Encoding.UTF8);
                if (JsonNode.Parse(text) is not JsonObject parsed)
                {
                    BackupCorrupt();
                    Data = CreateDefaults();
                    return;
                }
                // 浅合并：默认在下、实数据在上（对应 merged.update(data)），键序原样保留。
                // DeepClone 解绑父节点；底层表示（整数/浮点/字符串）原样保留，往返字节不变。
                var merged = CreateDefaults();
                foreach (var kv in parsed)
                    merged[kv.Key] = kv.Value?.DeepClone();
                Data = merged;
                MigrateFocus();
                return;
            }
            catch (JsonException) { BackupCorrupt(); Data = CreateDefaults(); return; }
            catch (IOException) { BackupCorrupt(); Data = CreateDefaults(); return; }
            catch (UnauthorizedAccessException) { BackupCorrupt(); Data = CreateDefaults(); return; }
        }
        Data = CreateDefaults();
    }

    private void BackupCorrupt()
    {
        try { File.Move(DataFile, DataFile + ".bak", overwrite: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>原子写入 data.json（.tmp + 覆盖移动），格式与 json.dump(ensure_ascii=False, indent=2) 字节一致。</summary>
    public void Save()
    {
        try
        {
            string json = PythonJson.ToJson(Data);
            string tmp = DataFile + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, DataFile, overwrite: true);
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        Changed?.Invoke();
    }

    /// <summary>静默保存（窗口位置等高频低价值写入）：写盘但不触发 Changed，避免拖动时整面板重建。</summary>
    public void SaveQuiet()
    {
        try
        {
            string json = PythonJson.ToJson(Data);
            string tmp = DataFile + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, DataFile, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------ 一次性任务（daily）

    /// <summary>
    /// 保证 data["daily"][today] 存在；跨日把最近一天整份归档到 archive（懒创建），不 save。
    /// </summary>
    public string EnsureToday()
    {
        string t = TodayStr();
        var daily = GetOrCreateObj(Data, "daily");
        if (daily.ContainsKey(t))
            return t;

        if (daily.Count > 0)
        {
            // ISO 日期字典序 = 时间序（对应 Python max(daily.keys())，不依赖 Linq）
            string lastDay = "";
            foreach (var kv in daily)
                if (string.Compare(kv.Key, lastDay, StringComparison.Ordinal) > 0)
                    lastDay = kv.Key;
            if (string.Compare(lastDay, t, StringComparison.Ordinal) < 0)
                GetOrCreateObj(Data, "archive")[lastDay] = daily[lastDay]?.DeepClone();
        }
        daily[t] = new JsonArray();
        return t;
    }

    public void AddTask(string text, bool done = false)
    {
        string day = EnsureToday();
        var daily = GetOrCreateObj(Data, "daily");
        ((JsonArray?)daily[day])?.Add(new JsonObject { ["text"] = text, ["done"] = done, ["plan"] = GetActivePlan() });
        Save();
    }

    public bool SetTaskDone(int index, bool done)
    {
        string day = TodayStr();
        if (GetObj(Data, "daily")?[day] is JsonArray arr && index >= 0 && index < arr.Count && arr[index] is JsonObject t)
        {
            t["done"] = done;
            Save();
            return true;
        }
        return false;
    }

    public bool DeleteTask(int index)
    {
        string day = TodayStr();
        if (GetObj(Data, "daily")?[day] is JsonArray arr && index >= 0 && index < arr.Count)
        {
            arr.RemoveAt(index);
            Save();
            return true;
        }
        return false;
    }

    public bool EditTask(int index, string text)
    {
        string day = TodayStr();
        if (GetObj(Data, "daily")?[day] is JsonArray arr && index >= 0 && index < arr.Count && arr[index] is JsonObject t)
        {
            t["text"] = text;
            Save();
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------ 固定任务（tasks）

    public static int ParseDays(object? value)
    {
        int days;
        switch (value)
        {
            case null:
                return 1;
            case JsonNode node when node is JsonValue v && v.TryGetValue<long>(out var l):
                days = (int)l;
                break;
            default:
                try { days = Convert.ToInt32(value, CultureInfo.InvariantCulture); }
                catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException) { return 1; }
                break;
        }
        return days >= 1 ? days : 1;
    }

    public string AddFixed(string text, string desc = "", object? targetDays = null)
    {
        string day = TodayStr();
        var task = new JsonObject
        {
            ["id"] = "t" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["text"] = text,
            ["plan"] = GetActivePlan(),
            ["desc"] = (desc ?? "").Trim(),
            ["target_days"] = (long)ParseDays(targetDays),
            ["progress"] = 0L,
            ["owed"] = 0L,
            ["last_done_date"] = day,
            ["done"] = false,
        };
        ((JsonArray?)Data["tasks"] ?? (JsonArray)(Data["tasks"] = new JsonArray())).Add(task);
        Save();
        return (string)task["id"]!;
    }

    public JsonObject? FindFixed(string id)
    {
        if (Data["tasks"] is JsonArray tasks)
        {
            foreach (var n in tasks)
                if (n is JsonObject t && GetString(t["id"]) == id)
                    return t;
        }
        return null;
    }

    /// <summary>
    /// 打卡 / 补卡 / 撤销打卡。三种动作相互独立：
    /// ・欠卡 &gt; 0 → 补卡：抵消 1 天欠卡、进度 +1，但【不打今天的卡】（last_done_date 不动）——
    ///   补的是过去的漏打，不占用今天的打卡额度，按钮回到「打卡」、今天仍可正常打卡一次。
    /// ・欠卡 = 0 → 打卡：进度 +1、last_done_date=今天；进度达 target_days 自动完成。
    /// ・已打卡（按钮显示「✅已打卡」）→ 再点撤销：progress-1、last_done_date 回退到昨天
    ///   （撤销的当天由次日 RolloverFixed 补记欠卡）、进度跌破 target 则解除完成。
    /// 当天是否打卡完成只存内存（_todayPunch），绝不落盘，保持 data.json 字节契约。
    /// </summary>
    public bool PunchFixed(string id)
    {
        var task = FindFixed(id);
        if (task is null)
            return false;
        string day = TodayStr();
        bool punchedToday = FixedPunchedTodayTask(task);
        bool infoToday = _todayPunch.TryGetValue(id, out var punchDay) && punchDay == day;

        if (punchedToday && (infoToday || !GetBool(task["done"])))
        {
            // 撤销今天的打卡：有当天标记（当天打卡完成），或本来就是未完成的任务（跨重启也可靠数据推导）
            task["progress"] = Math.Max(0L, GetInt(task["progress"]) - 1);
            _todayPunch.Remove(id);
            task["last_done_date"] = Today().AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (GetInt(task["progress"]) < GetInt(task["target_days"], 1))
                task["done"] = false;   // 撤销后进度不足 → 解除完成
            Save();
            return true;
        }
        if (GetBool(task["done"]))
            return false;   // 完成态（且非当天打卡完成）→ 拒绝

        if (GetInt(task["owed"]) > 0)
        {
            // 补卡：抵消 1 天欠卡、进度 +1；不打今天的卡，今天仍可正常打卡一次
            task["owed"] = Math.Max(0L, GetInt(task["owed"]) - 1);
            task["progress"] = GetInt(task["progress"]) + 1;
            if (GetInt(task["progress"]) >= GetInt(task["target_days"], 1))
            {
                task["done"] = true;
                task["owed"] = 0L;
            }
            Save();
            return true;
        }

        // 打卡：进度 +1、last_done_date=今天；进度达 target_days 自动完成
        task["progress"] = GetInt(task["progress"]) + 1;
        task["last_done_date"] = day;
        bool completed = GetInt(task["progress"]) >= GetInt(task["target_days"], 1);
        if (completed)
        {
            task["done"] = true;
            task["owed"] = 0L;
        }
        _todayPunch[id] = day;
        Save();
        return true;
    }

    /// <summary>
    /// 跨日结算欠卡：跳过漏打的天，累加 owed（封顶 target-progress）；结算点推进到昨天，
    /// 保证同一天重复调用不重复累加。冻结期间跳过。仅当有变化才 save。
    /// </summary>
    public void RolloverFixed()
    {
        if (GetBool(Data["frozen"]))
            return;
        var today = Today();
        string dayS = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string yesterday = today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        bool changed = false;

        if (Data["tasks"] is JsonArray tasks)
        {
            foreach (var n in tasks)
            {
                if (n is not JsonObject task)
                    continue;
                if (GetBool(task["done"]))
                    continue;
                string last = GetString(task["last_done_date"]);
                if (string.IsNullOrEmpty(last))
                    continue;
                if (last == dayS)
                    continue;   // 今天已打卡：不结算、不动结算点

                int gap;
                if (DateTime.TryParseExact(last, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var lastDate))
                    gap = (today - lastDate).Days;
                else
                    gap = 0;

                long missed = Math.Max(0L, gap - 1L);   // 昨天打过不算欠
                if (missed > 0)
                {
                    long cap = Math.Max(0L, GetInt(task["target_days"], 1) - GetInt(task["progress"]));
                    long newOwed = Math.Min(GetInt(task["owed"]) + missed, cap);
                    if (newOwed != GetInt(task["owed"]))
                    {
                        task["owed"] = newOwed;
                        changed = true;
                    }
                }
                if (last != yesterday)
                {
                    task["last_done_date"] = yesterday;
                    changed = true;
                }
            }
        }
        if (changed)
            Save();
    }

    public bool IsFixedCompleted(string id)
    {
        var t = FindFixed(id);
        return t is not null && IsFixedCompletedTask(t);
    }

    /// <summary>固定任务视为已完成：done 标记，或进度条已满（对应 fixed_completed）。</summary>
    public static bool IsFixedCompletedTask(JsonObject task)
    {
        if (GetBool(task["done"]))
            return true;
        int progress = (int)GetInt(task["progress"]);
        int target = Math.Max(1, (int)GetInt(task["target_days"], 1));
        return progress >= target;
    }

    /// <summary>固定任务「今日是否已打卡」：完成=True；或今天打过且 progress&gt;0（新建当天不算）。</summary>
    public static bool FixedPunchedTodayTask(JsonObject task)
    {
        if (IsFixedCompletedTask(task))
            return true;
        return GetString(task["last_done_date"]) == TodayStr() && GetInt(task["progress"]) > 0;
    }

    public bool EditFixed(string id, string text, string desc, object? targetDays)
    {
        var t = FindFixed(id);
        if (t is null) return false;
        string oldText = GetString(t["text"]);
        string newText = (text ?? "").Trim();
        t["text"] = newText;
        t["desc"] = (desc ?? "").Trim();
        t["target_days"] = (long)ParseDays(targetDays);
        // 固定任务改名 → focus_plan 里的专注时长跟随新名（同名求和防碰撞；学习数据不丢）
        if (oldText.Length > 0 && newText.Length > 0 && oldText != newText)
            MigrateFocusPlanName(oldText, newText);
        Save();
        return true;
    }

    public bool DeleteFixed(string id)
    {
        if (Data["tasks"] is JsonArray tasks)
        {
            for (int i = 0; i < tasks.Count; i++)
            {
                if (tasks[i] is JsonObject t && GetString(t["id"]) == id)
                {
                    tasks.RemoveAt(i);
                    Save();
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>冻结/解冻：冻结跳过欠卡积累并禁用打卡；解冻把未完成任务 last_done_date 置今天（冻结期不产生欠卡）。</summary>
    public void SetFrozen(bool on)
    {
        if (GetBool(Data["frozen"]) == on)
            return;
        Data["frozen"] = on;
        if (on)
        {
            Data["frozen_since"] = TodayStr();
        }
        else
        {
            string today = TodayStr();
            if (Data["tasks"] is JsonArray tasks)
                foreach (var n in tasks)
                    if (n is JsonObject t && !GetBool(t["done"]))
                        t["last_done_date"] = today;
            Data["frozen_since"] = "";
        }
        Save();
    }

    /// <summary>清理已完成：一次性任务去掉 done，固定任务去掉「已完成」（进度满或 done），然后保存。
    /// 传 plan 时只清理该计划内的已完成项（计划归类后主区按计划过滤，清理也应对齐）。</summary>
    public void ClearDone(string? plan = null)
    {
        string day = TodayStr();
        var daily = GetObj(Data, "daily");
        if (daily is not null && daily[day] is JsonArray arr)
        {
            var keep = new JsonArray();
            foreach (var n in arr)
                if (n is JsonObject t && (!GetBool(t["done"]) || (plan is not null && EffectivePlan(t) != plan)))
                    keep.Add(n?.DeepClone());
            daily[day] = keep;
        }
        if (Data["tasks"] is JsonArray tasks)
        {
            var keep = new JsonArray();
            foreach (var n in tasks)
                if (n is JsonObject t && (!IsFixedCompletedTask(t) || (plan is not null && EffectivePlan(t) != plan)))
                    keep.Add(n?.DeepClone());
            Data["tasks"] = keep;
        }
        Save();
    }

    // ------------------------------------------------------------ 计划（任务归类，Notion/Codex 式工作区）

    public const string DefaultPlanName = "考研计划";

    /// <summary>
    /// 计划列表（有序）。data.json 里没有 plans 键时返回默认单计划「考研计划」，
    /// 但不写盘（镜像 archive 惰性创建，保证 Load→Save 字节往返不受影响）。
    /// </summary>
    public List<string> GetPlans()
    {
        if (Data["plans"] is JsonArray arr && arr.Count > 0)
        {
            var list = new List<string>();
            foreach (var n in arr)
                if (n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0)
                    list.Add(s);
            if (list.Count > 0)
                return list;
        }
        return new List<string> { DefaultPlanName };
    }

    /// <summary>长期计划（固定任务）名称列表，按 tasks 顺序。专注计时按此列出可专注目标。</summary>
    public List<string> GetFixedTaskNames()
    {
        var list = new List<string>();
        if (Data["tasks"] is JsonArray tasks)
            foreach (var n in tasks)
                if (n is JsonObject t)
                {
                    string s = GetString(t["text"]);
                    if (s.Length > 0) list.Add(s);
                }
        return list;
    }

    /// <summary>默认计划 = 计划列表第一项；旧数据里没带 plan 字段的任务都归它。</summary>
    public string DefaultPlan()
    {
        var plans = GetPlans();
        return plans.Count > 0 ? plans[0] : DefaultPlanName;
    }

    /// <summary>当前选中的计划（data.json 无 active_plan 时取默认计划）。</summary>
    public string GetActivePlan()
    {
        string a = GetString(Data["active_plan"]);
        return a.Length > 0 ? a : DefaultPlan();
    }

    /// <summary>任务所属计划：显式 plan 字段优先；否则归默认计划（plans[0]）。</summary>
    public string EffectivePlan(JsonObject task)
    {
        string p = GetString(task["plan"]);
        return p.Length > 0 ? p : DefaultPlan();
    }

    public void SetActivePlan(string name)
    {
        if (!GetPlans().Contains(name))
            return;
        Data["active_plan"] = name;
        Save();
    }

    /// <summary>新建计划；名称非空且不重复。首次建时把默认计划一起落盘。</summary>
    public bool AddPlan(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0)
            return false;
        if (GetPlans().Contains(name))
            return false;
        var arr = Data["plans"] as JsonArray;
        if (arr is null)
            arr = (JsonArray)(Data["plans"] = new JsonArray());
        if (arr.Count == 0)
            arr.Add(DefaultPlanName);   // 默认计划一起持久化，untagged 旧任务才有归属入口
        arr.Add(name);
        Save();
        return true;
    }

    /// <summary>重命名计划：更新 plans 数组、显式 plan 字段、active_plan。untagged 任务自动跟随 plans[0]。</summary>
    public void RenamePlan(string oldName, string newName)
    {
        newName = (newName ?? "").Trim();
        if (newName.Length == 0 || newName == oldName)
            return;
        var plans = GetPlans();
        if (!plans.Contains(oldName) || plans.Contains(newName))
            return;

        // 旧数据可能没有 plans 键 → 先从内存默认列表落盘，再改名
        var arr = Data["plans"] as JsonArray;
        if (arr is null)
        {
            arr = (JsonArray)(Data["plans"] = new JsonArray());
            foreach (string p in plans) arr.Add(p);
        }

        for (int i = 0; i < arr.Count; i++)
            if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s) && s == oldName)
                arr[i] = newName;

        if (Data["tasks"] is JsonArray tasks)
            foreach (var n in tasks)
                if (n is JsonObject t && GetString(t["plan"]) == oldName)
                    t["plan"] = newName;

        if (Data["daily"] is JsonObject daily)
            foreach (var kv in daily)
                if (kv.Value is JsonArray dayArr)
                    foreach (var n in dayArr)
                        if (n is JsonObject t && GetString(t["plan"]) == oldName)
                            t["plan"] = newName;

        if (GetString(Data["active_plan"]) == oldName)
            Data["active_plan"] = newName;

        // focus_plan（按计划专注时长）同步改名；newName 若撞「已删计划残留键」则求和防丢。
        // 旧数据无 focus_plan 键 → 短路，零副作用（Load→Save 字节不动）。
        MigrateFocusPlanName(oldName, newName);

        Save();
    }

    /// <summary>focus_plan 按名称迁移（改名）：[date][old]→new，同名求和防碰撞。旧数据无键 → 短路。</summary>
    private void MigrateFocusPlanName(string oldName, string newName)
    {
        if (Data["focus_plan"] is not JsonObject fp) return;
        foreach (var kv in fp)
        {
            if (kv.Value is not JsonObject dayObj) continue;
            if (!dayObj.ContainsKey(oldName)) continue;
            double oldVal = GetDouble(dayObj[oldName]);
            dayObj[newName] = dayObj.ContainsKey(newName)
                ? Math.Round(oldVal + GetDouble(dayObj[newName]), 3)
                : oldVal;
            dayObj.Remove(oldName);
        }
    }

    /// <summary>删除计划（连同其任务）；至少保留一个。删除默认计划会一并清掉 untagged 任务。</summary>
    public bool DeletePlan(string name)
    {
        var plans = GetPlans();
        if (plans.Count <= 1 || !plans.Contains(name))
            return false;

        string defaultPlan = plans[0];   // 删除前的默认计划（untagged 任务归属）

        bool BelongsTo(JsonObject t)
        {
            string p = GetString(t["plan"]);
            return p.Length > 0 ? p == name : defaultPlan == name;
        }

        if (Data["tasks"] is JsonArray tasks)
            for (int i = tasks.Count - 1; i >= 0; i--)
                if (tasks[i] is JsonObject t && BelongsTo(t))
                    tasks.RemoveAt(i);

        if (Data["daily"] is JsonObject daily)
            foreach (var kv in daily)
                if (kv.Value is JsonArray dayArr)
                    for (int i = dayArr.Count - 1; i >= 0; i--)
                        if (dayArr[i] is JsonObject t && BelongsTo(t))
                            dayArr.RemoveAt(i);

        if (Data["plans"] is JsonArray arr)
            for (int i = arr.Count - 1; i >= 0; i--)
                if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s) && s == name)
                    arr.RemoveAt(i);

        if (GetString(Data["active_plan"]) == name)
            Data["active_plan"] = DefaultPlan();   // 删除后指向新 plans[0]

        Save();
        return true;
    }

    // ------------------------------------------------------------ 专注计时

    /// <summary>
    /// 专注秒数落盘：总时长写 focus_history[today][hour]（小时级，3 位小数）；若 plan 非空，
    /// 另写 focus_plan[today][plan]（天级按计划拆分）。focus_plan 是惰性键——无 plan 绝不创建。
    /// </summary>
    public void AddFocusSeconds(int hour, int seconds, string? plan = null)
    {
        var hist = GetOrCreateObj(Data, "focus_history");
        var dayHist = GetOrCreateObj(hist, TodayStr());
        string key = hour.ToString(CultureInfo.InvariantCulture);
        double prev = GetDouble(dayHist[key]);
        dayHist[key] = Math.Round(prev + seconds / 60.0, 3);

        if (!string.IsNullOrEmpty(plan))
        {
            var fp = GetOrCreateObj(Data, "focus_plan");
            var fpDay = GetOrCreateObj(fp, TodayStr());
            double pprev = GetDouble(fpDay[plan]);
            fpDay[plan] = Math.Round(pprev + seconds / 60.0, 3);
        }
    }

    // ------------------------------------------------------------ 旧版 focus_sessions 迁移

    /// <summary>
    /// 把旧版 focus_sessions（整轮记录）迁移到 focus_history 的按小时分布；只迁移一次。
    /// </summary>
    public void MigrateFocus()
    {
        var sessions = Data["focus_sessions"] as JsonArray;
        var hist = GetOrCreateObj(Data, "focus_history");
        if (hist.Count > 0 || sessions is null || sessions.Count == 0)
            return;

        foreach (var sn in sessions)
        {
            if (sn is not JsonObject s)
                continue;
            string? day = GetString(s["date"]);
            if (string.IsNullOrEmpty(day))
                continue;
            string startStr = day + " " + GetString(s["start"]);
            string endStr = day + " " + GetString(s["end"]);
            if (!DateTime.TryParseExact(startStr, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
                continue;
            if (!DateTime.TryParseExact(endStr, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
                continue;
            double minutes = GetDouble(s["duration_min"]);
            if (minutes <= 0 || end <= start)
                continue;
            double total = (end - start).TotalMinutes;
            var dayHist = GetOrCreateObj(hist, day);
            var t = start.Date.AddHours(start.Hour).AddMinutes(start.Minute);   // 秒/毫秒归零（对应 replace(second=0, microsecond=0)）
            while (t < end)
            {
                var hourEnd = t.Date.AddHours(t.Hour + 1);
                var segEnd = end < hourEnd ? end : hourEnd;
                double overlap = (segEnd - t).TotalMinutes;
                string key = t.Hour.ToString(CultureInfo.InvariantCulture);
                dayHist[key] = Math.Round(GetDouble(dayHist[key]) + minutes * overlap / total, 3);
                t = hourEnd;
            }
        }
    }

    // ------------------------------------------------------------ 便捷读取

    internal static JsonObject GetOrCreateObj(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing)
            return existing;
        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    internal static JsonObject? GetObj(JsonObject parent, string key)
        => parent[key] as JsonObject;

    internal static string GetString(JsonNode? node)
        => node is JsonValue v && v.TryGetValue<string>(out var s) ? s ?? "" : "";

    internal static bool GetBool(JsonNode? node, bool def = false)
        => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def;

    internal static long GetInt(JsonNode? node, long def = 0)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<int>(out var i)) return i;   // 防御：代码新建的值可能是 Int32
        }
        return def;
    }

    internal static double GetDouble(JsonNode? node, double def = 0.0)
        => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : def;
}
