using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;   // WinForms 同名冲突，全局未覆盖 → 文件级别名

namespace KaoyanPlanner.WPF.Controls;

/// <summary>
/// 统计页画廊：鼠标焦点缩放 + 有界滚动。
/// 「鼠标在哪，哪个卡就聚焦」：焦点卡 = 鼠标当前压着的卡（卡与卡之间的缝隙按最近中心判定），
/// 高度随「与焦点卡的间距」高斯衰减——焦点卡最高（FocusHeight）且最亮，越远越矮（BaseHeight）越淡；
/// 高度随鼠标移动逐帧缓动（不是跳变），卡内容填满槽位 → 高的卡自然露出更多行 = 显示更多信息。
/// 列表是**有界**的：从第一张卡顶滚到末张卡底即停，不做无限循环/回绕。
/// 滚轮 / 鼠标拖拽驱动；指针悬在内层可滚动列表（计划分布 / 近 10 天）上时把滚轮让给内层 ScrollViewer。
/// reduce_motion：关动画，直接跳变。
/// </summary>
public sealed class FocusGallery : UserControl
{
    /// <summary>未聚焦槽高（px）。</summary>
    public double BaseHeight { get; set; } = 150.0;

    /// <summary>聚焦槽高（px）——更高的卡内容区更大，露出更多行。</summary>
    public double FocusHeight { get; set; } = 264.0;

    /// <summary>区块间距（px）。</summary>
    public double SlotGap { get; set; } = 10.0;

    public bool ReduceMotion
    {
        get => _reduceMotion;
        set
        {
            if (_reduceMotion == value) return;
            _reduceMotion = value;
            _ticker.Stop();
            if (value) Snap();
            else StartMotion();
        }
    }

    private const double OpacityBase = 0.86;     // 非焦点卡的最小不透明度（高度已是主焦点提示，淡化轻微）
    private const double FalloffSigma = 1.2;     // 高斯窗口 σ（卡间距单位）：d=1 →0.50、d=2 →0.06
    private const double WheelPxPerNotch = 84;   // 一个滚轮刻度对应的滚动像素
    private const double DragSensitivity = 1.2;  // 拖拽位移 → 滚动像素倍率
    private const double SmoothTauMs = 130;      // 缓动时间常数（ms）→ 丝滑但跟手
    private const double FrameMs = 16;           // 帧间隔
    private static readonly double K = 1 - Math.Exp(-FrameMs / SmoothTauMs);

    private readonly Canvas _canvas;
    private readonly DispatcherTimer _ticker;                   // 帧循环：缓动偏移 + 缓动高度
    private readonly List<Border> _slots = new();
    private readonly List<double> _heights = new();             // 当前展示高度（缓动中）
    private readonly List<double> _tops = new();                // 各卡顶（内容坐标，最近一次布局）
    private IReadOnlyList<Func<UIElement>> _factories = Array.Empty<Func<UIElement>>();
    private bool _reduceMotion;
    private int _n;
    private double _targetScroll;                               // 用户滚动目标（像素，有界）
    private double _displayScroll;                              // 帧循环展示的滚动
    private int _focus;                                         // 当前焦点卡下标（默认 0 = 首卡）
    private bool _dragging;
    private bool _pointerInside;
    private double _pointerY;                                   // 指针 Y（控件坐标）
    private Point _dragLast;

