using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KaoyanPlanner.WPF.Controls;
using KaoyanPlanner.WPF.Services;

namespace KaoyanPlanner.WPF.Views.Tabs;

/// <summary>
/// 专注统计页：日/周切换，整个统计区是 FocusGallery（无限滚动 + 焦点缩放）。
/// 日视图 4 卡 = 24h 柱状图 + 单日摘要 + 各计划分布 + 近 10 天；
/// 周视图（本周一 00:00 至今）2 卡 = 周摘要 + 本周各计划分布（堆叠条 + 图例）。
/// 数据读 focus_history（总时长）+ focus_plan（按计划，惰性键）。
/// </summary>
public partial class StatsTab : UserControl
{
    private static readonly string[] WeekdayCn = { "星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日" };

    private readonly DataStore _store;
    private DateTime _day = DataStore.Today();
    private bool _isDay = true;
    private bool _loading = true;

    public StatsTab(DataStore store, FocusTimerService focusTimer)
    {
        InitializeComponent();
        _store = store;
        focusTimer.HistoryChanged += Refresh;   // 专注数据写盘 → 实时刷新
        _store.Changed += Refresh;              // 计划改名/删除等 → 保持分布与数据同步
        _loading = false;
        Refresh();
    }

    /// <summary>由 HistoryChanged / 切页激活 / 计划变化调用。ReduceMotion 每次同步（动画开关即时生效）。</summary>
    public void Refresh()
    {
        gallery.ReduceMotion = DataStore.GetBool(_store.Data["reduce_motion"]);
        if (_isDay) RefreshDay();
        else RefreshWeek();
    }

    // ------------------------------------------------------------ 日视图（4 卡）

    private void RefreshDay()
    {
        string dayKey = _day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var hist = DataStore.GetObj(_store.Data, "focus_history");
        var day = hist is not null ? hist[dayKey] as JsonObject : null;

        double[] minutes = new double[24];
        double total = 0;
        int active = 0;
        double peak = -1;
        int peakHour = -1;
        if (day is not null)
        {
            foreach (var kv in day)
            {
                if (!int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hour)
                    || hour < 0 || hour > 23)
                    continue;
                double v = DataStore.GetDouble(kv.Value);
                minutes[hour] = v;
                if (v > 0)
                {
                    total += v;
                    active++;
                    if (v > peak) { peak = v; peakHour = hour; }
                }
            }
        }

        bool isToday = _day.Date == DataStore.Today();
        dayLbl.Text = dayKey + (isToday ? "　·　今天" : "");
        nextBtn.IsEnabled = !isToday;
        dayNav.Visibility = Visibility.Visible;

