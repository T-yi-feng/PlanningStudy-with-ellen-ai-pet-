using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using KaoyanPlanner.WPF.Services;
using KaoyanPlanner.WPF.Views.Dialogs;
// UseWindowsForms 全局导入 → 文件级别名指向 WPF
using Cursors = System.Windows.Input.Cursors;

namespace KaoyanPlanner.WPF.Views.Tabs;

/// <summary>
/// 提醒页签：顶部 = 收窄单行日期条（今天居左 1/4）；下方 = **两天一张表**：
/// 选中日 + 次日并排，共用同一刻度列与**同一个滚动容器（完全联动，像一张表）**，无滚动条（Hidden），
/// 标题行固定在上方。点击日期条不同天 → 整块左右丝滑滑动，**滑动期间整体高斯模糊、滑完变清晰**。
/// 点击时间表任意格子 = 直接添加提醒（日期 = 该列日期，时间 = 该格）。过期提醒由 HeartbeatService 自动清理。
/// </summary>
public partial class ReminderTab : UserControl
{
    private const double CellRow = 60;        // 2 小时一格的格高
    private const int StripDays = 8;          // 日期条格数：今天在 index 2 = 左 1/4
    private const int StripCenterOffset = 2;  // 窗口 = [C-2 .. C+5]
    private const double CellWidth = 46;      // 日期条格宽（紧凑）

    private static readonly string[] WeekdayCn = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

    private readonly DataStore _store;
    private readonly HeartbeatService _heartbeat;
    private DateTime _selectedDate = DateTime.Today;
    private DateTime _centerDay = DateTime.Today;
    private HashSet<string> _markSet = new();
    private bool _loading = true;
    private bool _animating;

    // 日期条单元格引用（选中态即时刷新）
    private sealed record StripCell(DateTime Date, Border Root, TextBlock Week, TextBlock Num, Border Dot, Border Hover);
    private readonly List<StripCell> _stripCells = new();

    // 两天一张表：每列 = 表格 + 标题元素；两列共用一个 ScrollViewer（完全联动滚动）
    private sealed class DayTable
    {
        public required Grid Schedule;
        public required TextBlock Title, TagText, Lunar, Count, Empty;
        public required Border Tag;
        public required DateTime Date;
    }

    private Grid _groupA = new(), _groupB = new();
    private bool _showA = true;
    private DayTable[] _colA = Array.Empty<DayTable>();
    private DayTable[] _colB = Array.Empty<DayTable>();
    private ScrollViewer? _svA, _svB;

    // 丝滑滚动（整表一个）：DispatcherTimer 逐帧 CubicEase 补间
    private DispatcherTimer? _scrollTicker;
    private ScrollViewer? _scrollTarget;
    private double _scrollFrom, _scrollTo;
    private long _scrollStartMs;

    public ReminderTab(DataStore store, HeartbeatService heartbeat)
    {
        InitializeComponent();
        _store = store;
        _heartbeat = heartbeat;

        var cfg = DataStore.GetObj(_store.Data, "unfinished_reminder");
        unfinishedCheck.IsChecked = DataStore.GetBool(cfg?["enabled"], true);
        intervalStepper.Value = (int)Math.Clamp(DataStore.GetInt(cfg?["interval_min"], 60), 10, 180);
        _loading = false;

        unfinishedCheck.Click += UnfinishedCheck_Click;
        _heartbeat.DayChanged += Refresh;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Loaded 可能多次触发（切 Tab/布局变更）：重复 Add 同一 Visual 会抛
        // ArgumentException「指定的 Visual 已经是另一个 Visual 的子级」→ 防重复。
        if (!slideRoot.Children.Contains(_groupA)) slideRoot.Children.Add(_groupA);
        if (!slideRoot.Children.Contains(_groupB)) slideRoot.Children.Add(_groupB);
        slideRoot.SizeChanged -= OnSlideRootSizeChanged;
        slideRoot.SizeChanged += OnSlideRootSizeChanged;
        Refresh();
    }

    private void OnSlideRootSizeChanged(object? sender, SizeChangedEventArgs e) => PositionGroups();