    public FocusGallery()
    {
        _canvas = new Canvas();
        _ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(FrameMs),
            DispatcherPriority.Render, OnTick, Dispatcher);
        _ticker.Stop();
        ClipToBounds = true;
        Content = _canvas;
        PreviewMouseWheel += OnWheel;
        PreviewMouseLeftButtonDown += OnDragStart;
        PreviewMouseMove += OnMouseMove;
        PreviewMouseLeftButtonUp += OnDragEnd;
        MouseLeave += (_, _) => _pointerInside = false;
        SizeChanged += (_, _) => { if (_n == 0) return; Layout(); StartMotion(); };
    }

    /// <summary>
    /// 设置卡片（工厂形式，各卡独立实例）。prevCount 相同（实时刷新）保留滚动位置与焦点；
    /// 数量变化（日/周切换）聚焦首卡并回到顶。数据为空 → 清空。
    /// </summary>
    public void SetCards(IReadOnlyList<Func<UIElement>> factories)
    {
        int prevN = _n;
        _factories = factories;
        _n = factories.Count;
        _ticker.Stop();
        _canvas.Children.Clear();
        _slots.Clear();

        if (_n == 0)
        {
            _heights.Clear();
            _tops.Clear();
            _targetScroll = _displayScroll = 0;
            return;
        }

        if (prevN != _n)
        {
            // 切换视图：聚焦首卡、回到顶，高度直接给到静止目标（不从头缩放进场）
            _focus = 0;
            _targetScroll = _displayScroll = 0;
            _heights.Clear();
            for (int i = 0; i < _n; i++) _heights.Add(TargetHeight(i));
            _tops.Clear();
            for (int i = 0; i < _n; i++) _tops.Add(0);
        }
        else
        {
            // 实时刷新：保留滚动位置、焦点与当前缓动高度，只换卡内容
        }

        BuildSlots();
        ClampTargetScroll();
        Layout();
        StartMotion();
    }

    private void BuildSlots()
    {
        for (int s = 0; s < _n; s++)
        {
            var slot = new Border
            {
                ClipToBounds = true,
                Height = _heights[s],
                Child = _factories[s](),
            };
            Canvas.SetLeft(slot, 0);
            Canvas.SetTop(slot, 0);
            _canvas.Children.Add(slot);
            _slots.Add(slot);
        }
    }

    // ------------------------------------------------------------ 帧循环

    private void OnTick(object? sender, EventArgs e)
    {
        if (_n == 0 || ActualHeight <= 0) { _ticker.Stop(); return; }

        if (_pointerInside && !_dragging) HoverFromPointer();   // 内容滚动时焦点跟随指针

        // 1) 滚动缓动（有界）
        ClampTargetScroll();
        double dS = _targetScroll - _displayScroll;
        if (Math.Abs(dS) > 0.05) _displayScroll += dS * K;
        else _displayScroll = _targetScroll;

        // 2) 高度缓动
        bool settled = Math.Abs(dS) <= 0.05;
        for (int i = 0; i < _n; i++)
        {
            double th = TargetHeight(i);
            double d = th - _heights[i];
            if (Math.Abs(d) > 0.05) { _heights[i] += d * K; settled = false; }
            else _heights[i] = th;
        }

        Layout();

        if (settled) _ticker.Stop();
    }

    private void StartMotion()
    {
        if (_n == 0 || ActualHeight <= 0) return;
        if (ReduceMotion) { Snap(); return; }
        if (!_ticker.IsEnabled) _ticker.Start();
    }

    /// <summary>reduce_motion / 稳定态：把展示值直接跳到目标（不动画）。</summary>
    private void Snap()
    {
        if (_n == 0) return;
        ClampTargetScroll();
        _displayScroll = _targetScroll;
        for (int i = 0; i < _n; i++) _heights[i] = TargetHeight(i);
        Layout();
    }

    /// <summary>焦点卡的静止高度：与焦点卡相隔越远越矮（高斯衰减）。</summary>
    private double TargetHeight(int i)
    {
        double b = BaseHeight, f = FocusHeight;
        if (f <= b) return b;
        double d = Math.Abs(i - _focus);
        return b + (f - b) * Math.Exp(-(d * d) / (FalloffSigma * FalloffSigma));
    }

    /// <summary>可滚动的最大偏移：内容(高和+间距)超出视口的部分；不足视口则 0。</summary>
    private double MaxScroll()
    {
        double viewport = ActualHeight;
        if (_n == 0 || viewport <= 0) return 0;
        double content = 0;
        for (int i = 0; i < _n; i++) content += _heights[i];
        content += (_n - 1) * SlotGap;
        return Math.Max(0, content - viewport);
    }

    private void ClampTargetScroll()
        => _targetScroll = Math.Clamp(_targetScroll, 0, MaxScroll());

    /// <summary>用展示高度布位（Canvas 绝对定位），设高/宽/透明度。</summary>
    private void Layout()
    {
        double viewport = ActualHeight, cw = ActualWidth;
        if (_n == 0 || viewport <= 0 || cw <= 0) return;
        double gap = SlotGap;

        _tops.Clear();
        double acc = 0;
        for (int i = 0; i < _n; i++) { _tops.Add(acc); acc += _heights[i] + gap; }

        for (int i = 0; i < _n; i++)
        {
            var slot = _slots[i];
            Canvas.SetTop(slot, _tops[i] - _displayScroll);
            slot.Width = cw;
            slot.Height = _heights[i];
            double w = FocusHeight > BaseHeight
                ? Math.Clamp((_heights[i] - BaseHeight) / (FocusHeight - BaseHeight), 0, 1) : 0;
            slot.Opacity = OpacityBase + (1 - OpacityBase) * w;
        }
    }

    // ------------------------------------------------------------ 指针 → 焦点

    /// <summary>
    /// 焦点 = 指针压着的卡（按可见带判定，含半间距）；指针落在卡间缝隙/列表外空处时取中心最近的卡，
    /// 保证指针在画廊内始终有一张聚焦卡。
    /// </summary>
    private void HoverFromPointer()
    {
        if (_n == 0) return;
        double y = _pointerY;
        double gap = SlotGap;
        for (int i = 0; i < _n; i++)
        {
            double top = _tops[i] - _displayScroll;
            if (y >= top - gap / 2 && y <= top + _heights[i] + gap / 2) { _focus = i; return; }
        }
        // 缝隙 / 空区：取中心最近的卡
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < _n; i++)
        {
            double center = _tops[i] - _displayScroll + _heights[i] / 2;
            double d = Math.Abs(center - y);
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best >= 0) _focus = best;
    }

    // ------------------------------------------------------------ 输入

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_n == 0) return;
        if (InsideOverflowScrollViewer(e.OriginalSource as DependencyObject)) return;   // 内层列表先滚
        _targetScroll -= e.Delta / 120.0 * WheelPxPerNotch;
        e.Handled = true;
        StartMotion();
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (_n == 0) return;
        if (InsideOverflowScrollViewer(e.OriginalSource as DependencyObject)) return;   // 内层列表拖自己的
        _dragging = true;
        _dragLast = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        _pointerInside = true;
        _pointerY = p.Y;
        if (_dragging)
        {
            _targetScroll -= (p.Y - _dragLast.Y) * DragSensitivity;
            _dragLast = p;
            StartMotion();
            e.Handled = true;
            return;
        }
        HoverFromPointer();   // 焦点 = 指针所在卡；切换后高度随之缓动
        StartMotion();
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>指针是否落在一个可滚动（有溢出）的内层 ScrollViewer 上——是则把滚轮/拖拽让给它。</summary>
    private static bool InsideOverflowScrollViewer(DependencyObject? src)
    {
        while (src is not null)
        {
            if (src is ScrollViewer sv && sv.ExtentHeight > sv.ViewportHeight + 1)
                return true;
            src = VisualTreeHelper.GetParent(src);
        }
        return false;
    }
}
