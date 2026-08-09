"""主界面：无边框置顶浮窗，含今日计划 / 专注计时 / 提醒 / 专注统计四个页签。"""
import datetime
import time

import theme

from PyQt5.QtCore import Qt, QRectF, QTimer, QTime
from PyQt5.QtGui import QColor, QFont, QPainter
from PyQt5.QtWidgets import (
    QApplication, QCheckBox, QDateEdit, QDialog, QDialogButtonBox, QFormLayout,
    QFrame, QHBoxLayout, QInputDialog, QLabel, QLineEdit, QListWidget,
    QListWidgetItem, QMenu, QMessageBox, QProgressBar, QPushButton, QSpinBox,
    QTabWidget, QTimeEdit, QVBoxLayout, QWidget,
)

import chat
import notify
import pet
import storage

WEEKDAY_CN = ["星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日"]


def restyle(widget):
    """QSS 切换后刷新指定控件的样式。"""
    widget.style().unpolish(widget)
    widget.style().polish(widget)
    widget.update()


# ---------------------------------------------------------------- 弹窗提醒
class ReminderPopup(QFrame):
    """到点提醒的浮窗弹窗：右上角置顶、不抢焦点、自动关闭。"""

    _stack = []  # 当前在屏的弹窗

    def __init__(self, title, message, parent=None):
        super().__init__(parent)
        self.setObjectName("card")
        self.setWindowFlags(
            Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool
        )
        self.setAttribute(Qt.WA_ShowWithoutActivating, True)
        self.setFixedWidth(320)

        layout = QVBoxLayout(self)
        layout.setContentsMargins(14, 12, 14, 12)
        layout.setSpacing(8)

        bar = QFrame()
        bar.setFixedSize(40, 4)
        bar.setObjectName("accent_bar")
        layout.addWidget(bar)

        title_lbl = QLabel(title)
        title_lbl.setObjectName("title")
        title_lbl.setWordWrap(True)
        layout.addWidget(title_lbl)

        msg_lbl = QLabel(message)
        msg_lbl.setObjectName("subtitle")
        msg_lbl.setWordWrap(True)
        layout.addWidget(msg_lbl)

        self._timeout = QTimer(self)
        self._timeout.setSingleShot(True)
        self._timeout.timeout.connect(self.close)
        self._timeout.start(10000)

    def mousePressEvent(self, event):
        self.close()

    def show_float(self):
        """显示到屏幕右下角，多弹窗向上堆叠。"""
        self.adjustSize()
        screen = self.screen() if hasattr(self, "screen") else None
        if screen is None:
            screen = self.window().screen() if self.window() else None
        geo = screen.availableGeometry() if screen else None
        if geo is None:
            geo = self.window().frameGeometry() if self.window() else None

        x = geo.right() - self.width() - 16 if geo else 0
        y = geo.bottom() - self.height() - 16 if geo else 0
        # 向上堆叠
        for p in ReminderPopup._stack:
            if p.isVisible():
                y -= (p.height() + 8)
        ReminderPopup._stack.append(self)
        self.move(x, y)
        self.show()

    def closeEvent(self, event):
        if self in ReminderPopup._stack:
            ReminderPopup._stack.remove(self)
        super().closeEvent(event)


# ---------------------------------------------------------------- 今日计划任务行
class TaskItem(QWidget):
    def __init__(self, index, task, win, parent=None):
        super().__init__(parent)
        self.setObjectName("card")
        self._index = index
        self._task = task
        self._win = win

        layout = QHBoxLayout(self)
        layout.setContentsMargins(10, 6, 8, 6)
        layout.setSpacing(8)

        self.check = QCheckBox()
        self.check.setToolTip("标记完成")
        self.check.setChecked(bool(task.get("done")))
        self.check.toggled.connect(self._on_toggled)
        layout.addWidget(self.check)

        self.label = QLabel(task.get("text", ""))
        self.label.setWordWrap(True)
        self.label.setFont(QFont("Microsoft YaHei UI", 11))
        layout.addWidget(self.label, 1)

        self.edit_btn = QPushButton("✎")
        self.edit_btn.setObjectName("ghost_icon")
        self.edit_btn.setFixedSize(22, 22)
        self.edit_btn.setToolTip("编辑此任务")
        self.edit_btn.setCursor(Qt.PointingHandCursor)
        self.edit_btn.clicked.connect(self._on_edit)
        layout.addWidget(self.edit_btn)

        self.del_btn = QPushButton("✕")
        self.del_btn.setObjectName("danger_icon")
        self.del_btn.setFixedSize(22, 22)
        self.del_btn.setToolTip("删除此任务")
        self.del_btn.setCursor(Qt.PointingHandCursor)
        self.del_btn.clicked.connect(self._on_delete)
        layout.addWidget(self.del_btn)

        self._apply_done_state()

    def _apply_done_state(self):
        done = bool(self._task.get("done"))
        self.check.setChecked(done)
        f = self.label.font()
        f.setStrikeOut(done)
        self.label.setFont(f)
        if done:
            # 已完成：置灰并加删除线
            self.label.setStyleSheet("color: #8A8F98;")
        else:
            # 未完成：清除内联样式，使用主题默认文字颜色（color: transparent 会完全隐形）
            self.label.setStyleSheet("")

    def _on_toggled(self, checked):
        self._task["done"] = checked
        self._win.save()
        self._apply_done_state()
        self._win.update_header()

    def _on_delete(self):
        self._win.delete_task(self._index)

    def _on_edit(self):
        self._win.edit_task(self._index)

    def mouseDoubleClickEvent(self, event):
        if event.button() == Qt.LeftButton:
            self._on_edit()
            event.accept()
            return
        super().mouseDoubleClickEvent(event)

    def contextMenuEvent(self, event):
        menu = QMenu(self)
        edit_action = menu.addAction("编辑任务")
        done_action = menu.addAction("标记为未完成" if self._task.get("done") else "标记为完成")
        menu.addSeparator()
        del_action = menu.addAction("删除此任务")
        chosen = menu.exec_(event.globalPos())
        if chosen == edit_action:
            self._on_edit()
        elif chosen == done_action:
            self.check.setChecked(not self._task.get("done"))
        elif chosen == del_action:
            self._win.delete_task(self._index)


