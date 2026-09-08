using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KaoyanPlanner.WPF.Controls;
using KaoyanPlanner.WPF.Services;
using KaoyanPlanner.WPF.Views.Dialogs;

namespace KaoyanPlanner.WPF.Views.Tabs;

/// <summary>
/// 「今日计划」完整编辑面：横幅 + 两段列表（临时/固定）+ 进度 + 冻结/清理 + 输入行。
/// 镜像 widgets.py 的 PlanTab。所有变更走 DataStore（Save 触发 Changed → 本页 Refresh 重建）。
/// </summary>
public partial class PlanTab : UserControl
{
    private readonly DataStore _store;

    public PlanTab(DataStore store)
    {
        InitializeComponent();
        _store = store;

        freezeBtn.Click += FreezeBtn_Click;
        clearDoneBtn.Click += ClearDoneBtn_Click;
        addBtn.Click += AddBtn_Click;
        addFixedBtn.Click += AddFixedBtn_Click;
        inputBox.KeyDown += InputBox_KeyDown;
        inputBox.TextChanged += (_, _) =>
            inputHint.Visibility = string.IsNullOrEmpty(inputBox.Text) ? Visibility.Visible : Visibility.Collapsed;

        _store.Changed += Refresh;
        Refresh();
    }

    public void Refresh()
    {
        string plan = _store.GetActivePlan();
        string day = _store.EnsureToday();
        var daily = _store.Data["daily"] as JsonObject;
        var allOnce = daily is not null ? daily[day] as JsonArray : null;
        var allFixed = _store.Data["tasks"] as JsonArray;

        // 按当前计划过滤：临时任务保留原数组索引（SetTaskDone/DeleteTask 按索引操作）
        var once = new List<(JsonObject Task, int Index)>();
        if (allOnce is not null)
            for (int i = 0; i < allOnce.Count; i++)
                if (allOnce[i] is JsonObject t && _store.EffectivePlan(t) == plan)
                    once.Add((t, i));
        var fixedList = new List<JsonObject>();
        if (allFixed is not null)
            foreach (var n in allFixed)
                if (n is JsonObject t && _store.EffectivePlan(t) == plan)
                    fixedList.Add(t);

        taskStack.Children.Clear();
        taskStack.Children.Add(BuildPlanTitle(plan));

        bool any = false;
        if (once.Count > 0)
        {
            AddSection("今日临时任务", once.Count);
            foreach (var (t, i) in once)
                taskStack.Children.Add(new TaskItemControl(_store, t, "once", i));
            any = true;
        }
        if (fixedList.Count > 0)
        {
            AddSection("长期固定任务（每日打卡）", fixedList.Count);
            foreach (var t in fixedList)
                taskStack.Children.Add(new TaskItemControl(_store, t, "fixed"));
            any = true;
        }
        if (!any)
            taskStack.Children.Add(new TextBlock
            {
                Text = "这个计划还没有任务，用下方输入框添加，或点左侧「＋ 新建计划」开一个新计划",
                Style = (Style)FindResource("MutedText"),
                Margin = new Thickness(10, 14, 10, 0),
                TextWrapping = TextWrapping.Wrap,
            });

        listScroll.Visibility = Visibility.Visible;
        emptyCard.Visibility = Visibility.Collapsed;

        // 底部进度：已完成 = 一次性 done + 固定「完成」（进度条满或 done 标记）
        int done = once.Count(x => DataStore.GetBool(x.Task["done"]))
                 + fixedList.Count(DataStore.IsFixedCompletedTask);
        int total = once.Count + fixedList.Count;
        progressText.Text = $"已完成 {done}/{total}";
        progressBar.Maximum = Math.Max(total, 1);
        progressBar.Value = done;
        clearDoneBtn.IsEnabled = done > 0;

        bool frozen = DataStore.GetBool(_store.Data["frozen"]);
        freezeBtn.IsChecked = frozen;
        freezeBtn.Content = frozen ? "已冻结" : "冻结任务";

        UpdateBanner(frozen, fixedList);
    }