    // ------------------------------------------------------------ 刷新

    public void Refresh()
    {
        var today = DateTime.Today;
        if (_selectedDate.Date < today) _selectedDate = today;   // 跨日归档后追上新今天
        if (_centerDay.Date < today) _centerDay = today;

        _markSet = MarkedDateSet();
        RebuildStrip();
        var arr = GetReminders();
        _colA = BuildGroup(_groupA, _selectedDate, today, arr);
        _colB = BuildGroup(_groupB, _selectedDate, today, arr);
        _groupA.Visibility = _showA ? Visibility.Visible : Visibility.Collapsed;
        _groupB.Visibility = _showA ? Visibility.Collapsed : Visibility.Visible;
        SetTranslate(_groupA, 0);
        SetTranslate(_groupB, 0);
        UpdateSummary();
    }

    private JsonArray GetReminders()
    {
        if (_store.Data["reminders"] is JsonArray a) return a;
        var created = new JsonArray();
        _store.Data["reminders"] = created;
        return created;
    }

    private HashSet<string> MarkedDateSet()
    {
        var set = new HashSet<string>();
        foreach (var n in GetReminders())
        {
            if (n is not JsonObject r || ReminderService.IsLegacy(r)) continue;
            string d = DataStore.GetString(r["date"]);
            if (d.Length == 10) set.Add(d);
        }
        return set;
    }

    private void UpdateSummary()
    {
        var next = ReminderService.NextEnabled(GetReminders(), DateTime.Now);
        nextLbl.Text = next is null
            ? "没有开启中的提醒"
            : $"下次提醒：{next.Value.day} {next.Value.hhmm} · {next.Value.label}";
    }

    // ------------------------------------------------------------ 单行日期条（收窄）

    private void StripPrev_Click(object sender, RoutedEventArgs e)
    {
        _centerDay = _centerDay.AddDays(-7);
        RebuildStrip(animate: true);
    }

    private void StripNext_Click(object sender, RoutedEventArgs e)
    {
        _centerDay = _centerDay.AddDays(7);
        RebuildStrip(animate: true);
    }