# ---------------------------------------------------------------- 今日计划页签
class PlanTab(QWidget):
    def __init__(self, win, parent=None):
        super().__init__(parent)
        self._win = win

        layout = QVBoxLayout(self)
        layout.setContentsMargins(4, 6, 4, 4)
        layout.setSpacing(8)

        row = QHBoxLayout()
        self.input = QLineEdit()
        self.input.setPlaceholderText("添加今日计划，回车即可…")
        self.input.returnPressed.connect(self._add)
        row.addWidget(self.input, 1)
        add_btn = QPushButton("添加")
        add_btn.setObjectName("primary")
        add_btn.clicked.connect(self._add)
        row.addWidget(add_btn)
        layout.addLayout(row)

        self.list = QListWidget()
        self.list.setSpacing(4)
        layout.addWidget(self.list, 1)

        self.empty_lbl = QLabel("今天还没有计划\n先写下一个最小可完成任务")
        self.empty_lbl.setObjectName("empty")
        self.empty_lbl.setAlignment(Qt.AlignCenter)
        layout.addWidget(self.empty_lbl, 1)

        bottom = QHBoxLayout()
        self.progress_lbl = QLabel("已完成 0/0")
        self.progress_lbl.setObjectName("subtitle")
        bottom.addWidget(self.progress_lbl)
        self.progress = QProgressBar()
        self.progress.setTextVisible(False)
        self.progress.setFixedHeight(8)
        bottom.addWidget(self.progress, 1)
        self.clear_done_btn = QPushButton("清理已完成")
        self.clear_done_btn.setObjectName("secondary")
        self.clear_done_btn.setToolTip("删除今天所有已完成任务")
        self.clear_done_btn.clicked.connect(self._clear_done)
        bottom.addWidget(self.clear_done_btn)
        layout.addLayout(bottom)

        self.refresh()

    def _add(self):
        text = self.input.text().strip()
        if not text:
            return
        day = storage.ensure_today(self._win.data)
        self._win.data["daily"][day].append({"text": text, "done": False})
        self._win.save()
        self.input.clear()
        self.refresh()
        self._win.update_header()

    def _clear_done(self):
        day = storage.today_str()
        tasks = self._win.data["daily"].get(day, [])
        done_count = sum(1 for t in tasks if t.get("done"))
        if done_count <= 0:
            return
        reply = QMessageBox.question(
            self,
            "清理已完成任务",
            f"确定删除今天 {done_count} 项已完成任务吗？",
            QMessageBox.Yes | QMessageBox.No,
            QMessageBox.No,
        )
        if reply != QMessageBox.Yes:
            return
        self._win.data["daily"][day] = [t for t in tasks if not t.get("done")]
        self._win.save()
        self.refresh()
        self._win.update_header()

    def refresh(self):
        day = storage.ensure_today(self._win.data)
        tasks = self._win.data["daily"][day]
        self.list.clear()
        for i, task in enumerate(tasks):
            item_widget = TaskItem(i, task, self._win)
            item = QListWidgetItem()
            item.setSizeHint(item_widget.sizeHint())
            self.list.addItem(item)
            self.list.setItemWidget(item, item_widget)

        done = sum(1 for t in tasks if t.get("done"))
        total = len(tasks)
        self.list.setVisible(total > 0)
        self.empty_lbl.setVisible(total == 0)
        self.progress_lbl.setText(f"已完成 {done}/{total}")
        self.progress.setRange(0, max(total, 1))
        self.progress.setValue(done)
        self.clear_done_btn.setEnabled(done > 0)