        gallery.SetCards(new List<Func<UIElement>>
        {
            () => BuildHistogramCard(minutes, isToday),
            () => BuildDaySummaryCard(total, active, peakHour, minutes),
            () => BuildDayPlanCard(dayKey, total),
            () => BuildDailyCard(),
        });
    }

    /// <summary>24h 柱状图卡：自绘卡底，填满槽位（不包 CardStyle，避免双重底；高度随焦点变化 → 柱更高）。</summary>
    private UIElement BuildHistogramCard(double[] minutes, bool isToday)
    {
        var h = new HistogramControl { MarkCurrentHour = isToday };
        h.SetMinutes(minutes);
        return h;
    }

    private UIElement BuildDaySummaryCard(double total, int active, int peakHour, double[] minutes)
    {
        if (total <= 0)
            return WrapCard(new TextBlock
            {
                Text = "这一天还没有专注记录",
                Style = (Style)FindResource("BodyText"),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });

        // 最早/最晚专注时段（凌晨 2 点日界：跨 0 点时 0-2 点的时段在末尾）
        int first = -1, last = -1;
        for (int h = 0; h < 24; h++)
            if (minutes[h] > 0) { if (first < 0) first = h; last = h; }
        string range = first >= 0
            ? $"最早专注 {first:00}:00 · 最晚专注 {last:00}:00"
            : "";

        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = $"共 {Fmt.Minutes(total)} · 分布在 {active} 个小时段 · 高峰时段 {peakHour}:00",
            Style = (Style)FindResource("BodyText"),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        if (range.Length > 0)
            panel.Children.Add(new TextBlock
            {
                Text = range,
                Style = (Style)FindResource("MutedText"),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
            });
        return WrapCard(panel);
    }

    /// <summary>「各计划分布」卡：focus_plan[day] 各计划 + 未分类（总 − 已标记，钳制 ≥0），按时长降序。</summary>
    private UIElement BuildDayPlanCard(string dayKey, double total)
    {
        var fp = DataStore.GetObj(_store.Data, "focus_plan");
        var fd = fp is not null ? fp[dayKey] as JsonObject : null;
        var planMin = new Dictionary<string, double>();
        if (fd is not null)
            foreach (var kv in fd)
            {
                double v = DataStore.GetDouble(kv.Value);
                if (v > 0) planMin[kv.Key] = v;
            }
        double tagged = planMin.Values.Sum();
        double unclassified = Math.Max(0, total - tagged);

        // Grid 双行：标题 Auto + 列表 *（列表填满剩余高度 → 聚焦卡更高时露出更多行）
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        if (total <= 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "各计划分布",
                Style = (Style)FindResource("TitleText"),
                FontSize = 15,
            });
            var empty = new TextBlock
            {
                Text = "这一天还没有专注记录",
                Style = (Style)FindResource("MutedText"),
                Margin = new Thickness(0, 8, 0, 0),
            };
            panel.Children.Add(empty);
            Grid.SetRow(empty, 1);
            return WrapCard(panel);
        }

        panel.Children.Add(new TextBlock
        {
            Text = $"各计划分布　·　共 {Fmt.Minutes(total)}",
            Style = (Style)FindResource("TitleText"),
            FontSize = 15,
        });

        var plans = _store.GetFixedTaskNames();   // 专注目标 = 长期计划（固定任务）；已删除任务不在列表 → 灰
        var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (plan, m) in planMin.OrderByDescending(kv => kv.Value))
            list.Children.Add(BuildPlanRow(plan, m, total, plans));
        if (unclassified > 0.001)
            list.Children.Add(BuildPlanRow(null, unclassified, total, plans));
        var sv = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = list,
        };
        panel.Children.Add(sv);
        Grid.SetRow(sv, 1);
        return WrapCard(panel);
    }

    // ------------------------------------------------------------ 周视图（2 卡）

    private static DateTime Monday(DateTime d)
        => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    private void RefreshWeek()
    {
        DateTime monday = Monday(DataStore.Today());
        int span = (DataStore.Today() - monday).Days;   // 0..6
        var planMin = new Dictionary<string, double>();
        double total = 0;
        var hist = DataStore.GetObj(_store.Data, "focus_history");
        var fp = DataStore.GetObj(_store.Data, "focus_plan");
        for (int i = 0; i <= span; i++)
        {
            string k = monday.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (hist is not null && hist[k] is JsonObject hd)
                foreach (var kv in hd) total += DataStore.GetDouble(kv.Value);
            if (fp is not null && fp[k] is JsonObject fd)
                foreach (var kv in fd)
                {
                    double v = DataStore.GetDouble(kv.Value);
                    if (v > 0) planMin[kv.Key] = planMin.GetValueOrDefault(kv.Key) + v;
                }
        }
        double tagged = planMin.Values.Sum();
        double unclassified = Math.Max(0, total - tagged);

        dayNav.Visibility = Visibility.Collapsed;

        var rows = planMin.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ToList();
        gallery.SetCards(new List<Func<UIElement>>
        {
            () => BuildWeekSummaryCard(total, span, monday),
            () => BuildWeekDistributionCard(total, rows, unclassified),
        });
    }

    private UIElement BuildWeekSummaryCard(double total, int span, DateTime monday)
    {
        if (total <= 0)
            return WrapCard(new TextBlock
            {
                Text = "本周还没有专注记录",
                Style = (Style)FindResource("BodyText"),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });

        // 有专注的天数
        int days = 0;
        var hist = DataStore.GetObj(_store.Data, "focus_history");
        for (int i = 0; i <= span; i++)
        {
            string k = monday.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            bool has = false;
            if (hist is not null && hist[k] is JsonObject hd)
                foreach (var kv in hd)
                    if (DataStore.GetDouble(kv.Value) > 0) { has = true; break; }
            if (has) days++;
        }

        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = $"本周（{monday:MM-dd} 起）共专注 {Fmt.Minutes(total)} · 日均 {Fmt.Minutes(total / (span + 1.0))}",
            Style = (Style)FindResource("BodyText"),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{span + 1} 天里坚持专注 {days} 天" + (days > 0 ? $" · 最投入的一天约 {Fmt.Minutes(PeakDay(total, monday, span))}" : ""),
            Style = (Style)FindResource("MutedText"),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        });
        return WrapCard(panel);
    }

    private double PeakDay(double _fallback, DateTime monday, int span)
    {
        double best = 0;
        var hist = DataStore.GetObj(_store.Data, "focus_history");
        for (int i = 0; i <= span; i++)
        {
            string k = monday.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            double t = 0;
            if (hist is not null && hist[k] is JsonObject hd)
                foreach (var kv in hd) t += DataStore.GetDouble(kv.Value);
            if (t > best) best = t;
        }
        return best;
    }

    /// <summary>周分布卡：标题 + 堆叠比例条 + 图例行（共用 BuildPlanRow）。</summary>
    private UIElement BuildWeekDistributionCard(double total, List<KeyValuePair<string, double>> rows, double unclassified)
    {
        // Grid 双行：标题 Auto + 正文 *（堆叠条 Auto + 列表 Star → 列表填满剩余高度）
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        if (total <= 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "本周各计划分布",
                Style = (Style)FindResource("TitleText"),
                FontSize = 15,
            });
            var empty = new TextBlock
            {
                Text = "本周还没有专注记录",
                Style = (Style)FindResource("MutedText"),
                Margin = new Thickness(0, 8, 0, 0),
            };
            panel.Children.Add(empty);
            Grid.SetRow(empty, 1);
            return WrapCard(panel);
        }

        panel.Children.Add(new TextBlock
        {
            Text = "本周各计划分布",
            Style = (Style)FindResource("TitleText"),
            FontSize = 15,
        });

        var plans = _store.GetFixedTaskNames();   // 专注目标 = 长期计划（固定任务）

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 堆叠比例条：每个有色类别一段（未分类灰色放最后）
        var segments = new List<(string? Plan, double Min)>();
        foreach (var (p, m) in rows) segments.Add((p, m));
        if (unclassified > 0.001) segments.Add((null, unclassified));

        var stack = new Grid { Height = 14, Margin = new Thickness(0, 10, 0, 4) };
        foreach (var (p, m) in segments)
        {
            stack.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(0, m / total * 1000), GridUnitType.Star),
            });
            var seg = new Border
            {
                Background = (Brush)FindResource(PlanPalette.BrushKeyFor(p, plans)),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(p is null ? 0 : 1, 0, p is null ? 0 : 1, 0),
            };
            stack.Children.Add(seg);
            Grid.SetColumn(seg, stack.ColumnDefinitions.Count - 1);
        }
        body.Children.Add(stack);   // row 0

        var list = new StackPanel();
        foreach (var (p, m) in segments)
            list.Children.Add(BuildPlanRow(p, m, total, plans));
        var sv = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = list,
            Margin = new Thickness(0, 4, 0, 0),
        };
        body.Children.Add(sv);
        Grid.SetRow(sv, 1);
        panel.Children.Add(body);
        Grid.SetRow(body, 1);
        return WrapCard(panel);
    }

    // ------------------------------------------------------------ 计划分布行（日/周共用）

    /// <summary>一行：色点 + 计划名 + 时长 + 占比条 + 百分比。plan=null → 「未分类」（灰）。</summary>
    private UIElement BuildPlanRow(string? plan, double minutes, double total, List<string> plans)
    {
        var primary = (Brush)FindResource(PlanPalette.BrushKeyFor(plan, plans));

        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // Auto：时长列不被 72px 截断（「X 小时 Y 分钟」完整显示）
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Background = primary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
        };
        row.Children.Add(dot);

        var nameTxt = new TextBlock
        {
            Text = plan ?? "未分类",
            Style = (Style)FindResource("BodyText"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(nameTxt);
        Grid.SetColumn(nameTxt, 1);

        var durTxt = new TextBlock
        {
            Text = Fmt.Minutes(minutes),
            Style = (Style)FindResource("MetadataText"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(durTxt);
        Grid.SetColumn(durTxt, 2);

        double ratio = total > 0 ? minutes / total : 0;
        var bar = new Grid { Height = 6, Margin = new Thickness(14, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio * 1000, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, (1 - ratio) * 1000), GridUnitType.Star) });
        var fill = new Border { Background = primary, CornerRadius = new CornerRadius(3) };
        var track = new Border { Background = (Brush)FindResource("TrackBrush"), CornerRadius = new CornerRadius(3) };
        Grid.SetColumn(fill, 0);
        Grid.SetColumn(track, 1);
        bar.Children.Add(fill);
        bar.Children.Add(track);
        row.Children.Add(bar);
        Grid.SetColumn(bar, 3);

        var pctTxt = new TextBlock
        {
            Text = Math.Round(ratio * 100) + "%",
            Style = (Style)FindResource("MetadataText"),
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // 0% 项弱化：几乎没投入的计划不让它抢视线
        if (ratio <= 0.001)
        {
            pctTxt.Foreground = (Brush)FindResource("TextMutedBrush");
            pctTxt.Opacity = 0.6;
            nameTxt.Opacity = 0.75;
        }
        row.Children.Add(pctTxt);
        Grid.SetColumn(pctTxt, 4);

        return row;
    }

    // ------------------------------------------------------------ 近 10 天卡

    private UIElement BuildDailyCard()
    {
        var hist = DataStore.GetObj(_store.Data, "focus_history");
        var rows = new List<(DateTime Day, double Total)>();
        double total10 = 0;
        for (int i = 0; i < 10; i++)
        {
            DateTime d = DataStore.Today().AddDays(-i);
            var day = hist is not null
                ? hist[d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] as JsonObject
                : null;
            double t = 0;
            if (day is not null)
                foreach (var kv in day)
                    t += DataStore.GetDouble(kv.Value);
            total10 += t;
            rows.Add((d, t));
        }

        int streak = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Total > 0) streak++;
            else break;
        }

        // Grid 双行：头部 Auto + 列表 *（列表填满剩余高度 → 聚焦卡更高时显示更多天）
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Text = "每天学习时间", Style = (Style)FindResource("TitleText"), FontSize = 15 };
        var summary = new TextBlock
        {
            Text = $"近 10 天共 {Fmt.Minutes(total10)} · 日均 {Fmt.Minutes(total10 / 10.0)} · 连续 {streak} 天",
            Style = (Style)FindResource("MutedText"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(title);
        header.Children.Add(summary);
        Grid.SetColumn(summary, 1);
        panel.Children.Add(header);

        double maxTotal = rows.Count > 0 ? rows.Max(r => r.Total) : 0;
        var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (d, t) in rows)
            list.Children.Add(BuildDayRow(d, t, maxTotal));
        var sv = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = list,
        };
        panel.Children.Add(sv);
        Grid.SetRow(sv, 1);
        return WrapCard(panel);
    }

    private UIElement BuildDayRow(DateTime d, double total, double maxTotal)
    {
        bool peak = total > 0 && total >= maxTotal;
        var primary = peak ? (Brush)FindResource("AccentStrongBrush") : (Brush)FindResource("TextSecondaryBrush");

        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        string wd = WeekdayCn[((int)d.DayOfWeek + 6) % 7][2].ToString();   // 「星期一」→「一」
        var dateText = new TextBlock
        {
            Text = $"{d:MM-dd} 周{wd}",
            Style = (Style)FindResource("MetadataText"),
            Foreground = primary,
            FontWeight = peak ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(dateText);

        // 迷你条：两列星权重等比（track + fill）
        var bar = new Grid { Height = 5, Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(total * 1000, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength((maxTotal - total) * 1000, GridUnitType.Star) });
        var fill = new Border
        {
            Background = (Brush)FindResource("AccentBrush"),
            CornerRadius = new CornerRadius(2.5),
        };
        var track = new Border
        {
            Background = (Brush)FindResource("TrackBrush"),
            CornerRadius = new CornerRadius(2.5),
        };
        Grid.SetColumn(fill, 0);
        Grid.SetColumn(track, 1);
        bar.Children.Add(fill);
        bar.Children.Add(track);
        row.Children.Add(bar);
        Grid.SetColumn(bar, 1);

        var durText = new TextBlock
        {
            Text = total > 0 ? Fmt.Minutes(total) : "—",
            Style = (Style)FindResource("MetadataText"),
            Foreground = primary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(durText);
        Grid.SetColumn(durText, 2);

        return row;
    }

    // ------------------------------------------------------------ 卡片外壳 / 日导航 / 切换

    /// <summary>标准卡外壳：CardStyle + 内边距，拉伸填满槽位（高度随焦点变化 → 内容区更大）。</summary>
    private UIElement WrapCard(UIElement content)
        => new Border
        {
            Style = (Style)FindResource("CardStyle"),
            Padding = new Thickness(16, 12, 16, 12),
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = content,
        };

    private void Prev_Click(object sender, RoutedEventArgs e)
    {
        _day = _day.AddDays(-1);
        Refresh();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        _day = _day.AddDays(1);
        if (_day.Date > DataStore.Today()) _day = DataStore.Today();   // clamp 到窗口日
        Refresh();
    }

    private void Period_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var rb = (FrameworkElement)sender;
        bool day = (string)rb.Tag == "day";
        if (day == _isDay) return;
        _isDay = day;
        dayRb.IsChecked = day;      // 程序化赋值不触发 Click，无重入
        weekRb.IsChecked = !day;
        Refresh();
    }
}
