using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KaoyanPlanner.WPF.Services;
// UseWindowsForms 会隐式导入 System.Windows.Forms → 命名冲突文件级别名
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using ColorConverter = System.Windows.Media.ColorConverter;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;

namespace KaoyanPlanner.WPF.Views.Pet;

/// <summary>
/// 字幕黑板：独立于对话气泡的常驻小窗，实时滚动显示媒体字幕（移植 blackboard.py）。
/// 黑色黑板渐变 + 木框 + 粉笔字；可拖动（拖到哪停哪）、可自由缩放（四边四角都能拉，
/// 字号保持设置值不变）；单击清空。与宠物气泡完全独立，互不影响。
/// 绘制全部走自定义 OnRender（黑渐变/木框/粉笔字/闪烁光标/右下角缩放点）。
/// </summary>
public partial class BlackboardWindow : Window
{
    private const int MinW = 280, MinH = 120;
    private const int ResizePad = 22;   // 右下角缩放热区大小
    private const int EdgePad = 8;      // 右/下边缘缩放热区宽度

    private string? _resizeZone;
    private bool _dragged;
    private bool _pressed;
    private double _startLeft, _startTop, _startW, _startH;
    private Point _startMouse;   // 按下时鼠标屏幕坐标

    public BlackboardWindow(Func<JsonObject?> getCfg)
    {
        InitializeComponent();
        board.Cfg = getCfg;
    }

    // ------------------------------------------------------------ 供 PetWindow 调用的 API

    /// <summary>状态提示（聆听中/错误/未配 Key）。</summary>
    public void SetStatus(string status) { board.Status = status; board.InvalidateVisual(); }

    /// <summary>语言标签（自动识别/中文…），刷新黑板头部。</summary>
    public void SetLanguageLabel(string label) { board.LangLabel = label; board.InvalidateVisual(); }

    /// <summary>半成品预览：替换当前“进行中”行，不新增（不刷屏、不重复）。</summary>
    public void ShowInterim(string text) => board.ShowInterim(text);

    /// <summary>整句识别完成：用定稿替换预览行；相同则只定稿；空文本则收起预览行。</summary>
    public void FinalizeInterim(string text) => board.FinalizeInterim(text);

    /// <summary>单击清空。</summary>
    public void ClearBoard() => board.Clear();

    /// <summary>字体/颜色/语言变化后刷新外观（保持当前窗口大小）。</summary>
    public void ApplySettings() => board.InvalidateVisual();

    /// <summary>定位到宠物右侧中部并显示（工作区边界内）。</summary>
    public void ShowNear(PetWindow pet)
    {
        double x = pet.Left + pet.ActualWidth + 14;
        double y = pet.Top + pet.ActualHeight / 2 - ActualHeight / 2;
        var wa = SystemParameters.WorkArea;
        x = Math.Max(wa.Left + 8, Math.Min(x, wa.Right - ActualWidth - 8));
        y = Math.Max(wa.Top + 8, Math.Min(y, wa.Bottom - ActualHeight - 8));
        Left = x;
        Top = y;
        Show();
    }

    // ------------------------------------------------------------ 交互：拖动 + 缩放

    private static string? HitZone(Point p, double w, double h)
    {
        double x = p.X, y = p.Y;
        if (x >= w - ResizePad && y >= h - ResizePad) return "br";
        if (x < ResizePad && y < ResizePad) return "tl";
        if (x >= w - ResizePad && y < ResizePad) return "tr";
        if (x < ResizePad && y >= h - ResizePad) return "bl";
        if (x >= w - EdgePad) return "r";
        if (x < EdgePad) return "l";
        if (y >= h - EdgePad) return "b";
        if (y < EdgePad) return "t";
        return null;
    }

    private static Cursor CursorFor(string? zone) => zone switch
    {
        "tl" or "br" => Cursors.SizeNWSE,
        "tr" or "bl" => Cursors.SizeNESW,
        "l" or "r" => Cursors.SizeWE,
        "t" or "b" => Cursors.SizeNS,
        _ => Cursors.Hand,
    };