# ---------------------------------------------------------------- 专注计时页签（正计时）
class TimerTab(QWidget):
    """正计时：从 0 向上累加，专注分钟按小时写入 focus_history。

    - 每小时（可调）提醒喝口水休息一下，计时继续。
    - 使用 time.monotonic() 计算真实流逝时间，避免系统时钟/休眠漂移。
    """

    def __init__(self, win, parent=None):
        super().__init__(parent)
        self._win = win
        self._running = False
        self._elapsed = 0.0       # 累计专注秒数（暂停后保留）
        self._mark_mono = None    # 本次运行段开始的 monotonic 时间
        self._last_mono = None    # 上次 tick 的 monotonic 时间
        self._acc_sec = 0.0       # 尚未写入历史的秒数累计
        self._last_saved = 0.0    # 上次写盘的已专注秒数
        self._last_reminder_idx = 0  # 已触发过的喝水提醒序号

        cfg = self._win.data.setdefault("focus_reminder", {"enabled": True, "interval_min": 60})

        layout = QVBoxLayout(self)
        layout.setContentsMargins(8, 8, 8, 8)
        layout.setSpacing(10)

        self.time_lbl = QLabel("00:00:00")
        self.time_lbl.setObjectName("big_time")
        self.time_lbl.setAlignment(Qt.AlignCenter)
        layout.addWidget(self.time_lbl)

        self.status_lbl = QLabel("未开始 · 点击开始进入专注")
        self.status_lbl.setObjectName("status")
        self.status_lbl.setAlignment(Qt.AlignCenter)
        layout.addWidget(self.status_lbl)

        ctrl_row = QHBoxLayout()
        self.start_btn = QPushButton("开始")
        self.start_btn.setObjectName("primary")
        self.start_btn.setFixedHeight(36)
        self.start_btn.clicked.connect(self._toggle)
        ctrl_row.addWidget(self.start_btn, 1)
        reset_btn = QPushButton("重置")
        reset_btn.setObjectName("secondary")
        reset_btn.setFixedHeight(36)
        reset_btn.clicked.connect(self._reset)
        ctrl_row.addWidget(reset_btn, 1)
        layout.addLayout(ctrl_row)

        # 喝水/休息定时提醒：计时继续
        remind_row = QHBoxLayout()
        remind_row.setSpacing(6)
        self.remind_check = QCheckBox("定时提醒喝水休息")
        self.remind_check.setToolTip("专注过程中每隔设定时间提醒喝口水、活动一下，计时不会停")
        self.remind_check.setChecked(bool(cfg.get("enabled", True)))
        remind_row.addWidget(self.remind_check)
        remind_row.addStretch(1)
        every_lbl = QLabel("每")
        every_lbl.setObjectName("subtitle")
        remind_row.addWidget(every_lbl)
        self.remind_spin = QSpinBox()
        self.remind_spin.setRange(10, 240)
        self.remind_spin.setSuffix(" 分钟")
        self.remind_spin.setValue(int(cfg.get("interval_min", 60)))
        self.remind_spin.setToolTip("提醒间隔（分钟）")
        remind_row.addWidget(self.remind_spin)
        once_lbl = QLabel("提醒一次")
        once_lbl.setObjectName("subtitle")
        remind_row.addWidget(once_lbl)
        layout.addLayout(remind_row)

        # 今日专注统计
        stats_card = QFrame()
        stats_card.setObjectName("card")
        stats_layout = QVBoxLayout(stats_card)
        stats_layout.setContentsMargins(12, 10, 12, 10)
        stats_layout.setSpacing(8)

        stats_title_row = QHBoxLayout()
        stats_title = QLabel("今日专注统计")
        stats_title.setObjectName("title")
        stats_title_row.addWidget(stats_title)
        stats_title_row.addStretch(1)
        stats_btn = QPushButton("专注统计 ▸")
        stats_btn.setObjectName("secondary")
        stats_btn.setFixedHeight(26)
        stats_btn.setToolTip("查看按小时分布的专注直方图")
        stats_btn.clicked.connect(self._open_stats_tab)
        stats_title_row.addWidget(stats_btn)
        stats_layout.addLayout(stats_title_row)

        self.today_stats_lbl = QLabel("0 分钟 · 0 个活跃时段")
        self.today_stats_lbl.setObjectName("stat_value")
        stats_layout.addWidget(self.today_stats_lbl)
        layout.addWidget(stats_card)

        layout.addStretch(1)

        self._tick_timer = QTimer(self)
        self._tick_timer.setInterval(250)  # 250ms，显示平滑且漂移小
        self._tick_timer.timeout.connect(self._tick)

        self.remind_check.toggled.connect(self._remind_changed)
        self.remind_spin.valueChanged.connect(self._remind_changed)
        self.refresh_stats()

    # ---------- 工具 ----------
    def _fmt(self, seconds):
        h, rem = divmod(max(0, int(seconds)), 3600)
        m, s = divmod(rem, 60)
        return f"{h:02d}:{m:02d}:{s:02d}"

    # ---------- 控制 ----------
    def _toggle(self):
        if self._running:
            self._pause()
            return
        self._mark_mono = time.monotonic()
        self._last_mono = self._mark_mono
        self._running = True
        self._tick_timer.start()
        self.start_btn.setText("暂停")
        self.status_lbl.setText("专注中 · 按小时自动统计分布")
        self._win.save()

    def _pause(self):
        self._running = False
        self._tick_timer.stop()
        self._flush_pending()
        self._mark_mono = None
        self._last_mono = None
        self.start_btn.setText("继续")
        self.status_lbl.setText(f"已暂停 · 已专注 {int(self._elapsed) // 60} 分钟")
        self._win.save()
        self.refresh_stats()

    def _reset(self):
        self._running = False
        self._tick_timer.stop()
        self._flush_pending()
        self._mark_mono = None
        self._last_mono = None
        self._elapsed = 0.0
        self._acc_sec = 0.0
        self._last_saved = 0.0
        self._last_reminder_idx = 0
        self.time_lbl.setText("00:00:00")
        self.status_lbl.setText("未开始 · 点击开始进入专注")
        self.start_btn.setText("开始")
        self._win.save()
        self.refresh_stats()

    # ---------- 心跳 ----------
    def _tick(self):
        now = time.monotonic()
        delta = now - (self._last_mono or now)
        self._last_mono = now
        if delta < 0 or delta > 2.0:
            # 系统休眠/卡顿导致的异常跳变：忽略该段，不污染统计
            return
        self._elapsed += delta
        self._acc_sec += delta
        whole = int(self._acc_sec)
        if whole >= 1:
            self._acc_sec -= whole
            self._add_focus_seconds(datetime.datetime.now().hour, whole)

        self.time_lbl.setText(self._fmt(self._elapsed))

        # 每隔约 15 秒写一次盘，避免每 tick 都写文件
        if self._elapsed - self._last_saved >= 15.0:
            self._last_saved = self._elapsed
            self._win.save()

        self._check_focus_reminder()

    def _add_focus_seconds(self, hour, seconds):
        """把 seconds 秒累加到当天 hour 小时的专注分钟数。"""
        hist = self._win.data.setdefault("focus_history", {})
        day_hist = hist.setdefault(storage.today_str(), {})
        day_hist[str(hour)] = round(day_hist.get(str(hour), 0.0) + seconds / 60.0, 3)

    def _flush_pending(self):
        """暂停/重置时把尚未写入的整秒补写进历史。"""
        whole = int(self._acc_sec)
        if whole >= 1:
            self._acc_sec -= whole
            self._add_focus_seconds(datetime.datetime.now().hour, whole)

    def _check_focus_reminder(self):
        cfg = self._win.data.get("focus_reminder", {})
        if not cfg.get("enabled", True):
            return
        interval = max(1, int(cfg.get("interval_min", 60))) * 60
        idx = int(self._elapsed) // interval
        if idx > self._last_reminder_idx:
            self._last_reminder_idx = idx
            self._win.notify(
                "该休息一下了 ☕",
                f"已连续专注 {int(self._elapsed) // 60} 分钟，喝口水、活动一下再继续，计时不会停",
            )

    def _remind_changed(self):
        cfg = self._win.data.setdefault("focus_reminder", {"enabled": True, "interval_min": 60})
        cfg["enabled"] = self.remind_check.isChecked()
        cfg["interval_min"] = self.remind_spin.value()
        self._win.save()

    def refresh_stats(self):
        hist = self._win.data.setdefault("focus_history", {})
        day_hist = hist.get(storage.today_str(), {})
        total_min = int(round(sum(day_hist.values())))
        active = sum(1 for v in day_hist.values() if v > 0)
        self.today_stats_lbl.setText(f"{total_min} 分钟 · {active} 个活跃时段")

    def _open_stats_tab(self):
        self._win.tabs.setCurrentIndex(3)
        self._win.stats_tab.refresh()


