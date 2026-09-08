using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

// UseWindowsForms=true 下 System.Drawing 全局可见 → 显式别名到 WPF 版本
using Pen = System.Windows.Media.Pen;

namespace KaoyanPlanner.WPF.Controls;

/// <summary>
/// 24 小时专注分布柱状图（2 点日界版）：widgets.py FocusHistogram.paintEvent 的 WPF 移植，按凌晨 2 点日界重排。
/// 传入按日历小时键索引的 double[24]（focus_history[today] 的小时键），内部合并成 12 根
/// 「每根 = 2 小时」的柱：窗口日 D 的时序是 D 当天 02:00→24:00（键 2..23）+ 次日 00:00–02:00（键 0..1），
/// 故横轴从 2:00 起、到次日 2:00 收，标签为区间名（2-4 … 22-0 0-2）。
/// Y 轴固定满值 = 120 分钟（两小时），不随当日峰值缩放。
/// FrameworkElement.OnRender(DrawingContext) 自绘：卡片底、横/纵 2h 网格线 60% alpha、
/// accent 柱 min 2px、每柱下区间标签、左上最大 120分 / 左下 0 的 Y 标签、
/// 今天页标记当前时刻竖线、空数据居中提示。
/// </summary>
public sealed class HistogramControl : FrameworkElement
{
    private double[] _minutes = new double[24];

    /// <summary>是否标记当前时刻竖线（仅「今天」页为 true）。</summary>
    public bool MarkCurrentHour { get; set; } = true;

    public void SetMinutes(double[] minutes)
    {
        _minutes = minutes.Length == 24 ? minutes : new double[24];
        InvalidateVisual();
    }

    /// <summary>
    /// 纯逻辑：按日历小时键索引的 24 个值 → 12 个从凌晨 2 点起、每 2 小时一组的桶值。
    /// 小时键 h 落进桶 ((h+22) % 24) / 2（h=2,3→桶0 … h=22,23→桶10，h=0,1→桶11）。
    /// </summary>
    public static double[] TwoHourBuckets(double[] minutes24)
    {
        var outB = new double[12];
        if (minutes24.Length != 24) return outB;
        for (int i = 0; i < 24; i++)
            outB[((i + 22) % 24) / 2] += minutes24[i];
        return outB;
    }

