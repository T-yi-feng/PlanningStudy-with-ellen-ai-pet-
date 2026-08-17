"""主界面：无边框置顶浮窗，含今日计划 / 专注计时 / 提醒 / 专注统计四个页签。"""
import datetime
import time

import theme

from PyQt5.QtCore import QEasingCurve, QPropertyAnimation, Qt, QRectF, QTimer, QTime
from PyQt5.QtGui import QColor, QFont, QPainter
from PyQt5.QtWidgets import (
    QAbstractItemView, QApplication, QCheckBox, QDateEdit, QDialog,
    QDialogButtonBox, QFormLayout, QFrame, QHBoxLayout, QInputDialog, QLabel,
    QLineEdit, QListWidget, QListWidgetItem, QMenu, QMessageBox, QProgressBar,
    QPushButton, QSpinBox, QTabWidget, QTimeEdit, QVBoxLayout, QWidget,
    QGraphicsOpacityEffect,
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


def fmt_minutes(minutes):
    """把专注分钟数显示得更友好：超过 100 分钟才转成「X 小时 Y 分钟」。"""
    m = int(round(float(minutes or 0)))
    if m <= 100:
        return f"{m} 分钟"
    h, rem = divmod(m, 60)
    if rem == 0:
        return f"{h} 小时"
    return f"{h} 小时 {rem} 分钟"


def fixed_completed(task):
    """固定任务视为已完成：打了 done 标记，或进度条已满（progress >= target_days）。

    进度条满了就说明目标天数已经达到，无论 done 标记是否同步，都应计入
    「已完成」——这同时修复了「进度条满但一键清理按钮不可用」的 bug。
    """
    return bool(task.get("done")) or int(task.get("progress", 0) or 0) >= max(
        1, int(task.get("target_days", 1) or 1)
    )


def fixed_punched_today(task):
    """固定任务「今日是否已打卡」：最近打卡日是今天，且真的打过。

    add_fixed 时 last_done_date 就记为今天（progress=0），所以必须 progress>0
    才能算「打过」，否则新建当天会误报成已打卡。
    """
    if fixed_completed(task):
        return True
    return (
        task.get("last_done_date") == storage.today_str()
        and int(task.get("progress", 0) or 0) > 0
    )


# ---------------------------------------------------------------- 弹窗提醒
class ReminderPopup(QFrame):
    """到点提醒的浮窗弹窗：右上角置顶、不抢焦点、自动关闭。"""

    _stack = []  # 当前在屏的弹窗

    def paintEvent(self, event):
        """透明顶层窗口的 QSS 背景不会画，玻璃卡片在这里手动画。"""
        theme.paint_glass_background(self, QPainter(self), "panel")
        super().paintEvent(event)

    def __init__(self, title, message, parent=None):
        super().__init__(parent)
        self.setObjectName("glass_popup")
        self.setWindowFlags(
            Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool
        )
        self.setAttribute(Qt.WA_ShowWithoutActivating, True)
        self.setAttribute(Qt.WA_TranslucentBackground, True)
        self.setAttribute(Qt.WA_StyledBackground, True)
        theme.try_acrylic(self)   # 玻璃弹窗：失败时用不透明渐变，不影响外观
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
    """一行任务。kind="once" 一次性；kind="fixed" 固定任务（每日固定=长期目标）。

    once 按索引路由；fixed 按稳定 id 路由。
    """

    def __init__(self, kind, task, win, index=None, tid=None, parent=None):
        super().__init__(parent)
        self.setObjectName("card")
        self.setAttribute(Qt.WA_StyledBackground, True)
        self._kind = kind
        self._task = task
        self._win = win
        self._index = index
        self._tid = tid

        outer = QVBoxLayout(self)
        outer.setContentsMargins(8, 3, 6, 3)
        outer.setSpacing(2)

        row = QHBoxLayout()
        row.setSpacing(6)
        outer.addLayout(row)

        self.check = QCheckBox()
        self.check.setToolTip("划去完成" if kind == "fixed" else "标记完成")
        self.check.setChecked(bool(task.get("done")))
        self.check.toggled.connect(self._on_toggled)
        row.addWidget(self.check)

        self.label = QLabel(task.get("text", ""))
        self.label.setWordWrap(True)
        self.label.setFont(QFont("Microsoft YaHei UI", 11))
        row.addWidget(self.label, 1)

        # 今日状态小标签
        self.today_tag = QLabel()
        self.today_tag.setCursor(Qt.PointingHandCursor)
        self.today_tag.setToolTip("今日是否完成")
        if kind == "once":
            # 一次性任务第一行有空位（无打卡按钮）：状态标签放这里，省掉整条进度行
            row.addWidget(self.today_tag)

        if kind == "fixed":
            self.punch_btn = QPushButton("打卡")
            self.punch_btn.setObjectName("secondary")
            self.punch_btn.setFixedHeight(24)
            self.punch_btn.setToolTip("今天完成一次；当天再点一次=补卡，抵消一天欠卡")
            self.punch_btn.setCursor(Qt.PointingHandCursor)
            self.punch_btn.clicked.connect(self._on_punch)
            row.addWidget(self.punch_btn)

        self.edit_btn = QPushButton("✎")
        self.edit_btn.setObjectName("ghost_icon")
        self.edit_btn.setFixedSize(22, 22)
        self.edit_btn.setToolTip("编辑此任务")
        self.edit_btn.setCursor(Qt.PointingHandCursor)
        self.edit_btn.clicked.connect(self._on_edit)
        row.addWidget(self.edit_btn)

        self.del_btn = QPushButton("✕")
        self.del_btn.setObjectName("danger_icon")
        self.del_btn.setFixedSize(22, 22)
        self.del_btn.setToolTip("删除此任务")
        self.del_btn.setCursor(Qt.PointingHandCursor)
        self.del_btn.clicked.connect(self._on_delete)
        row.addWidget(self.del_btn)

        # 进度行：固定任务 X/N + 今日状态标签；一次性任务由第一行标签表达，整行隐藏
        prog_row = QHBoxLayout()
        prog_row.setSpacing(6)
        outer.addLayout(prog_row)
        self.progress = QProgressBar()
        self.progress.setTextVisible(False)
        self.progress.setFixedHeight(6)
        self.frac_lbl = QLabel()
        self.frac_lbl.setObjectName("subtitle")
        if kind == "once":
            self.progress.hide()
            self.frac_lbl.hide()
            prog_row.addWidget(self.frac_lbl)
        else:
            prog_row.addWidget(self.today_tag)   # 固定任务第一行已被打卡/编辑/删除占满
            prog_row.addWidget(self.progress, 1)
            prog_row.addWidget(self.frac_lbl)

        self.desc_lbl = QLabel()
        self.desc_lbl.setObjectName("subtitle")
        self.desc_lbl.setWordWrap(True)
        self.desc_lbl.setVisible(False)
        outer.addWidget(self.desc_lbl)

        self._apply_done_state()

    def _apply_done_state(self):
        done = fixed_completed(self._task) if self._kind == "fixed" else bool(self._task.get("done"))
        self.check.setChecked(done)
        f = self.label.font()
        f.setStrikeOut(done)
        self.label.setFont(f)
        if done:
            # 已完成：置灰并加删除线
            self.label.setStyleSheet("color: #8A8F98;")
        else:
            # 未完成：清除内联样式，使用主题默认文字颜色
            self.label.setStyleSheet("")

        if self._kind == "once":
            self.progress.setRange(0, 1)
            self.progress.setValue(1 if done else 0)
            self.frac_lbl.setText("1/1" if done else "0/1")
        else:
            total = max(int(self._task.get("target_days", 1) or 1), 1)
            prog = int(self._task.get("progress", 0) or 0)
            self.progress.setRange(0, total)
            self.progress.setValue(min(prog, total))
            self.frac_lbl.setText(f"{prog}/{total}")
            desc = (self._task.get("desc") or "").strip()
            self.desc_lbl.setVisible(bool(desc))
            if desc:
                self.desc_lbl.setText(desc)

        # 今日状态标签文案 + 样式（一次性看 done；固定任务看「今日是否已打卡」）
        if self._kind == "fixed":
            if fixed_completed(self._task):
                text, obj = "✓ 已完成", "today_tag_done"
            elif fixed_punched_today(self._task):
                text, obj = "✓ 今日已打卡", "today_tag_done"
            elif self._win.data.get("frozen"):
                text, obj = "❄ 冻结中", "today_tag_todo"
            else:
                text, obj = "○ 今日未打卡", "today_tag_todo"
        else:
            text, obj = ("✓ 已完成", "today_tag_done") if done else ("○ 未完成", "today_tag_todo")
        self.today_tag.setText(text)
        if self.today_tag.objectName() != obj:
            self.today_tag.setObjectName(obj)
            restyle(self.today_tag)

        # 冻结：固定任务禁用打卡与勾选（已完成的保留操作，方便删除）
        if self._kind == "fixed":
            disabled = bool(self._win.data.get("frozen")) and not done
            self.punch_btn.setEnabled(not disabled)
            self.check.setEnabled(not disabled)

    def _on_toggled(self, checked):
        if self._kind == "fixed" and self._tid is not None:
            task = storage.find_fixed(self._win.data, self._tid)
            if task is None:
                return
            task["done"] = checked
            if checked:
                task["owed"] = 0   # 提前划去完成，欠卡清零
            self._win.save()
            self._apply_done_state()
            self._win.update_header()
            return
        self._task["done"] = checked
        self._win.save()
        self._apply_done_state()
        self._win.update_header()

    def _on_punch(self):
        if self._kind != "fixed" or self._tid is None:
            return
        if self._win.data.get("frozen"):
            return
        self._win.punch_fixed_task(self._tid)

    def _on_delete(self):
        if self._kind == "fixed" and self._tid is not None:
            self._win.delete_fixed_task(self._tid)
        else:
            self._win.delete_task(self._index)

    def _on_edit(self):
        if self._kind == "fixed" and self._tid is not None:
            self._win.edit_fixed_task(self._tid)
        else:
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
            if self._kind == "fixed" and self._win.data.get("frozen") and not self._task.get("done"):
                pass   # 冻结中不允许手动划去未完成固定任务
            else:
                self.check.setChecked(not self._task.get("done"))
        elif chosen == del_action:
            self._on_delete()


# ---------------------------------------------------------------- 今日计划页签
class FixedTaskDialog(QDialog):
    """添加/编辑固定任务：内容 + 可选简介 + 需几天完成。"""

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("添加固定任务")
        self.setMinimumWidth(320)
        form = QFormLayout(self)
        form.setSpacing(10)

        self.text_edit = QLineEdit()
        self.text_edit.setPlaceholderText("如：背单词")
        form.addRow("内容", self.text_edit)

        self.desc_edit = QLineEdit()
        self.desc_edit.setPlaceholderText("可选，如：每天 50 个")
        form.addRow("简介", self.desc_edit)

        self.days_spin = QSpinBox()
        self.days_spin.setRange(1, 9999)
        self.days_spin.setValue(1)
        self.days_spin.setSuffix(" 天")
        form.addRow("需几天完成", self.days_spin)

        btns = QDialogButtonBox(QDialogButtonBox.Ok | QDialogButtonBox.Cancel)
        btns.accepted.connect(self.accept)
        btns.rejected.connect(self.reject)
        form.addRow(btns)

        self.text_edit.setFocus()

    def result_data(self):
        return self.text_edit.text().strip(), self.desc_edit.text().strip(), self.days_spin.value()


class PlanTab(QWidget):
    def __init__(self, win, parent=None):
        super().__init__(parent)
        self._win = win

        layout = QVBoxLayout(self)
        layout.setContentsMargins(4, 5, 4, 2)
        layout.setSpacing(6)

        row = QHBoxLayout()
        self.input = QLineEdit()
        self.input.setPlaceholderText("添加今日计划，回车即可…")
        self.input.returnPressed.connect(self._add)
        row.addWidget(self.input, 1)
        add_btn = QPushButton("添加")
        add_btn.setObjectName("primary")
        add_btn.clicked.connect(self._add)
        row.addWidget(add_btn)
        fixed_btn = QPushButton("＋固定")
        fixed_btn.setObjectName("secondary")
        fixed_btn.setToolTip("添加固定任务（每日固定 = 长期目标）：可加简介、可设需几天完成")
        fixed_btn.clicked.connect(self._add_fixed)
        row.addWidget(fixed_btn)
        layout.addLayout(row)

        # 冻结 / 欠卡横幅
        self.banner_lbl = QLabel()
        self.banner_lbl.setObjectName("subtitle")
        self.banner_lbl.setWordWrap(True)
        self.banner_lbl.setStyleSheet("color: #E6A23C;")
        self.banner_lbl.setVisible(False)
        layout.addWidget(self.banner_lbl)

        self.list = QListWidget()
        self.list.setSpacing(3)
        self.list.setVerticalScrollMode(QAbstractItemView.ScrollPerPixel)
        self.list.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        self.list.setVerticalScrollBarPolicy(Qt.ScrollBarAsNeeded)
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
        self.freeze_btn = QPushButton("❄ 冻结")
        self.freeze_btn.setObjectName("secondary")
        self.freeze_btn.setFixedHeight(26)
        self.freeze_btn.setCheckable(True)
        self.freeze_btn.setToolTip("有事暂停：冻结长期任务进度与欠卡，解冻后不补欠卡")
        self.freeze_btn.toggled.connect(self._on_freeze_toggled)
        bottom.addWidget(self.freeze_btn)
        self.clear_done_btn = QPushButton("清理已完成")
        self.clear_done_btn.setObjectName("secondary")
        self.clear_done_btn.setFixedHeight(26)
        self.clear_done_btn.setToolTip("删除所有已完成任务（一次性 + 固定）")
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

    def _add_fixed(self):
        dlg = FixedTaskDialog(self)
        if dlg.exec_() != QDialog.Accepted:
            return
        text, desc, days = dlg.result_data()
        if not text:
            QMessageBox.information(self, "添加固定任务", "任务内容不能为空。")
            return
        self._win.pet_add_fixed_task(text, desc, days)

    def _on_freeze_toggled(self, checked):
        self._win.pet_freeze_tasks(checked)

    def _update_banner(self):
        data = self._win.data
        frozen = bool(data.get("frozen"))
        debts = [t for t in data.get("tasks", [])
                 if not t.get("done") and t.get("owed", 0) > 0]
        msgs = []
        if frozen:
            since = data.get("frozen_since") or ""
            msgs.append("❄ 任务已冻结" + (f"（自 {since}）" if since else ""))
        if debts:
            total_owed = sum(t.get("owed", 0) for t in debts)
            names = "、".join(t.get("text", "") for t in debts[:2])
            more = "等" if len(debts) > 2 else ""
            msgs.append(
                f"⚠ {len(debts)} 个长期任务欠卡共 {total_owed} 天"
                f"（{names}{more}），当天再打卡一次可抵消一天")
        self.banner_lbl.setText("   |  ".join(msgs))
        self.banner_lbl.setVisible(bool(msgs))

    def _clear_done(self):
        day = storage.today_str()
        tasks = self._win.data["daily"].get(day, [])
        fixed = self._win.data.get("tasks", [])
        once_done = sum(1 for t in tasks if t.get("done"))
        fixed_done = sum(1 for t in fixed if fixed_completed(t))
        if once_done <= 0 and fixed_done <= 0:
            return
        reply = QMessageBox.question(
            self,
            "清理已完成任务",
            f"确定删除 {once_done} 项已完成一次性任务和 {fixed_done} 项已完成固定任务吗？",
            QMessageBox.Yes | QMessageBox.No,
            QMessageBox.No,
        )
        if reply != QMessageBox.Yes:
            return
        self._win.data["daily"][day] = [t for t in tasks if not t.get("done")]
        self._win.data["tasks"] = [t for t in fixed if not fixed_completed(t)]
        self._win.save()
        self.refresh()
        self._win.update_header()

    def _make_section(self, title, count, object_name):
        """返回 (item, widget)：不可选中的分栏标题行。"""
        w = QWidget()
        lay = QVBoxLayout(w)
        lay.setContentsMargins(0, 3, 0, 1)
        lbl = QLabel(f"{title}　{count}")
        lbl.setObjectName(object_name)
        lay.addWidget(lbl)
        item = QListWidgetItem()
        item.setFlags(Qt.NoItemFlags)
        item.setSizeHint(w.sizeHint())
        return item, w

    def refresh(self):
        day = storage.ensure_today(self._win.data)
        tasks = self._win.data["daily"][day]
        fixed = self._win.data.get("tasks", [])

        self.list.clear()
        # 分栏：一次性任务 / 固定任务分开，各自带标题，不再堆成一堆
        if tasks:
            item, w = self._make_section("📋 今日临时任务", len(tasks), "section_header")
            self.list.addItem(item)
            self.list.setItemWidget(item, w)
            for i, task in enumerate(tasks):
                item_widget = TaskItem("once", task, self._win, index=i)
                item = QListWidgetItem()
                item.setSizeHint(item_widget.sizeHint())
                self.list.addItem(item)
                self.list.setItemWidget(item, item_widget)
        if fixed:
            item, w = self._make_section("🎯 长期固定任务（每日打卡）", len(fixed), "section_header_fixed")
            self.list.addItem(item)
            self.list.setItemWidget(item, w)
            for task in fixed:
                item_widget = TaskItem("fixed", task, self._win, tid=task.get("id"))
                item = QListWidgetItem()
                item.setSizeHint(item_widget.sizeHint())
                self.list.addItem(item)
                self.list.setItemWidget(item, item_widget)

        # 底部进度：已完成 = 一次性 done + 固定「完成」（进度条满或 done 标记）
        # 只有真正完成的任务才计入，进度条满 ⇔ 一键清理可用（修复原 bug）
        once_done = sum(1 for t in tasks if t.get("done"))
        fixed_done = sum(1 for t in fixed if fixed_completed(t))
        done = once_done + fixed_done
        total = len(tasks) + len(fixed)

        self.list.setVisible(total > 0)
        self.empty_lbl.setVisible(total == 0)
        self.progress_lbl.setText(f"已完成 {done}/{total}")
        self.progress.setRange(0, max(total, 1))
        self.progress.setValue(done)
        self.clear_done_btn.setEnabled(done > 0)

        # 冻结按钮 + 横幅
        self.freeze_btn.blockSignals(True)
        self.freeze_btn.setChecked(bool(self._win.data.get("frozen")))
        self.freeze_btn.blockSignals(False)
        self._update_banner()


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
        self.today_stats_lbl.setText(f"{fmt_minutes(total_min)} · {active} 个活跃时段")

    def _open_stats_tab(self):
        self._win.tabs.setCurrentIndex(3)
        self._win.stats_tab.refresh()


# ---------------------------------------------------------------- 专注统计页签（直方图）
class FocusHistogram(QWidget):
    """专注时间直方图：X 轴为一天中的小时（0-23），Y 轴为专注分钟数。"""

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setMinimumHeight(120)
        self._minutes = [0.0] * 24

    def set_minutes(self, minutes_list):
        self._minutes = [max(0.0, float(x)) for x in minutes_list]
        self.update()

    def paintEvent(self, event):
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        dark = getattr(theme, "current", "light") == "dark"
        bg = QColor(10, 14, 24, 70) if dark else QColor(255, 255, 255, 55)
        track = QColor(255, 255, 255, 40) if dark else QColor(255, 255, 255, 120)
        bar = QColor("#5B8DEF") if dark else QColor("#4F8BFF")
        axis = QColor("#8A94A8") if dark else QColor("#B9C2D4")
        text = QColor("#A9ADB7") if dark else QColor("#5B6472")

        w, h = self.width(), self.height()
        p.setBrush(bg)
        p.setPen(Qt.NoPen)
        p.drawRoundedRect(0, 0, w, h, 10, 10)
        p.setPen(QColor(255, 255, 255, 24) if dark else QColor(255, 255, 255, 190))
        p.setBrush(Qt.NoBrush)
        p.drawRoundedRect(0, 0, w - 1, h - 1, 10, 10)
        # 玻璃顶部反光：一条淡淡的亮线
        p.setPen(QColor(255, 255, 255, 60) if dark else QColor(255, 255, 255, 230))
        p.drawLine(8, 1, w - 8, 1)

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
        # Y 轴最大值：分钟太多时转成「X.X 时」，刻度栏才放得下
        y_max_lbl = f"{max_min:g}分" if max_min < 100 else f"{max_min / 60.0:.1f}时"
        p.drawText(
            QRectF(0, margin_t - 6, margin_l - 4, 16),
            Qt.AlignRight | Qt.AlignVCenter,
            y_max_lbl,
        )

        # 当前小时高亮标记（仅今天）
        p.setPen(axis)
        now_h = datetime.datetime.now().hour
        x = margin_l + now_h * plot_w / 24.0 + bar_w / 2
        p.drawLine(int(x), margin_t + plot_h - 8, int(x), margin_t + plot_h + 2)

        p.end()


class StatsTab(QWidget):
    """专注统计页签：◀/▶ 切换日期看当天直方图，下方是最近 10 天的每天学习时间统计。"""

    _DAILY_DAYS = 10   # 最近 N 天的每天学习时间

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

        # ---- 最近 N 天：每天学习时间统计 ----
        daily_card = QFrame()
        daily_card.setObjectName("card")
        daily_lay = QVBoxLayout(daily_card)
        daily_lay.setContentsMargins(8, 6, 8, 6)
        daily_lay.setSpacing(4)

        daily_head = QHBoxLayout()
        daily_title = QLabel("📅 每天学习时间")
        daily_title.setObjectName("subtitle")
        daily_title.setStyleSheet("font-weight: bold; font-size: 12px;")
        daily_head.addWidget(daily_title)
        daily_head.addStretch(1)
        daily_lay.addLayout(daily_head)

        self.daily_summary_lbl = QLabel()
        self.daily_summary_lbl.setObjectName("accent")
        self.daily_summary_lbl.setStyleSheet("font-size: 12px;")
        daily_lay.addWidget(self.daily_summary_lbl)

        self.daily_list = QListWidget()
        self.daily_list.setSpacing(1)
        self.daily_list.setMaximumHeight(92)
        daily_lay.addWidget(self.daily_list)

        self.daily_card = daily_card
        layout.addWidget(daily_card)

        layout.addStretch(1)
        self.refresh()

    def _shift(self, delta):
        today = datetime.date.today()
        self._day = min(self._day + datetime.timedelta(days=delta), today)
        self.refresh()

    def _day_row(self, date_obj, minutes, max_min, is_today):
        """一天一行：日期 + 迷你进度条 + 时长（超过 100 分钟转小时）。"""
        w = QWidget()
        h = QHBoxLayout(w)
        h.setContentsMargins(4, 0, 4, 0)
        h.setSpacing(6)

        d = QLabel(f"{date_obj.month:02d}-{date_obj.day:02d} 周{WEEKDAY_CN[date_obj.weekday()][2]}")
        d.setObjectName("accent" if is_today else "subtitle")
        d.setFixedWidth(82)
        h.addWidget(d)

        bar = QProgressBar()
        bar.setTextVisible(False)
        bar.setFixedHeight(5)
        bar.setRange(0, max(int(max_min), 1))
        bar.setValue(int(round(minutes)))
        h.addWidget(bar, 1)

        t = QLabel("—" if minutes <= 0 else fmt_minutes(minutes))
        t.setObjectName("day_total_peak" if minutes > 0 and minutes >= max_min else "day_total")
        t.setAlignment(Qt.AlignRight | Qt.AlignVCenter)
        t.setFixedWidth(76)
        h.addWidget(t)

        return w

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
                f"共 {fmt_minutes(total)} · 分布在 {active} 个小时段 · 高峰时段 {peak}:00"
            )

        # 最近 N 天的每天学习时间
        days = [today - datetime.timedelta(days=i) for i in range(self._DAILY_DAYS - 1, -1, -1)]
        totals = [sum(hist.get(d.isoformat(), {}).values()) for d in days]
        grand = int(round(sum(totals)))
        active_days = sum(1 for x in totals if x > 0)
        avg = int(round(grand / active_days)) if active_days else 0
        streak = 0
        for x in reversed(totals):
            if x > 0:
                streak += 1
            else:
                break
        self.daily_summary_lbl.setText(
            f"近 {self._DAILY_DAYS} 天共 {fmt_minutes(grand)} · 日均 {fmt_minutes(avg)} · 连续 {streak} 天"
        )

        max_min = max(totals) if totals else 0
        self.daily_list.clear()
        for i, d in enumerate(days):
            row = self._day_row(d, totals[i], max_min, d == today)
            item = QListWidgetItem()
            item.setFlags(Qt.ItemIsEnabled)
            item.setSizeHint(row.sizeHint())
            self.daily_list.addItem(item)
            self.daily_list.setItemWidget(item, row)