# ---------------------------------------------------------------- 专注统计页签（直方图）
class FocusHistogram(QWidget):
    """专注时间直方图：X 轴为一天中的小时（0-23），Y 轴为专注分钟数。"""

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setMinimumHeight(190)
        self._minutes = [0.0] * 24

    def set_minutes(self, minutes_list):
        self._minutes = [max(0.0, float(x)) for x in minutes_list]
        self.update()

    def paintEvent(self, event):
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        dark = getattr(theme, "current", "light") == "dark"
        bg = QColor("#202228") if dark else QColor("#FBFAF7")
        track = QColor("#2C2F36") if dark else QColor("#EFEAE1")
        bar = QColor("#5B8DEF") if dark else QColor("#2563EB")
        axis = QColor("#555B66") if dark else QColor("#C3BBAE")
        text = QColor("#A9ADB7") if dark else QColor("#6E716C")

        w, h = self.width(), self.height()
        p.fillRect(self.rect(), bg)

        margin_l, margin_r, margin_t, margin_b = 28, 8, 20, 22
        plot_w = w - margin_l - margin_r
        plot_h = h - margin_t - margin_b
        if plot_w <= 0 or plot_h <= 0:
            return

        max_min = max(self._minutes) if self._minutes else 0
        if max_min <= 0:
            # 空数据提示
            p.setFont(QFont("Microsoft YaHei UI", 11))
            p.setPen(text)
            p.drawText(self.rect(), Qt.AlignCenter, "这一天还没有专注记录\n开始专注计时后自动统计")
            return

        # 底轴 + 左轴
        p.setPen(axis)
        p.drawLine(margin_l, margin_t + plot_h, margin_l + plot_w, margin_t + plot_h)
        p.drawLine(margin_l, margin_t, margin_l, margin_t + plot_h)

        # 每 2 小时画一条淡网格竖线
        p.setPen(QColor(axis.red(), axis.green(), axis.blue(), 60))
        for i in range(0, 24, 2):
            x = margin_l + i * plot_w / 24.0
            p.drawLine(int(x), margin_t, int(x), margin_t + plot_h)

        # 柱子
        bar_w = plot_w / 24.0
        gap = max(1.0, bar_w * 0.2)
        for i, minutes in enumerate(self._minutes):
            if minutes <= 0:
                continue
            x = margin_l + i * bar_w + gap / 2
            bar_h = max(2.0, (minutes / max_min) * (plot_h - 10))
            p.setPen(Qt.NoPen)
            p.setBrush(bar)
            p.drawRoundedRect(
                int(x), int(margin_t + plot_h - bar_h), int(bar_w - gap), int(bar_h), 2, 2
            )

        # X 轴小时刻度（每 2 小时标一次）
        p.setFont(QFont("Microsoft YaHei UI", 8))
        for i in range(0, 24, 2):
            x = margin_l + i * plot_w / 24.0
            p.setPen(text)
            p.drawText(
                QRectF(x - 12, margin_t + plot_h + 4, 24, 14), Qt.AlignCenter, str(i)
            )

        # Y 轴刻度：0 与最大值
        p.drawText(
            QRectF(0, margin_t + plot_h - 8, margin_l - 4, 16),
            Qt.AlignRight | Qt.AlignVCenter,
            "0",
        )
        p.drawText(
            QRectF(0, margin_t - 6, margin_l - 4, 16),
            Qt.AlignRight | Qt.AlignVCenter,
            f"{max_min:g}分",
        )

        # 当前小时高亮标记（仅今天）
        p.setPen(axis)
        now_h = datetime.datetime.now().hour
        x = margin_l + now_h * plot_w / 24.0 + bar_w / 2
        p.drawLine(int(x), margin_t + plot_h - 8, int(x), margin_t + plot_h + 2)

        p.end()


class StatsTab(QWidget):
    """专注统计页签：◀/▶ 切换日期，展示该天 24 小时专注分布直方图。"""

    def __init__(self, win, parent=None):
        super().__init__(parent)
        self._win = win
        self._day = datetime.date.today()

        layout = QVBoxLayout(self)
        layout.setContentsMargins(8, 8, 8, 8)
        layout.setSpacing(8)

        nav_row = QHBoxLayout()
        prev_btn = QPushButton("◀")
        prev_btn.setObjectName("secondary")
        prev_btn.setFixedSize(34, 28)
        prev_btn.setToolTip("前一天")
        prev_btn.clicked.connect(lambda: self._shift(-1))
        nav_row.addWidget(prev_btn)
        self.day_lbl = QLabel()
        self.day_lbl.setObjectName("pill_accent")
        self.day_lbl.setAlignment(Qt.AlignCenter)
        nav_row.addWidget(self.day_lbl, 1)
        self.next_btn = QPushButton("▶")
        self.next_btn.setObjectName("secondary")
        self.next_btn.setFixedSize(34, 28)
        self.next_btn.setToolTip("后一天")
        self.next_btn.clicked.connect(lambda: self._shift(1))
        nav_row.addWidget(self.next_btn)
        layout.addLayout(nav_row)

        self.histogram = FocusHistogram()
        layout.addWidget(self.histogram, 1)

        self.summary_lbl = QLabel()
        self.summary_lbl.setObjectName("status")
        self.summary_lbl.setAlignment(Qt.AlignCenter)
        layout.addWidget(self.summary_lbl)

        layout.addStretch(1)
        self.refresh()

    def _shift(self, delta):
        today = datetime.date.today()
        self._day = min(self._day + datetime.timedelta(days=delta), today)
        self.refresh()

    def refresh(self):
        today = datetime.date.today()
        self.day_lbl.setText(
            self._day.isoformat() + ("　·　今天" if self._day == today else "")
        )
        self.next_btn.setEnabled(self._day < today)

        hist = self._win.data.setdefault("focus_history", {})
        day_hist = hist.get(self._day.isoformat(), {})
        minutes = [float(day_hist.get(str(i), 0.0)) for i in range(24)]
        self.histogram.set_minutes(minutes)

        total = int(round(sum(minutes)))
        active = sum(1 for m in minutes if m > 0)
        if total <= 0:
            self.summary_lbl.setText("这一天还没有专注记录")
        else:
            peak = max(range(24), key=lambda i: minutes[i])
            self.summary_lbl.setText(
                f"共 {total} 分钟 · 分布在 {active} 个小时段 · 高峰时段 {peak}:00"
            )


# ---------------------------------------------------------------- 提醒页签
class ReminderRow(QWidget):
    def __init__(self, index, reminder, win, parent=None):
        super().__init__(parent)
        self.setObjectName("card")
        self._index = index
        self._reminder = reminder
        self._win = win

        layout = QHBoxLayout(self)
        layout.setContentsMargins(10, 6, 8, 6)
        layout.setSpacing(8)

        self.toggle_btn = QPushButton()
        self.toggle_btn.setFixedSize(36, 24)
        self.toggle_btn.setCursor(Qt.PointingHandCursor)
        self.toggle_btn.clicked.connect(self._toggle)
        layout.addWidget(self.toggle_btn)

        time_lbl = QLabel(reminder.get("time", "--:--"))
        time_lbl.setObjectName("accent")
        layout.addWidget(time_lbl)
        text_lbl = QLabel(reminder.get("label", ""))
        text_lbl.setWordWrap(True)
        layout.addWidget(text_lbl, 1)

        del_btn = QPushButton("✕")
        del_btn.setObjectName("danger_icon")
        del_btn.setFixedSize(22, 22)
        del_btn.setToolTip("删除此提醒")
        del_btn.setCursor(Qt.PointingHandCursor)
        del_btn.clicked.connect(lambda: self._win.delete_reminder(self._index))
        layout.addWidget(del_btn)

        self._apply_state()

    def _apply_state(self):
        on = bool(self._reminder.get("enabled", True))
        self.toggle_btn.setText("开" if on else "关")
        self.toggle_btn.setObjectName("toggle_on" if on else "toggle_off")
        restyle(self.toggle_btn)

    def _toggle(self):
        self._reminder["enabled"] = not self._reminder.get("enabled", True)
        self._win.save()
        self._apply_state()
        if hasattr(self._win, "reminder_tab"):
            self._win.reminder_tab.update_summary()


