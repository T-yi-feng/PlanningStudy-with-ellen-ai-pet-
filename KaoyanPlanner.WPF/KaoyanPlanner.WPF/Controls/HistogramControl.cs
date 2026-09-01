using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

// UseWindowsForms=true 下 System.Drawing 全局可见 → 显式别名到 WPF 版本
using Pen = System.Windows.Media.Pen;

namespace KaoyanPlanner.WPF.Controls;

/// <summary>
/// 24 小时专注分布柱状图：widgets.py FocusHistogram.paintEvent 的 WPF 移植。
/// FrameworkElement.OnRender(DrawingContext) 自绘：
/// 卡片底（骨白 Surface + 1px 发丝线）、边距 28/8/20/22、每 2h 网格线 60% alpha、
/// accent 柱 min 2px、每 2h X 标签、左上最大 / 左下 0 的 Y 标签、
/// 今天页标记当前小时竖线、空数据居中提示。
/// </summary>
public sealed class HistogramControl : FrameworkElement
{
    private double[] _minutes = new double[24];

    /// <summary>是否标记当前小时竖线（仅「今天」页为 true）。</summary>
    public bool MarkCurrentHour { get; set; } = true;

    public void SetMinutes(double[] minutes)
    {
        _minutes = minutes.Length == 24 ? minutes : new double[24];
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 卡片底：骨白 Surface + 1px 发丝线（对应 QColor(255,255,255,55) 的玻璃卡观感）
        dc.DrawRoundedRectangle(Res("SurfaceBrush"), new Pen(Res("BorderBrush"), 1),
            new Rect(0, 0, w, h), 10, 10);

        const double ml = 28, mt = 8, mr = 20, mb = 22;   // 左/上/右/下
        double plotW = w - ml - mr;
        double plotH = h - mt - mb;
        if (plotW <= 8 || plotH <= 8) return;

        double maxMin = _minutes.Max();
        if (maxMin <= 0)
        {
            DrawEmptyHint(dc, w, h, dpi);
            return;
        }

        double barW = plotW / 24.0;
        double gap = Math.Max(1.0, barW * 0.2);
        var axisPen = new Pen(Res("BorderStrongBrush"), 1);
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(150, 0xD7, 0xD1, 0xC2)), 1);   // 网格 60% alpha
        var barBrush = Res("AccentBrush");
        var textBrush = Res("TextMutedBrush");

        // 底部轴线
        dc.DrawLine(axisPen, new Point(ml, mt + plotH), new Point(ml + plotW, mt + plotH));

        // 每 2h 网格竖线 + X 标签
        for (int i = 0; i <= 22; i += 2)
        {
            double cx = ml + (i + 0.5) * barW;   // 该小时格中心
            dc.DrawLine(gridPen, new Point(cx, mt), new Point(cx, mt + plotH));
            DrawText(dc, i.ToString(CultureInfo.InvariantCulture), textBrush, 11,
                cx - 6, mt + plotH + 5, dpi);
        }

        // 柱：高 = max(2, (m/max)*(plotH-10))，中心对齐每格
        for (int i = 0; i < 24; i++)
        {
            double m = _minutes[i];
            if (m <= 0) continue;
            double barH = Math.Max(2.0, m / maxMin * (plotH - 10));
            double x = ml + i * barW + gap / 2;
            double y = mt + plotH - barH;
            dc.DrawRoundedRectangle(barBrush, null, new Rect(x, y, barW - gap, barH), 2, 2);
        }

        // 今天：当前小时高亮竖线
        if (MarkCurrentHour)
        {
            double cx = ml + (DateTime.Now.Hour + 0.5) * barW;
            dc.DrawRectangle(Res("AccentBrush"), null, new Rect(cx - 1, mt, 2, plotH + 2));
        }

        // Y 轴标签：左上最大 / 左下 0（max 格式：&lt;100 分用 :g，否则转小时 1 位小数）
        string maxLbl = maxMin < 100
            ? maxMin.ToString("G6", CultureInfo.InvariantCulture) + "分"
            : (maxMin / 60.0).ToString("0.0", CultureInfo.InvariantCulture) + "时";
        DrawText(dc, maxLbl, textBrush, 11, 3, mt - 2, dpi);
        DrawText(dc, "0", textBrush, 11, 3, mt + plotH - 16, dpi);
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