# ---------------------------------------------------------------- 提醒页签
class ReminderRow(QWidget):
    def __init__(self, index, reminder, win, parent=None):
        super().__init__(parent)
        self.setObjectName("card")
        self.setAttribute(Qt.WA_StyledBackground, True)
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
    def paintEvent(self, event):
        """透明顶层窗口的 QSS 背景不会画，玻璃底板在这里手动画（极光渐变）。"""
        theme.paint_glass_background(self, QPainter(self), "aurora")
        super().paintEvent(event)

    def __init__(self):
        super().__init__(None)
        self.data = storage.load_data()
        storage.ensure_today(self.data)
        storage.rollover_fixed(self.data)   # 启动时结算长期任务欠卡（冻结自动跳过）

        self.setWindowFlags(
            Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool
        )
        # 玻璃质感：透明窗口 + Windows 亚克力模糊；亚克力不可用时回退到不透明渐变
        self.setAttribute(Qt.WA_TranslucentBackground, True)
        self.setAttribute(Qt.WA_StyledBackground, True)
        self.setObjectName(
            "float_window_acrylic" if theme.try_acrylic(self) else "float_window"
        )
        # A slightly wider canvas prevents the four primary areas from feeling
        # cramped, while remaining a compact desktop companion.
        self.setFixedWidth(380)

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
        title = QLabel("考研复习")
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

        self.settings_btn = QPushButton("设置")
        self.settings_btn.setObjectName("header_button")
        self.settings_btn.setFixedHeight(28)
        self.settings_btn.setCursor(Qt.PointingHandCursor)
        self.settings_btn.clicked.connect(self._open_settings)
        header_layout.addWidget(self.settings_btn)

        root.addWidget(self.header)

        # 页签（透明页，露出玻璃背景）
        self.tabs = QTabWidget()
        self.tabs.setObjectName("main_tabs")
        self.plan_tab = PlanTab(self)
        self.plan_tab.setObjectName("plan_tab")
        self.timer_tab = TimerTab(self)
        self.timer_tab.setObjectName("timer_tab")
        self.reminder_tab = ReminderTab(self)
        self.reminder_tab.setObjectName("reminder_tab")
        self.stats_tab = StatsTab(self)
        self.stats_tab.setObjectName("stats_tab")
        self.tabs.addTab(self.plan_tab, "今日计划")
        self.tabs.addTab(self.timer_tab, "专注计时")
        self.tabs.addTab(self.reminder_tab, "提醒")
        self.tabs.addTab(self.stats_tab, "专注统计")
        self.tabs.currentChanged.connect(self._animate_tab_change)
        self._tab_fade = None
        root.addWidget(self.tabs, 1)

    def _animate_tab_change(self, index):
        """A short, interruptible fade makes navigation feel deliberate.

        It is intentionally subtle (140ms) so focus workflows stay instant and
        keyboard navigation remains unaffected.
        """
        page = self.tabs.widget(index)
        if page is None:
            return
        effect = page.graphicsEffect()
        if not isinstance(effect, QGraphicsOpacityEffect):
            effect = QGraphicsOpacityEffect(page)
            page.setGraphicsEffect(effect)
        if self._tab_fade is not None:
            self._tab_fade.stop()
        effect.setOpacity(0.72)
        self._tab_fade = QPropertyAnimation(effect, b"opacity", page)
        self._tab_fade.setDuration(140)
        self._tab_fade.setStartValue(0.72)
        self._tab_fade.setEndValue(1.0)
        self._tab_fade.setEasingCurve(QEasingCurve.OutCubic)
        self._tab_fade.start()

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

        today = storage.today_str()
        tasks = self.data["daily"].get(today, [])
        done = sum(1 for t in tasks if t.get("done"))
        total = len(tasks)
        # 固定任务：已 done 或今天已打卡计入已完成
        fixed = self.data.get("tasks", [])
        fixed_done = sum(1 for t in fixed if t.get("done") or t.get("last_done_date") == today)
        prefix = "❄ " if self.data.get("frozen") else ""
        self.progress_lbl.setText(f"{prefix}今日 {done + fixed_done}/{total + len(fixed)}")

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

    # ---------- 固定任务操作 ----------
    def delete_fixed_task(self, tid):
        task = storage.find_fixed(self.data, tid)
        if task is None:
            return
        text = task.get("text", "任务")
        reply = QMessageBox.question(
            self,
            "删除固定任务",
            f"确定删除「{text}」吗？",
            QMessageBox.Yes | QMessageBox.No,
            QMessageBox.No,
        )
        if reply != QMessageBox.Yes:
            return
        self.data["tasks"] = [t for t in self.data.get("tasks", []) if t.get("id") != tid]
        self.save()
        self.plan_tab.refresh()
        self.update_header()

    def edit_fixed_task(self, tid):
        task = storage.find_fixed(self.data, tid)
        if task is None:
            return
        dlg = FixedTaskDialog(self)
        dlg.setWindowTitle("编辑固定任务")
        dlg.text_edit.setText(task.get("text", ""))
        dlg.desc_edit.setText(task.get("desc", ""))
        dlg.days_spin.setValue(max(1, int(task.get("target_days", 1) or 1)))
        if dlg.exec_() != QDialog.Accepted:
            return
        text, desc, days = dlg.result_data()
        if not text:
            QMessageBox.information(self, "编辑固定任务", "任务内容不能为空。")
            return
        task["text"] = text
        task["desc"] = desc
        task["target_days"] = days
        if task.get("progress", 0) >= days:   # 新天数已达标 → 直接完成
            task["done"] = True
            task["owed"] = 0
        self.save()
        self.plan_tab.refresh()
        self.update_header()

    def punch_fixed_task(self, tid):
        """UI「打卡」按钮：真正打卡/补卡。"""
        if self.data.get("frozen"):
            return "任务已冻结，暂时不能打卡哦～"
        if storage.punch_fixed(self.data, tid):
            self.plan_tab.refresh()
            self.update_header()
            self._refresh_pet_status()
            return "打卡成功 ✅"
        return "没找到这个固定任务，或它已经完成啦～"

    def pet_add_fixed_task(self, text, desc="", days=1):
        text = (text or "").strip()
        if not text:
            return "固定任务内容不能为空，说「添加长期任务 背单词 30天」试试～"
        try:
            days = int(days)
        except (TypeError, ValueError):
            days = 1
        if days < 1:
            days = 1
        storage.add_fixed(self.data, text, desc, days)
        self.plan_tab.refresh()
        self.update_header()
        self._refresh_pet_status()
        return f"好，长期任务「{text}」建好啦（每天打卡，{days} 天完成）✅"

    def pet_freeze_tasks(self, on):
        """冻结/解冻：冻结跳过欠卡积累并禁用打卡；解冻重置 last_done_date=today。"""
        on = bool(on)
        data = self.data
        if bool(data.get("frozen")) == on:
            self.plan_tab.refresh()
            if on:
                return "任务已经在冻结状态啦，说「解冻任务」就能恢复～"
            return "任务没有冻结哦～"
        data["frozen"] = on
        if on:
            data["frozen_since"] = storage.today_str()
        else:
            today = storage.today_str()
            for t in data.get("tasks", []):
                if not t.get("done"):
                    t["last_done_date"] = today   # 冻结期不产生欠卡
            data["frozen_since"] = ""
        self.save()
        self.plan_tab.refresh()
        self.update_header()
        self._refresh_pet_status()
        if on:
            return "长期任务已冻结 ❄ 进度和欠卡都先冻住，解冻后也不补欠卡。忙你的～"
        return "解冻啦！长期任务恢复正常打卡 📅"

    def pet_punch_task(self, name):
        """聊天指令「打卡 <任务名>」：模糊匹配未完成固定任务并打卡/补卡。"""
        name = (name or "").strip()
        if self.data.get("frozen"):
            return "任务已冻结，暂时不能打卡哦～"
        fixed = [t for t in self.data.get("tasks", []) if not t.get("done")]
        if not fixed:
            return "现在没有进行中的长期任务。说「添加长期任务 背单词 30天」建一个～"
        matched = [t for t in fixed
                   if name == t.get("text") or (len(name) >= 2 and name in t.get("text", ""))]
        if not matched and len(name) >= 2:
            matched = [
                t for t in fixed
                if sum(1 for ch in name if ch in t.get("text", "")) >= max(2, len(name) // 2)
            ]
        if not matched:
            return f"没找到「{name}」这个长期任务。可以说「长期任务进度」看看有哪些～"
        if len(matched) > 1:
            names = "、".join(t.get("text", "") for t in matched)
            return f"「{name}」匹配到好几个：{names}，说具体一点～"
        t = matched[0]
        already = t.get("last_done_date") == storage.today_str()
        storage.punch_fixed(self.data, t["id"])
        self.plan_tab.refresh()
        self.update_header()
        self._refresh_pet_status()
        action = "补卡" if already else "打卡"
        return f"「{t['text']}」{action}成功！进度 {t.get('progress', 0)}/{t.get('target_days', 1)}"

    def pet_debt_report(self):
        """聊天指令「欠卡/长期任务进度」：汇报欠卡与进行中任务。"""
        data = self.data
        if data.get("frozen"):
            since = data.get("frozen_since") or ""
            return "任务已冻结，欠卡暂不计时。" + (f"（自 {since} 起冻结）" if since else "")
        fixed = data.get("tasks", [])
        active = [t for t in fixed if not t.get("done")]
        debts = [t for t in active if t.get("owed", 0) > 0]
        if not active:
            return "现在没有进行中的长期任务～"
        lines = []
        if debts:
            total_owed = sum(t.get("owed", 0) for t in debts)
            lines.append(f"⚠ 有 {len(debts)} 个长期任务欠卡共 {total_owed} 天：")
            for t in debts:
                lines.append(
                    f"  {t.get('text', '')}：欠 {t.get('owed', 0)} 天，"
                    f"进度 {t.get('progress', 0)}/{t.get('target_days', 1)}")
            lines.append("当天再打卡一次（第 2 次）就能抵消一天欠卡。")
        else:
            lines.append("当前没有欠卡，全部按时打卡 👍")
        lines.append("进行中：")
        for t in active:
            lines.append(
                f"  {t.get('text', '')} {t.get('progress', 0)}/{t.get('target_days', 1)}"
                + (f"（欠 {t.get('owed', 0)} 天）" if t.get("owed", 0) else ""))
        return "\n".join(lines)

    def _refresh_pet_status(self):
        pet = getattr(self, "_pet", None)
        if pet is not None:
            pet.refresh_status()

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
        """聊天指令「划掉 <任务名>」：先匹配一次性任务，再匹配固定任务（提前划去完成）。"""
        name = (name or "").strip()
        day = storage.today_str()
        tasks = self.data["daily"].get(day, [])
        pending = [t for t in tasks if not t.get("done")]
        fixed_pending = [t for t in self.data.get("tasks", []) if not t.get("done")]
        if not pending and not fixed_pending:
            return "今天没有未完成的任务啦，很棒！"

        def _match(cands):
            if not cands:
                return []
            m = [t for t in cands
                 if name == t.get("text") or (len(name) >= 2 and name in t.get("text", ""))]
            if not m and len(name) >= 2:
                m = [
                    t for t in cands
                    if sum(1 for ch in name if ch in t.get("text", "")) >= max(2, len(name) // 2)
                ]
            return m

        matched = _match(pending)
        if matched:
            if len(matched) > 1:
                names = "、".join(t["text"] for t in matched)
                return f"「{name}」匹配到好几项：{names}，能说得再具体点吗？"
            t = matched[0]
            t["done"] = True
            self.save()
            self.plan_tab.refresh()
            self.update_header()
            return f"搞定！「{t['text']}」已划掉 ✅"
        matched_f = _match(fixed_pending)
        if matched_f:
            if len(matched_f) > 1:
                names = "、".join(t.get("text", "") for t in matched_f)
                return f"「{name}」匹配到好几项：{names}，能说得再具体点吗？"
            t = matched_f[0]
            t["done"] = True
            t["owed"] = 0
            self.save()
            self.plan_tab.refresh()
            self.update_header()
            return f"搞定！长期任务「{t['text']}」提前划去完成啦 🎉"
        return f"没找到「{name}」。可以说「列出计划」看看有哪些～"

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
        fixed = [t for t in self.data.get("tasks", []) if not t.get("done")]
        if not tasks and not fixed:
            return "今天还没有计划，要不要加一条？跟我说「添加 背单词」就行～"
        pending = [t for t in tasks if not t.get("done")]
        done = [t for t in tasks if t.get("done")]
        lines = [f"今日共 {len(tasks)} 项一次性任务，已完成 {len(done)} 项："]
        if pending:
            lines.append("未完成：")
            lines += [f"{i + 1}. {t['text']}" for i, t in enumerate(pending)]
        if done:
            done_names = "、".join(t["text"] for t in done[:5])
            lines.append(f"已完成：{done_names}{'…' if len(done) > 5 else ''}")
        if fixed:
            lines.append(f"长期任务 {len(fixed)} 项：")
            for i, t in enumerate(fixed):
                tail = f"（欠 {t.get('owed', 0)} 天）" if t.get("owed", 0) else ""
                lines.append(
                    f"  {i + 1}. {t.get('text', '')} "
                    f"{t.get('progress', 0)}/{t.get('target_days', 1)}{tail}")
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
            storage.rollover_fixed(self.data)   # 跨日结算长期任务欠卡（冻结自动跳过）
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
        self._pet._tts.stop_server()   # 退出时终止宠物拉起的语音服务（释放显存/线程）
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
        self.setAttribute(Qt.WA_StyledBackground, True)
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