class ReminderTab(QWidget):
    def __init__(self, win, parent=None):
        super().__init__(parent)
        self._win = win

        layout = QVBoxLayout(self)
        layout.setContentsMargins(4, 6, 4, 4)
        layout.setSpacing(8)

        add_row = QHBoxLayout()
        self.time_edit = QTimeEdit()
        self.time_edit.setDisplayFormat("HH:mm")
        now = QTime.currentTime().addSecs(60)
        self.time_edit.setTime(now)
        self.time_edit.setFixedWidth(78)
        add_row.addWidget(self.time_edit)
        self.label_input = QLineEdit()
        self.label_input.setPlaceholderText("提醒内容，如：背单词")
        self.label_input.returnPressed.connect(self._add)
        add_row.addWidget(self.label_input, 1)
        add_btn = QPushButton("添加")
        add_btn.setObjectName("primary")
        add_btn.clicked.connect(self._add)
        add_row.addWidget(add_btn)
        layout.addLayout(add_row)

        self.list = QListWidget()
        self.list.setSpacing(4)
        layout.addWidget(self.list, 1)

        self.empty_lbl = QLabel("还没有提醒\n可以先设一个晚间复盘或背词提醒")
        self.empty_lbl.setObjectName("empty")
        self.empty_lbl.setAlignment(Qt.AlignCenter)
        layout.addWidget(self.empty_lbl, 1)

        self.next_lbl = QLabel()
        self.next_lbl.setObjectName("subtitle")
        layout.addWidget(self.next_lbl)

        # 未完成任务提醒：每 N 分钟检查当天待办，未完成则在右下角弹窗
        unfinished_row = QHBoxLayout()
        unfinished_row.setSpacing(6)
        cfg = self._win.data.setdefault(
            "unfinished_reminder", {"enabled": True, "interval_min": 60}
        )
        self.unfinished_check = QCheckBox("未完成任务提醒")
        self.unfinished_check.setToolTip("当天有待办未完成时，每隔设定时间在右下角弹窗")
        self.unfinished_check.setChecked(bool(cfg.get("enabled", True)))
        unfinished_row.addWidget(self.unfinished_check)
        unfinished_row.addStretch(1)
        every_lbl = QLabel("每")
        every_lbl.setObjectName("subtitle")
        unfinished_row.addWidget(every_lbl)
        self.interval_spin = QSpinBox()
        self.interval_spin.setRange(10, 180)
        self.interval_spin.setSuffix(" 分钟")
        self.interval_spin.setValue(int(cfg.get("interval_min", 30)))
        self.interval_spin.setToolTip("检查间隔（分钟）")
        unfinished_row.addWidget(self.interval_spin)
        remind_lbl = QLabel("弹窗提醒待办")
        remind_lbl.setObjectName("subtitle")
        unfinished_row.addWidget(remind_lbl)
        layout.addLayout(unfinished_row)

        self.unfinished_check.toggled.connect(self._unfinished_changed)
        self.interval_spin.valueChanged.connect(self._unfinished_changed)

        self._sort_reminders()
        self.refresh()

    def _sort_reminders(self):
        self._win.data["reminders"].sort(
            key=lambda r: (r.get("time", "99:99"), r.get("label", ""))
        )

    def _add(self):
        time_str = self.time_edit.time().toString("HH:mm")
        label = self.label_input.text().strip() or "提醒"
        for reminder in self._win.data["reminders"]:
            if reminder.get("time") == time_str and reminder.get("label") == label:
                QMessageBox.information(self, "添加提醒", "这个提醒已经存在。")
                return
        self._win.data["reminders"].append(
            {"time": time_str, "label": label, "enabled": True}
        )
        self._sort_reminders()
        self._win.save()
        self.label_input.clear()
        self.refresh()

    def refresh(self):
        self.list.clear()
        reminders = self._win.data["reminders"]
        for i, reminder in enumerate(reminders):
            row = ReminderRow(i, reminder, self._win)
            item = QListWidgetItem()
            item.setSizeHint(row.sizeHint())
            self.list.addItem(item)
            self.list.setItemWidget(item, row)
        self.list.setVisible(len(reminders) > 0)
        self.empty_lbl.setVisible(len(reminders) == 0)
        self.update_summary()

    def update_summary(self):
        enabled = [r for r in self._win.data["reminders"] if r.get("enabled", True)]
        if not enabled:
            self.next_lbl.setText("没有开启中的定时提醒")
            return

        now = datetime.datetime.now()
        candidates = []
        for reminder in enabled:
            try:
                hour, minute = [int(part) for part in reminder.get("time", "").split(":")]
                due = now.replace(hour=hour, minute=minute, second=0, microsecond=0)
            except ValueError:
                continue
            if due < now:
                due += datetime.timedelta(days=1)
            candidates.append((due, reminder))

        if not candidates:
            self.next_lbl.setText("没有可用的定时提醒")
            return
        due, reminder = min(candidates, key=lambda item: item[0])
        day_text = "今天" if due.date() == now.date() else "明天"
        self.next_lbl.setText(
            f"下次提醒：{day_text} {due.strftime('%H:%M')} · "
            f"{reminder.get('label', '提醒')}"
        )

    def _unfinished_changed(self):
        cfg = self._win.data.setdefault(
            "unfinished_reminder", {"enabled": True, "interval_min": 60}
        )
        cfg["enabled"] = self.unfinished_check.isChecked()
        cfg["interval_min"] = self.interval_spin.value()
        self._win.save()
        self._win._update_unfinished_timer()


