using System.Media;
using KaoyanPlanner.WPF.Views.Dialogs;

namespace KaoyanPlanner.WPF.Services;

/// <summary>
/// 通知：提示音 + 右下角浮窗弹窗（栈式堆叠）。
/// Notify = 完整提醒（声音+弹窗，定时提醒/未完成提醒用）；NotifyFloat = 仅弹窗（轻打扰场景）。
/// M6 接入托盘气泡（NotifyIcon.ShowBalloonTip）。
/// </summary>
public sealed class NotificationService
{
    public void Notify(string title, string message)
    {
        SystemSounds.Asterisk.Play();
        ShowPopup(title, message);
    }

    public void NotifyFloat(string title, string message)
        => ShowPopup(title, message);

    private static void ShowPopup(string title, string message)
        => new ReminderPopupWindow(title, message).Show();
}
