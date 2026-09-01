using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace KaoyanPlanner.WPF.Native;

/// <summary>
/// Windows DWM / 窗口原生互操作（Win11 圆角、无激活弹窗等）。
/// </summary>
public static class DwmInterop
{
    // DWMWINDOWATTRIBUTE
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    // DWMWINDOWCORNERPREFERENCE
    private const int DWMWCP_DEFAULT = 0;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWCP_ROUND = 2;

    // 窗口扩展样式
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", CharSet = CharSet.Unicode)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private static IntPtr GetWindowLong(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : new IntPtr(SetWindowLong32(hWnd, nIndex, (int)dwNewLong));

    /// <summary>让无边框窗口四角圆角（Win11 DWM 原生圆角 + 原生阴影/缩放）。</summary>
    public static void ApplyRoundedCorners(Window window)
    {
        if (window == null) return;
        window.SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            int preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        };
    }

    /// <summary>加 WS_EX_NOACTIVATE：弹窗出现不抢焦点（提醒弹窗用）。</summary>
    public static void SetNoActivate(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            IntPtr style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, new IntPtr(style.ToInt64() | WS_EX_NOACTIVATE));
        };
    }

    /// <summary>加 WS_EX_TOOLWINDOW：不显示在 Alt-Tab（小浮窗用）。</summary>
    public static void SetToolWindow(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            IntPtr style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, new IntPtr(style.ToInt64() | WS_EX_TOOLWINDOW));
        };
    }
}