# ---------------------------------------------------------------- 设置考试日期
class SettingsDialog(QDialog):
    def __init__(self, win, parent=None):
        super().__init__(parent)
        self._win = win
        self.setWindowTitle("设置")
        self.setModal(True)
        self.setMinimumWidth(320)

        layout = QVBoxLayout(self)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(10)

        form = QFormLayout()
        self.date_edit = QDateEdit()
        self.date_edit.setCalendarPopup(True)
        self.date_edit.setDisplayFormat("yyyy-MM-dd")
        exam_date = self._win.data.get("exam_date", "")
        if exam_date:
            try:
                d = datetime.date.fromisoformat(exam_date)
                self.date_edit.setDate(d)
            except ValueError:
                self.date_edit.setDate(datetime.date.today())
        else:
            self.date_edit.setDate(datetime.date.today())
        form.addRow("考研日期", self.date_edit)
        layout.addLayout(form)

        self.clear_btn = QPushButton("清除考试日期（只显示日历）")
        self.clear_btn.setObjectName("secondary")
        self.clear_btn.clicked.connect(self._clear)
        layout.addWidget(self.clear_btn)

        # ---- 桌宠 AI 对话 ----
        chat_cfg = self._win.data.setdefault("pet_chat", dict(chat.DEFAULT_PET_CHAT))
        chat_box = QFrame()
        chat_box.setObjectName("card")
        chat_lay = QVBoxLayout(chat_box)
        chat_lay.setContentsMargins(12, 10, 12, 10)
        chat_lay.setSpacing(8)
        chat_title = QLabel("桌宠 AI 对话")
        chat_title.setObjectName("title")
        chat_lay.addWidget(chat_title)
        self.chat_check = QCheckBox("启用 AI 对话（无 Key 时用本地回复）")
        self.chat_check.setChecked(bool(chat_cfg.get("enabled", True)))
        chat_lay.addWidget(self.chat_check)
        chat_form = QFormLayout()
        self.chat_url = QLineEdit(chat_cfg.get("base_url", ""))
        self.chat_model = QLineEdit(chat_cfg.get("model", ""))
        self.chat_key = QLineEdit(chat_cfg.get("api_key", ""))
        self.chat_key.setEchoMode(QLineEdit.Password)
        chat_form.addRow("API 地址", self.chat_url)
        chat_form.addRow("模型", self.chat_model)
        chat_form.addRow("API Key", self.chat_key)
        chat_lay.addLayout(chat_form)
        tip = QLabel(
            "已配置：智谱 GLM-4.6V-Flash（免费，支持图像识别，可识别你粘贴的图片）。"
            "在聊天框 Ctrl+V 粘贴图片即可发图给艾莲。留空 Key 时桌宠用本地回复，"
            "改计划功能照常可用。"
        )
        tip.setObjectName("subtitle")
        tip.setWordWrap(True)
        chat_lay.addWidget(tip)
        layout.addWidget(chat_box)

        buttons = QDialogButtonBox(QDialogButtonBox.Ok | QDialogButtonBox.Cancel)
        buttons.button(QDialogButtonBox.Ok).setText("确定")
        buttons.button(QDialogButtonBox.Cancel).setText("取消")
        buttons.accepted.connect(self._accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)

    def _save_chat(self):
        chat_cfg = self._win.data.setdefault("pet_chat", dict(chat.DEFAULT_PET_CHAT))
        chat_cfg["enabled"] = self.chat_check.isChecked()
        chat_cfg["base_url"] = self.chat_url.text().strip()
        chat_cfg["model"] = self.chat_model.text().strip()
        chat_cfg["api_key"] = self.chat_key.text().strip()
        self._win.save()

    def _clear(self):
        self._save_chat()
        self._win.data["exam_date"] = ""
        self._win.save()
        self._win.update_header()
        self.accept()

    def _accept(self):
        self._save_chat()
        d = self.date_edit.date().toPyDate()
        self._win.data["exam_date"] = d.isoformat()
        self._win.save()
        self._win.update_header()
        self.accept()


