using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KaoyanPlanner.WPF.Services;
// UseWindowsForms 全局导入 → 文件级别名指向 WPF
using Cursors = System.Windows.Input.Cursors;

namespace KaoyanPlanner.WPF.Controls;

/// <summary>
/// 小型日历（提醒用）：骨白圆角卡片、黑字、每页一个月；
/// 选中日期 = 蓝底圆角方块 + 白字；今天 = 蓝色描边圆；过去日期 = 灰字；
/// 顶部 ‹ 年月 › 可翻月，点标题弹出年/月选择器；可显示农历小字与「有提醒」标记点。
/// 月份切换带 160ms 淡入 + 横向滑动（尊重系统动效与 reduce_motion 由调用方判定）。
/// </summary>
public partial class MiniCalendar : UserControl
{
    private static readonly string[] WeekdayCn = { "日", "一", "二", "三", "四", "五", "六" };
    private const int CellMinHeight = 34;

    private sealed record CellRef(Border Root, Border SelOverlay, Border TodayRing, Border MarkDot, TextBlock DayText, TextBlock? LunarText);

    private readonly Dictionary<string, CellRef> _cells = new();
    private readonly Dictionary<string, Border> _markDots = new();
    private int _nav = 0;                 // -1 上月 / +1 下月 / 0 其他（动画方向）
    private bool _suppress;               // 程序化赋值不重播动画

    public MiniCalendar()
    {
        InitializeComponent();
        for (int i = 0; i < 7; i++)
            weekRow.Children.Add(new TextBlock
            {
                Text = WeekdayCn[i],
                FontSize = 11,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Height = 20,
            });
        DisplayedMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        Rebuild();
    }

    // ------------------------------------------------------------ 依赖属性

    public static readonly DependencyProperty SelectedDateProperty = DependencyProperty.Register(
        nameof(SelectedDate), typeof(DateTime?), typeof(MiniCalendar),
        new PropertyMetadata(null, (d, _) => ((MiniCalendar)d).UpdateSelection()));

    public static readonly DependencyProperty DisplayedMonthProperty = DependencyProperty.Register(
        nameof(DisplayedMonth), typeof(DateTime), typeof(MiniCalendar),
        new PropertyMetadata(DateTime.Today, (d, e) =>
        {
            var mc = (MiniCalendar)d;
            if ((DateTime)e.NewValue == (DateTime)e.OldValue) return;
            if (mc._suppress) return;
            mc.Rebuild();
        }));

    public static readonly DependencyProperty ShowLunarProperty = DependencyProperty.Register(
        nameof(ShowLunar), typeof(bool), typeof(MiniCalendar),
        new PropertyMetadata(false, (d, _) => ((MiniCalendar)d).Rebuild()));

    public static readonly DependencyProperty ShowOutsideDaysProperty = DependencyProperty.Register(
        nameof(ShowOutsideDays), typeof(bool), typeof(MiniCalendar),
        new PropertyMetadata(false, (d, _) => ((MiniCalendar)d).Rebuild()));

    public static readonly DependencyProperty MarkedDatesProperty = DependencyProperty.Register(
        nameof(MarkedDates), typeof(IReadOnlyCollection<string>), typeof(MiniCalendar),
        new PropertyMetadata(null, (d, _) => ((MiniCalendar)d).UpdateMarks()));

    /// <summary>选中日期（可空）。</summary>
    public DateTime? SelectedDate { get => (DateTime?)GetValue(SelectedDateProperty); set => SetValue(SelectedDateProperty, value); }

    /// <summary>当前展示的月份（该月 1 日）。</summary>
    public DateTime DisplayedMonth { get => (DateTime)GetValue(DisplayedMonthProperty); set => SetValue(DisplayedMonthProperty, value); }

    /// <summary>是否在日期下显示农历小字。</summary>
    public bool ShowLunar { get => (bool)GetValue(ShowLunarProperty); set => SetValue(ShowLunarProperty, value); }

    /// <summary>是否显示相邻月份的灰字日期（iPhone 式满 6 行网格）。</summary>
    public bool ShowOutsideDays { get => (bool)GetValue(ShowOutsideDaysProperty); set => SetValue(ShowOutsideDaysProperty, value); }

    /// <summary>有提醒的日期集合（yyyy-MM-dd），对应日期格下方显示蓝点。</summary>
    public IReadOnlyCollection<string>? MarkedDates { get => (IReadOnlyCollection<string>?)GetValue(MarkedDatesProperty); set => SetValue(MarkedDatesProperty, value); }

