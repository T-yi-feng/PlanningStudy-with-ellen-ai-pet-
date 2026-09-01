using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;   // WinForms 同名冲突，全局未覆盖 → 文件级别名
using Panel = System.Windows.Controls.Panel;   // UseWindowsForms 下有 System.Windows.Forms.Panel，须文件级别名

namespace KaoyanPlanner.WPF.Controls;

/// <summary>
/// 统计页画廊：弹性焦点滚动 + 无限循环 + 松手对齐。
/// 焦点线在视口上方 25%：离它最近的卡「真高」最大（FocusHeight）且最亮，越远越矮（BaseHeight）越淡——
/// 高度与透明度随滚动逐帧缓动（不是整体缩放），卡内容填满槽位 → 高的卡自然露出更多行 = 显示更多信息。
/// 卡片复制 ≥3 份实现无极滚动（副本像素周期相同，跳转不可见）；停止滚动后把焦点卡中心自动对齐到焦点线。
/// 滚轮 / 鼠标拖拽驱动；指针悬在内层可滚动列表（计划分布 / 近 10 天）上时让事件传给内层 ScrollViewer。
/// reduce_motion：关动画，直接跳变、不做对齐动画。
/// </summary>
public sealed class FocusGallery : UserControl
{
    /// <summary>未聚焦槽高（px）。</summary>
    public double BaseHeight { get; set; } = 150.0;

    /// <summary>聚焦槽高（px）——更高的卡内容区更大，露出更多行。</summary>
    public double FocusHeight { get; set; } = 264.0;

    /// <summary>区块间距（px）。</summary>
    public double SlotGap { get; set; } = 10.0;

    /// <summary>焦点线 = 视口高度 × 该比例（距视口顶）。</summary>
    public double FocusLineRatio { get; set; } = 0.25;

    public bool ReduceMotion
    {
        get => _reduceMotion;
        set
        {
            if (_reduceMotion == value) return;
            _reduceMotion = value;
            _ticker.Stop();
            if (value) { SnapToTarget(); Layout(); }
        }
    }

    private const double OpacityBase = 0.90;   // 远离焦点的最小不透明度（高度已是主焦点提示，淡化轻微）
    private const double Sigma = 1.0;          // 高斯窗口 σ（步长单位）：dd=1 →0.37、dd=2 →0.02
    private const double WheelPxPerNotch = 84; // 一个滚轮刻度对应的滚动像素
    private const double SmoothTauMs = 130;    // 缓动时间常数（ms）→ 丝滑但跟手
    private const double FrameMs = 16;         // 帧间隔
    private static readonly double K = 1 - Math.Exp(-FrameMs / SmoothTauMs);

