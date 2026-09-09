using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KaoyanPlanner.WPF.Services;
using KaoyanPlanner.WPF.Views.Dialogs;
namespace KaoyanPlanner.WPF.Controls;

/// <summary>
/// 任务行：一次性任务（once）与固定任务（fixed）两种形态，镜像 widgets.py 的 TaskItem。
/// 全部用 Click 事件（而非 Checked）：程序化设置 IsChecked 不会重入，天然防信号风暴。
/// </summary>
public partial class TaskItemControl : UserControl
{
    private readonly DataStore _store;
    private readonly JsonObject _task;
    private readonly string _kind;
    private readonly int _index;
    private readonly string _id = "";
    private bool _inlineCommitted;

    public TaskItemControl(DataStore store, JsonObject task, string kind, int index = -1)
    {
        InitializeComponent();
        _store = store;
        _task = task;
        _kind = kind;
        _index = index;

        bool done = DataStore.GetBool(task["done"]);
        check.IsChecked = done;

        string text = DataStore.GetString(task["text"]);
        textLbl.Text = text;
        ctxDone.Header = done ? "标记为未完成" : "标记为完成";
        ApplyDoneStyle(done);

        bool frozen = DataStore.GetBool(_store.Data["frozen"]);

        if (_kind == "fixed")
        {
            _id = DataStore.GetString(task["id"]);

            bool completed = DataStore.IsFixedCompletedTask(task);
            bool punchedToday = DataStore.FixedPunchedTodayTask(task);

            string tag;
            bool tagDone;
            if (completed) { tag = "✓ 已完成"; tagDone = true; }
            else if (punchedToday) { tag = "✓ 今日已打卡"; tagDone = true; }
            else if (frozen) { tag = "❄ 冻结中"; tagDone = false; }
            else { tag = "○ 今日未打卡"; tagDone = false; }
            todayTagText.Text = tag;
            ApplyTagStyle(tagDone, frozen);

            long total = Math.Max(1, DataStore.GetInt(task["target_days"], 1));
            long prog = DataStore.GetInt(task["progress"]);
            miniBar.Maximum = total;
            miniBar.Value = Math.Min(prog, total);
            fracText.Text = $"{prog}/{total}";

            string desc = DataStore.GetString(task["desc"]).Trim();
            descText.Text = desc;
            descText.Visibility = desc.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            ApplyPunchButton(task, completed, punchedToday, frozen);

            // 固定任务：打卡由打卡按钮承担，勾选框隐藏（避免与打卡按钮两套完成控件并存）
            check.Visibility = Visibility.Collapsed;
            metaRow.Margin = new Thickness(8, 0, 0, 0);
            descText.Margin = new Thickness(8, 0, 0, 0);
        }
        else
        {
            todayTagText.Text = done ? "✓ 已完成" : "○ 未完成";
            ApplyTagStyle(done, false);
            miniBar.Maximum = 1;
            miniBar.Value = done ? 1 : 0;
            fracText.Text = done ? "1/1" : "0/1";
            punchBtn.Visibility = Visibility.Collapsed;
        }

        // 事件在 InitializeComponent 后挂：程序化 IsChecked 不触发 Click，无重入
        check.Click += Check_Click;
        punchBtn.Click += Punch_Click;
        editBtn.Click += Edit_Click;
        delBtn.Click += Del_Click;
        rootBd.MouseLeftButtonDown += RootBd_MouseLeftButtonDown;
    }

    private void ApplyDoneStyle(bool done)
    {
        if (done)
        {
            textLbl.TextDecorations = TextDecorations.Strikethrough;
            textLbl.Foreground = (Brush)FindResource("TextDoneBrush");
        }
    }

    private void ApplyTagStyle(bool done, bool frozen)
    {
        if (done)
        {
            todayTagPill.Style = (Style)FindResource("PillAccentStyle");
            todayTagText.Foreground = Brushes.White;   // 实心蓝底 → 白字（Codex 原则）
        }
        else if (frozen)
        {
            todayTagPill.Style = (Style)FindResource("PillStyle");
            todayTagText.Foreground = (Brush)FindResource("WarnBrush");
        }
        else
        {
            todayTagPill.Style = (Style)FindResource("PillStyle");
            todayTagText.Foreground = (Brush)FindResource("TextSecondaryBrush");
        }
    }