# ---------------------------------------------------------------- 主浮窗
class FloatWindow(QWidget):
    def __init__(self):
        super().__init__(None)
        self.data = storage.load_data()
        storage.ensure_today(self.data)

        self.setWindowFlags(
            Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool
        )
        self.setAttribute(Qt.WA_TranslucentBackground, False)
        self.setObjectName("float_window")
        self.setFixedWidth(320)

        self._collapsed = bool(self.data.get("collapsed", False))
        self._drag_offset = None
        self._fired_times = set()
        self._last_day = storage.today_str()
        self._allow_quit = False

        self._build_ui()

        # 桌宠：主窗口隐藏后显示
        self._pet = pet.PetWindow(self)
        self._pet.hide()
        self._pet_ever_shown = False

        # 恢复上次位置
        pos = self.data.get("window_pos")
        if isinstance(pos, list) and len(pos) == 2:
            self.move(pos[0], pos[1])

        self.update_header()
        self._apply_collapsed()

        # 全局心跳：驱动倒计时到点 + 提醒扫描
        self._heartbeat = QTimer(self)
        self._heartbeat.setInterval(1000)
        self._heartbeat.timeout.connect(self._on_heartbeat)
        self._heartbeat.start()

        # 未完成任务提醒：每 interval 分钟检查一次当天待办
        self._unfinished_timer = QTimer(self)
        self._unfinished_timer.timeout.connect(self._check_unfinished)
        self._update_unfinished_timer()
        # 启动后先快速检查一次，让用户立刻看到提醒是否生效
        QTimer.singleShot(8000, self._check_unfinished)

    # ---------- UI 构建 ----------
    def _build_ui(self):
        root = QVBoxLayout(self)
        root.setContentsMargins(10, 10, 10, 10)
        root.setSpacing(8)

        # 头部（可拖动区域）
        self.header = _DragArea(self)
        self.header.setObjectName("header")
        header_layout = QVBoxLayout(self.header)
        header_layout.setContentsMargins(12, 10, 12, 10)
        header_layout.setSpacing(8)

        top_row = QHBoxLayout()
        title = QLabel("📚 考研复习")
        title.setObjectName("title")
        top_row.addWidget(title)
        top_row.addStretch(1)

        self.collapse_btn = QPushButton("—" if not self._collapsed else "＋")
        self.collapse_btn.setObjectName("window_icon")
        self.collapse_btn.setFixedSize(26, 26)
        self.collapse_btn.setToolTip("折叠/展开")
        self.collapse_btn.setCursor(Qt.PointingHandCursor)
        self.collapse_btn.clicked.connect(self._toggle_collapsed)
        top_row.addWidget(self.collapse_btn)

        self.close_btn = QPushButton("×")
        self.close_btn.setObjectName("close_icon")
        self.close_btn.setFixedSize(26, 26)
        self.close_btn.setToolTip("隐藏到托盘")
        self.close_btn.setCursor(Qt.PointingHandCursor)
        self.close_btn.clicked.connect(self._collapse_to_circle)
        top_row.addWidget(self.close_btn)

        header_layout.addLayout(top_row)

        info_row = QHBoxLayout()
        self.countdown_lbl = QLabel()
        self.countdown_lbl.setObjectName("pill_accent")
        info_row.addWidget(self.countdown_lbl)
        info_row.addStretch(1)
        self.progress_lbl = QLabel()
        self.progress_lbl.setObjectName("pill")
        info_row.addWidget(self.progress_lbl)
        header_layout.addLayout(info_row)

        self.settings_btn = QPushButton("⚙ 设置")
        self.settings_btn.setObjectName("header_button")
        self.settings_btn.setFixedHeight(28)
        self.settings_btn.setCursor(Qt.PointingHandCursor)
        self.settings_btn.clicked.connect(self._open_settings)
        header_layout.addWidget(self.settings_btn)

        root.addWidget(self.header)

        # 页签
        self.tabs = QTabWidget()
        self.plan_tab = PlanTab(self)
        self.timer_tab = TimerTab(self)
        self.reminder_tab = ReminderTab(self)
        self.stats_tab = StatsTab(self)
        self.tabs.addTab(self.plan_tab, "今日计划")
        self.tabs.addTab(self.timer_tab, "专注计时")
        self.tabs.addTab(self.reminder_tab, "提醒")
        self.tabs.addTab(self.stats_tab, "专注统计")
        root.addWidget(self.tabs, 1)

    # ---------- 头部信息 ----------
    def update_header(self):
        today = datetime.date.today()
        exam_date = self.data.get("exam_date", "")
        if exam_date:
            try:
                delta = (datetime.date.fromisoformat(exam_date) - today).days
                if delta > 0:
                    self.countdown_lbl.setText(f"距考研还有 {delta} 天")
                elif delta == 0:
                    self.countdown_lbl.setText("今天就是考研日，冲！")
                else:
                    self.countdown_lbl.setText("考研已开始")
            except ValueError:
                self.countdown_lbl.setText(today.strftime("%Y-%m-%d"))
        else:
            self.countdown_lbl.setText(
                today.strftime("%Y-%m-%d") + " " + WEEKDAY_CN[today.weekday()]
            )

        tasks = self.data["daily"].get(storage.today_str(), [])
        done = sum(1 for t in tasks if t.get("done"))
        total = len(tasks)
        self.progress_lbl.setText(f"今日 {done}/{total}")

    # ---------- 折叠 ----------
    def _apply_collapsed(self):
        self.tabs.setVisible(not self._collapsed)
        self.settings_btn.setVisible(not self._collapsed)
        self.collapse_btn.setText("＋" if self._collapsed else "—")
        if self._collapsed:
            self.setFixedHeight(84)
            self.setToolTip("点击展开")
        else:
            self.setFixedHeight(480)
            self.setToolTip("")

    def _toggle_collapsed(self):
        self._collapsed = not self._collapsed
        self.data["collapsed"] = self._collapsed
        self.save()
        self._apply_collapsed()

    # ---------- 数据 ----------
    def save(self):
        storage.save_data(self.data)

    def delete_task(self, index):
        day = storage.today_str()
        tasks = self.data["daily"].get(day, [])
        if 0 <= index < len(tasks):
            text = tasks[index].get("text", "任务")
            reply = QMessageBox.question(
                self,
                "删除任务",
                f"确定删除「{text}」吗？",
                QMessageBox.Yes | QMessageBox.No,
                QMessageBox.No,
            )
            if reply != QMessageBox.Yes:
                return
            del tasks[index]
            self.save()
            self.plan_tab.refresh()
            self.update_header()

    def edit_task(self, index):
        day = storage.today_str()
        tasks = self.data["daily"].get(day, [])
        if not (0 <= index < len(tasks)):
            return
        old_text = tasks[index].get("text", "")
        new_text, ok = QInputDialog.getText(
            self,
            "编辑任务",
            "任务内容",
            QLineEdit.Normal,
            old_text,
        )
        if not ok:
            return
        new_text = new_text.strip()
        if not new_text:
            QMessageBox.information(self, "编辑任务", "任务内容不能为空。")
            return
        tasks[index]["text"] = new_text
        self.save()
        self.plan_tab.refresh()
        self.update_header()

    def delete_reminder(self, index):
        reminders = self.data["reminders"]
        if 0 <= index < len(reminders):
            del reminders[index]
            self.save()
            self.reminder_tab.refresh()

    def save_position(self):
        self.data["window_pos"] = [self.x(), self.y()]
        self.save()

    # ---------- 桌宠指令操作 ----------
    def pet_add_task(self, text):
        text = (text or "").strip()
        if not text:
            return "嗯？想加什么计划呀？直接说「添加 背单词」就好～"
        day = storage.ensure_today(self.data)
        self.data["daily"][day].append({"text": text, "done": False})
        self.save()
        self.plan_tab.refresh()
        self.update_header()
        return f"好，已经把「{text}」加进今天啦 ✅"

    def pet_complete_all(self):
        day = storage.today_str()
        tasks = self.data["daily"].get(day, [])
        pending = [t for t in tasks if not t.get("done")]
        if not pending:
            return "今天已经全部完成啦，太棒了！🏆"
        for t in pending:
            t["done"] = True
        self.save()
        self.plan_tab.refresh()
        self.update_header()
        return f"好耶，一次划掉 {len(pending)} 项！今天超高效 💪"

    def pet_complete_task(self, name):
        name = (name or "").strip()
        day = storage.today_str()
        tasks = self.data["daily"].get(day, [])
        pending = [t for t in tasks if not t.get("done")]
        if not pending:
            return "今天没有未完成的任务啦，很棒！"
        matched = [t for t in pending if name == t["text"] or (len(name) >= 2 and name in t["text"])]
        if not matched and len(name) >= 2:
            # 宽松匹配：任务文字包含用户输入中足够多的字
            matched = [
                t for t in pending
                if sum(1 for ch in name if ch in t["text"]) >= max(2, len(name) // 2)
            ]
        if not matched:
            return f"没找到「{name}」这个未完成任务。可以说「列出计划」看看有哪些～"
        if len(matched) > 1:
            names = "、".join(t["text"] for t in matched)
            return f"「{name}」匹配到好几项：{names}，能说得再具体点吗？"
        t = matched[0]
        t["done"] = True
        self.save()
        self.plan_tab.refresh()
        self.update_header()
        return f"搞定！「{t['text']}」已划掉 ✅"

    def pet_delete_task(self, name):
        name = (name or "").strip()
        day = storage.today_str()
        tasks = self.data["daily"].get(day, [])
        matched = [t for t in tasks if name == t["text"] or (len(name) >= 2 and name in t["text"])]
        if not matched and len(name) >= 2:
            matched = [
                t for t in tasks
                if sum(1 for ch in name if ch in t["text"]) >= max(2, len(name) // 2)
            ]
        if not matched:
            return f"没找到「{name}」，可以说「列出计划」看看～"
        if len(matched) > 1:
            names = "、".join(t["text"] for t in matched)
            return f"「{name}」匹配到好几项：{names}，删哪一项呢？"
        text = matched[0]["text"]
        tasks.remove(matched[0])
        self.save()
        self.plan_tab.refresh()
        self.update_header()
        return f"已删除「{text}」"

    def pet_list_tasks(self):
        day = storage.today_str()
        tasks = self.data["daily"].get(day, [])
        if not tasks:
            return "今天还没有计划，要不要加一条？跟我说「添加 背单词」就行～"
        pending = [t for t in tasks if not t.get("done")]
        done = [t for t in tasks if t.get("done")]
        lines = [f"今日共 {len(tasks)} 项，已完成 {len(done)} 项："]
        if pending:
            lines.append("未完成：")
            lines += [f"{i + 1}. {t['text']}" for i, t in enumerate(pending)]
        if done:
            done_names = "、".join(t["text"] for t in done[:5])
            lines.append(f"已完成：{done_names}{'…' if len(done) > 5 else ''}")
        return "\n".join(lines)

    # ---------- 专注计时控制（聊天指令） ----------
    def pet_start_focus(self):
        """聊天指令「开始专注」：真正启动正计时。"""
        tt = self.timer_tab
        if tt._running:
            return "已经在专注啦，继续保持！🎯"
        tt._mark_mono = time.monotonic()
        tt._last_mono = tt._mark_mono
        tt._running = True
        tt._tick_timer.start()
        tt.start_btn.setText("暂停")
        tt.status_lbl.setText("专注中 · 按小时自动统计分布")
        self.save()
        return "好，开始专注！时间从现在开始累计 🎯"

    def pet_pause_focus(self):
        """聊天指令「暂停专注」：暂停正计时（保留进度可继续）。"""
        tt = self.timer_tab
        if not tt._running:
            return "现在没有在专注哦～"
        tt._pause()
        return f"暂停啦，已专注 {int(tt._elapsed) // 60} 分钟，想继续就说「继续专注」"

    def pet_resume_focus(self):
        """聊天指令「继续专注」：从暂停处继续正计时（没进度则直接开始）。"""
        tt = self.timer_tab
        if tt._running:
            return "正在专注中呀～"
        tt._mark_mono = time.monotonic()
        tt._last_mono = tt._mark_mono
        tt._running = True
        tt._tick_timer.start()
        tt.start_btn.setText("暂停")
        tt.status_lbl.setText("专注中 · 按小时自动统计分布")
        self.save()
        if tt._elapsed > 0:
            return f"继续专注，已累计 {int(tt._elapsed) // 60} 分钟，加油！🎯"
        return "好，继续专注！🎯"

    def pet_reset_focus(self):
        """聊天指令「结束/停止专注」：清零本次计时。"""
        tt = self.timer_tab
        was = tt._elapsed
        tt._reset()
        if was:
            return f"结束专注，本次共专注 {int(was) // 60} 分钟，辛苦啦！"
        return "计时已清零，说「开始专注」随时再战～"

    # ---------- 提醒 ----------
    def notify(self, title, message):
        notify.play_alert_sound()
        notify.system_notify(title, message)
        popup = ReminderPopup(title, message, self)
        popup.show_float()

    def notify_float(self, title, message):
        """仅右下角浮窗弹窗（无系统通知、无提示音），用于未完成任务提醒。"""
        popup = ReminderPopup(title, message, self)
        popup.show_float()

    def _update_unfinished_timer(self):
        """按配置启停未完成任务提醒的定时器。"""
        cfg = self.data.setdefault(
            "unfinished_reminder", {"enabled": True, "interval_min": 60}
        )
        self._unfinished_timer.stop()
        if cfg.get("enabled", True):
            interval = max(1, int(cfg.get("interval_min", 30))) * 60 * 1000
            self._unfinished_timer.start(interval)

    def _check_unfinished(self):
        """每 N 分钟：若当天有待办未完成，右下角系统通知 + 弹窗 + 提示音。"""
        cfg = self.data.get("unfinished_reminder", {})
        if not cfg.get("enabled", True):
            return
        tasks = self.data["daily"].get(storage.today_str(), [])
        pending = [t for t in tasks if not t.get("done")]
        if not pending:
            return
        total = len(pending)
        lines = [f"{i + 1}. {t.get('text', '任务')}" for i, t in enumerate(pending[:3])]
        if total > 3:
            lines.append(f"… 还有 {total - 3} 项")
        # 用完整 notify（系统通知 + 声音 + 右下角浮窗），确保能被看到
        self.notify(f"今日还有 {total} 项待办未完成", "\n".join(lines))

    def _on_heartbeat(self):
        today = storage.today_str()
        if today != self._last_day:
            self._last_day = today
            self._fired_times.clear()
            self.plan_tab.refresh()
            self.timer_tab.refresh_stats()
            self.stats_tab._day = datetime.date.today()
            self.stats_tab.refresh()
            self.update_header()
        self._check_reminders()

    def _check_reminders(self):
        now = datetime.datetime.now()
        current = now.strftime("%H:%M")
        # 跨分钟清一次今日已触发记录，避免同一分钟重复
        key = (self._last_day, current)
        if key not in self._fired_times:
            self._fired_times.add(key)
            for reminder in self.data["reminders"]:
                if not reminder.get("enabled", True):
                    continue
                if reminder.get("time") == current:
                    self.notify("时间到 ⏰", reminder.get("label", "提醒"))

    # ---------- 折叠为桌宠 / 恢复 ----------
    def _collapse_to_circle(self):
        """隐藏主窗口，显示桌宠。"""
        self.hide()
        if not self._pet_ever_shown:
            self._pet.position_bottom_right()
            self._pet_ever_shown = True
        self._pet.show()
        self._pet.raise_()

    def restore_from_circle(self):
        """点击桌宠：关闭气泡/预览/聊天，恢复主窗口。"""
        self._pet._close_bubble()
        self._pet._close_preview()
        self._pet.hide()
        self.show()
        self.raise_()

    def toggle_visible(self):
        """显示/隐藏切换（托盘菜单用）。"""
        if self.isVisible():
            self._collapse_to_circle()
        else:
            self.restore_from_circle()

    def quit_now(self):
        """真正退出：关闭桌宠、允许关闭并保存位置。"""
        self._allow_quit = True
        self._pet._close_bubble()
        self._pet._close_preview()
        if self._pet._chat is not None:
            self._pet._chat.close()
        self._pet.hide()
        self.save_position()
        self.close()

    def closeEvent(self, event):
        self.save_position()
        if self._allow_quit:
            event.accept()
            return
        # Windows 已知怪癖：模态对话框（如设置）关闭后，会向宿主浮窗补发一次
        # closeEvent。这里统一忽略且不隐藏，避免浮窗无故消失；
        # 显式隐藏请用「×」按钮（_collapse_to_circle）或托盘菜单。
        event.ignore()

    # ---------- 设置 ----------
    def _open_settings(self):
        SettingsDialog(self).exec_()


# ---------------------------------------------------------------- 头部拖动区域
class _DragArea(QWidget):
    def __init__(self, win, parent=None):
        super().__init__(parent)
        self._win = win
        self._drag_offset = None

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            if self._win._collapsed:
                self._win._toggle_collapsed()
                return
            self._drag_offset = (
                event.globalPos() - self._win.frameGeometry().topLeft()
            )

    def mouseMoveEvent(self, event):
        if (
            self._drag_offset is not None
            and event.buttons() & Qt.LeftButton
            and not self._win._collapsed
        ):
            self._win.move(event.globalPos() - self._drag_offset)

    def mouseReleaseEvent(self, event):
        if self._drag_offset is not None:
            self._drag_offset = None
            self._win.save_position()