    /// <summary>用户点选某日（含相邻月日期）。</summary>
    public event Action<DateTime>? DateSelected;

    /// <summary>翻月/跳月完成。</summary>
    public event Action? MonthChanged;

    // ------------------------------------------------------------ 构建

    private DateTime MonthFirst => new(DisplayedMonth.Year, DisplayedMonth.Month, 1);

    private void Rebuild()
    {
        _cells.Clear();
        _markDots.Clear();
        dayHost.Children.Clear();
        dayHost.RowDefinitions.Clear();
        dayHost.ColumnDefinitions.Clear();
        for (int c = 0; c < 7; c++)
            dayHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var month = MonthFirst;
        int leading = (int)month.DayOfWeek;          // 0 = 周日
        int daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);
        int total = ShowOutsideDays ? 42 : (leading + daysInMonth + 6) / 7 * 7;
        int rows = total / 7;
        for (int r = 0; r < rows; r++)
            dayHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < total; i++)
        {
            var date = month.AddDays(i - leading);
            bool inMonth = date.Month == month.Month;
            if (!ShowOutsideDays && !inMonth)
            {
                dayHost.Children.Add(new Border());   // 占位
                continue;
            }
            var cellRef = BuildCell(date, inMonth);
            Grid.SetRow(cellRef.Root, i / 7);
            Grid.SetColumn(cellRef.Root, i % 7);
            dayHost.Children.Add(cellRef.Root);
            _cells[date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] = cellRef;
        }

        titleText.Text = $"{month.Year}年{month.Month}月";
        UpdateSelection();
        UpdateMarks();
        PlayEnter();
    }

    private CellRef BuildCell(DateTime date, bool inMonth)
    {
        var today = DateTime.Today;
        bool isToday = date.Date == today;
        bool isPast = date.Date < today;
        var accentStrong = (Brush)FindResource("AccentStrongBrush");
        var textPrimary = (Brush)FindResource("TextPrimaryBrush");
        var textMuted = (Brush)FindResource("TextMutedBrush");
        var textDone = (Brush)FindResource("TextDoneBrush");

        var selOverlay = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("AccentBrush"),
            IsHitTestVisible = false,
        };
        var todayRing = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            BorderBrush = accentStrong,
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        var markDot = new Border
        {
            Width = 4,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = (Brush)FindResource("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2),
            IsHitTestVisible = false,
        };
        var hoverOverlay = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("HoverFillBrush"),
            Opacity = 0,
            IsHitTestVisible = false,
        };

        var dayText = new TextBlock
        {
            Text = date.Day.ToString(CultureInfo.InvariantCulture),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition());
        TextBlock? lunarText = null;
        if (ShowLunar)
        {
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            lunarText = new TextBlock
            {
                Text = LunarCalendar.MonthDayCn(date),
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -2, 0, 0),
            };
            Grid.SetRow(lunarText, 1);
            content.Children.Add(lunarText);
        }
        Grid.SetRow(dayText, 0);
        content.Children.Add(dayText);

        var cell = new Border
        {
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(1.5),
            MinHeight = CellMinHeight,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
        };
        var layer = new Grid();
        layer.Children.Add(hoverOverlay);
        layer.Children.Add(selOverlay);
        layer.Children.Add(todayRing);
        layer.Children.Add(markDot);
        layer.Children.Add(content);
        cell.Child = layer;

        // 悬停：叠层淡入淡出（Opacity 动画，不共享画刷变色）
        cell.MouseEnter += (_, _) => AnimOpacity(hoverOverlay, 1, 120);
        cell.MouseLeave += (_, _) => AnimOpacity(hoverOverlay, 0, 100);

        DateTime clickDate = date;
        cell.MouseLeftButtonDown += (_, _) =>
        {
            if (!inMonth)
            {
                _nav = clickDate > MonthFirst ? 1 : -1;
                _suppress = true;
                DisplayedMonth = new DateTime(clickDate.Year, clickDate.Month, 1);
                _suppress = false;
                Rebuild();
            }
            SelectedDate = clickDate;
            DateSelected?.Invoke(clickDate);
        };

        var refCell = new CellRef(cell, selOverlay, todayRing, markDot, dayText, lunarText);
        ApplyCellVisual(refCell, date, inMonth, isToday, isPast);
        return refCell;
    }

    private void ApplyCellVisual(CellRef c, DateTime date, bool inMonth, bool isToday, bool isPast)
    {
        bool selected = SelectedDate is DateTime sd && sd.Date == date.Date;
        var accentStrong = (Brush)FindResource("AccentStrongBrush");
        var textPrimary = (Brush)FindResource("TextPrimaryBrush");
        var textMuted = (Brush)FindResource("TextMutedBrush");
        var textDone = (Brush)FindResource("TextDoneBrush");

        c.SelOverlay.Opacity = selected ? 1 : 0;
        c.TodayRing.Opacity = isToday && !selected ? 1 : 0;

        c.DayText.Foreground = selected ? Brushes.White
            : isPast || !inMonth ? textDone
            : isToday ? accentStrong
            : textPrimary;
        if (c.LunarText is not null)
            c.LunarText.Foreground = selected ? Brushes.White : textMuted;
    }

    private void UpdateSelection()
    {
        var today = DateTime.Today;
        foreach (var (key, cell) in _cells)
        {
            if (!DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;
            ApplyCellVisual(cell, date, date.Month == DisplayedMonth.Month, date.Date == today, date.Date < today);
        }
    }

    private void UpdateMarks()
    {
        foreach (var dot in _markDots.Values) dot.Opacity = 0;
        _markDots.Clear();
        if (MarkedDates is null) return;
        foreach (string key in MarkedDates)
        {
            if (_cells.TryGetValue(key, out var c))
            {
                c.MarkDot.Opacity = 1;
                _markDots[key] = c.MarkDot;
            }
        }
    }

    private void PlayEnter()
    {
        // 月份切换：内容根整体淡入 + 横向 14px 滑动（方向随 ‹ ›）
        if (!SystemParameters.ClientAreaAnimation) return;
        dayHost.Opacity = 0;
        var tt = new TranslateTransform(_nav == 1 ? -14 : 14, 0);
        dayHost.RenderTransform = tt;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        dayHost.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(_nav == 1 ? -14 : 14, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        _nav = 0;
        MonthChanged?.Invoke();
    }

    private static void AnimOpacity(FrameworkElement el, double to, int ms)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
    }

    // ------------------------------------------------------------ 头部按钮

    private void Prev_Click(object sender, RoutedEventArgs e)
    {
        _nav = -1;
        DisplayedMonth = MonthFirst.AddMonths(-1);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        _nav = 1;
        DisplayedMonth = MonthFirst.AddMonths(1);
    }

    private void Title_Click(object sender, RoutedEventArgs e)
    {
        ymYearText.Text = MonthFirst.Year.ToString(CultureInfo.InvariantCulture);
        ymMonths.Children.Clear();
        for (int m = 1; m <= 12; m++)
        {
            int month = m;
            var btn = new Button
            {
                Style = (Style)FindResource("GhostIconButtonStyle"),
                Content = $"{m}月",
                FontSize = 12,
                Margin = new Thickness(2),
                Padding = new Thickness(0, 6, 0, 6),
                Tag = month,
            };
            if (m == MonthFirst.Month && ymYearText.Text == MonthFirst.Year.ToString(CultureInfo.InvariantCulture))
                btn.Background = (Brush)FindResource("AccentSoftFillBrush");
            btn.Click += (_, _) =>
            {
                _nav = 0;
                ymPopup.IsOpen = false;
                _suppress = true;
                DisplayedMonth = new DateTime(int.Parse(ymYearText.Text, CultureInfo.InvariantCulture), month, 1);
                _suppress = false;
                Rebuild();
            };
            ymMonths.Children.Add(btn);
        }
        ymPopup.IsOpen = true;
    }

    private void YmPrevYear_Click(object sender, RoutedEventArgs e)
    {
        int y = int.Parse(ymYearText.Text, CultureInfo.InvariantCulture) - 1;
        if (y < 1900) y = 1900;
        ymYearText.Text = y.ToString(CultureInfo.InvariantCulture);
    }

    private void YmNextYear_Click(object sender, RoutedEventArgs e)
    {
        int y = int.Parse(ymYearText.Text, CultureInfo.InvariantCulture) + 1;
        if (y > 2100) y = 2100;
        ymYearText.Text = y.ToString(CultureInfo.InvariantCulture);
    }
}
