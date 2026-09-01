using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using KaoyanPlanner.WPF.Services;
using Xunit;

namespace KaoyanPlanner.WPF.Tests;

/// <summary>
/// DataStore / PythonJson 测试。核心验收线：真实 data.json 加载→保存必须字节级一致。
/// 所有测试都在临时目录操作副本，绝不触碰仓库里的真实 data.json。
/// </summary>
public class DataStoreTests
{
    // ------------------------------------------------------------ 工具

    /// <summary>向上找仓库根（含 data.json 的目录），保证测试不依赖 CWD。</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "data.json")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根目录（data.json）");
    }

    private static string NewTempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "kp_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static string WriteTemp(string json)
    {
        string d = NewTempDir();
        string path = Path.Combine(d, "data.json");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
    }

    // ------------------------------------------------------------ 浮点格式（Python repr）

    [Theory]
    [InlineData(24.0, "24.0")]
    [InlineData(0.0, "0.0")]
    [InlineData(61.2, "61.2")]
    [InlineData(0.034, "0.034")]
    [InlineData(28.067, "28.067")]
    [InlineData(61.183, "61.183")]
    [InlineData(1.53, "1.53")]
    [InlineData(16.15, "16.15")]
    [InlineData(1e20, "1e+20")]
    [InlineData(1e-05, "1e-05")]
    public void PythonFloat_MatchesPythonRepr(double value, string expected)
    {
        Assert.Equal(expected, PythonJson.PythonFloat(value));
    }

    // ------------------------------------------------------------ 最高验收线：字节级往返

    [Fact]
    public void RoundTrip_RealData_BytesIdentical()
    {
        string repoData = Path.Combine(FindRepoRoot(), "data.json");
        string dir = NewTempDir();
        try
        {
            string copy = Path.Combine(dir, "data.json");
            File.Copy(repoData, copy);
            byte[] original = File.ReadAllBytes(copy);

            var store = new DataStore(copy);
            store.Load();
            store.Save();

            byte[] after = File.ReadAllBytes(copy);
            Assert.Equal(original, after);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void RoundTrip_PreservesArchive_WhenTodayExists()
    {
        // 今天已在 daily → EnsureToday 不改 archive；Load→Save 字节一致（含 archive 与嵌套配置）
        string dir = NewTempDir();
        try
        {
            string copy = Path.Combine(dir, "data.json");
            File.Copy(Path.Combine(FindRepoRoot(), "data.json"), copy);
            var store = new DataStore(copy);
            store.Load();
            string today = DataStore.TodayStr();
            var daily = store.Data["daily"] as JsonObject;
            if (daily is not null && daily.ContainsKey(today))
            {
                store.EnsureToday();
                store.Save();
                byte[] original = File.ReadAllBytes(copy);
                store.Save();
                Assert.Equal(original, File.ReadAllBytes(copy));
            }
        }
        finally { Cleanup(dir); }
    }

    // ------------------------------------------------------------ 未知键 / 缺省补齐

    [Fact]
    public void Load_PreservesUnknownKeysAndAddsMissingDefaults()
    {
        string json = "{\"title\":\"t\",\"daily\":{},\"custom_meta\":{\"a\":[1,2.5,\"x\"]}}";
        string path = WriteTemp(json);
        try
        {
            var store = new DataStore(path);
            store.Load();

            Assert.True(store.Data.ContainsKey("custom_meta"));
            Assert.True(store.Data.ContainsKey("tasks"));     // 默认补齐
            Assert.True(store.Data.ContainsKey("pet_chat"));  // 默认补齐

            store.Save();
            string text = File.ReadAllText(path);
            Assert.Contains("\"custom_meta\": {", text);
            Assert.Contains("2.5", text);
            Assert.Contains("\"title\": \"t\"", text);
        }
        finally { Cleanup(Path.GetDirectoryName(path)!); }
    }

    // ------------------------------------------------------------ EnsureToday 归档

    [Fact]
    public void EnsureToday_ArchivesLastDay_AndCreatesEmptyToday()
    {
        string yesterday = DataStore.Today().AddDays(-1).ToString("yyyy-MM-dd");
        string json = $"{{\"title\":\"t\",\"daily\":{{\"{yesterday}\":[{{\"text\":\"a\",\"done\":true}}]}},\"tasks\":[]}}";
        string path = WriteTemp(json);
        try
        {
            var store = new DataStore(path);
            store.Load();
            string today = DataStore.TodayStr();

            Assert.Equal(today, store.EnsureToday());
            var daily = store.Data["daily"] as JsonObject;
            Assert.NotNull(daily);
            Assert.True(daily!.ContainsKey(today));
            Assert.Empty((JsonArray)daily[today]!);

            var archive = store.Data["archive"] as JsonObject;
            Assert.NotNull(archive);
            Assert.True(archive!.ContainsKey(yesterday));
            Assert.Single((JsonArray)archive[yesterday]!);
        }
        finally { Cleanup(Path.GetDirectoryName(path)!); }
    }

    // ------------------------------------------------------------ 一次性任务

    [Fact]
    public void AddTask_ThenSetDone_ThenDelete_WriteThrough()
    {
        string dir = NewTempDir();
        try
        {
            string path = Path.Combine(dir, "data.json");
            File.WriteAllText(path, "{\"title\":\"t\",\"daily\":{},\"tasks\":[]}", new UTF8Encoding(false));
            var store = new DataStore(path);
            store.Load();

            store.AddTask("背单词");
            string today = DataStore.TodayStr();
            var daily = store.Data["daily"] as JsonObject;
            Assert.Single((JsonArray)daily![today]!);

            Assert.True(store.SetTaskDone(0, true));
            Assert.True((bool)(((JsonObject)((JsonArray)daily[today]!)[0]!)["done"])!);

            Assert.True(store.DeleteTask(0));
            Assert.Empty((JsonArray)daily[today]!);
        }
        finally { Cleanup(dir); }
    }

    // ------------------------------------------------------------ 固定任务打卡

    [Fact]
    public void PunchFixed_Backfill_ThenNormalPunch_SameDay()
    {
        string yesterday = DataStore.Today().AddDays(-1).ToString("yyyy-MM-dd");
        string json = $"{{\"title\":\"t\",\"daily\":{{}},\"tasks\":[{{\"id\":\"t1\",\"text\":\"x\",\"desc\":\"\",\"target_days\":100,\"progress\":0,\"owed\":1,\"last_done_date\":\"{yesterday}\",\"done\":false}}]}}";
        string path = WriteTemp(json);
        try
        {
            var store = new DataStore(path);
            store.Load();
            string today = DataStore.TodayStr();

            // 第一次点（欠卡）= 补卡：进度 +1、抵消 1 天欠卡，但不打今天的卡（last_done_date 不动）
            Assert.True(store.PunchFixed("t1"));
            var t = store.FindFixed("t1")!;
            Assert.Equal(1, GetInt(t, "progress"));
            Assert.Equal(0, GetInt(t, "owed"));
            Assert.Equal(yesterday, GetStr(t, "last_done_date"));   // 今天未被占用
            Assert.False(DataStore.FixedPunchedTodayTask(t));        // 按钮仍是「打卡」/「今日未打卡」

            // 再点 = 正常打卡：进度 +1、今天记为已打卡
            Assert.True(store.PunchFixed("t1"));
            t = store.FindFixed("t1")!;
            Assert.Equal(2, GetInt(t, "progress"));
            Assert.Equal(today, GetStr(t, "last_done_date"));
            Assert.True(DataStore.FixedPunchedTodayTask(t));

            // 再点 = 撤销今天的打卡：只撤今天那次，补卡的进度保留
            Assert.True(store.PunchFixed("t1"));
            t = store.FindFixed("t1")!;
            Assert.Equal(1, GetInt(t, "progress"));   // 补卡的 +1 仍在
            Assert.Equal(0, GetInt(t, "owed"));
            Assert.Equal(yesterday, GetStr(t, "last_done_date"));
            Assert.False(DataStore.FixedPunchedTodayTask(t));

            // 之前完成的任务拒绝打卡
            store.Data["tasks"]!.AsArray().Clear();
            store.Data["tasks"]!.AsArray().Add(new JsonObject
            {
                ["id"] = "t2",
                ["text"] = "y",
                ["target_days"] = 1,
                ["progress"] = 1,
                ["owed"] = 0,
                ["last_done_date"] = yesterday,
                ["done"] = true,
            });
            Assert.False(store.PunchFixed("t2"));
        }
        finally { Cleanup(Path.GetDirectoryName(path)!); }
    }

    [Fact]
    public void PunchFixed_CompletedToday_CanUndo()
    {
        string yesterday = DataStore.Today().AddDays(-1).ToString("yyyy-MM-dd");
        string json = $"{{\"title\":\"t\",\"daily\":{{}},\"tasks\":[{{\"id\":\"t1\",\"text\":\"x\",\"target_days\":3,\"progress\":2,\"owed\":0,\"last_done_date\":\"{yesterday}\",\"done\":false}}]}}";
        string path = WriteTemp(json);
        try
        {
            var store = new DataStore(path);
            store.Load();

            // 打卡达目标 → 完成
            Assert.True(store.PunchFixed("t1"));
            var t = store.FindFixed("t1")!;
            Assert.True(GetBool(t, "done"));
            Assert.Equal(3, GetInt(t, "progress"));
            Assert.True(store.IsFixedCompleted("t1"));

            // 当天刚打卡完成可撤销：解除完成、进度 -1、回未打卡
            Assert.True(store.PunchFixed("t1"));
            t = store.FindFixed("t1")!;
            Assert.False(GetBool(t, "done"));
            Assert.Equal(2, GetInt(t, "progress"));
            Assert.Equal(yesterday, GetStr(t, "last_done_date"));
            Assert.False(store.IsFixedCompleted("t1"));

            // 未打卡状态再点 → 再次打卡完成
            Assert.True(store.PunchFixed("t1"));
            t = store.FindFixed("t1")!;
            Assert.Equal(3, GetInt(t, "progress"));
            Assert.True(GetBool(t, "done"));
        }
        finally { Cleanup(Path.GetDirectoryName(path)!); }
    }

    [Fact]
    public void PunchFixed_UndoAfterReload_StillUndoesForActiveTask()
    {
        string yesterday = DataStore.Today().AddDays(-1).ToString("yyyy-MM-dd");
        string json = $"{{\"title\":\"t\",\"daily\":{{}},\"tasks\":[{{\"id\":\"t1\",\"text\":\"x\",\"target_days\":10,\"progress\":0,\"owed\":0,\"last_done_date\":\"{yesterday}\",\"done\":false}}]}}";
        string path = WriteTemp(json);
        try
        {
            var store = new DataStore(path);
            store.Load();
            Assert.True(store.PunchFixed("t1"));   // 打卡

            // 模拟重启：新实例加载同一文件（内存标记丢失）→ 未完成任务仍可靠数据撤销
            var store2 = new DataStore(path);
            store2.Load();
            Assert.True(store2.PunchFixed("t1"));
            var t = store2.FindFixed("t1")!;
            Assert.Equal(0, GetInt(t, "progress"));
            Assert.Equal(yesterday, GetStr(t, "last_done_date"));
            Assert.Equal(0, GetInt(t, "owed"));   // 普通打卡无欠卡需还原
        }
        finally { Cleanup(Path.GetDirectoryName(path)!); }
    }

    [Fact]
    public void PunchFixed_ReachesTarget_MarksDone()
    {
        string yesterday = DataStore.Today().AddDays(-1).ToString("yyyy-MM-dd");
        string json = $"{{\"title\":\"t\",\"daily\":{{}},\"tasks\":[{{\"id\":\"t1\",\"text\":\"x\",\"target_days\":3,\"progress\":2,\"owed\":0,\"last_done_date\":\"{yesterday}\",\"done\":false}}]}}";
        string path = WriteTemp(json);
        try
        {
            var store = new DataStore(path);
            store.Load();
            Assert.True(store.PunchFixed("t1"));
            var t = store.FindFixed("t1")!;
            Assert.Equal(3, GetInt(t, "progress"));
            Assert.True(GetBool(t, "done"));
            Assert.Equal(0, GetInt(t, "owed"));
            Assert.True(store.IsFixedCompleted("t1"));
            Assert.True(DataStore.FixedPunchedTodayTask(store.FindFixed("t1")!));
        }
        finally { Cleanup(Path.GetDirectoryName(path)!); }
    }

    [Fact]
    public void PunchFixed_BackfillReachesTarget_MarksDone_NotTodayPunch()
    {
        string yesterday = DataStore.Today().AddDays(-1).ToString("yyyy-MM-dd");
        string json = $"{{\"title\":\"t\",\"daily\":{{}},\"tasks\":[{{\"id\":\"t1\",\"text\":\"x\",\"target_days\":3,\"progress\":2,\"owed\":1,\"last_done_date\":\"{yesterday}\",\"done\":false}}]}}";
        string path = WriteTemp(json);
        try
        {
            var store = new DataStore(path);
            store.Load();

            Assert.True(store.PunchFixed("t1"));   // 补卡把最后一天补上 → 完成
            var t = store.FindFixed("t1")!;
            Assert.Equal(3, GetInt(t, "progress"));
            Assert.True(GetBool(t, "done"));
            Assert.Equal(0, GetInt(t, "owed"));
            Assert.True(store.IsFixedCompleted("t1"));
            Assert.Equal(yesterday, GetStr(t, "last_done_date"));   // 补卡不打今天的卡
        }
        finally { Cleanup(Path.GetDirectoryName(path)!); }
    }

    // ------------------------------------------------------------ 跨日结算

    [Fact]
    public void RolloverFixed_AccumulatesOwed_AdvancesSettlement_IsIdempotent()
    {
        string threeDaysAgo = DataStore.Today().AddDays(-3).ToString("yyyy-MM-dd");
        string yesterday = DataStore.Today().AddDays(-1).ToString("yyyy-MM-dd");
        string json = $"{{\"title\":\"t\",\"frozen\":false,\"daily\":{{}},\"tasks\":[{{\"id\":\"t1\",\"text\":\"x\",\"target_days\":10,\"progress\":2,\"owed\":0,\"last_done_date\":\"{threeDaysAgo}\",\"done\":false}}]}}";
        string path = WriteTemp(json);
        try
        {
            var store = new DataStore(path);
            store.Load();

            store.RolloverFixed();
            var t = store.FindFixed("t1")!;
            Assert.Equal(2, GetInt(t, "owed"));   // gap=3 → missed=2 → min(0+2, 10-2)=2
            Assert.Equal(yesterday, GetStr(t, "last_done_date"));

            // 幂等：第二次调用不落盘、不改数据
            string before = File.ReadAllText(path);
            store.RolloverFixed();
            Assert.Equal(before, File.ReadAllText(path));
        }
        finally { Cleanup(Path.GetDirectoryName(path)!); }
    }

    [Fact]
    public void RolloverFixed_SkipsWhenFrozen()
    {
        string threeDaysAgo = DataStore.Today().AddDays(-3).ToString("yyyy-MM-dd");
        string json = $"{{\"title\":\"t\",\"frozen\":true,\"daily\":{{}},\"tasks\":[{{\"id\":\"t1\",\"text\":\"x\",\"target_days\":10,\"progress\":2,\"owed\":0,\"last_done_date\":\"{threeDaysAgo}\",\"done\":false}}]}}";
        string path = WriteTemp(json);
        try
        {
            var store = new DataStore(path);
            store.Load();
            store.RolloverFixed();
            var t = store.FindFixed("t1")!;
            Assert.Equal(0, GetInt(t, "owed"));
            Assert.Equal(threeDaysAgo, GetStr(t, "last_done_date"));
        }
        finally { Cleanup(Path.GetDirectoryName(path)!); }
    }

    [Fact]
    public void RolloverFixed_SkipsDoneAndPunchedToday()
    {
        string today = DataStore.TodayStr();
        string yesterday = DataStore.Today().AddDays(-1).ToString("yyyy-MM-dd");
        string json = $"{{\"title\":\"t\",\"frozen\":false,\"daily\":{{}},\"tasks\":[" +
            $"{{\"id\":\"done\",\"text\":\"x\",\"target_days\":10,\"progress\":10,\"owed\":0,\"last_done_date\":\"{threeDaysAgo()}\",\"done\":true}}," +
            $"{{\"id\":\"today\",\"text\":\"y\",\"target_days\":10,\"progress\":3,\"owed\":0,\"last_done_date\":\"{today}\",\"done\":false}}," +
            $"{{\"id\":\"yest\",\"text\":\"z\",\"target_days\":10,\"progress\":3,\"owed\":0,\"last_done_date\":\"{yesterday}\",\"done\":false}}" +
            "]}";
        string path = WriteTemp(json);
        try
        {
            var store = new DataStore(path);
            store.Load();
            store.RolloverFixed();
            Assert.Equal(0, GetInt(store.FindFixed("done")!, "owed"));
            Assert.Equal(0, GetInt(store.FindFixed("today")!, "owed"));
            // 昨天打过 → missed=0，owed 不变
            Assert.Equal(0, GetInt(store.FindFixed("yest")!, "owed"));
        }
        finally { Cleanup(Path.GetDirectoryName(path)!); }

        static string threeDaysAgo() => DataStore.Today().AddDays(-3).ToString("yyyy-MM-dd");
    }

    [Fact]
    public void RolloverFixed_AfterBackfill_DoesNotRecountPaidDay()
    {
        string threeDaysAgo = DataStore.Today().AddDays(-3).ToString("yyyy-MM-dd");
        string yesterday = DataStore.Today().AddDays(-1).ToString("yyyy-MM-dd");
        string json = $"{{\"title\":\"t\",\"frozen\":false,\"daily\":{{}},\"tasks\":[{{\"id\":\"t1\",\"text\":\"x\",\"target_days\":10,\"progress\":5,\"owed\":0,\"last_done_date\":\"{threeDaysAgo}\",\"done\":false}}]}}";
        string path = WriteTemp(json);
        try
        {
            var store = new DataStore(path);
            store.Load();

            store.RolloverFixed();
            var t = store.FindFixed("t1")!;
            Assert.Equal(2, GetInt(t, "owed"));              // 漏了 2 天
            Assert.Equal(yesterday, GetStr(t, "last_done_date"));

            store.PunchFixed("t1");                          // 补卡：抵消 1 天，不打今天的卡
            t = store.FindFixed("t1")!;
            Assert.Equal(1, GetInt(t, "owed"));
            Assert.Equal(yesterday, GetStr(t, "last_done_date"));

            // 再次结算幂等：已补的那天不再重复算（欠卡保持 1，不回到 2）
            string before = File.ReadAllText(path);
            store.RolloverFixed();
            Assert.Equal(before, File.ReadAllText(path));
            Assert.Equal(1, GetInt(store.FindFixed("t1")!, "owed"));
        }
        finally { Cleanup(Path.GetDirectoryName(path)!); }
    }

    // ------------------------------------------------------------ focus_sessions 迁移

    [Fact]
    public void MigrateFocus_DistributesByHour()
    {
        string json = "{\"title\":\"t\",\"daily\":{},\"focus_sessions\":[{\"date\":\"2026-01-01\",\"start\":\"13:36\",\"end\":\"14:21\",\"duration_min\":45,\"period\":\"下午\"}],\"focus_history\":{}}";
        string path = WriteTemp(json);
        try
        {
            var store = new DataStore(path);
            store.Load();   // Load 内部调 MigrateFocus
            var hist = store.Data["focus_history"] as JsonObject;
            var day = hist!["2026-01-01"] as JsonObject;
            Assert.NotNull(day);
            Assert.Equal(24.0, (double)day!["13"]!, 3);
            Assert.Equal(21.0, (double)day["14"]!, 3);
        }
        finally { Cleanup(Path.GetDirectoryName(path)!); }
    }

    [Fact]
    public void MigrateFocus_DoesNotOverwriteExistingHistory()
    {
        string json = "{\"title\":\"t\",\"daily\":{},\"focus_sessions\":[{\"date\":\"2026-01-01\",\"start\":\"13:00\",\"end\":\"14:00\",\"duration_min\":60}],\"focus_history\":{\"2026-01-01\":{\"9\": 5.5}}}";
        string path = WriteTemp(json);
        try
        {
            var store = new DataStore(path);
            store.Load();
            var hist = store.Data["focus_history"] as JsonObject;
            var day = hist!["2026-01-01"] as JsonObject;
            Assert.True(day!.ContainsKey("9"));
            Assert.False(day.ContainsKey("13"));
        }
        finally { Cleanup(Path.GetDirectoryName(path)!); }
    }

    // ------------------------------------------------------------ 损坏恢复

    [Fact]
    public void Load_CorruptFile_BacksUpAndReturnsDefaults()
    {
        string dir = NewTempDir();
        try
        {
            string path = Path.Combine(dir, "data.json");
            File.WriteAllText(path, "{ this is not json", new UTF8Encoding(false));
            var store = new DataStore(path);
            store.Load();
            Assert.Equal("考研复习", store.Data["title"]!.GetValue<string>());
            Assert.True(File.Exists(path + ".bak"));
            Assert.Equal("{ this is not json", File.ReadAllText(path + ".bak"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void PetSkin_LazyWriteThenRemove_BytesIdentical()
    {
        // pet_skin 惰性：进 CreateDefaults 会写脏新装数据。只在用户实际切换时才落盘；
        // 切回默认时 Remove → 字节与原始完全一致（最高契约）。
        string dir = NewTempDir();
        try
        {
            string copy = Path.Combine(dir, "data.json");
            File.Copy(Path.Combine(FindRepoRoot(), "data.json"), copy);
            byte[] original = File.ReadAllBytes(copy);

            var store = new DataStore(copy);
            store.Load();
            Assert.Null(store.Data["pet_skin"]);   // 默认无键

            store.Data["pet_skin"] = "小蓝";
            store.Save();
            string written = File.ReadAllText(copy);
            Assert.Contains("\"pet_skin\": \"小蓝\"", written);

            store.Data.Remove("pet_skin");
            store.Save();
            Assert.Equal(original, File.ReadAllBytes(copy));   // 还原字节形状
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void FocusPlan_LazyWriteThenRemove_BytesIdentical()
    {
        // focus_plan（按计划专注时长）是惰性键：不进 CreateDefaults；只在有计划的专注时才落盘。
        // Remove 后字节与原始完全一致（最高契约）。
        string dir = NewTempDir();
        try
        {
            string copy = Path.Combine(dir, "data.json");
            File.Copy(Path.Combine(FindRepoRoot(), "data.json"), copy);
            byte[] original = File.ReadAllBytes(copy);

            var store = new DataStore(copy);
            store.Load();
            Assert.Null(store.Data["focus_plan"]);   // 默认无键

            store.Data["focus_plan"] = new JsonObject
            {
                [DataStore.TodayStr()] = new JsonObject { ["考研计划"] = 1.5 },
            };
            store.Save();
            Assert.Contains("\"focus_plan\"", File.ReadAllText(copy));

            store.Data.Remove("focus_plan");
            store.Save();
            Assert.Equal(original, File.ReadAllBytes(copy));   // 还原字节形状
        }
        finally { Cleanup(dir); }
    }

    // ------------------------------------------------------------ 2 点日界（WindowToday）

    [Theory]
    [InlineData(2026, 9, 1, 0, 30, 2026, 8, 31)]   // 00:30 → 前一天（凌晨归昨天）
    [InlineData(2026, 9, 1, 1, 59, 2026, 8, 31)]   // 01:59 → 前一天
    [InlineData(2026, 9, 1, 2, 0, 2026, 9, 1)]     // 02:00 整 → 当天
    [InlineData(2026, 9, 1, 2, 1, 2026, 9, 1)]     // 02:01 → 当天
    [InlineData(2026, 9, 1, 12, 0, 2026, 9, 1)]    // 中午 → 当天
    [InlineData(2026, 9, 1, 23, 59, 2026, 9, 1)]   // 23:59 → 当天
    public void WindowToday_Boundary(int y, int mo, int d, int h, int mi, int ey, int emo, int ed)
        => Assert.Equal(new DateTime(ey, emo, ed),
            DataStore.WindowToday(new DateTime(y, mo, d, h, mi, 0)));

    [Fact]
    public void TodayStr_MatchesWindowToday()
        => Assert.Equal(DataStore.TodayStr(),
            DataStore.Today().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    // ------------------------------------------------------------ 辅助断言

    private static int GetInt(JsonObject t, string key)
    {
        var n = t[key];
        if (n is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return (int)l;
            if (v.TryGetValue<int>(out var i)) return i;
        }
        return 0;
    }

    private static string GetStr(JsonObject t, string key)
        => t[key]?.GetValue<string>() ?? "";

    private static bool GetBool(JsonObject t, string key)
        => t[key]?.GetValue<bool>() ?? false;
}