    private void RebuildStrip(bool animate = false)
    {
        _stripCells.Clear();
        stripGrid.Children.Clear();
        for (int i = 0; i < StripDays; i++)
            stripGrid.Children.Add(BuildStripCell(_centerDay.AddDays(i - StripCenterOffset)));
        if (animate && SystemParameters.ClientAreaAnimation)
        {
            stripGrid.Opacity = 0;
            stripGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private Border BuildStripCell(DateTime d)
    {
        var today = DateTime.Today;
        bool selected = d.Date == _selectedDate.Date;
        bool isToday = d.Date == today;
        bool marked = _markSet.Contains(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var hover = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("HoverFillBrush"),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        var week = new TextBlock { Text = WeekdayCn[(int)d.DayOfWeek], FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center };
        var num = new TextBlock
        {
            Text = d.Day.ToString(CultureInfo.InvariantCulture),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var dot = new Border
        {
            Width = 3,
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = (Brush)FindResource("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
            Visibility = isToday || marked ? Visibility.Visible : Visibility.Collapsed,
        };

        var cell = new Border
        {
            Width = CellWidth,
            MinHeight = 42,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(2),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
        };
        var layer = new Grid();
        layer.Children.Add(hover);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(week);
        stack.Children.Add(num);
        stack.Children.Add(dot);
        layer.Children.Add(stack);
        cell.Child = layer;

        cell.MouseEnter += (_, _) => AnimOpacity(hover, 1, 120);
        cell.MouseLeave += (_, _) => AnimOpacity(hover, 0, 100);
        DateTime clickDate = d;
        cell.MouseLeftButtonDown += (_, _) => SlideTo(clickDate);

        var stripCell = new StripCell(d, cell, week, num, dot, hover);
        _stripCells.Add(stripCell);
        ApplyStripVisual(stripCell, selected, isToday, marked);
        return cell;
    }

    private void UpdateStripSelection()
    {
        var today = DateTime.Today;
        foreach (var c in _stripCells)
        {
            bool selected = c.Date.Date == _selectedDate.Date;
            bool isToday = c.Date.Date == today;
            bool marked = _markSet.Contains(c.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            ApplyStripVisual(c, selected, isToday, marked);
        }
    }

    private void ApplyStripVisual(StripCell c, bool selected, bool isToday, bool marked)
    {
        var accent = (Brush)FindResource("AccentBrush");
        var accentStrong = (Brush)FindResource("AccentStrongBrush");
        var textPrimary = (Brush)FindResource("TextPrimaryBrush");
        var textMuted = (Brush)FindResource("TextMutedBrush");

        c.Root.Background = selected ? accent : Brushes.Transparent;
        c.Week.Foreground = selected ? Brushes.White : textMuted;
        c.Num.Foreground = selected ? Brushes.White : isToday ? accentStrong : textPrimary;
        c.Dot.Background = selected ? Brushes.White : accent;
        c.Dot.Visibility = isToday || marked ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------ 两天一张表（双组轮播）

    /// <summary>按 [d1, d1+1] 重建一个组：固定标题行 + 两列共用同一滚动容器（完全联动）。</summary>
    private DayTable[] BuildGroup(Grid group, DateTime d1, DateTime today, JsonArray reminders)
    {
        group.Children.Clear();
        group.RowDefinitions.Clear();
        group.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        group.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var c1 = CreateTable(d1);
        var c2 = CreateTable(d1.AddDays(1));

        // 标题行（固定，不随表格滚动）：左列标题 | 分隔 | 右列标题
        var header = new Grid { Margin = new Thickness(12, 8, 12, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var hLeft = BuildHeaderHost(c1);
        var hsep = new Border
        {
            Width = 1,
            Background = (Brush)FindResource("BorderBrush"),
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 2, 0, 2),
        };
        Grid.SetColumn(hsep, 1);
        var hRight = BuildHeaderHost(c2);
        Grid.SetColumn(hRight, 2);

        header.Children.Add(hLeft);
        header.Children.Add(hsep);
        header.Children.Add(hRight);

        // 滚动区：一个 ScrollViewer（无滚动条），内部 = 刻度列 | 左表 | 分隔 | 右表
        var sv = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
        };
        var tbl = new Grid();
        tbl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        tbl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tbl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tbl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 共用刻度列（每 2 小时一格）
        var labels = new StackPanel();
        var labelsBrush = (Brush)FindResource("TextMutedBrush");
        for (int i = 0; i < 12; i++)
            labels.Children.Add(new TextBlock
            {
                Text = (i * 2).ToString("00", CultureInfo.InvariantCulture) + ":00",
                FontSize = 11,
                Height = CellRow,
                Margin = new Thickness(8, 2, 8, 0),
                Foreground = labelsBrush,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
            });
        var labelsBorder = new Border
        {
            Background = (Brush)FindResource("SurfaceBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = labels,
        };
        var tsep = new Border
        {
            Width = 1,
            Background = (Brush)FindResource("BorderBrush"),
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 8, 0, 8),
        };
        Grid.SetColumn(tsep, 2);

        tbl.Children.Add(labelsBorder);
        tbl.Children.Add(c1.Schedule);
        tbl.Children.Add(tsep);
        tbl.Children.Add(c2.Schedule);
        tbl.Children.Add(c1.Empty);
        tbl.Children.Add(c2.Empty);
        Grid.SetColumn(c1.Schedule, 1);
        Grid.SetColumn(c2.Schedule, 3);
        Grid.SetColumn(c1.Empty, 1);
        Grid.SetColumn(c2.Empty, 3);

        sv.Content = tbl;
        sv.PreviewMouseWheel += (_, e) =>
        {
            SmoothScrollTo(sv, sv.VerticalOffset + (e.Delta > 0 ? -90 : 90));
            e.Handled = true;
        };

        group.Children.Add(header);
        group.Children.Add(sv);
        Grid.SetRow(sv, 1);

        // 渲染两列表格 + 标题
        RenderDayTable(c1, today, reminders);
        RenderDayTable(c2, today, reminders);

        // 今天列定位到当前时间附近
        if (c1.Date.Date == today || c2.Date.Date == today)
        {
            ScrollViewer targetSv = sv;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                targetSv.ScrollToVerticalOffset(Math.Min(Math.Max(0, DateTime.Now.Hour / 2 - 1) * CellRow, targetSv.ScrollableHeight))));
        }

        if (ReferenceEquals(group, _groupA)) _svA = sv; else _svB = sv;
        return new[] { c1, c2 };
    }

    /// <summary>标题容器：Tag + M月d日·周X + 农历 + N条提醒。</summary>
    private static Grid BuildHeaderHost(DayTable t)
    {
        var h = new Grid();
        h.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        h.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        h.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        h.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        h.Children.Add(t.Tag);
        h.Children.Add(t.Title);
        h.Children.Add(t.Lunar);
        h.Children.Add(t.Count);
        Grid.SetColumn(t.Title, 1);
        Grid.SetColumn(t.Lunar, 2);
        Grid.SetColumn(t.Count, 3);
        return h;
    }

    private DayTable CreateTable(DateTime date)
    {
        var tag = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(6, 1, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        var tagText = new TextBlock { FontSize = 10, FontWeight = FontWeights.SemiBold };
        tag.Child = tagText;

        var title = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0),
        };
        var lunar = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 0, 1),
            Foreground = (Brush)FindResource("TextMutedBrush"),
        };
        var count = new TextBlock
        {
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextMutedBrush"),
        };

        var sched = new Grid { Background = (Brush)FindResource("ElevatedBrush") };
        var empty = new TextBlock
        {
            Text = "这一天还没有提醒\n点击格子直接添加",
            FontSize = 12,
            Foreground = (Brush)FindResource("TextMutedBrush"),
            TextAlignment = TextAlignment.Center,
            LineHeight = 19,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        // 点击任意格子 → 直接添加提醒（日期 = 该列日期，时间 = 该格）
        DateTime clickDate = date;
        sched.MouseLeftButtonDown += (_, e) =>
        {
            double y = e.GetPosition(sched).Y;
            int cell = (int)Math.Clamp(Math.Floor(y / CellRow), 0, 11);
            int start = cell * 120;
            OpenAddDialog(clickDate, start, start + 120);
        };

        return new DayTable
        {
            Schedule = sched, Empty = empty, Title = title, TagText = tagText,
            Lunar = lunar, Count = count, Tag = tag, Date = date,
        };
    }

    private void RenderDayTable(DayTable t, DateTime today, JsonArray reminders)
    {
        DateTime date = t.Date;
        bool isToday = date.Date == today;
        bool isTomorrow = date.Date == today.AddDays(1);
        string sel = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        t.Title.Text = $"{date.Month}月{date.Day}日 · {WeekdayCn[(int)date.DayOfWeek]}";
        t.Lunar.Text = LunarCalendar.MonthDayCn(date);

        if (isToday)
        {
            t.Tag.Visibility = Visibility.Visible;
            t.Tag.Background = (Brush)FindResource("AccentBrush");
            t.TagText.Foreground = Brushes.White;
            t.TagText.Text = "今天";
        }
        else if (isTomorrow)
        {
            t.Tag.Visibility = Visibility.Visible;
            t.Tag.Background = (Brush)FindResource("ElevatedBrush");
            t.TagText.Foreground = (Brush)FindResource("TextSecondaryBrush");
            t.TagText.Text = "明天";
        }
        else
        {
            t.Tag.Visibility = Visibility.Collapsed;
        }

        // 当日块（旧版每日提醒任何日期都显示；新版仅命中日期）
        var blocks = new List<(JsonObject R, bool Legacy, int Start, int End, bool Past)>();
        foreach (var n in reminders)
        {
            if (n is not JsonObject r) continue;
            if (ReminderService.IsLegacy(r))
            {
                if (!ReminderService.TryParseHm(DataStore.GetString(r["time"]), out int h, out int m)) continue;
                blocks.Add((r, true, h * 60 + m, Math.Min(1440, h * 60 + m + 60), false));
            }
            else
            {
                if (DataStore.GetString(r["date"]) != sel) continue;
                if (!ReminderService.TryParseHm(DataStore.GetString(r["start"]), out int sh, out int sm) ||
                    !ReminderService.TryParseHm(DataStore.GetString(r["end"]), out int eh, out int em)) continue;
                int s = sh * 60 + sm, e = eh * 60 + em;
                if (e <= s) e = Math.Min(1440, s + 60);
                blocks.Add((r, false, s, e, date.Date < today));
            }
        }
        blocks.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start)
            : string.CompareOrdinal(ReminderService.LabelOf(a.R), ReminderService.LabelOf(b.R)));

        t.Count.Text = blocks.Count == 0 ? "没有提醒" : $"共 {blocks.Count} 条提醒";

        // 表格分界线（12 条，含底边）
        t.Schedule.Children.Clear();
        var lineBrush = (Brush)FindResource("BorderBrush");
        double totalH = 12 * CellRow;
        for (int i = 1; i <= 12; i++)
        {
            t.Schedule.Children.Add(new Border
            {
                Height = 1,
                Background = lineBrush,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, i * CellRow - 1, 0, 0),
                IsHitTestVisible = false,
            });
        }

        // 提醒块（绝对定位）
        foreach (var (r, legacy, start, end, past) in blocks)
        {
            double top = start / 1440.0 * totalH;
            double bottom = totalH - Math.Min(1440, end) / 1440.0 * totalH;
            var block = BuildBlock(r, legacy, start, end, past);
            block.Margin = new Thickness(4, Math.Max(0, top), 4, Math.Max(0, bottom));
            block.VerticalAlignment = VerticalAlignment.Stretch;
            t.Schedule.Children.Add(block);
        }

        // 当前时间蓝线（仅今天）
        if (isToday)
        {
            var now = DateTime.Now;
            double y = (now.Hour * 60 + now.Minute) / 1440.0 * totalH;
            var line = new Border
            {
                Height = 2,
                Background = (Brush)FindResource("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, y - 1, 0, 0),
                IsHitTestVisible = false,
            };
            line.Child = new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(3.5),
                Background = (Brush)FindResource("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(-3.5, 0, 0, 0),
                IsHitTestVisible = false,
            };
            t.Schedule.Children.Add(line);
        }

        t.Empty.Visibility = blocks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border BuildBlock(JsonObject r, bool legacy, int startMin, int endMin, bool past)
    {
        int pri = ReminderService.PriorityOf(r);
        bool on = DataStore.GetBool(r["enabled"], true);
        string label = ReminderService.LabelOf(r);

        Brush fill, border, metaBrush;
        switch (pri)
        {
            case 2:
                fill = (Brush)FindResource("DangerSoftFillBrush");
                border = (Brush)FindResource("DangerBrush");
                metaBrush = (Brush)FindResource("DangerBrush");
                break;
            case 1:
                fill = (Brush)FindResource("WarnSoftFillBrush");
                border = (Brush)FindResource("WarnBrush");
                metaBrush = (Brush)FindResource("WarnBrush");
                break;
            default:
                fill = (Brush)FindResource("AccentSoftFillBrush");
                border = (Brush)FindResource("AccentBorderSoftBrush");
                metaBrush = (Brush)FindResource("AccentStrongBrush");
                break;
        }

        string hmStart = $"{startMin / 60:00}:{startMin % 60:00}";
        string meta = legacy
            ? $"{hmStart} · 每日"
            : $"{hmStart} – {endMin / 60:00}:{endMin % 60:00}" +
              (DataStore.GetInt(r["interval_min"], 0) > 0
                  ? $" · 每{DataStore.GetInt(r["interval_min"], 0)}分钟"
                  : " · 仅一次");
        if (past) meta += " · 已过期";

        var block = new Border
        {
            Background = fill,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 4, 6, 4),
            Opacity = on ? (past ? 0.45 : 1) : 0.4,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        stack.Children.Add(new TextBlock
        {
            Text = meta,
            FontSize = 11,
            Foreground = metaBrush,
            Margin = new Thickness(0, 1, 0, 0),
        });
        grid.Children.Add(stack);

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0,
        };
        var toggle = new Button
        {
            Style = (Style)FindResource("GhostIconButtonStyle"),
            Content = on ? "开" : "关",
            FontSize = 11,
            Width = 30,
            Height = 22,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0),
            ToolTip = on ? "关闭此提醒" : "开启此提醒",
        };
        if (on)
        {
            toggle.Background = (Brush)FindResource("AccentBrush");
            toggle.Foreground = Brushes.White;
        }
        else
        {
            toggle.Background = (Brush)FindResource("ElevatedBrush");
            toggle.Foreground = (Brush)FindResource("TextSecondaryBrush");
        }
        toggle.Click += (_, _) =>
        {
            r["enabled"] = !DataStore.GetBool(r["enabled"], true);
            _store.Save();
            Refresh();
        };
        var del = new Button
        {
            Style = (Style)FindResource("DangerIconButtonStyle"),
            Content = "✕",
            FontSize = 10,
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            ToolTip = "删除此提醒",
        };
        del.Click += (_, _) =>
        {
            var arr = GetReminders();
            for (int i = 0; i < arr.Count; i++)
                if (ReferenceEquals(arr[i], r)) { arr.RemoveAt(i); break; }
            _store.Save();
            Refresh();
        };
        btns.Children.Add(toggle);
        btns.Children.Add(del);
        Grid.SetColumn(btns, 1);
        grid.Children.Add(btns);

        block.MouseEnter += (_, _) => AnimOpacity(btns, 1, 120);
        block.MouseLeave += (_, _) => AnimOpacity(btns, 0, 100);
        block.Child = grid;
        return block;
    }