    private void Board_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(board);
        _dragged = false;
        _pressed = true;
        _startMouse = board.PointToScreen(pos);   // 屏幕坐标（与 Left/Top/Width 同坐标系）
        _startLeft = Left; _startTop = Top; _startW = ActualWidth; _startH = ActualHeight;
        _resizeZone = HitZone(pos, ActualWidth, ActualHeight);
        board.CaptureMouse();
    }

    private void Board_MouseMove(object sender, MouseEventArgs e)
    {
        // 坑：必须用屏幕坐标，不能用 e.GetPosition(null)（窗口根相对坐标）。
        // 拖动/缩放时窗口位置在变，根坐标系跟着挪 → 差值恒带反馈 → 抖动。
        var g = board.PointToScreen(e.GetPosition(board));
        if (_resizeZone is not null)
        {
            _dragged = true;
            ApplyResize(g);
            return;
        }
        if (_pressed && e.LeftButton == MouseButtonState.Pressed)
        {
            _dragged = true;
            Left = _startLeft + (g.X - _startMouse.X);
            Top = _startTop + (g.Y - _startMouse.Y);
            return;
        }
        board.Cursor = CursorFor(HitZone(e.GetPosition(board), ActualWidth, ActualHeight));
    }

    private void ApplyResize(Point g)
    {
        double dx = g.X - _startMouse.X;
        double dy = g.Y - _startMouse.Y;
        double x = _startLeft, y = _startTop, w = _startW, h = _startH;
        string z = _resizeZone!;
        if (z.Contains("l"))
        {
            w = _startW - dx;
            if (w < MinW) { w = MinW; dx = _startW - MinW; }
            x = _startLeft + dx;
        }
        else if (z.Contains("r"))
        {
            w = Math.Max(MinW, _startW + dx);
        }
        if (z.Contains("t"))
        {
            h = _startH - dy;
            if (h < MinH) { h = MinH; dy = _startH - MinH; }
            y = _startTop + dy;
        }
        else if (z.Contains("b"))
        {
            h = Math.Max(MinH, _startH + dy);
        }
        Left = x; Top = y; Width = w; Height = h;
    }

    private void Board_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        bool wasDrag = _dragged;
        _resizeZone = null;
        _pressed = false;
        if (e.LeftButton == MouseButtonState.Released && !wasDrag)
            board.Clear();   // 单击（非拖拽/缩放）才清空黑板
        board.ReleaseMouseCapture();
    }
}

/// <summary>黑板绘制画布：全部外观用 OnRender 画，由 BlackboardWindow 驱动状态。</summary>
public sealed class BlackboardCanvas : FrameworkElement
{
    private const int MaxLines = 6;
    private const int Pad = 14;   // 内边距（blackboard.py 的 _MARGIN）
    private const int Frame = 7;
    private const string FontFace = "Microsoft YaHei UI";

    private readonly List<(string Ts, string Text)> _lines = new();   // 最新在前
    private bool _hasLive;                 // 是否有一条“半成品”字幕正在被持续替换
    private bool _cursorVisible = true;
    private double? _dpi;
    private readonly DispatcherTimer _cursorTimer;

    public BlackboardCanvas()
    {
        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _cursorTimer.Tick += (_, _) => { _cursorVisible = !_cursorVisible; InvalidateVisual(); };
    }

    internal Func<JsonObject?>? Cfg { get; set; }
    internal string Status = "🔇 未开启";
    internal string LangLabel = "自动识别";

    private double PixelsPerDip => _dpi ??= VisualTreeHelper.GetDpi(this).PixelsPerDip;

    // ------------------------------------------------------------ 数据

    public void AddLine(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        _hasLive = false;
        _cursorTimer.Stop();
        _lines.Insert(0, (DateTime.Now.ToString("HH:mm:ss"), text));
        Trim();
        InvalidateVisual();
    }

    public void ShowInterim(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        if (_hasLive)
            _lines[0] = (_lines[0].Ts, text);
        else
        {
            _lines.Insert(0, (DateTime.Now.ToString("HH:mm:ss"), text));
            Trim();
            _hasLive = true;
        }
        _cursorVisible = true;
        _cursorTimer.Start();   // 半成品存在期间光标持续闪烁
        InvalidateVisual();
    }

    public void FinalizeInterim(string text)
    {
        if (_hasLive)
        {
            if (!string.IsNullOrEmpty(text))
            {
                if (text != _lines[0].Text) _lines[0] = (_lines[0].Ts, text);
            }
            else
            {
                _lines.RemoveAt(0);
            }
            _hasLive = false;
            _cursorTimer.Stop();
        }
        else if (!string.IsNullOrEmpty(text))
        {
            AddLine(text);
        }
        InvalidateVisual();
    }

    public void Clear()
    {
        _lines.Clear();
        _hasLive = false;
        _cursorTimer.Stop();
        _cursorVisible = true;
        InvalidateVisual();
    }

    private void Trim()
    {
        while (_lines.Count > MaxLines) _lines.RemoveAt(_lines.Count - 1);
    }

    // ------------------------------------------------------------ 文本度量

    private FormattedText Fmt(string text, double size, FontWeight weight, Brush brush)
        => new(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
               new Typeface(new FontFamily(FontFace), FontStyles.Normal, weight, FontStretches.Normal),
               size, brush, PixelsPerDip);

