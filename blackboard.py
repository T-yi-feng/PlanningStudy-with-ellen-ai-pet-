# -*- coding: utf-8 -*-
"""字幕黑板：独立于对话气泡的常驻小窗，实时滚动显示媒体字幕。

样式：黑色黑板 + 木框 + 粉笔字；可拖动（拖到哪停哪）、可自由缩放
（四边四角都能拉，字号保持设置值不变）；单击清空。
与宠物气泡完全独立：各自是无边框 Tool 窗口，互不影响。
"""
import time

from PyQt5.QtCore import Qt, QTimer
from PyQt5.QtGui import QColor, QFont, QLinearGradient, QPainter, QPen
from PyQt5.QtWidgets import QApplication, QWidget

MAX_LINES = 6            # 黑板最多保留的字幕条数
DEFAULT_W, DEFAULT_H = 400, 170
MIN_W, MIN_H = 280, 120
REF_H = DEFAULT_H        # 字号基准高度：黑板高=REF_H 时字显示为设置值
_RESIZE_PAD = 22         # 右下角缩放热区大小
_EDGE_PAD = 8            # 右/下边缘缩放热区宽度
_MARGIN = 14
_FRAME = 7


class Blackboard(QWidget):
    """一块「黑板」：黑底 + 木框 + 粉笔字。单击清空、按住拖动、拖角落/边缘缩放。"""

    def __init__(self, get_cfg):
        super().__init__(None)
        self._get_cfg = get_cfg          # () -> dict{font_size,color,language}
        self._lines = []                 # [(ts, text)]，最新在前
        self._status = "🔇 未开启"
        self._lang_label = "自动识别"
        self._has_live = False           # 是否有一条“半成品”字幕正在被持续替换
        self._cursor_visible = True      # 打字光标当前是否可见（闪烁）
        self._cursor_timer = QTimer(self)
        self._cursor_timer.setInterval(530)
        self._cursor_timer.timeout.connect(self._tick_cursor)
        self._drag_off = None
        self._press_pos = None
        self._resize_zone = None         # None | 四角 tl/tr/bl/br | 四边 l/r/t/b
        self._resize_start = None        # (globalPos, geometry)
        self._dragged = False            # 本次按下是否发生过拖动/缩放（用于区分“单击清空”）

        self.setWindowFlags(
            Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool
        )
        self.setAttribute(Qt.WA_TranslucentBackground, True)
        self.setAttribute(Qt.WA_ShowWithoutActivating, True)
        self.setMouseTracking(True)
        self.setMinimumSize(MIN_W, MIN_H)
        self.resize(DEFAULT_W, DEFAULT_H)

    # ---------- 外观 ----------
    def _cfg(self):
        try:
            return self._get_cfg() or {}
        except Exception:
            return {}

    def _base_size(self):
        return int(self._cfg().get("font_size", 18))

    def _color(self):
        return QColor(self._cfg().get("color", "#F5F0E6"))

    def _font_size(self):
        """字幕字号 = 设置值（缩放黑板大小不会改变字号）。"""
        return max(9, self._base_size())

    def apply_settings(self):
        """字体/颜色/语言变化后刷新外观（保持当前窗口大小）。"""
        self.update()

    # ---------- 数据 ----------
    def set_status(self, status):
        self._status = status
        self.update()

    def set_language_label(self, label):
        self._lang_label = label
        self.update()

    def add_line(self, text):
        """追加一条字幕（定稿），并结束当前“半成品”行。"""
        text = (text or "").strip()
        if not text:
            return
        self._has_live = False
        self._cursor_timer.stop()
        self._lines.insert(0, (time.strftime("%H:%M:%S"), text))
        del self._lines[MAX_LINES:]
        self.update()

    def show_interim(self, text):
        """实时半成品：只替换当前“进行中”那一行，不新增（不刷屏、不重复）。
        半成品行尾部带闪烁打字光标（模拟正在打字）。"""
        text = (text or "").strip()
        if not text:
            return
        if self._has_live:
            self._lines[0] = (self._lines[0][0], text)
        else:
            self._lines.insert(0, (time.strftime("%H:%M:%S"), text))
            del self._lines[MAX_LINES:]
            self._has_live = True
        self._cursor_visible = True
        self._cursor_timer.start()
        self.update()

    def finalize_interim(self, text):
        """整句识别完成：用定稿替换预览行；相同则只定稿；空文本则收起预览行。"""
        if self._has_live:
            if text:
                if text != self._lines[0][1]:
                    self._lines[0] = (self._lines[0][0], text)
            else:
                self._lines.pop(0)
            self._has_live = False
            self._cursor_timer.stop()
        elif text:
            self.add_line(text)
        self.update()

    def clear(self):
        self._lines = []
        self._has_live = False
        self._cursor_timer.stop()
        self._cursor_visible = True
        self.update()

    def _tick_cursor(self):
        self._cursor_visible = not self._cursor_visible
        self.update()

    # ---------- 显示/定位 ----------
    def show_near(self, pet):
        g = pet.frameGeometry()
        x = g.right() + 14
        y = g.center().y() - self.height() // 2
        geo = QApplication.primaryScreen().availableGeometry()
        x = max(geo.left() + 8, min(x, geo.right() - self.width() - 8))
        y = max(geo.top() + 8, min(y, geo.bottom() - self.height() - 8))
        self.move(x, y)
        self.show()
        self.raise_()

    # ---------- 交互：拖动 + 缩放 ----------
    _CURSOR = {
        "tl": Qt.SizeFDiagCursor, "br": Qt.SizeFDiagCursor,
        "tr": Qt.SizeBDiagCursor, "bl": Qt.SizeBDiagCursor,
        "l": Qt.SizeHorCursor, "r": Qt.SizeHorCursor,
        "t": Qt.SizeVerCursor, "b": Qt.SizeVerCursor,
    }

    def _hit_zone(self, pos):
        x, y = pos.x(), pos.y()
        w, h = self.width(), self.height()
        if x >= w - _RESIZE_PAD and y >= h - _RESIZE_PAD:
            return "br"
        if x < _RESIZE_PAD and y < _RESIZE_PAD:
            return "tl"
        if x >= w - _RESIZE_PAD and y < _RESIZE_PAD:
            return "tr"
        if x < _RESIZE_PAD and y >= h - _RESIZE_PAD:
            return "bl"
        if x >= w - _EDGE_PAD:
            return "r"
        if x < _EDGE_PAD:
            return "l"
        if y >= h - _EDGE_PAD:
            return "b"
        if y < _EDGE_PAD:
            return "t"
        return None

    def mousePressEvent(self, event):
        if event.button() != Qt.LeftButton:
            return
        zone = self._hit_zone(event.pos())
        self._dragged = False
        self._press_pos = event.pos()
        if zone:
            self._resize_zone = zone
            self._resize_start = (event.globalPos(), self.frameGeometry())
            self._drag_off = None
        else:
            self._resize_zone = None   # 复位：上次缩放若被外部打断，新按下即退出缩放态
            self._resize_start = None
            self._drag_off = event.globalPos() - self.frameGeometry().topLeft()

    def _apply_resize(self, pos):
        """按热区实时调整窗口几何：四角/四边都能拉，反向拉带最小尺寸钳制。"""
        g0, s0 = self._resize_start
        zone = self._resize_zone
        dx = pos.x() - g0.x()
        dy = pos.y() - g0.y()
        x, y, w, h = s0.x(), s0.y(), s0.width(), s0.height()
        if "l" in zone:
            w = s0.width() - dx
            if w < MIN_W:
                w = MIN_W
                dx = s0.width() - MIN_W
            x = s0.x() + dx
        elif "r" in zone:
            w = max(MIN_W, s0.width() + dx)
        if "t" in zone:
            h = s0.height() - dy
            if h < MIN_H:
                h = MIN_H
                dy = s0.height() - MIN_H
            y = s0.y() + dy
        elif "b" in zone:
            h = max(MIN_H, s0.height() + dy)
        self.setGeometry(x, y, w, h)

    def mouseMoveEvent(self, event):
        if self._resize_zone:
            self._dragged = True
            self._apply_resize(event.globalPos())
            return
        if self._drag_off is not None and event.buttons() & Qt.LeftButton:
            self._dragged = True
            self.move(event.globalPos() - self._drag_off)
            return
        # 悬停光标提示
        self.setCursor(self._CURSOR.get(self._hit_zone(event.pos()), Qt.OpenHandCursor))

    def mouseReleaseEvent(self, event):
        was_drag = self._dragged
        self._resize_zone = None
        self._resize_start = None
        self._drag_off = None
        self._press_pos = None
        if event.button() == Qt.LeftButton and not was_drag:
            self.clear()   # 单击（非拖拽/缩放）才清空黑板

    # ---------- 绘制 ----------
    def _wrap(self, text, fm, maxw):
        out = []
        line = ""
        for ch in text:
            if fm.horizontalAdvance(line + ch) > maxw:
                if line:
                    out.append(line)
                    line = ch
                else:
                    out.append(ch)
            else:
                line += ch
        if line:
            out.append(line)
        return out

    def paintEvent(self, event):
        color = self._color()
        size = self._font_size()
        w, h = self.width(), self.height()
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)

        # 黑板底色（黑色渐变）
        grad = QLinearGradient(0, 0, 0, h)
        grad.setColorAt(0, QColor("#222222"))
        grad.setColorAt(1, QColor("#0A0A0A"))
        p.setPen(Qt.NoPen)
        p.setBrush(grad)
        p.drawRoundedRect(0, 0, w, h, 14, 14)

        # 木框
        p.setBrush(Qt.NoBrush)
        p.setPen(QPen(QColor("#8A5A2B"), 3))
        p.drawRoundedRect(_FRAME - 1, _FRAME - 1, w - 2 * (_FRAME - 1), h - 2 * (_FRAME - 1), 10, 10)
        p.setPen(QPen(QColor("#3A3A3A"), 1))
        p.drawRoundedRect(_FRAME + 4, _FRAME + 4, w - 2 * (_FRAME + 4), h - 2 * (_FRAME + 4), 7, 7)

        # 顶部状态行
        p.setFont(QFont("Microsoft YaHei UI", 11))
        fm = p.fontMetrics()
        header = "📝 字幕 · %s　%s" % (self._lang_label, self._status)
        p.setPen(QColor("#8A8A8A"))
        p.drawText(_MARGIN + 4, _FRAME + 22, header)
        p.setPen(QPen(QColor("#3A3A3A"), 1))
        p.drawLine(_MARGIN, _FRAME + 32, w - _MARGIN, _FRAME + 32)

        # 字幕行（最新在底部，旧字幕往上滚动，超出高度的只画放得下的）
        p.setFont(QFont("Microsoft YaHei UI", size, QFont.Bold))
        fm = p.fontMetrics()
        maxw = w - 2 * _MARGIN - 8
        rows = []   # (timestamp, display_lines) 从新到旧
        for ts, text in self._lines:
            rows.append((ts, self._wrap(text, fm, maxw)))
        line_h = int(size * 1.35 + 5)
        y = h - _MARGIN
        for ts, disp in rows:
            for part in reversed(disp):
                if y < _FRAME + 40:
                    break
                y -= line_h
                p.setPen(color)
                p.drawText(_MARGIN + 4, y + fm.ascent(), part)
            if y < _FRAME + 40:
                break
            # 时间戳（淡一点，小号）
            y -= 14
            p.setPen(QColor("#5A5A5A"))
            p.setFont(QFont("Microsoft YaHei UI", max(9, size - 4)))
            ts_fm = p.fontMetrics()
            p.drawText(_MARGIN + 4, y + ts_fm.ascent(), ts)
            p.setFont(QFont("Microsoft YaHei UI", size, QFont.Bold))
            fm = p.fontMetrics()

        # 打字光标：半成品行末尾闪烁的竖杠（模拟正在打字）
        if self._has_live and self._cursor_visible and rows:
            _ts, disp = rows[0]
            p.setFont(QFont("Microsoft YaHei UI", size, QFont.Bold))
            fmm = p.fontMetrics()
            cx = _MARGIN + 4 + fmm.horizontalAdvance(disp[-1]) + 3
            cy = h - _MARGIN - line_h + fmm.ascent()
            p.setPen(QPen(color, 2))
            p.drawLine(int(cx), int(cy - fmm.ascent() + 1), int(cx), int(cy + fmm.descent() - 1))

        # 右下角缩放提示（三个小点）
        p.setPen(QColor("#555555"))
        dot = 2
        for i in range(3):
            x = w - 16 + i * 5
            yy = h - 16 + i * 5
            p.drawEllipse(x, yy, dot, dot)
        p.end()
