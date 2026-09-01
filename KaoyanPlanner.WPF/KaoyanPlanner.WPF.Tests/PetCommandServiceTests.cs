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
}