    /// <summary>
    /// 打卡按钮按状态显示：欠卡 → 「补卡」蓝底（打一次抵消一天欠卡）；
    /// 今日已打卡（或今天刚打卡完成）→ 「✅已打卡」，再点一次撤销回未打卡；
    /// 之前完成的任务 → 「已完成」禁用；默认 → 「打卡」。
    /// </summary>
    private void ApplyPunchButton(JsonObject task, bool completed, bool punchedToday, bool frozen)
    {
        long owed = DataStore.GetInt(task["owed"]);
        bool doneToday = DataStore.GetString(task["last_done_date"]) == DataStore.TodayStr();
        bool undoable = completed
            ? doneToday && DataStore.GetInt(task["progress"]) >= DataStore.GetInt(task["target_days"], 1)
            : punchedToday;

        if (undoable)
        {
            punchBtn.Content = "✅已打卡";
            punchBtn.Style = (Style)FindResource("SecondaryButtonStyle");
            punchBtn.IsEnabled = !frozen;
        }
        else if (completed)
        {
            punchBtn.Content = "已完成";
            punchBtn.Style = (Style)FindResource("SecondaryButtonStyle");
            punchBtn.IsEnabled = false;
        }
        else if (owed > 0)
        {
            punchBtn.Content = "补卡";
            punchBtn.Style = (Style)FindResource("PrimaryButtonStyle");   // 蓝底白字
            punchBtn.IsEnabled = !frozen;
        }
        else
        {
            punchBtn.Content = "打卡";
            punchBtn.Style = (Style)FindResource("AccentButtonStyle");
            punchBtn.IsEnabled = !frozen;
        }
    }

    // ------------------------------------------------------------ 操作

    private void Check_Click(object sender, RoutedEventArgs e)
    {
        bool checkedState = check.IsChecked == true;
        bool wasDone = DataStore.GetBool(_task["done"]);
        if (_kind == "fixed")
        {
            if (DataStore.GetBool(_store.Data["frozen"]) && !DataStore.GetBool(_task["done"]))
            {
                check.IsChecked = DataStore.GetBool(_task["done"]);   // 冻结中禁止勾选，回滚
                return;
            }
            if (checkedState && !wasDone)
                AnimateDoneThen(() => { _task["done"] = true; _task["owed"] = 0L; _store.Save(); });
            else
            {
                _task["done"] = checkedState;
                if (checkedState) _task["owed"] = 0L;   // 提前划去完成，欠卡清零
                _store.Save();
            }
        }
        else
        {
            if (checkedState && !wasDone)
                AnimateDoneThen(() => _store.SetTaskDone(_index, true));
            else
                _store.SetTaskDone(_index, checkedState);
        }
    }