    // ------------------------------------------------------------ 左右丝滑切换（整体高斯模糊）

    private void SlideTo(DateTime date)
    {
        if (_animating || date.Date == _selectedDate.Date) return;
        int delta = (date.Date - _selectedDate.Date).Days;
        double w = slideRoot.ActualWidth;
        if (w <= 8) w = 760;

        _selectedDate = date.Date;
        UpdateStripSelection();

        var current = _showA ? _groupA : _groupB;
        var other = _showA ? _groupB : _groupA;
        var otherCols = BuildGroup(other, _selectedDate, DateTime.Today, GetReminders());
        if (_showA) _colB = otherCols; else _colA = otherCols;

        other.Visibility = Visibility.Visible;
        var tCur = new TranslateTransform();
        var tNew = new TranslateTransform();
        current.RenderTransform = tCur;
        other.RenderTransform = tNew;
        tCur.X = 0;
        tNew.X = delta > 0 ? w : -w;

        _animating = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // 滑动期间：整体高斯模糊，滑完恢复正常
        var blur = new BlurEffect { Radius = 8 };
        slideRoot.Effect = blur;

        double toCur = delta > 0 ? -w : w;
        var aCur = new DoubleAnimation(0, toCur, TimeSpan.FromMilliseconds(340)) { EasingFunction = ease };
        var aNew = new DoubleAnimation(tNew.X, 0, TimeSpan.FromMilliseconds(340)) { EasingFunction = ease };

        bool done = false;
        void Finish()
        {
            if (done) return;
            done = true;
            _showA = !_showA;
            _animating = false;
            var hidden = _showA ? _groupB : _groupA;
            var hiddenCols = _showA ? _colB : _colA;
            hidden.Visibility = Visibility.Collapsed;
            SetTranslate(hidden, 0);
            hiddenCols = BuildGroup(hidden, _selectedDate, DateTime.Today, GetReminders());
            if (_showA) _colA = hiddenCols; else _colB = hiddenCols;
            // 模糊 → 清晰
            var aBlur = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease };
            aBlur.Completed += (_, _) => slideRoot.Effect = null;
            blur.BeginAnimation(BlurEffect.RadiusProperty, aBlur);
            UpdateSummary();
        }
        aCur.Completed += (_, _) => Finish();
        aNew.Completed += (_, _) => Finish();
        tCur.BeginAnimation(TranslateTransform.XProperty, aCur);
        tNew.BeginAnimation(TranslateTransform.XProperty, aNew);
    }

    private static void SetTranslate(FrameworkElement el, double x)
    {
        if (el.RenderTransform is TranslateTransform t)
        {
            t.BeginAnimation(TranslateTransform.XProperty, null);
            t.X = x;
        }
        else
        {
            el.RenderTransform = new TranslateTransform(x, 0);
        }
    }

    private void PositionGroups()
    {
        double w = slideRoot.ActualWidth;
        if (w <= 0) return;
        _animating = false;
        slideRoot.Effect = null;
        _groupA.Width = w;
        _groupB.Width = w;
        SetTranslate(_groupA, 0);
        SetTranslate(_groupB, 0);
    }

    // ------------------------------------------------------------ 添加 / 今天

    private void Add_Click(object sender, RoutedEventArgs e) => OpenAddDialog(_selectedDate);

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        if (_animating) return;
        _centerDay = DateTime.Today;
        RebuildStrip(animate: true);
        SlideTo(DateTime.Today);
    }

    private void OpenAddDialog(DateTime date, int startMin = -1, int endMin = -1)
    {
        string? s = startMin >= 0 ? $"{startMin / 60:00}:{startMin % 60:00}" : null;
        string? e = endMin >= 0 ? $"{endMin / 60:00}:{endMin % 60:00}" : null;
        var dlg = new AddReminderDialog(_store, date, s, e);
        if (Window.GetWindow(this) is Window owner)
            dlg.Owner = owner;
        if (dlg.ShowDialog() == true && dlg.CreatedReminder is JsonObject created)
        {
            GetReminders().Add(created);
            _store.Save();
            Refresh();
        }
    }

    // ------------------------------------------------------------ 丝滑滚动（整表一个）

    private void SmoothScrollTo(ScrollViewer sv, double target)
    {
        target = Math.Clamp(target, 0, sv.ScrollableHeight);
        if (Math.Abs(target - sv.VerticalOffset) < 0.5) return;
        _scrollTicker ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scrollTicker.Stop();
        _scrollTarget = sv;
        _scrollFrom = sv.VerticalOffset;
        _scrollTo = target;
        _scrollStartMs = Environment.TickCount64;
        _scrollTicker.Tick -= ScrollTick;
        _scrollTicker.Tick += ScrollTick;
        _scrollTicker.Start();
    }

    private void ScrollTick(object? sender, EventArgs e)
    {
        double p = (Environment.TickCount64 - _scrollStartMs) / 320.0;
        if (p >= 1)
        {
            _scrollTicker!.Stop();
            _scrollTarget?.ScrollToVerticalOffset(_scrollTo);
            return;
        }
        double eased = 1 - Math.Pow(1 - p, 3);
        _scrollTarget?.ScrollToVerticalOffset(_scrollFrom + (_scrollTo - _scrollFrom) * eased);
    }

    /// <summary>表格卡片兜底：未被表格处理的滚轮一律吞掉，页面/窗口不会整块联动。</summary>
    private void TableCard_MouseWheel(object sender, MouseWheelEventArgs e) => e.Handled = true;

    // ------------------------------------------------------------ 未完成任务提醒配置

    private void UnfinishedCheck_Click(object sender, RoutedEventArgs e) => SaveUnfinishedConfig();

    private void IntervalStepper_Changed(int value) => SaveUnfinishedConfig();

    private void SaveUnfinishedConfig()
    {
        if (_loading) return;
        var cfg = DataStore.GetObj(_store.Data, "unfinished_reminder");
        if (cfg is null)
        {
            cfg = new JsonObject();
            _store.Data["unfinished_reminder"] = cfg;
        }
        cfg["enabled"] = unfinishedCheck.IsChecked == true;
        cfg["interval_min"] = Math.Clamp(intervalStepper.Value, 10, 180);
        _store.Save();
        _heartbeat.UpdateUnfinishedTimer();
    }

    private static void AnimOpacity(FrameworkElement el, double to, int ms)
    {
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}