    private UIElement BuildPlanTitle(string plan)
    {
        var border = new Border
        {
            Margin = new Thickness(0, 0, 0, 4),
            Padding = new Thickness(0, 0, 0, 6),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        border.Child = new TextBlock
        {
            Text = $"🗂 {plan}",
            FontFamily = (FontFamily)FindResource("UIFont"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
        };
        return border;
    }

    private void AddSection(string title, int count)
    {
        var border = new Border
        {
            Style = (Style)FindResource("SectionHeaderStyle"),
            Margin = new Thickness(0, 8, 0, 4),
        };
        border.Child = new TextBlock
        {
            Text = $"{title}　{count}",
            FontFamily = (FontFamily)FindResource("UIFont"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Margin = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        taskStack.Children.Add(border);
    }

    private void UpdateBanner(bool frozen, List<JsonObject> fixedList)
    {
        var msgs = new List<string>();
        if (frozen)
        {
            string since = DataStore.GetString(_store.Data["frozen_since"]);
            msgs.Add("❄ 任务已冻结" + (since.Length > 0 ? $"（自 {since}）" : ""));
        }
        var debts = fixedList
            .Where(t => !DataStore.GetBool(t["done"]) && DataStore.GetInt(t["owed"]) > 0)
            .ToList();
        if (debts.Count > 0)
        {
            long totalOwed = debts.Sum(t => DataStore.GetInt(t["owed"]));
            var names = string.Join("、", debts.Take(2).Select(t => DataStore.GetString(t["text"])));
            string more = debts.Count > 2 ? "等" : "";
            msgs.Add($"⚠ {debts.Count} 个长期任务欠卡共 {totalOwed} 天（{names}{more}），当天点「补卡」打一次可抵消一天");
        }
        bannerText.Text = string.Join("   |  ", msgs);
        bannerBar.Visibility = msgs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------ 操作

    /// <summary>命令面板：切到今日计划并聚焦输入框。</summary>
    public void FocusNewTask()
    {
        inputBox.Focus();
        inputBox.CaretIndex = inputBox.Text.Length;
    }

    /// <summary>命令面板：切换冻结状态。</summary>
    public void ToggleFrozen()
    {
        freezeBtn.IsChecked = !freezeBtn.IsChecked;
        FreezeBtn_Click(freezeBtn, new RoutedEventArgs());
    }

    /// <summary>命令面板：清理当前计划已完成项（沿用确认框）。</summary>
    public void ClearDoneNow() => ClearDoneBtn_Click(clearDoneBtn, new RoutedEventArgs());

    private void FreezeBtn_Click(object sender, RoutedEventArgs e)
        => _store.SetFrozen(freezeBtn.IsChecked == true);

    private void ClearDoneBtn_Click(object sender, RoutedEventArgs e)
    {
        string plan = _store.GetActivePlan();
        string day = DataStore.TodayStr();
        var daily = _store.Data["daily"] as JsonObject;
        var allOnce = daily is not null ? daily[day] as JsonArray : null;
        var allFixed = _store.Data["tasks"] as JsonArray;

        int once = 0, fxd = 0;
        if (allOnce is not null)
            foreach (var n in allOnce)
                if (n is JsonObject t && _store.EffectivePlan(t) == plan && DataStore.GetBool(t["done"])) once++;
        if (allFixed is not null)
            foreach (var n in allFixed)
                if (n is JsonObject t && _store.EffectivePlan(t) == plan && DataStore.IsFixedCompletedTask(t)) fxd++;
        if (once <= 0 && fxd <= 0) return;

        var win = Window.GetWindow(this);
        var r = MessageBox.Show(win, $"确定删除「{plan}」里 {once} 项已完成临时任务和 {fxd} 项已完成固定任务吗？",
            "清理已完成任务", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        _store.ClearDone(plan);
    }

    private void AddBtn_Click(object sender, RoutedEventArgs e) => CommitInput();

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitInput();
    }

    private void CommitInput()
    {
        string text = inputBox.Text.Trim();
        if (text.Length == 0) return;
        _store.AddTask(text);
        inputBox.Clear();
        inputBox.Focus();
    }

    private void AddFixedBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new FixedTaskDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        if (dlg.ResultText.Length == 0) return;
        _store.AddFixed(dlg.ResultText, dlg.ResultDesc, dlg.ResultDays);
    }
}