    private double TextWidth(string text, double size, FontWeight weight)
        => Fmt(text, size, weight, Brushes.Black).Width;

    /// <summary>按像素宽度折行（逐字符），镜像 blackboard.py 的 _wrap。</summary>
    private static List<string> Wrap(string text, Func<string, double> widthOf, double maxw)
    {
        var out_ = new List<string>();
        string line = "";
        foreach (char ch in text)
        {
            if (widthOf(line + ch) > maxw)
            {
                if (line.Length > 0) { out_.Add(line); line = ch.ToString(); }
                else out_.Add(ch.ToString());
            }
            else
            {
                line += ch;
            }
        }
        if (line.Length > 0) out_.Add(line);
        return out_;
    }

    // ------------------------------------------------------------ 绘制

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var cfg = Cfg?.Invoke() ?? new JsonObject();
        string colorHex = DataStore.GetString(cfg["color"]);
        if (string.IsNullOrEmpty(colorHex)) colorHex = "#F5F0E6";
        var chalk = (Color)ColorConverter.ConvertFromString(colorHex);
        int size = Math.Max(9, (int)DataStore.GetInt(cfg["font_size"], 18));
        double lineH = size * 1.35 + 5;

        // 黑板底色（黑色渐变）
        var grad = new LinearGradientBrush(
            Color.FromRgb(0x22, 0x22, 0x22), Color.FromRgb(0x0A, 0x0A, 0x0A), 90);
        dc.DrawRoundedRectangle(grad, null, new Rect(0, 0, w, h), 14, 14);

        // 木框
        var wood = new Pen(new SolidColorBrush(Color.FromRgb(0x8A, 0x5A, 0x2B)), 3);
        dc.DrawRoundedRectangle(null, wood, new Rect(Frame - 1, Frame - 1, w - 2 * (Frame - 1), h - 2 * (Frame - 1)), 10, 10);
        var inner = new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)), 1);
        dc.DrawRoundedRectangle(null, inner, new Rect(Frame + 4, Frame + 4, w - 2 * (Frame + 4), h - 2 * (Frame + 4)), 7, 7);

        // 顶部状态行
        var header = Fmt($"📝 字幕 · {LangLabel}　{Status}", 11, FontWeights.Normal,
                         new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)));
        dc.DrawText(header, new Point(Pad + 4, Frame + 22 - header.Baseline));
        dc.DrawLine(inner, new Point(Pad, Frame + 32), new Point(w - Pad, Frame + 32));

        // 字幕行（最新在底部，旧字幕往上滚动，超出高度的只画放得下的）
        double maxw = w - 2 * Pad - 8;
        var grey = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A));
        var chalkBrush = new SolidColorBrush(chalk);
        double y = h - Pad;
        var rows = new List<(string Ts, List<string> Disp)>();   // (时间戳, 折行) 从新到旧
        foreach (var (ts, text) in _lines)
            rows.Add((ts, Wrap(text, t => TextWidth(t, size, FontWeights.Bold), maxw)));
        foreach (var (ts, disp) in rows)
        {
            for (int i = disp.Count - 1; i >= 0; i--)
            {
                if (y < Frame + 40) break;
                y -= lineH;
                dc.DrawText(Fmt(disp[i], size, FontWeights.Bold, chalkBrush), new Point(Pad + 4, y));
            }
            if (y < Frame + 40) break;
            y -= 14;
            int tsSize = Math.Max(9, size - 4);
            dc.DrawText(Fmt(ts, tsSize, FontWeights.Normal, grey), new Point(Pad + 4, y));
        }

        // 打字光标：半成品行末尾闪烁竖杠
        if (_hasLive && _cursorVisible && rows.Count > 0)
        {
            var disp = rows[0].Disp;
            double cx = Pad + 4 + TextWidth(disp[disp.Count - 1], size, FontWeights.Bold) + 3;
            double lineTop = h - Pad - lineH;
            var f = Fmt(disp[disp.Count - 1], size, FontWeights.Bold, chalkBrush);
            double topY = lineTop + 1;
            double botY = lineTop + f.Height - 1;
            dc.DrawLine(new Pen(chalkBrush, 2), new Point(cx, topY), new Point(cx, botY));
        }

        // 右下角缩放提示（三个小点）
        var dotPen = new Pen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), 1);
        for (int i = 0; i < 3; i++)
        {
            double x = w - 16 + i * 5;
            double yy = h - 16 + i * 5;
            dc.DrawEllipse(null, dotPen, new Point(x + 1, yy + 1), 1, 1);
        }
    }
}
