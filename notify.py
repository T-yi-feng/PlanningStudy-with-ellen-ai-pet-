"""通知模块：Windows 系统通知 + 提示音。"""
import sys


def system_notify(title, message):
    """发送 Windows 系统通知。按可靠性降级：winotify → plyer → win10toast。

    返回 True 表示已发出系统通知；False 表示都不可用。
    """
    # 1) winotify：纯 WinRT toast，不依赖 pywin32，最可靠
    try:
        from winotify import Notification
        Notification(app_id="考研复习计划", title=title, msg=message, duration="short").show()
        return True
    except Exception:
        pass
    # 2) plyer：ctypes 气泡
    try:
        from plyer import notification
        notification.notify(
            title=title,
            message=message,
            app_name="考研计划",
            timeout=8,
        )
        return True
    except Exception:
        pass
    # 3) win10toast：老接口，与新 pywin32 有兼容问题，仅兜底
    try:
        from win10toast import ToastNotifier
        ToastNotifier().show_toast(title, message, duration=8, threaded=True)
        return True
    except Exception:
        return False


def play_alert_sound():
    """播放提醒提示音（Windows 系统提示音）。"""
    try:
        import winsound
        winsound.MessageBeep(winsound.MB_ICONASTERISK)
    except Exception:
        # 非 Windows 环境静默
        sys.stdout.write("\a")
        sys.stdout.flush()
