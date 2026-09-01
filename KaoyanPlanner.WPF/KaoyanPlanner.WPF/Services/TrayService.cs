using System.Windows;
using KaoyanPlanner.WPF.Views;
using KaoyanPlanner.WPF.Views.Pet;
using Forms = System.Windows.Forms;

namespace KaoyanPlanner.WPF.Services;

/// <summary>
/// 系统托盘：程序化「蓝底白勾」图标（对齐 main.py make_tray_icon）+ 菜单（显示/隐藏、退出）+ 双击切换。
/// 关闭按钮只隐藏主窗口；只有托盘「退出」真正结束进程（对齐旧版语义）。
/// 走 WinForms NotifyIcon（csproj 已 UseWindowsForms=true）。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly MainWindow _owner;
    private readonly PetWindow? _pet;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;

    public TrayService(MainWindow owner, PetWindow? pet = null)
    {
        _owner = owner;
        _pet = pet;

        _icon = new Forms.NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "考研复习计划",
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => Toggle();

        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("显示 / 隐藏 主窗口", null, (_, _) => Toggle());
        if (_pet is not null)
            _menu.Items.Add("显示 / 隐藏 桌宠", null, (_, _) => TogglePet());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => Quit());
        _icon.ContextMenuStrip = _menu;
    }

    /// <summary>托盘「显示/隐藏 桌宠」：艾莲独立于主窗口，可单独开关。</summary>
    private void TogglePet()
    {
        if (_pet is null) return;
        if (_pet.IsVisible) _pet.HidePet();
        else _pet.ShowPet();
    }

    public void Toggle()
    {
        if (_owner.IsVisible) _owner.Hide();
        else ShowMain();
    }

    /// <summary>从托盘还原主窗口（单实例唤醒也走这里）。</summary>
    public void ShowMain()
    {
        if (_owner.WindowState == WindowState.Minimized) _owner.WindowState = WindowState.Normal;
        _owner.Show();
        _owner.Activate();
    }

    /// <summary>托盘「退出」：放开 OnClosing 取消后才真正结束进程。</summary>
    public void Quit()
    {
        _owner.AllowClose = true;
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    // ------------------------------------------------------------ 图标（蓝底白勾，round cap 手绘对勾）

    private static System.Drawing.Icon MakeIcon()
    {
        using var bmp = new System.Drawing.Bitmap(64, 64);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x33, 0x9C, 0xFF));
            using var bg = RoundedRect(0, 0, 64, 64, 14);
            g.FillPath(fill, bg);
            using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 6f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
            };
            // ✓ 对勾：左下 → 中 → 右上
            g.DrawLines(pen, new[]
            {
                new System.Drawing.PointF(17, 35),
                new System.Drawing.PointF(28, 46),
                new System.Drawing.PointF(48, 20),
            });
        }
        // Icon.FromHandle 不拥有句柄 → Clone 出独立副本后再释放原始句柄
        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = System.Drawing.Icon.FromHandle(h);
            return (System.Drawing.Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        float d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
