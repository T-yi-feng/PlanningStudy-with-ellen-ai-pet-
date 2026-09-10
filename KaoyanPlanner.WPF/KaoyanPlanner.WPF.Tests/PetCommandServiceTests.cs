using System.IO;
using System.Text.Json.Nodes;
using KaoyanPlanner.WPF.Services;
using Xunit;

namespace KaoyanPlanner.WPF.Tests;

/// <summary>
/// 桌宠本地指令测试：添加/划掉/删除/列出/打卡/冻结/专注控制。
/// 全程操作临时 data.json 副本；专注控制用假实现验证逻辑，不碰真实计时器。
/// </summary>
public class PetCommandServiceTests
{
    private static DataStore NewStore()
    {
        string d = Path.Combine(Path.GetTempPath(), "kp_pet_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        var store = new DataStore(Path.Combine(d, "data.json"));
        store.Load();
        return store;
    }

    private static PetCommandService NewService(DataStore store, FakeFocus? focus = null)
        => new(store, focus ?? new FakeFocus());

    private sealed class FakeFocus : IPetFocus
    {
        public bool Running { get; set; }
        public double ElapsedSeconds { get; set; }
        public void Start() => Running = true;
        public void Pause() => Running = false;
        public void Reset() { Running = false; ElapsedSeconds = 0; }
    }

    private static JsonArray TodayTasks(DataStore store)
    {
        var daily = store.Data["daily"] as JsonObject;
        return daily![DataStore.TodayStr()] as JsonArray ?? new JsonArray();
    }

    // DataStore 的内部静态助手不在测试程序集可见 → 本地镜像（与 DataStoreTests 同套路）
    private static string GetString(JsonNode? node) => node?.GetValue<string>() ?? "";
    private static bool GetBool(JsonNode? node, bool def = false) => node is null ? def : node.GetValue<bool>();
    private static long GetInt(JsonNode? node, long def = 0) => node is null ? def : node.GetValue<long>();

    // ------------------------------------------------------------ 添加 / 划掉 / 删除 / 列出

    [Fact]
    public void HandleCommand_AddTask_AppendsTodayAndSaves()
    {
        var store = NewStore();
        var svc = NewService(store);
        var (handled, reply) = svc.HandleCommand("添加 背单词");
        Assert.True(handled);
        Assert.Contains("背单词", reply);
        Assert.Single(TodayTasks(store));
    }

    [Fact]
    public void HandleCommand_AddLongTask_ParsesNameDescDays()
    {
        var store = NewStore();
        var svc = NewService(store);
        var (handled, reply) = svc.HandleCommand("添加长期任务 背单词 30天 简介：每天50个");
        Assert.True(handled);
        Assert.Contains("背单词", reply);
        var task = (store.Data["tasks"] as JsonArray)![0] as JsonObject;
        Assert.Equal(30, (long)task!["target_days"]!);
        Assert.Equal("每天50个", GetString(task["desc"]));
    }

    [Fact]
    public void HandleCommand_CompleteTask_StrikesThrough()
    {
        var store = NewStore();
        var svc = NewService(store);
        svc.HandleCommand("添加 背单词");
        var (handled, reply) = svc.HandleCommand("划掉 背单词");
        Assert.True(handled);
        Assert.Contains("已划掉", reply);
        var task = TodayTasks(store)[0] as JsonObject;
        Assert.True(GetBool(task!["done"]));
    }

    [Fact]
    public void HandleCommand_DeleteTask_Removes()
    {
        var store = NewStore();
        var svc = NewService(store);
        svc.HandleCommand("添加 背单词");
        var (handled, reply) = svc.HandleCommand("删除 背单词");
        Assert.True(handled);
        Assert.Contains("已删除", reply);
        Assert.Empty(TodayTasks(store));
    }

    [Fact]
    public void HandleCommand_ListTasks_ListsBothKinds()
    {
        var store = NewStore();
        var svc = NewService(store);
        svc.HandleCommand("添加 背单词");
        var (handled, reply) = svc.HandleCommand("列出计划");
        Assert.True(handled);
        Assert.Contains("背单词", reply);
        Assert.Contains("今日共 1 项", reply);
    }

    [Fact]
    public void HandleCommand_CompleteAll_MarksAllDone()
    {
        var store = NewStore();
        var svc = NewService(store);
        svc.HandleCommand("添加 背单词");
        svc.HandleCommand("添加 刷真题");
        var (handled, reply) = svc.HandleCommand("全部完成");
        Assert.True(handled);
        Assert.Contains("2 项", reply);
        foreach (var n in TodayTasks(store))
            Assert.True(GetBool((n as JsonObject)!["done"]));
    }

    // ------------------------------------------------------------ 固定任务：打卡 / 冻结 / 欠卡

    [Fact]
    public void HandleCommand_PunchTask_Punches()
    {
        var store = NewStore();
        var svc = NewService(store);
        svc.HandleCommand("添加长期任务 背单词 30天");
        // 建任务当天 progress=0 → 未打卡 → 首次打卡是「打卡」（欠卡才显示/回复「补卡」）
        var (handled, reply) = svc.HandleCommand("打卡 背单词");
        Assert.True(handled);
        Assert.Contains("打卡成功", reply);
        Assert.Contains("1/30", reply);
        var task = (store.Data["tasks"] as JsonArray)![0] as JsonObject;
        Assert.Equal(1L, GetInt(task!["progress"]));
        Assert.Equal(DataStore.TodayStr(), GetString(task["last_done_date"]));
    }

    [Fact]
    public void HandleCommand_Freeze_ThenDebtReport_ReportsFrozen()
    {
        var store = NewStore();
        var svc = NewService(store);
        svc.HandleCommand("添加长期任务 背单词 30天");
        var (handled, _) = svc.HandleCommand("冻结任务");
        Assert.True(handled);
        Assert.True(GetBool(store.Data["frozen"]));
        var (h2, reply) = svc.HandleCommand("欠卡");
        Assert.True(h2);
        Assert.Contains("冻结", reply);
    }

    [Fact]
    public void HandleCommand_Unfreeze_ResetsActiveLastDoneDate()
    {
        var store = NewStore();
        var svc = NewService(store);
        svc.HandleCommand("添加长期任务 背单词 30天");
        svc.HandleCommand("冻结任务");
        svc.HandleCommand("解冻任务");
        Assert.False(GetBool(store.Data["frozen"]));
        var task = (store.Data["tasks"] as JsonArray)![0] as JsonObject;
        Assert.Equal(DataStore.TodayStr(), GetString(task!["last_done_date"]));
    }

    [Fact]
    public void HandleCommand_Punch_WhileFrozen_Rejected()
    {
        var store = NewStore();
        var svc = NewService(store);
        svc.HandleCommand("添加长期任务 背单词 30天");
        svc.HandleCommand("冻结任务");
        var (handled, reply) = svc.HandleCommand("打卡 背单词");
        Assert.True(handled);
        Assert.Contains("冻结", reply);
    }

    // ------------------------------------------------------------ 专注控制

    [Fact]
    public void Focus_StartPauseResumeReset_Flow()
    {
        var store = NewStore();
        var focus = new FakeFocus();
        var svc = NewService(store, focus);

        var (h1, _) = svc.HandleCommand("开始专注");
        Assert.True(h1);
        Assert.True(focus.Running);

        // 已在进行中再开始 → 仍被处理并给出提示，不应报错
        var (h2, r2) = svc.HandleCommand("开始专注");
        Assert.True(h2);
        Assert.Contains("已经在专注", r2);

        focus.ElapsedSeconds = 12 * 60 + 30;   // 12.5 分钟
        var (h3, r3) = svc.HandleCommand("暂停专注");
        Assert.True(h3);
        Assert.False(focus.Running);
        Assert.Contains("12 分钟", r3);

        var (h4, _) = svc.HandleCommand("继续专注");
        Assert.True(h4);
        Assert.True(focus.Running);

        var (h5, r5) = svc.HandleCommand("结束专注");
        Assert.True(h5);
        Assert.False(focus.Running);
        Assert.Contains("12 分钟", r5);
    }

    [Fact]
    public void Focus_Pause_WhenNotRunning_Explains()
    {
        var store = NewStore();
        var focus = new FakeFocus();
        var svc = NewService(store, focus);
        var (handled, reply) = svc.HandleCommand("暂停专注");
        Assert.True(handled);
        Assert.Contains("没有在专注", reply);
    }

    // ------------------------------------------------------------ 兜底 / 帮助

    [Fact]
    public void HandleCommand_Help_ReturnsHelpText()
    {
        var store = NewStore();
        var svc = NewService(store);
        var (handled, reply) = svc.HandleCommand("帮助");
        Assert.True(handled);
        Assert.Contains("添加 背单词", reply);
    }

    [Fact]
    public void HandleCommand_Unknown_Unhandled()
    {
        var store = NewStore();
        var svc = NewService(store);
        var (handled, _) = svc.HandleCommand("今天天气怎么样");
        Assert.False(handled);
    }

    // ------------------------------------------------------------ 提醒（自然语言添加 / 列出）

    private static JsonArray Reminders(DataStore store)
        => store.Data["reminders"] as JsonArray ?? new JsonArray();

    [Fact]
    public void HandleCommand_AddReminder_ParsesAndSaves()
    {
        var store = NewStore();
        var svc = NewService(store);
        var (handled, reply) = svc.HandleCommand("提醒我 明天下午3点 喝水");
        Assert.True(handled);
        Assert.Contains("喝水", reply);
        Assert.Contains("明天", reply);

        var arr = Reminders(store);
        Assert.Single(arr);
        var r = (JsonObject)arr[0]!;
        Assert.Equal("喝水", GetString(r["label"]));
        Assert.Equal(15 * 60, ReminderService.TryParseHm(GetString(r["start"]), out int h, out int m) ? h * 60 + m : -1);
        Assert.Equal(DateTime.Today.AddDays(1).ToString("yyyy-MM-dd"), GetString(r["date"]));
        Assert.Equal(0L, GetInt(r["interval_min"]));
        Assert.Equal(0L, GetInt(r["priority"]));
        Assert.True(GetBool(r["enabled"], true));
    }

    [Fact]
    public void HandleCommand_AddReminder_FullForm_IntervalAndPriority()
    {
        var store = NewStore();
        var svc = NewService(store);
        var (handled, reply) = svc.HandleCommand("提醒我 9月20号 晚上8点到10点 背单词 每30分钟 重要");
        Assert.True(handled);
        Assert.Contains("背单词", reply);
        Assert.Contains("每 30 分钟", reply);

        var r = (JsonObject)Reminders(store)[0]!;
        Assert.Equal("2026-09-20", GetString(r["date"]));
        Assert.Equal("20:00", GetString(r["start"]));
        Assert.Equal("22:00", GetString(r["end"]));
        Assert.Equal(30L, GetInt(r["interval_min"]));
        Assert.Equal(1L, GetInt(r["priority"]));
    }

    [Fact]
    public void AddReminder_NoTime_DefaultsFromNow_OnlyOnce()
    {
        var store = NewStore();
        var svc = NewService(store);
        var now = new DateTime(2026, 9, 10, 10, 30, 0);
        var parsed = ReminderTextParser.Parse("提醒我 喝水", now)!;
        string reply = svc.AddReminder(parsed, now);
        Assert.Contains("喝水", reply);
        Assert.Contains("10:31", reply);   // 现在 +1 分钟

        var r = (JsonObject)Reminders(store)[0]!;
        Assert.Equal("10:31", GetString(r["start"]));
        Assert.Equal(0L, GetInt(r["interval_min"]));
    }

    [Fact]
    public void AddReminder_PastTimeToday_RollsToTomorrow_AndTells()
    {
        var store = NewStore();
        var svc = NewService(store);
        var now = new DateTime(2026, 9, 10, 10, 30, 0);
        var parsed = ReminderTextParser.Parse("提醒我 今天 9点 喝水", now)!;
        string reply = svc.AddReminder(parsed, now);
        Assert.Contains("改到明天", reply);

        var r = (JsonObject)Reminders(store)[0]!;
        Assert.Equal("2026-09-11", GetString(r["date"]));
    }

    [Fact]
    public void HandleCommand_ReminderOnly_AsksForContent()
    {
        var store = NewStore();
        var svc = NewService(store);
        var (handled, reply) = svc.HandleCommand("提醒");
        Assert.True(handled);
        Assert.Contains("想提醒你什么", reply);
        Assert.Empty(Reminders(store));
    }

    [Fact]
    public void HandleCommand_ListReminders_ShowsAll()
    {
        var store = NewStore();
        var svc = NewService(store);
        svc.HandleCommand("提醒我 明天下午3点 喝水");
        svc.HandleCommand("提醒我 9月20号 晚上8点 背单词 每30分钟 重要");

        var (handled, reply) = svc.HandleCommand("列出提醒");
        Assert.True(handled);
        Assert.Contains("2 条提醒", reply);
        Assert.Contains("喝水", reply);
        Assert.Contains("背单词", reply);
    }

    [Fact]
    public void HandleCommand_ListReminders_Empty_Explains()
    {
        var store = NewStore();
        var svc = NewService(store);
        var (handled, reply) = svc.HandleCommand("查看提醒");
        Assert.True(handled);
        Assert.Contains("还没有提醒", reply);
    }
}