    /// <summary>
    /// 完成动效：勾选后先 80ms 轻微缩放（1→0.97→1），动画播完才 Save →
    /// Changed → 重建出行带划线/文字灰。同步重建会让动画秒失效，故推迟落盘；
    /// 回调里再查一次勾选态，防 80ms 内快速取消勾选的竞态。
    /// </summary>
    private void AnimateDoneThen(Action then)
    {
        rootBd.RenderTransformOrigin = new Point(0.5, 0.5);
        rootBd.RenderTransform = new ScaleTransform(1, 1);
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0.97, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(40))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        anim.Completed += (_, _) =>
        {
            if (check.IsChecked == true) then();
        };
        rootBd.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        rootBd.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void Punch_Click(object sender, RoutedEventArgs e)
    {
        if (_kind != "fixed" || string.IsNullOrEmpty(_id)) return;
        if (DataStore.GetBool(_store.Data["frozen"])) return;
        _store.PunchFixed(_id);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_kind == "fixed" && !string.IsNullOrEmpty(_id))
        {
            var dlg = new FixedTaskDialog(
                DataStore.GetString(_task["text"]),
                DataStore.GetString(_task["desc"]),
                (int)Math.Max(1, DataStore.GetInt(_task["target_days"], 1)))
            { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && dlg.ResultText.Length > 0)
                _store.EditFixed(_id, dlg.ResultText, dlg.ResultDesc, dlg.ResultDays);
        }
        else
        {
            BeginInlineEdit();
        }
    }

    private void RootBd_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            Edit_Click(sender, e);
    }

    private void CtxEdit_Click(object sender, RoutedEventArgs e) => Edit_Click(sender, e);

    private void CtxDone_Click(object sender, RoutedEventArgs e)
    {
        if (_kind == "fixed" && DataStore.GetBool(_store.Data["frozen"]) && !DataStore.GetBool(_task["done"]))
            return;   // 冻结中不允许手动划去未完成固定任务
        bool want = !DataStore.GetBool(_task["done"]);
        check.IsChecked = want;
        Check_Click(sender, e);   // 程序化设置不触发 Click，手动走一遍逻辑
    }

    private void CtxDel_Click(object sender, RoutedEventArgs e) => Del_Click(sender, e);

    private void Del_Click(object sender, RoutedEventArgs e)
    {
        var win = Window.GetWindow(this);
        string text = DataStore.GetString(_task["text"]);
        string day = DataStore.TodayStr();
        JsonNode? saved = null;
        int savedIndex = -1;

        if (_kind == "fixed" && !string.IsNullOrEmpty(_id))
        {
            if (_store.Data["tasks"] is JsonArray tasks)
            {
                for (int i = 0; i < tasks.Count; i++)
                    if (tasks[i] == _task) { savedIndex = i; break; }
                saved = _task.DeepClone();
                _store.DeleteFixed(_id);
            }
        }
        else if (DataStore.GetObj(_store.Data, "daily")?[day] is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
                if (arr[i] == _task) { savedIndex = i; break; }
            saved = _task.DeepClone();
            _store.DeleteTask(_index);
        }

        if (saved == null) return;
        ToastHost.Show(win, $"已删除「{text}」", "撤销", () =>
        {
            if (_kind == "fixed")
            {
                if (_store.Data["tasks"] is JsonArray tasks)
                {
                    var node = saved.DeepClone();
                    if (savedIndex >= 0 && savedIndex <= tasks.Count) tasks.Insert(savedIndex, node);
                    else tasks.Add(node);
                }
            }
            else if (DataStore.GetObj(_store.Data, "daily")?[day] is JsonArray arr)
            {
                var node = saved.DeepClone();
                if (savedIndex >= 0 && savedIndex <= arr.Count) arr.Insert(savedIndex, node);
                else arr.Add(node);
            }
            _store.Save();
        });
    }

    // ------------------------------------------------------------ 一次性任务内联改名

    private void BeginInlineEdit()
    {
        if (_kind != "once") return;
        var tb = new TextBox
        {
            Text = DataStore.GetString(_task["text"]),
            FontFamily = textLbl.FontFamily,
            FontSize = textLbl.FontSize,
            Padding = new Thickness(4, 1, 4, 1),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) CommitInlineEdit(tb);
            else if (e.Key == Key.Escape) CancelInlineEdit(tb);
        };
        tb.LostKeyboardFocus += (_, _) => CommitInlineEdit(tb);
        textSlot.Content = tb;
        tb.Focus();
        tb.SelectAll();
    }

    private void CommitInlineEdit(TextBox tb)
    {
        if (_inlineCommitted) return;
        _inlineCommitted = true;
        string t = tb.Text.Trim();
        if (t.Length > 0)
            _store.EditTask(_index, t);
        textSlot.Content = textLbl;
    }

    private void CancelInlineEdit(TextBox tb)
    {
        if (_inlineCommitted) return;
        _inlineCommitted = true;
        textSlot.Content = textLbl;
    }
}
