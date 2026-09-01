using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using KaoyanPlanner.WPF.Services;
using Xunit;

namespace KaoyanPlanner.WPF.Tests;

/// <summary>
/// 计划归类（任务种类）测试：plans 列表 / active_plan / EffectivePlan / 新建·重命名·删除·清理。
/// 全程操作临时 data.json；读操作（GetPlans 等）必须只读不落盘（字节往返契约）。
/// </summary>
public class PlanTests
{
    private static DataStore NewStore()
    {
        string d = Path.Combine(Path.GetTempPath(), "kp_plan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        var store = new DataStore(Path.Combine(d, "data.json"));
        store.Load();
        return store;
    }

    // DataStore 内部静态助手不在测试程序集可见 → 本地镜像（与 DataStoreTests 同套路）
    private static string GetString(JsonNode? node) => node?.GetValue<string>() ?? "";

    private static JsonArray TodayArr(DataStore store)
        => (JsonArray)((JsonObject)store.Data["daily"]!)[DataStore.TodayStr()]!;

    // ------------------------------------------------------------ 读 / 默认

    [Fact]
    public void GetPlans_Default_DoesNotPersist()
    {
        var store = NewStore();
        Assert.Equal(new[] { DataStore.DefaultPlanName }, store.GetPlans());
        Assert.Equal(DataStore.DefaultPlanName, store.GetActivePlan());
        Assert.False(store.Data.ContainsKey("plans"));   // 只读访问不落盘
        Assert.False(store.Data.ContainsKey("active_plan"));
    }

    [Fact]
    public void EffectivePlan_UntaggedTask_BelongsToDefault()
    {
        var store = NewStore();
        string day = store.EnsureToday();
        var daily = (JsonObject)store.Data["daily"]!;
        ((JsonArray)daily[day]!).Add(new JsonObject { ["text"] = "背单词", ["done"] = false });   // 无 plan 字段
        var t = (JsonObject)TodayArr(store)[0]!;
        Assert.Equal(DataStore.DefaultPlanName, store.EffectivePlan(t));
    }

    // ------------------------------------------------------------ 新建 / 选择

    [Fact]
    public void AddPlan_CreatesWithDefaultFirst()
    {
        var store = NewStore();
        Assert.True(store.AddPlan("健身计划"));
        Assert.Equal(new[] { "考研计划", "健身计划" }, store.GetPlans());
        Assert.Equal("考研计划", store.GetActivePlan());
    }

    [Fact]
    public void AddPlan_DuplicateOrEmpty_Rejected()
    {
        var store = NewStore();
        Assert.False(store.AddPlan("考研计划"));
        Assert.False(store.AddPlan("   "));
        Assert.Equal(new[] { "考研计划" }, store.GetPlans());
    }

    [Fact]
    public void SetActivePlan_Switches()
    {
        var store = NewStore();
        store.AddPlan("健身计划");
        store.SetActivePlan("健身计划");
        Assert.Equal("健身计划", store.GetActivePlan());
    }

    [Fact]
    public void AddTask_And_AddFixed_TagActivePlan()
    {
        var store = NewStore();
        store.AddPlan("健身计划");
        store.SetActivePlan("健身计划");
        store.AddTask("跑步");
        store.AddFixed("俯卧撑", "", 30);
        Assert.Equal("健身计划", GetString(((JsonObject)TodayArr(store)[0]!)["plan"]));
        Assert.Equal("健身计划", GetString(((JsonObject)((JsonArray)store.Data["tasks"]!)[0]!)["plan"]));
    }

    // ------------------------------------------------------------ 重命名

    [Fact]
    public void RenamePlan_MigratesTaggedTasksAndActive()
    {
        var store = NewStore();
        store.AddPlan("健身计划");
        store.SetActivePlan("健身计划");
        store.AddFixed("俯卧撑", "", 30);
        store.RenamePlan("健身计划", "运动计划");
        Assert.Equal(new[] { "考研计划", "运动计划" }, store.GetPlans());
        Assert.Equal("运动计划", store.GetActivePlan());
        Assert.Equal("运动计划", GetString(((JsonObject)((JsonArray)store.Data["tasks"]!)[0]!)["plan"]));
    }

    [Fact]
    public void RenamePlan_Default_UntaggedFollowsNewFirstPlan()
    {
        var store = NewStore();
        string day = store.EnsureToday();
        var daily = (JsonObject)store.Data["daily"]!;
        ((JsonArray)daily[day]!).Add(new JsonObject { ["text"] = "背单词", ["done"] = false });   // untagged
        var t = (JsonObject)TodayArr(store)[0]!;
        store.RenamePlan("考研计划", "学习计划");
        Assert.Equal("学习计划", store.EffectivePlan(t));   // untagged 跟随 plans[0] 改名
    }

    // ------------------------------------------------------------ 删除

    [Fact]
    public void DeletePlan_RemovesPlanAndTasks_AndSwitchesActive()
    {
        var store = NewStore();
        store.AddPlan("健身计划");
        store.SetActivePlan("健身计划");
        store.AddFixed("俯卧撑", "", 30);
        store.AddTask("跑步");
        Assert.True(store.DeletePlan("健身计划"));
        Assert.Equal(new[] { "考研计划" }, store.GetPlans());
        Assert.Empty((JsonArray)store.Data["tasks"]!);
        Assert.Empty(TodayArr(store));
        Assert.Equal("考研计划", store.GetActivePlan());
    }

    [Fact]
    public void DeletePlan_LastPlan_Refused()
    {
        var store = NewStore();
        Assert.False(store.DeletePlan("考研计划"));
        Assert.Equal(new[] { "考研计划" }, store.GetPlans());
    }

    // ------------------------------------------------------------ 清理（按计划）

    [Fact]
    public void ClearDone_ScopedToPlan()
    {
        var store = NewStore();
        store.AddTask("A");                       // 考研计划
        store.AddPlan("健身计划");
        store.SetActivePlan("健身计划");
        store.AddTask("B");                       // 健身计划
        var arr = TodayArr(store);
        ((JsonObject)arr[0]!)["done"] = true;
        ((JsonObject)arr[1]!)["done"] = true;

        store.ClearDone("健身计划");
        var after = TodayArr(store);   // ClearDone 重建数组，需重新取引用
        Assert.Single(after);
        Assert.Equal("A", GetString(((JsonObject)after[0]!)["text"]));   // 只清健身计划，保留考研计划
    }

    // ------------------------------------------------------------ 专注时长按计划（focus_plan 惰性键）

    private static double GetDouble(JsonNode? node)
        => node is JsonValue v && v.TryGetValue<double>(out double d) ? d : 0;

    private static double DayTotalFocus(DataStore store)
    {
        var hist = store.Data["focus_history"] as JsonObject;
        var day = hist?[DataStore.TodayStr()] as JsonObject;
        double t = 0;
        if (day is not null)
            foreach (var kv in day)
                t += GetDouble(kv.Value);
        return t;
    }

    private static double PlanDay(DataStore store, string plan)
    {
        var fp = store.Data["focus_plan"] as JsonObject;
        var day = fp?[DataStore.TodayStr()] as JsonObject;
        return GetDouble(day?[plan]);
    }

    [Fact]
    public void AddFocusSeconds_WithPlan_WritesTotalAndPlan()
    {
        var store = NewStore();
        store.AddPlan("健身计划");
        store.AddFocusSeconds(14, 120, "健身计划");   // 2 分钟 → 总时长 + focus_plan 双写

        Assert.Equal(2.0, DayTotalFocus(store), 3);
        Assert.True(store.Data.ContainsKey("focus_plan"));
        Assert.Equal(2.0, PlanDay(store, "健身计划"), 3);
    }

    [Fact]
    public void AddFocusSeconds_NoPlan_DoesNotCreateFocusPlan()
    {
        var store = NewStore();
        store.AddFocusSeconds(14, 120);        // 不指定计划
        store.AddFocusSeconds(14, 120, "");    // 空串同样

        Assert.Equal(4.0, DayTotalFocus(store), 3);
        Assert.False(store.Data.ContainsKey("focus_plan"));   // 惰性：无 plan 绝不落盘
    }

    [Fact]
    public void RenamePlan_MigratesFocusPlan()
    {
        var store = NewStore();
        store.AddPlan("健身计划");
        string today = DataStore.TodayStr();
        string yesterday = DataStore.Today().AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        store.Data["focus_plan"] = new JsonObject
        {
            [today] = new JsonObject { ["健身计划"] = 30.0, ["考研计划"] = 10.0 },
            [yesterday] = new JsonObject { ["健身计划"] = 20.0 },
        };

        store.RenamePlan("健身计划", "运动计划");

        var fp = (JsonObject)store.Data["focus_plan"]!;
        var td = (JsonObject)fp[today]!;
        Assert.Equal(30.0, GetDouble(td["运动计划"]), 3);
        Assert.Equal(10.0, GetDouble(td["考研计划"]), 3);   // 其它计划不动
        Assert.False(td.ContainsKey("健身计划"));
        var yd = (JsonObject)fp[yesterday]!;
        Assert.Equal(20.0, GetDouble(yd["运动计划"]), 3);
        Assert.False(yd.ContainsKey("健身计划"));
    }

    [Fact]
    public void RenamePlan_NoFocusPlanKey_NoOp()
    {
        var store = NewStore();
        store.AddPlan("健身计划");
        store.RenamePlan("健身计划", "运动计划");
        Assert.False(store.Data.ContainsKey("focus_plan"));   // 无键不创建、不抛
    }

    [Fact]
    public void DeletePlan_DoesNotTouchFocusPlan()
    {
        var store = NewStore();
        store.AddPlan("健身计划");
        store.Data["focus_plan"] = new JsonObject
        {
            [DataStore.TodayStr()] = new JsonObject { ["健身计划"] = 30.0 },
        };
        store.DeletePlan("健身计划");
        Assert.Equal(30.0, PlanDay(store, "健身计划"), 3);   // 学习数据不删
    }

    [Fact]
    public void RenamePlan_FocusPlanCollision_Sums()
    {
        var store = NewStore();
        store.AddPlan("A计划");
        store.AddPlan("B计划");
        store.DeletePlan("B计划");   // B 从 plans 移除，但 focus_plan 保留 B 残留数据
        store.Data["focus_plan"] = new JsonObject
        {
            [DataStore.TodayStr()] = new JsonObject { ["A计划"] = 10.0, ["B计划"] = 20.0 },
        };
        store.RenamePlan("A计划", "B计划");   // newName 不在 plans → 允许，同名碰撞求和

        var td = (JsonObject)((JsonObject)store.Data["focus_plan"]!)[DataStore.TodayStr()]!;
        Assert.Equal(30.0, GetDouble(td["B计划"]), 3);
        Assert.False(td.ContainsKey("A计划"));
    }

    // ------------------------------------------------------------ 专注目标 = 长期计划（固定任务）

    [Fact]
    public void GetFixedTaskNames_ListsTaskTexts()
    {
        var store = NewStore();
        Assert.Empty(store.GetFixedTaskNames());
        store.AddFixed("数学");
        store.AddFixed("政治");
        Assert.Equal(new[] { "数学", "政治" }, store.GetFixedTaskNames());
    }

    [Fact]
    public void EditFixed_Rename_MigratesFocusPlan()
    {
        var store = NewStore();
        string id = store.AddFixed("数学");
        store.Data["focus_plan"] = new JsonObject
        {
            [DataStore.TodayStr()] = new JsonObject { ["数学"] = 30.0, ["政治"] = 10.0 },
        };

        store.EditFixed(id, "高数", "", null);   // 固定任务改名 → focus_plan 跟随新名

        var td = (JsonObject)((JsonObject)store.Data["focus_plan"]!)[DataStore.TodayStr()]!;
        Assert.Equal(30.0, GetDouble(td["高数"]), 3);
        Assert.Equal(10.0, GetDouble(td["政治"]), 3);   // 其它任务不动
        Assert.False(td.ContainsKey("数学"));
    }

    [Fact]
    public void EditFixed_CollisionWithOtherName_Sums()
    {
        var store = NewStore();
        string a = store.AddFixed("A");
        string b = store.AddFixed("B");
        store.Data["focus_plan"] = new JsonObject
        {
            [DataStore.TodayStr()] = new JsonObject { ["A"] = 10.0, ["B"] = 20.0 },
        };

        store.EditFixed(a, "B", "", null);   // A 改名为 B → 撞残留键求和

        var td = (JsonObject)((JsonObject)store.Data["focus_plan"]!)[DataStore.TodayStr()]!;
        Assert.Equal(30.0, GetDouble(td["B"]), 3);
        Assert.False(td.ContainsKey("A"));
    }

    [Fact]
    public void EditFixed_SameText_NoMigration()
    {
        var store = NewStore();
        string id = store.AddFixed("数学");
        store.Data["focus_plan"] = new JsonObject
        {
            [DataStore.TodayStr()] = new JsonObject { ["数学"] = 30.0 },
        };

        store.EditFixed(id, "数学", "新描述", null);   // 只改描述，text 不变 → 不迁移

        Assert.Equal(30.0, PlanDay(store, "数学"), 3);
    }

    [Fact]
    public void DeleteFixed_KeepsFocusPlan()
    {
        var store = NewStore();
        string id = store.AddFixed("数学");
        store.Data["focus_plan"] = new JsonObject
        {
            [DataStore.TodayStr()] = new JsonObject { ["数学"] = 30.0 },
        };

        store.DeleteFixed(id);   // 删除任务不删专注数据（历史仍在，统计里灰显）

        Assert.Empty(store.GetFixedTaskNames());
        Assert.Equal(30.0, PlanDay(store, "数学"), 3);
    }
}
