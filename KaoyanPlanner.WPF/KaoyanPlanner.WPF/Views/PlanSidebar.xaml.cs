using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KaoyanPlanner.WPF.Services;
using KaoyanPlanner.WPF.Views.Dialogs;
// UseWindowsForms 全局导入 System.Windows.Forms/System.Drawing → 文件级别名指向 WPF
using Cursors = System.Windows.Input.Cursors;
using Dock = System.Windows.Controls.Dock;

namespace KaoyanPlanner.WPF.Views;

/// <summary>
/// 272px 计划导航栏（Notion/Codex 式）：列出全部「计划」，点击切换主区显示对应计划的任务。
/// 计划归类的载体；主区的 PlanTab 按 active_plan 过滤临时 + 固定任务。
/// 支持新建 / 重命名 / 删除（右键菜单）。由 DataStore.Changed 驱动重建。
/// </summary>
public partial class PlanSidebar : UserControl
{
    private readonly DataStore _store;

    public PlanSidebar(DataStore store)
    {
        InitializeComponent();
        _store = store;

        // ⚠ newPlanBtn 的 Click 已在 XAML 挂过（Click="NewPlan_Click"），这里不再重复挂，否则点一次弹两次窗
        _store.Changed += Refresh;
        Refresh();
    }

    public void Refresh()
    {
        var plans = _store.GetPlans();
        string active = _store.GetActivePlan();
        planCount.Text = $"{plans.Count} 个";
        planList.Children.Clear();
        foreach (string p in plans)
            planList.Children.Add(BuildPlanRow(p, CountTasks(p), p == active));
    }

    private int CountTasks(string plan)
    {
        int n = 0;
        if (_store.Data["tasks"] is JsonArray tasks)
            foreach (var t in tasks)
                if (t is JsonObject o && _store.EffectivePlan(o) == plan) n++;
        string day = DataStore.TodayStr();
        if (_store.Data["daily"] is JsonObject daily && daily[day] is JsonArray dayArr)
            foreach (var t in dayArr)
                if (t is JsonObject o && _store.EffectivePlan(o) == plan) n++;
        return n;
    }

    private UIElement BuildPlanRow(string name, int count, bool active)
    {
        string nameCopy = name;

        // 左 accent 竖条（选中才显示）
        var accent = new Border
        {
            Width = 2,
            Background = active ? (Brush)FindResource("AccentBrush") : Brushes.Transparent,
            CornerRadius = new CornerRadius(1),
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 2, 0, 2),
        };

        var nameTxt = new TextBlock
        {
            Text = name,
            FontFamily = (FontFamily)FindResource("UIFont"),
            FontSize = 13,
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = active ? (Brush)FindResource("AccentStrongBrush") : (Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var countTxt = new TextBlock
        {
            Text = count > 0 ? $"{count} 项" : "",
            Style = (Style)FindResource("MutedText"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 2, 0),
        };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(accent, Dock.Left);
        DockPanel.SetDock(countTxt, Dock.Right);
        dock.Children.Add(accent);
        dock.Children.Add(countTxt);
        dock.Children.Add(nameTxt);

        var rowBg = new SolidColorBrush(active
            ? (Color)FindResource("AccentSoftFillColor")
            : Colors.Transparent);
        var row = new Border
        {
            Background = rowBg,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(6, 7, 6, 7),
            Cursor = Cursors.Hand,
            Child = dock,
        };
        row.MouseEnter += (_, _) =>
        {
            if (!active) Controls.UiMotion.TweenColor(rowBg, (Color)FindResource("ElevatedColor"));
        };
        row.MouseLeave += (_, _) =>
        {
            if (!active) Controls.UiMotion.TweenColor(rowBg, Colors.Transparent);
        };
        row.MouseLeftButtonDown += (_, _) => _store.SetActivePlan(nameCopy);

        var ctx = new ContextMenu();
        var rename = new MenuItem { Header = "重命名" };
        rename.Click += (_, _) => RenamePlan(nameCopy);
        var del = new MenuItem { Header = "删除" };
        del.Click += (_, _) => DeletePlan(nameCopy);
        ctx.Items.Add(rename);
        ctx.Items.Add(del);
        row.ContextMenu = ctx;

        return row;
    }

    // ------------------------------------------------------------ 计划操作

    private void NewPlan_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PlanNameDialog("新建计划") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
            _store.AddPlan(dlg.ResultName);   // Save → Changed → 刷新列表 + PlanTab
    }

    private void RenamePlan(string oldName)
    {
        var dlg = new PlanNameDialog("重命名计划", oldName) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
            _store.RenamePlan(oldName, dlg.ResultName);
    }

    private void DeletePlan(string name)
    {
        int count = CountTasks(name);
        var win = Window.GetWindow(this);
        var msg = count > 0
            ? $"确定删除计划「{name}」吗？其中的 {count} 项任务会一并删除。"
            : $"确定删除计划「{name}」吗？";
        var r = MessageBox.Show(win, msg, "删除计划", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        _store.DeletePlan(name);
    }
}