    private readonly Canvas _canvas;
    private readonly DispatcherTimer _ticker;                       // 帧循环：缓动偏移 + 缓动高度
    private readonly List<FrameworkElement> _slots = new();
    private readonly List<double> _heights = new();                 // 当前展示高度（缓动中）
    private readonly List<double> _targetHeights = new();           // 目标高度（窗口函数每帧重算）
    private readonly List<double> _tops = new();                    // 最近布局的卡顶（内容坐标）
    private IReadOnlyList<Func<UIElement>> _factories = Array.Empty<Func<UIElement>>();
    private bool _reduceMotion;
    private int _n;
    private int _copies = 3;
    private double _block;
    private double _targetOffset;                                  // 用户滚动目标（像素）
    private double _displayOffset;                                 // 帧循环展示的偏移
    private int _focusIdx;
    private bool _dragging;
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
        PreviewMouseMove += OnDragMove;
        PreviewMouseLeftButtonUp += OnDragEnd;
        SizeChanged += (_, _) => { if (_n == 0) return; RecomputeCopiesIfNeeded(); StartMotion(); };
    }

    /// <summary>
    /// 设置卡片（工厂形式，复制多份时各自 new 实例）。prevCount 相同（实时刷新）保留滚动位置与焦点；
    /// 数量变化（日/周切换）聚焦第二份首卡。数据为空 → 清空。
    /// </summary>
    public void SetCards(IReadOnlyList<Func<UIElement>> factories)
    {
        int prevN = _n;
        _factories = factories;
        _n = factories.Count;
        _ticker.Stop();
        _canvas.Children.Clear();
        _slots.Clear();
        _heights.Clear();
        _targetHeights.Clear();
        _tops.Clear();
        if (_n == 0) { _targetOffset = _displayOffset = 0; return; }

        double step = BaseHeight + SlotGap;
        _block = _n * step;
        double viewport = ActualHeight;
        _copies = viewport > 0 ? Math.Max(3, (int)Math.Ceiling(2.0 * viewport / _block)) : 3;
        BuildSlots();

        if (prevN == _n)
        {
            // 实时刷新（专注每 15s）：保留滚动位置与焦点
            ClampAndWrapTarget();
        }
        else
        {
            // 切换视图：第二份首卡中心对准焦点线
            _targetOffset = _displayOffset = _block + BaseHeight / 2 - ActualHeight * FocusLineRatio;
            ClampAndWrapTarget();
        }
        Layout();
        StartMotion();
    }

    private void BuildSlots()
    {
        for (int s = 0; s < _copies * _n; s++)
        {
            int idx = s % _n;
            var slot = new Border { ClipToBounds = true };
            Canvas.SetLeft(slot, 0);
            Canvas.SetTop(slot, 0);
            slot.Child = _factories[idx]();
            _canvas.Children.Add(slot);
            _slots.Add(slot);
            _heights.Add(BaseHeight);
            _targetHeights.Add(BaseHeight);
            _tops.Add(0);
        }
    }

    private void RecomputeCopiesIfNeeded()
    {
        if (_n == 0 || ActualHeight <= 0 || _block <= 0) return;
        int copies = Math.Max(3, (int)Math.Ceiling(2.0 * ActualHeight / _block));
        if (copies > _copies)
        {
            double keep = _displayOffset;
            _copies = copies;
            _canvas.Children.Clear();
            _slots.Clear();
            _heights.Clear();
            _targetHeights.Clear();
            _tops.Clear();
            BuildSlots();
            _targetOffset = _displayOffset = keep;
            ClampAndWrapTarget();
        }
    }

    // ------------------------------------------------------------ 帧循环

    private void OnTick(object? sender, EventArgs e)
    {
        if (_n == 0 || ActualHeight <= 0) { _ticker.Stop(); return; }

        UpdateTargets();   // 从当前展示偏移算目标高（窗口函数）

        // 1) 偏移缓动
        double dOff = _targetOffset - _displayOffset;
        if (Math.Abs(dOff) > 0.05) _displayOffset += dOff * K;
        else _displayOffset = _targetOffset;

        // 2) 高度缓动
        bool hMoving = false;
        for (int i = 0; i < _heights.Count; i++)
        {
            double d = _targetHeights[i] - _heights[i];
            if (Math.Abs(d) > 0.05) { _heights[i] += d * K; hMoving = true; }
            else _heights[i] = _targetHeights[i];
        }

        Layout();

        if (!hMoving && Math.Abs(dOff) <= 0.05)
        {
            // 稳定 → 把焦点卡中心对齐到焦点线（若已对齐则停）
            double target = SnapTarget();
            if (Math.Abs(target - _targetOffset) > 0.5) { _targetOffset = target; ClampAndWrapTarget(); }
            else _ticker.Stop();
        }
    }

    private void StartMotion()
    {
        if (_n == 0 || ActualHeight <= 0) return;
        if (ReduceMotion) { SnapToTarget(); Layout(); return; }
        if (!_ticker.IsEnabled) _ticker.Start();
    }

    /// <summary>reduce_motion / 稳定态：把展示值直接跳到目标（不动画）。</summary>
    private void SnapToTarget()
    {
        _displayOffset = _targetOffset;
        for (int i = 0; i < _heights.Count; i++) _heights[i] = _targetHeights[i];
    }

    /// <summary>目标高 = 高斯窗口 × 卡中心距焦点线的距离（两步逼近，收敛到「中心恰好在线」）。</summary>
    private void UpdateTargets()
    {
        int total = _slots.Count;
        double gap = SlotGap, baseH = BaseHeight, focusH = FocusHeight, step = baseH + gap;
        double L = ActualHeight * FocusLineRatio;
        double offset = _displayOffset;

        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < total; i++)
            {
                double center = _tops[i] + _heights[i] / 2;
                double dd = (center - (offset + L)) / step;
                _targetHeights[i] = baseH + (focusH - baseH) * Window(dd);
            }
            if (pass == 0)
                for (int i = 1; i < total; i++) _tops[i] = _tops[i - 1] + _targetHeights[i - 1] + gap;
        }
    }

    /// <summary>用展示高度布位（Canvas 绝对定位，避免 ScrollViewer 重入），设高/宽/透明度/Z 序。</summary>
    private void Layout()
    {
        double viewport = ActualHeight;
        if (_n == 0 || viewport <= 0) return;
        double gap = SlotGap, baseH = BaseHeight, focusH = FocusHeight;
        int total = _slots.Count;
        double offset = _displayOffset;

        // 归一化进有效带（副本周期 block，周期内像素相同，跳变不可见）
        double block = _n * (baseH + gap); _block = block;
        double lo = block * 0.5, hi = block * (_copies - 0.5);
        if (lo < hi)
        {
            while (offset < lo) offset += block;
            while (offset > hi) offset -= block;
            _displayOffset = offset;
        }

        _tops[0] = 0;
        for (int i = 1; i < total; i++) _tops[i] = _tops[i - 1] + _heights[i - 1] + gap;

        double cw = _canvas.ActualWidth > 0 ? _canvas.ActualWidth : ActualWidth;
        double L = viewport * FocusLineRatio;
        double best = double.MaxValue;
        int focus = 0;
        for (int i = 0; i < total; i++)
        {
            var slot = _slots[i];
            Canvas.SetTop(slot, _tops[i] - offset);
            slot.Width = cw;
            slot.Height = _heights[i];
            double w = focusH > baseH ? Math.Clamp((_heights[i] - baseH) / (focusH - baseH), 0, 1) : 0;
            slot.Opacity = OpacityBase + (1 - OpacityBase) * w;
            double dist = Math.Abs(_tops[i] + _heights[i] / 2 - (offset + L));
            if (dist < best) { best = dist; focus = i; }
        }
        _focusIdx = focus;
        for (int i = 0; i < total; i++)
            Panel.SetZIndex(_slots[i], i == focus ? 10 : 0);
    }

    /// <summary>焦点卡中心对准焦点线所需偏移。</summary>
    private double SnapTarget()
    {
        if (_focusIdx < 0 || _focusIdx >= _tops.Count) return _targetOffset;
        double L = ActualHeight * FocusLineRatio;
        return _tops[_focusIdx] + _heights[_focusIdx] / 2 - L;
    }

    // ------------------------------------------------------------ 输入

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_n == 0) return;
        if (InsideScrollableScrollViewer(e.OriginalSource as DependencyObject)) return;   // 内层列表先滚
        _targetOffset -= e.Delta / 120.0 * WheelPxPerNotch;
        e.Handled = true;
        ClampAndWrapTarget();
        StartMotion();
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (_n == 0) return;
        if (InsideScrollableScrollViewer(e.OriginalSource as DependencyObject)) return;   // 内层列表拖自己的
        _dragging = true;
        _dragLast = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        _targetOffset -= (p.Y - _dragLast.Y) * 1.2;
        _dragLast = p;
        ClampAndWrapTarget();
        StartMotion();
        e.Handled = true;
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>指针是否落在一个可滚动（有溢出）的内层 ScrollViewer 上——是则把滚轮/拖拽让给它。</summary>
    private static bool InsideScrollableScrollViewer(DependencyObject? src)
    {
        while (src is not null)
        {
            if (src is ScrollViewer sv && sv.ExtentHeight > sv.ViewportHeight + 1)
                return true;
            src = VisualTreeHelper.GetParent(src);
        }
        return false;
    }

    /// <summary>目标偏移钳进有效带；跨带时整体平移一份长（副本像素相同 → 无缝）。</summary>
    private void ClampAndWrapTarget()
    {
        if (_block <= 0) return;
        double lo = _block * 0.5, hi = _block * (_copies - 0.5);
        if (lo >= hi) return;
        while (_targetOffset < lo) { _targetOffset += _block; _displayOffset += _block; }
        while (_targetOffset > hi) { _targetOffset -= _block; _displayOffset -= _block; }
        _targetOffset = Math.Clamp(_targetOffset, lo, hi);
    }

    private static double Window(double dd)
    {
        double x = dd / Sigma;
        return Math.Exp(-x * x);   // 高斯：dd=0 →1（完全聚焦）、dd=σ →0.37、dd=2σ →0.02
    }
}
