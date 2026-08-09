"""考研复习桌面浮窗计划工具 —— 入口。

运行：python main.py
单实例：重复启动不会开第二个进程，而是唤醒已运行的实例。
点「×」收起为圆形小浮窗，提醒仍会触发；托盘菜单可退出程序。
"""
import sys

from PyQt5.QtCore import Qt
from PyQt5.QtGui import QColor, QFont, QIcon, QPainter, QPixmap
from PyQt5.QtNetwork import QLocalServer, QLocalSocket
from PyQt5.QtWidgets import QApplication, QMenu, QSystemTrayIcon

import theme
from widgets import FloatWindow

_SINGLE_SOCKET = "KaoyanPlanner_SingleInstance"


class SingleInstance:
    """单实例锁：仅允许一个实例运行。

    - 启动时尝试连接既定管道名：能连上说明已有实例在运行 → 唤醒它并退出自己。
    - 连不上则自己创建服务器成为主实例；若创建失败（监听冲突）也退出，保证不重复。
    """

    def __init__(self):
        self._server = None

    def try_acquire(self):
        """尝试成为主实例。返回 True=本实例继续运行，False=已有实例、本实例应退出。"""
        probe = QLocalSocket()
        probe.connectToServer(_SINGLE_SOCKET)
        if probe.waitForConnected(300):
            # 已有实例在运行：发唤醒信号后退出
            probe.write(b"show")
            probe.flush()
            probe.disconnectFromServer()
            return False

        self._server = QLocalServer()
        self._server.removeServer(_SINGLE_SOCKET)  # 清理上次异常退出残留
        if self._server.listen(_SINGLE_SOCKET):
            return True
        # 监听失败：说明已有其他实例持有该管道 → 不重复启动
        return False

    def on_wake(self, callback):
        """有第二个实例启动时，主实例收到连接，调用 callback 恢复窗口。"""
        if self._server is None:
            return

        def _wake():
            conn = self._server.nextPendingConnection()
            if conn is None:
                return
            conn.readyRead.connect(lambda: conn.readAll())
            conn.disconnected.connect(conn.deleteLater)
            conn.write(b"ok")
            conn.flush()
            callback()

        self._server.newConnection.connect(_wake)


def make_tray_icon():
    """程序化生成托盘图标：蓝底白勾。"""
    pm = QPixmap(64, 64)
    pm.fill(Qt.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.Antialiasing)
    p.setBrush(QColor("#3B82F6"))
    p.setPen(Qt.NoPen)
    p.drawRoundedRect(0, 0, 64, 64, 14, 14)
    p.setPen(QColor("#FFFFFF"))
    font = QFont("Microsoft YaHei UI", 36, QFont.Bold)
    p.setFont(font)
    p.drawText(pm.rect(), Qt.AlignCenter, "✓")
    p.end()
    return QIcon(pm)


def main():
    app = QApplication(sys.argv)
    app.setApplicationName("考研复习计划")
    app.setFont(QFont("Microsoft YaHei UI", 10))
    # 主浮窗/桌宠是 Qt.Tool 窗口，不参与「最后一个窗口关闭即退出」判断。
    # 若不禁用，桌宠场景下设置对话框关闭会成为最后一个非 Tool 窗口，导致整个程序退出。
    app.setQuitOnLastWindowClosed(False)

    # 跟随系统主题
    theme.apply_theme(app, theme.detect_system_theme())

    # 单实例检查：已有实例则唤醒后退出
    single = SingleInstance()
    if not single.try_acquire():
        print("已有一个实例在运行，本实例退出。")
        sys.exit(0)

    win = FloatWindow()
    win.show()
    single.on_wake(win.restore_from_circle)

    # 系统托盘
    tray = QSystemTrayIcon(make_tray_icon(), app)
    tray.setToolTip("考研复习计划")

    menu = QMenu()
    toggle_action = menu.addAction("显示/隐藏 浮窗")
    menu.addSeparator()
    quit_action = menu.addAction("退出")

    def _toggle_window():
        # 显示↔折叠为圆形浮窗
        win.toggle_visible()

    toggle_action.triggered.connect(_toggle_window)
    quit_action.triggered.connect(win.quit_now)
    tray.setContextMenu(menu)
    tray.activated.connect(
        lambda reason: _toggle_window() if reason == QSystemTrayIcon.DoubleClick else None
    )
    tray.show()

    # 主题变化时实时刷新（QSS 全局已生效，这里只确保重绘）
    theme.start_theme_watcher(app, lambda t: app.processEvents())

    win.destroyed.connect(tray.hide)

    exit_code = app.exec_()
    win.quit_now()
    sys.exit(exit_code)


if __name__ == "__main__":
    main()