    /// <summary>12 根柱的区间名标签：2-4 … 20-22、22-0、0-2（桶 b 起点 = (2+2b)%24，终点 = (4+2b)%24）。</summary>
    public static string[] TwoHourLabels()
    {
        var labels = new string[12];
        for (int b = 0; b < 12; b++)
            labels[b] = ((2 + 2 * b) % 24).ToString(CultureInfo.InvariantCulture) + "-"
                        + ((4 + 2 * b) % 24).ToString(CultureInfo.InvariantCulture);
        return labels;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 卡片底：骨白 Surface + 1px 发丝线
        dc.DrawRoundedRectangle(Res("SurfaceBrush"), new Pen(Res("BorderBrush"), 1),
            new Rect(0, 0, w, h), 10, 10);

        const double ml = 28, mt = 8, mr = 20, mb = 22;   // 左/上/右/下
        double plotW = w - ml - mr;
        double plotH = h - mt - mb;
        if (plotW <= 8 || plotH <= 8) return;

        double[] buckets = TwoHourBuckets(_minutes);
        if (buckets.All(v => v <= 0))
        {
            DrawEmptyHint(dc, w, h, dpi);
            return;
        }

        const double maxMin = 120;                       // Y 轴固定满值 = 2 小时
        double slotW = plotW / 12.0;
        double gap = Math.Max(1.0, slotW * 0.08);
        var axisPen = new Pen(Res("BorderStrongBrush"), 1);
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(150, 0xD7, 0xD1, 0xC2)), 1);   // 网格 60% alpha
        var barBrush = Res("AccentBrush");
        var textBrush = Res("TextMutedBrush");

        // 底部轴线
        dc.DrawLine(axisPen, new Point(ml, mt + plotH), new Point(ml + plotW, mt + plotH));

        // 每 2h 边界纵向网格线（隔开 12 桶）
        for (int i = 1; i < 12; i++)
        {
            double x = ml + i * slotW;
            dc.DrawLine(gridPen, new Point(x, mt), new Point(x, mt + plotH));
        }

        // 30/60/90 分钟横向网格线 + 左上 120分 / 左下 0 标签
        double[] marks = { 30, 60, 90 };
        foreach (double mm in marks)
        {
            double y = mt + plotH - mm / maxMin * plotH;
            dc.DrawLine(gridPen, new Point(ml, y), new Point(ml + plotW, y));
        }
        DrawText(dc, "120分", textBrush, 11, 3, mt - 2, dpi);
        DrawText(dc, "0", textBrush, 11, 3, mt + plotH - 16, dpi);

        // 柱：每桶一根，高 = max(2, m/120*(plotH-10))，中心对齐槽位
        for (int b = 0; b < 12; b++)
        {
            double m = buckets[b];
            if (m <= 0) continue;
            double barH = Math.Max(2.0, m / maxMin * (plotH - 10));
            double x = ml + b * slotW + gap / 2;
            double y = mt + plotH - barH;
            dc.DrawRoundedRectangle(barBrush, null, new Rect(x, y, slotW - gap, barH), 2, 2);
        }

        // 每柱下居中区间标签（2-4 … 22-0 0-2）
        string[] labels = TwoHourLabels();
        for (int b = 0; b < 12; b++)
        {
            double cx = ml + (b + 0.5) * slotW;
            var ft = new FormattedText(labels[b], CultureInfo.GetCultureInfo("zh-CN"),
                System.Windows.FlowDirection.LeftToRight, NewTypeface(), 10, textBrush, dpi);
            dc.DrawText(ft, new Point(cx - ft.Width / 2, mt + plotH + 6));
        }

        // 今天：当前时刻竖线（窗口日从凌晨 2 点起算）
        if (MarkCurrentHour)
        {
            var now = DateTime.Now;
            var winStart = (now.Hour < 2 ? now.Date.AddDays(-1) : now.Date).AddHours(2);
            double frac = (now - winStart).TotalMinutes / 1440.0;
            if (frac >= 0 && frac <= 1)
            {
                double cx = ml + frac * plotW;
                dc.DrawRectangle(Res("AccentBrush"), null, new Rect(cx - 1, mt, 2, plotH + 2));
            }
        }
    }

    private void DrawEmptyHint(DrawingContext dc, double w, double h, double dpi)
    {
        var brush = Res("TextMutedBrush");
        var typeface = NewTypeface();
        var ft0 = new FormattedText("这一天还没有专注记录", CultureInfo.GetCultureInfo("zh-CN"),
            System.Windows.FlowDirection.LeftToRight, typeface, 14, brush, dpi);
        var ft1 = new FormattedText("开始专注计时后自动统计", CultureInfo.GetCultureInfo("zh-CN"),
            System.Windows.FlowDirection.LeftToRight, typeface, 14, brush, dpi);
        double totalH = ft0.Height + ft1.Height + 4;
        dc.DrawText(ft0, new Point((w - ft0.Width) / 2, (h - totalH) / 2));
        dc.DrawText(ft1, new Point((w - ft1.Width) / 2, (h - totalH) / 2 + ft0.Height + 4));
    }

    private void DrawText(DrawingContext dc, string text, Brush brush, double size,
        double x, double y, double dpi)
    {
        var ft = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"),
            System.Windows.FlowDirection.LeftToRight, NewTypeface(), size, brush, dpi);
        dc.DrawText(ft, new Point(x, y));
    }

    private static Typeface NewTypeface()
        => new(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal,
            FontWeights.Normal, FontStretches.Normal);

    private static Brush Res(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
