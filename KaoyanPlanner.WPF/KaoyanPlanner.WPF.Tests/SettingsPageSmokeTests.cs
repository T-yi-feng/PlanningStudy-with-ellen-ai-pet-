using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using KaoyanPlanner.WPF.Services;
using KaoyanPlanner.WPF.Views.Settings;
using KaoyanPlanner.WPF.Views.Tabs;
using Xunit;

namespace KaoyanPlanner.WPF.Tests;

/// <summary>
/// 设置页各分栏的运行时冒烟：构建只校验 C#，样式 TargetType 不匹配 / 资源缺失这类
/// XamlParseException 只有真正加载 BAML 时才爆（曾因在 TextBlock 上挂 TargetType=Border 的
/// SectionHeaderStyle 崩过）。这里在 STA 线程 + 合并主题资源后逐个实例化并逐栏切换。
/// </summary>
public class SettingsPageSmokeTests
{
    private const string AssemblyPackPrefix = "pack://application:,,,/PetPlanner;component/";

    private static void OnSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current;
                if (app is null)
                {
                    app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    foreach (string theme in new[] { "Colors", "Typography", "Styles", "Animations" })
                        app.Resources.MergedDictionaries.Add(
                            new ResourceDictionary { Source = new Uri(AssemblyPackPrefix + "Theme/" + theme + ".xaml") });
                }
                body();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new Xunit.Sdk.XunitException("STA 冒烟异常：" + error.GetType().Name + " " + error.Message);
    }

    [Fact]
    public void All_Settings_Tabs_Load_Without_XamlError()
    {
        OnSta(() =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "kp_settings_smoke_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("KAOYAN_DATA_DIR", dir);
            try
            {
                var store = new DataStore(Path.Combine(dir, "data.json"));
                store.Load();
                // 逐个构造（MainWindow 传 null：构造期只存字段，不用它）
                _ = new SettingsGeneralTab(store, null!);
                _ = new SettingsChatTab(store);
                _ = new SettingsTtsTab(store);
                _ = new SettingsCaptionTab(store, null!);
                _ = new SettingsPetTab(store, null!);
                _ = new SettingsAboutTab();
                // 宿主页：构造默认进「通用」+ 逐栏切换（触发全部惰性构造与动画路径）
                var page = new SettingsPage(store, null!);
                foreach (string tag in new[] { "general", "pet", "chat", "tts", "caption", "about" })
                    page.ShowSection(tag);
            }
            finally
            {
                Environment.SetEnvironmentVariable("KAOYAN_DATA_DIR", null);
                try { Directory.Delete(dir, recursive: true); } catch { /* 清理失败忽略 */ }
            }
        });
    }

    /// <summary>
    /// 计时页 + 统计页运行期冒烟：种子 focus_history（总时长）+ 长期计划（固定任务）+ focus_plan（按任务名），
    /// 实例化 TimerTab / StatsTab（StatsTab 构造即 Refresh → 走 ChartNBrush FindResource + 各计划分布行）。
    /// 专抓：Chart1..6Brush 主题缺失、样式 TargetType 不匹配等只有加载 BAML 才爆的运行期错误。
    /// </summary>
    [Fact]
    public void TimerTab_And_StatsTab_Load_Without_XamlError()
    {
        OnSta(() =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "kp_tab_smoke_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var store = new DataStore(Path.Combine(dir, "data.json"));
                store.Load();

                // 种子：固定任务（专注目标）+ 今日总时长 + focus_plan 按任务名（含一个已删除任务 → 灰）
                string today = DataStore.TodayStr();
                store.Data["tasks"] = new JsonArray
                {
                    new JsonObject { ["id"] = "t1", ["text"] = "数学", ["plan"] = "考研计划", ["desc"] = "", ["target_days"] = 100L, ["progress"] = 0L, ["owed"] = 0L, ["last_done_date"] = today, ["done"] = false },
                    new JsonObject { ["id"] = "t2", ["text"] = "政治", ["plan"] = "考研计划", ["desc"] = "", ["target_days"] = 100L, ["progress"] = 0L, ["owed"] = 0L, ["last_done_date"] = today, ["done"] = false },
                };
                store.Data["focus_history"] = new JsonObject
                {
                    [today] = new JsonObject { ["9"] = 30.0, ["14"] = 45.0 },   // 今日 75 分钟
                };
                store.Data["focus_plan"] = new JsonObject
                {
                    [today] = new JsonObject { ["数学"] = 30.0, ["政治"] = 30.0, ["已删除任务"] = 10.0 },   // 数学→Chart1 政治→Chart2 已删→灰，未分类 5
                };

                var focus = new FocusTimerService(store, () => 0L);
                _ = new TimerTab(store, focus, () => { });
                var stats = new StatsTab(store, focus);   // 构造即 Refresh → 走 ChartNBrush + 各计划分布行
                stats.Refresh();                          // 再刷一次（日视图默认，未分类行 + 色点路径）
                // 切周视图：BuildWeekDistributionCard 堆叠条 + 图例 + 画廊 2 卡（Period_Click 走 Refresh 重建）
                ((RadioButton)stats.FindName("weekRb")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                stats.Refresh();
                // 切回日视图：画廊 4 卡 + 柱状图重建
                ((RadioButton)stats.FindName("dayRb")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                stats.Refresh();
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* 清理失败忽略 */ }
            }
        });
    }
}
