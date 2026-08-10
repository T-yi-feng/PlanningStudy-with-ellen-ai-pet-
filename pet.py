"""桌宠「艾莲」：图片宠物 + 头顶状态条 + 下方常驻圆角输入框 + 对话气泡。

交互：
- 单击宠物仅用于拖动（不再打开主窗口）；右键 → 菜单（回到主窗口 / 设置 / 打开聊天记录 / 退出）
- 桌宠下方常驻圆角输入框：输入内容回车，宠物用对话气泡回复
  （本地指令改计划：添加/划掉/删除/列出；普通聊天走免费 AI 艾莲，未配 Key 用本地话术）
- 输入框支持 Ctrl+V 粘贴图片，艾莲用 glm-4.6v-flash 识别图片并回复
- 桌宠上方淡蓝状态条：实时显示任务进度 + 专注正计时
- 偶尔主动蹦出加油/闲话气泡
"""
import base64
import os
import random
import time

from PyQt5.QtCore import Qt, QTimer, pyqtSignal
from PyQt5.QtCore import QBuffer, QByteArray, QThread
from PyQt5.QtGui import QColor, QFont, QImage, QPainter, QPixmap
from PyQt5.QtWidgets import (
    QApplication, QDialog, QFormLayout, QFrame, QHBoxLayout, QInputDialog,
    QLabel, QLineEdit, QMenu, QPushButton, QScrollArea, QVBoxLayout, QWidget,
)

from ani import AniClip, load_ani
from blackboard import Blackboard
from captions import CaptionEngine, lang_label
from tts import SpeakingEngine
from voice import VoiceInputEngine

import chat
import secret
import storage

_DIR = os.path.dirname(os.path.abspath(__file__))
PET_IMAGE = os.path.join(_DIR, "desk_pet", "normal_1.png")  # 旧静态图路径（现由 .ani 动画取代）
ANI_FILES = {
    "normal": "normal_1.ani",    # 常态
    "talking": "talking_1.ani",  # 对话/思考/回答时
    "happy": "happy_1.ani",      # 被夸 / 高兴鼓励
    "present": "present_1.ani",  # 点赞表扬（待办完成）
    "sleep": "alternate.ani",    # 太久没互动 → 趴睡/换姿势
}
PET_WIDTH = 190  # 桌宠显示宽度（保持宽高比）
ROUND_FONT = "YouYuan"  # 圆润可爱字体，缺失时系统自动回退到雅黑

# 太久没有互动（点击/拖动/输入）时进入「趴睡」状态
IDLE_TOO_LONG_MS = 5 * 60 * 1000

# 回复中出现这些字样 → 任务刚完成，触发「点赞」动画
_DONE_REPLY_MARKS = ("搞定", "划掉", "已勾掉", "全部完成", "完成啦", "太棒了")

# 用户消息里出现这些字样 → 被夸奖，触发「高兴」动画
_PRAISE_KEYS = (
    "夸", "表扬", "好棒", "真棒", "很棒", "棒棒", "棒", "厉害",
    "可爱", "聪明", "漂亮", "乖", "喜欢", "爱你", "欣赏", "👍", "❤", "😍",
)


def _is_praise(text):
    return any(k in (text or "") for k in _PRAISE_KEYS)


def _clamp_to_screen(x, y, w, h):
    """把窗口坐标夹到屏幕可用区内，保证部件不跑出屏幕。"""
    geo = QApplication.primaryScreen().availableGeometry()
    x = max(geo.left() + 4, min(x, geo.right() - w - 4))
    y = max(geo.top() + 4, y)
    return x, y


def _image_to_png_b64(img, max_dim=1024):
    """把 QImage 缩放到合适尺寸后编码为 base64 PNG（给视觉模型用）。"""
    if img.width() > max_dim or img.height() > max_dim:
        img = img.scaled(max_dim, max_dim, Qt.KeepAspectRatio, Qt.SmoothTransformation)
    ba = QByteArray()
    buf = QBuffer(ba)
    buf.open(QBuffer.WriteOnly)
    img.save(buf, "PNG")
    buf.close()
    return base64.b64encode(bytes(ba)).decode("ascii")


class ImagePasteLineEdit(QLineEdit):
    """支持粘贴图片的输入框：Ctrl+V 或右键粘贴图片时发出 image_pasted(QImage)。

    同时保证所在窗口激活后再聚焦（Qt.Tool 窗口在 Windows 上不会自动激活，
    直接点击可能拿不到键盘焦点——这是「点输入框没反应」的根因）。
    """
    image_pasted = pyqtSignal(object)
    focus_gained = pyqtSignal()
    focus_lost = pyqtSignal()

    def _emit_paste(self, mime):
        if mime.hasImage():
            img = mime.imageData()
            if isinstance(img, QImage) and not img.isNull():
                self.image_pasted.emit(img)
                return True
        return False

    def keyPressEvent(self, event):
        if event.key() == Qt.Key_V and int(event.modifiers() & Qt.ControlModifier):
            mime = QApplication.clipboard().mimeData()
            if self._emit_paste(mime):
                event.accept()
                return
        super().keyPressEvent(event)

    def insertFromMimeData(self, mime):
        if self._emit_paste(mime):
            return
        super().insertFromMimeData(mime)

    def mousePressEvent(self, event):
        w = self.window()
        if w is not None and not w.isActiveWindow():
            w.activateWindow()
        super().mousePressEvent(event)
        self.setFocus()

    def focusInEvent(self, event):
        super().focusInEvent(event)
        self.focus_gained.emit()

    def focusOutEvent(self, event):
        super().focusOutEvent(event)
        self.focus_lost.emit()


class ChatWorker(QThread):
    """后台调用 AI（文字或图文），避免卡住界面。"""
    reply_ready = pyqtSignal(str)

    def __init__(self, service, text, image_b64=None, parent=None):
        super().__init__(parent)
        self._service = service
        self._text = text
        self._image_b64 = image_b64

    def run(self):
        try:
            if self._image_b64:
                reply = self._service.respond_vision(self._text, self._image_b64)
            else:
                reply = self._service.respond(self._text)
        except Exception:
            reply = chat.local_reply(self._text)
        self.reply_ready.emit(reply)


class PetBubble(QFrame):
    """宠物头上的对话气泡（可含图片缩略图）：短暂显示后自动消失。"""
    closed = pyqtSignal()  # 超时自动消失时发出（手动关闭不发出）

    def __init__(self, pet, text="", pixmap=None, timeout_ms=10000):
        super().__init__(None)
        self.setObjectName("pet_bubble")
        self.setWindowFlags(
            Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool
        )
        self.setAttribute(Qt.WA_ShowWithoutActivating, True)
        lay = QVBoxLayout(self)
        lay.setContentsMargins(10, 7, 10, 7)
        if pixmap is not None:
            img_lbl = QLabel()
            img_lbl.setObjectName("pb_text")
            img_lbl.setPixmap(pixmap.scaledToWidth(150, Qt.SmoothTransformation))
            lay.addWidget(img_lbl)
        self._lbl = QLabel(text)
        self._lbl.setObjectName("pb_text")
        self._lbl.setWordWrap(True)
        self._lbl.setMaximumWidth(210)
        lay.addWidget(self._lbl)
        self.adjustSize()

        # 放在桌宠上方；若状态条可见则放到状态条上方，避免遮挡
        g = pet.frameGeometry()
        ref_top = g.top()
        status = getattr(pet, "_status", None)
        if status is not None and status.isVisible():
            ref_top = min(ref_top, status.frameGeometry().top())
        x = g.center().x() - self.width() // 2
        y = ref_top - self.height() - 10
        x, y = _clamp_to_screen(x, y, self.width(), self.height())
        self.move(x, y)

        self._timer = QTimer(self)
        self._timer.setSingleShot(True)
        self._timer.timeout.connect(self._auto_close)
        self._timer.start(timeout_ms)

    def _auto_close(self):
        self.closed.emit()
        self.close()

    def lbl_text(self):
        return self._lbl.text()


class PetStatus(QFrame):
    """桌宠头顶状态条：任务进度 + 专注状态。淡蓝圆润字体，每秒刷新。"""

    def __init__(self, pet):
        super().__init__(None)
        self._pet = pet
        self.setObjectName("pet_status")
        self.setWindowFlags(
            Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool
        )
        self.setAttribute(Qt.WA_TranslucentBackground, True)
        self.setAttribute(Qt.WA_ShowWithoutActivating, True)
        self.setAttribute(Qt.WA_StyledBackground, True)

        lay = QVBoxLayout(self)
        lay.setContentsMargins(10, 3, 10, 3)
        self.label = QLabel()
        self.label.setObjectName("ps_text")
        self.label.setFont(QFont(ROUND_FONT, 12, QFont.Bold))
        self.label.setAlignment(Qt.AlignCenter)
        lay.addWidget(self.label)
        self.adjustSize()

        self._timer = QTimer(self)
        self._timer.setInterval(1000)
        self._timer.timeout.connect(self.refresh)
        self._timer.start()
        self.refresh()

    def refresh(self):
        """刷新进度与专注时间。专注中显示本次正计时，否则显示未专注。"""
        win = self._pet._win
        day = storage.today_str()
        tasks = win.data.get("daily", {}).get(day, [])
        done = sum(1 for t in tasks if t.get("done"))
        total = len(tasks)

        tt = getattr(win, "timer_tab", None)
        if tt is not None and getattr(tt, "_running", False):
            focus = "专注 " + tt._fmt(int(getattr(tt, "_elapsed", 0)))
        else:
            focus = "未专注"
        self.label.setText(f"📋 {done}/{total}　·　🎯 {focus}")
        self.adjustSize()
        self._reposition()

    def _reposition(self):
        g = self._pet.frameGeometry()
        x = g.center().x() - self.width() // 2
        y = g.top() - self.height() - 8
        x, y = _clamp_to_screen(x, y, self.width(), self.height())
        self.move(x, y)


class PetChatBar(QFrame):
    """桌宠下方常驻的圆角输入框：回车发送（文字/图片），宠物用气泡回复。"""

    def __init__(self, pet):
        super().__init__(None)
        self._pet = pet
        self._worker = None
        self._pending_b64 = None
        self.setObjectName("pet_bar")
        self.setWindowFlags(
            Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool
        )
        self.setAttribute(Qt.WA_TranslucentBackground, True)
        self.setAttribute(Qt.WA_StyledBackground, True)
        # 注意：不给输入框容器设 WA_ShowWithoutActivating，
        # 否则这个 Tool 窗口在 Windows 上永不激活，输入框点击/打字会「无效」。

        lay = QHBoxLayout(self)
        lay.setContentsMargins(8, 5, 8, 5)
        lay.setSpacing(6)
        self.input = ImagePasteLineEdit()
        self.input.setObjectName("pet_input")
        self.input.setPlaceholderText("和艾莲说点什么…（可粘贴图片）")
        self.input.returnPressed.connect(self._send)
        self.input.image_pasted.connect(self._on_image_pasted)
        # 对话时播放 talking 动画
        self.input.focus_gained.connect(
            lambda: (self._pet._touch(), self._pet._set_anim("talking")))
        self.input.focus_lost.connect(lambda: self._pet._set_anim("normal"))
        lay.addWidget(self.input, 1)
        send_btn = QPushButton("➤")
        send_btn.setObjectName("primary")
        send_btn.setFixedSize(28, 26)
        send_btn.setCursor(Qt.PointingHandCursor)
        send_btn.setToolTip("发送")
        send_btn.clicked.connect(self._send)
        lay.addWidget(send_btn)

        # 语音输入：按住 🎤 说话，松开识别成文字直接发送（可在右键菜单开关）
        self.mic_btn = QPushButton("🎤")
        self.mic_btn.setObjectName("secondary")
        self.mic_btn.setFixedSize(28, 26)
        self.mic_btn.setCursor(Qt.PointingHandCursor)
        self.mic_btn.setToolTip("按住说话，松开识别发送")
        self.mic_btn.pressed.connect(self._mic_pressed)
        self.mic_btn.released.connect(self._mic_released)
        self.mic_btn.hide()
        lay.addWidget(self.mic_btn)

        self.setFixedWidth(296)
        self.adjustSize()

    def set_voice_enabled(self, enabled):
        """语音开关：显示/隐藏麦克风按钮。"""
        if enabled:
            self.mic_btn.show()
        else:
            self.mic_btn.hide()
            self.mic_btn.setStyleSheet("")

    def _mic_pressed(self):
        pet = self._pet
        pet._touch()
        if pet._voice is not None:
            pet._voice.start()
            self.mic_btn.setStyleSheet(
                "QPushButton { background: #C0392B; color: white; border-radius: 5px; }")
            self.mic_btn.setText("🔴")

    def _mic_released(self):
        pet = self._pet
        if pet._voice is not None:
            pet._voice.stop()
        self.mic_btn.setStyleSheet("")
        self.mic_btn.setText("🎤")

    def _reposition(self):
        """始终放在桌宠下方；下方放不下时把宠物往上挪，而不是让输入框跑到上方。"""
        pet = self._pet
        g = pet.frameGeometry()
        x = g.center().x() - self.width() // 2
        y = g.bottom() + 6
        geo = QApplication.primaryScreen().availableGeometry()
        if y + self.height() > geo.bottom() - 4:
            need = self.height() + 12
            pet._suppress_reposition = True
            try:
                pet.move(g.left(), max(geo.top() + 4 + need, g.top() - need))
            finally:
                pet._suppress_reposition = False
            g = pet.frameGeometry()
            y = g.bottom() + 6
        x, y = _clamp_to_screen(x, y, self.width(), self.height())
        self.move(x, y)

    def _set_busy(self, busy):
        self.input.setEnabled(not busy)
        self.input.setPlaceholderText(
            "艾莲在想着…" if busy else "和艾莲说点什么…（可粘贴图片）"
        )

    def _on_image_pasted(self, img):
        self._pending_b64 = _image_to_png_b64(img)
        self._pet.speak_bubble("已粘贴图片，回车发送…", pixmap=QPixmap.fromImage(img))

    def _send(self):
        text = self.input.text().strip()
        b64 = self._pending_b64
        self._pending_b64 = None
        if not text and not b64:
            return
        self.input.clear()
        pet = self._pet
        pet._touch()
        pet._last_user_text = text  # 给「被夸」识别用
        pet._set_anim("talking")    # 思考中
        chat_win = pet._chat if pet._chat is not None and pet._chat.isVisible() else None
        if chat_win is not None:
            if b64:
                chat_win.append_image("me", b64)
            if text:
                chat_win.append_message("me", text)

        if b64:
            # 有图片：直接走视觉模型（图片指令不好本地解析）
            self._set_busy(True)
            service = chat.ChatService(pet._win.data.get("pet_chat"))
            self._worker = ChatWorker(service, text, image_b64=b64)
            self._worker.reply_ready.connect(self._deliver)
            self._worker.finished.connect(lambda: self._set_busy(False))
            self._worker.start()
        else:
            handled, reply = chat.handle_command(text, pet._win)
            if handled:
                self._deliver(reply)
            else:
                self._set_busy(True)
                service = chat.ChatService(pet._win.data.get("pet_chat"))
                self._worker = ChatWorker(service, text)
                self._worker.reply_ready.connect(self._deliver)
                self._worker.finished.connect(lambda: self._set_busy(False))
                self._worker.start()

    def _deliver(self, reply):
        self._set_busy(False)
        pet = self._pet
        pet.refresh_status()
        pet.speak_bubble(reply)
        pet._tts_speak(reply)
        pet.react_to_reply(reply)
        if pet._chat is not None and pet._chat.isVisible():
            pet._chat.append_message("pet", reply)


class ChatWindow(QWidget):
    """完整聊天记录窗口：气泡消息 + 输入框，可指令改计划、可 AI 闲聊、可发图。"""

    def __init__(self, win, pet):
        super().__init__(None)
        self._win = win
        self._pet = pet
        self._worker = None
        self._drag_off = None
        self._pending_b64 = None

        self.setObjectName("chat_window")
        self.setWindowFlags(
            Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool
        )
        self.setAttribute(Qt.WA_StyledBackground, True)
        self.setFixedSize(320, 400)
        self._position_near_pet()

        root = QVBoxLayout(self)
        root.setContentsMargins(0, 0, 0, 0)
        root.setSpacing(0)

        # 头部
        head = QHBoxLayout()
        head.setContentsMargins(12, 8, 6, 4)
        title = QLabel("💬 和艾莲聊聊")
        title.setObjectName("title")
        head.addWidget(title)
        head.addStretch(1)
        self.busy_lbl = QLabel("…")
        self.busy_lbl.setObjectName("subtitle")
        self.busy_lbl.setVisible(False)
        head.addWidget(self.busy_lbl)
        settings_btn = QPushButton("⚙")
        settings_btn.setObjectName("ghost_icon")
        settings_btn.setFixedSize(24, 24)
        settings_btn.setToolTip("AI 对话设置")
        settings_btn.clicked.connect(self._open_settings)
        head.addWidget(settings_btn)
        close_btn = QPushButton("✕")
        close_btn.setObjectName("close_icon")
        close_btn.setFixedSize(24, 24)
        close_btn.clicked.connect(self.hide)
        head.addWidget(close_btn)
        root.addLayout(head)

        # 消息区
        self.scroll = QScrollArea()
        self.scroll.setObjectName("chat_scroll")
        self.scroll.setWidgetResizable(True)
        self.scroll.setFrameShape(QFrame.NoFrame)
        self.msg_host = QWidget()
        self.msg_host.setObjectName("chat_host")
        self.msg_layout = QVBoxLayout(self.msg_host)
        self.msg_layout.setContentsMargins(10, 4, 10, 4)
        self.msg_layout.setSpacing(8)
        self.msg_layout.addStretch(1)
        self.scroll.setWidget(self.msg_host)
        root.addWidget(self.scroll, 1)

        # 输入区
        in_row = QHBoxLayout()
        in_row.setContentsMargins(8, 6, 8, 8)
        self.input = ImagePasteLineEdit()
        self.input.setObjectName("chat_input")
        self.input.setPlaceholderText("输入消息或粘贴图片，回车发送…")
        self.input.returnPressed.connect(self._send)
        self.input.image_pasted.connect(self._on_image_pasted)
        self.input.focus_gained.connect(
            lambda: (self._pet._touch(), self._pet._set_anim("talking")))
        self.input.focus_lost.connect(lambda: self._pet._set_anim("normal"))
        in_row.addWidget(self.input, 1)
        send_btn = QPushButton("发送")
        send_btn.setObjectName("primary")
        send_btn.setFixedHeight(30)
        send_btn.clicked.connect(self._send)
        in_row.addWidget(send_btn)
        root.addLayout(in_row)

        self.append_message(
            "pet",
            "你好呀～我是艾莲，你的桌面宠物。"
            "可以陪你聊天、帮你管计划，比如「添加 背单词」「划掉 背单词」"
            "「列出计划」，也可以给我发图片哦。",
        )

    def closeEvent(self, event):
        # 防止 Windows 模态对话框怪癖把聊天窗关掉导致程序退出
        event.ignore()

    # ---------- 位置/拖动 ----------
    def _position_near_pet(self):
        g = self._pet.frameGeometry()
        x = g.left() - (self.width() - self._pet.width()) // 2
        y = g.top() - self.height() - 12
        screen = QApplication.primaryScreen()
        geo = screen.availableGeometry()
        x = max(geo.left() + 8, min(x, geo.right() - self.width() - 8))
        y = max(geo.top() + 8, min(y, geo.bottom() - self.height() - 8))
        self.move(x, y)

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            self._drag_off = event.globalPos() - self.frameGeometry().topLeft()

    def mouseMoveEvent(self, event):
        if self._drag_off is not None and event.buttons() & Qt.LeftButton:
            self.move(event.globalPos() - self._drag_off)

    def mouseReleaseEvent(self, event):
        self._drag_off = None

    # ---------- 消息 ----------
    def append_message(self, role, text):
        """追加一条文本消息（role: 'me' / 'pet'）。"""
        if not text:
            return
        bubble = QLabel(text)
        bubble.setWordWrap(True)
        bubble.setTextInteractionFlags(Qt.TextSelectableByMouse)
        host = QWidget()
        host.setObjectName("chat_host")
        h = QHBoxLayout(host)
        h.setContentsMargins(0, 0, 0, 0)
        if role == "me":
            bubble.setObjectName("chat_me")
            h.addStretch(1)
            h.addWidget(bubble)
        else:
            bubble.setObjectName("chat_pet")
            h.addWidget(bubble)
            h.addStretch(1)
        self.msg_layout.insertWidget(self.msg_layout.count() - 1, host)
        self._scroll_bottom()

    def append_image(self, role, b64):
        """追加一张图片消息（base64 PNG）。"""
        if not b64:
            return
        pm = QPixmap()
        pm.loadFromData(base64.b64decode(b64), "PNG")
        img = QLabel()
        img.setPixmap(pm.scaledToWidth(140, Qt.SmoothTransformation))
        img.setObjectName("chat_pet" if role == "pet" else "chat_me")
        host = QWidget()
        host.setObjectName("chat_host")
        h = QHBoxLayout(host)
        h.setContentsMargins(0, 0, 0, 0)
        if role == "me":
            h.addStretch(1)
            h.addWidget(img)
        else:
            h.addWidget(img)
            h.addStretch(1)
        self.msg_layout.insertWidget(self.msg_layout.count() - 1, host)
        self._scroll_bottom()

    def _scroll_bottom(self):
        QTimer.singleShot(
            0,
            lambda: self.scroll.verticalScrollBar().setValue(
                self.scroll.verticalScrollBar().maximum()
            ),
        )

    def _set_busy(self, busy):
        self.busy_lbl.setVisible(busy)
        self.input.setEnabled(not busy)

    def _on_image_pasted(self, img):
        self._pending_b64 = _image_to_png_b64(img)
        self.append_image("me", self._pending_b64)

    def _send(self):
        text = self.input.text().strip()
        b64 = self._pending_b64
        self._pending_b64 = None
        if not text and not b64:
            return
        self.input.clear()
        if text:
            self.append_message("me", text)
        self._pet._touch()
        self._pet._last_user_text = text  # 给「被夸」识别用
        self._pet._set_anim("talking")    # 思考中
        self._set_busy(True)

        if b64:
            service = chat.ChatService(self._win.data.get("pet_chat"))
            self._worker = ChatWorker(service, text, image_b64=b64)
        else:
            handled, reply = chat.handle_command(text, self._win)
            if handled:
                QTimer.singleShot(150, lambda: self._deliver(reply))
                return
            service = chat.ChatService(self._win.data.get("pet_chat"))
            self._worker = ChatWorker(service, text)
        self._worker.reply_ready.connect(self._deliver)
        self._worker.finished.connect(lambda: self._set_busy(False))
        self._worker.start()

    def _deliver(self, reply):
        self._set_busy(False)
        self.append_message("pet", reply)
        self._pet.refresh_status()
        self._pet.speak_bubble(reply)
        self._pet._tts_speak(reply)
        self._pet.react_to_reply(reply)

    def _open_settings(self):
        from widgets import SettingsDialog

        SettingsDialog(self._win).exec_()


class PetWindow(QWidget):
    """桌宠「艾莲」主窗口：显示宠物图片，支持拖拽、右键菜单、气泡与常驻输入框。

    单击仅用于拖动，不再打开主窗口；打开主窗口/设置等操作都在右键菜单里。
    """

    def __init__(self, win):
        super().__init__(None)
        self._win = win
        self._drag_offset = None
        self._press_pos = None
        self._bubble = None
        self._preview = None
        self._chat = None
        self._suppress_reposition = False
        self._last_user_text = ""  # 最近一次用户发送的消息（用于识别夸奖）

        # 加载 4 组动画（常态/对话/高兴/点赞）；任一缺失则整组退回蓝色圆球
        self._clips = {}
        for name, fname in ANI_FILES.items():
            clip = load_ani(os.path.join(_DIR, "desk_pet", fname), PET_WIDTH)
            if clip is None:
                self._clips = {n: self._make_fallback_clip() for n in ANI_FILES}
                break
            self._clips[name] = clip
        base = self._clips["normal"]
        self.setFixedSize(base.width, base.height)

        # 动画状态机：当前动画 / 帧号 / 是否一次性（播完回常态或对话态）
        self._anim_name = "normal"
        self._frame_idx = 0
        self._oneshot = False
        self._anim_timer = QTimer(self)
        self._anim_timer.timeout.connect(self._anim_tick)
        self._anim_timer.start(base.delay_ms)

        # 字幕说话动画：字幕生成时进入“说话”表情；字幕停止约 1.6s 后回常态
        self._caption_timer = QTimer(self)
        self._caption_timer.setSingleShot(True)
        self._caption_timer.setInterval(1600)
        self._caption_timer.timeout.connect(self._caption_speak_done)

        # 闲置监测：最后互动时间 + 定期检查是否太久没互动（太久 → 趴睡）
        self._last_interaction = time.monotonic()
        self._idle_check = QTimer(self)
        self._idle_check.setInterval(15 * 1000)
        self._idle_check.timeout.connect(self._check_idle)
        self._idle_check.start()
        self.setWindowFlags(
            Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool
        )
        self.setAttribute(Qt.WA_TranslucentBackground, True)
        self.setCursor(Qt.PointingHandCursor)
        self.setToolTip("拖动移动 · 右键菜单打开/设置 · 下方输入框直接对话")

        # 头顶状态条 + 下方常驻输入框
        self._status = PetStatus(self)
        self._status.hide()
        self._bar = PetChatBar(self)
        self._bar.hide()

        # 闲置闲聊：启动后 90 秒先聊一次，之后按配置间隔
        QTimer.singleShot(90 * 1000, self._idle_speak)
        cfg = win.data.setdefault("pet_idle", {"enabled": True, "interval_min": 8})
        self._idle_timer = QTimer(self)
        self._idle_timer.timeout.connect(self._idle_speak)
        self._idle_timer.start(max(2, int(cfg.get("interval_min", 8))) * 60 * 1000)

        # 实时媒体字幕（黑板）：独立于对话气泡，右键菜单开关 + 设置
        self._caption_cfg = win.data.setdefault("caption", dict(storage.DEFAULT_CAPTION))
        self._blackboard = Blackboard(lambda: self._caption_cfg)
        self._blackboard.set_language_label(lang_label(self._caption_cfg.get("language", "auto")))
        self._engine = CaptionEngine(
            get_key=lambda: secret.get("asr_api_key"),
            get_lang=lambda: (self._win.data.get("caption") or {}).get("language", "auto"),
            get_model=lambda: (self._win.data.get("caption") or {}).get(
                "model", "FunAudioLLM/SenseVoiceSmall"),
        )
        self._engine.text_ready.connect(self._on_caption_text)
        self._engine.interim_ready.connect(self._on_caption_interim)
        self._engine.status_changed.connect(self._on_caption_status)
        if self._caption_cfg.get("enabled"):
            self._engine.start()

        # 语音输入：按住麦克风按钮说话 → 识别成文字 → 当打字发出去（可开关）
        self._voice_cfg = win.data.setdefault("voice", dict(storage.DEFAULT_VOICE))
        self._voice = VoiceInputEngine(
            get_key=lambda: secret.get("asr_api_key"),
            get_lang=lambda: "auto",
            get_model=lambda: (self._win.data.get("caption") or {}).get(
                "model", "FunAudioLLM/SenseVoiceSmall"),
        )
        self._voice.text_ready.connect(self._on_voice_text)
        self._voice.status_changed.connect(self._on_voice_status)
        if self._voice_cfg.get("enabled"):
            self._bar.set_voice_enabled(True)

        # 语音播报：艾莲的回复/闲话用本地 GPT-SoVITS 合成声音（可开关）
        self._tts_cfg = win.data.setdefault("tts", dict(storage.DEFAULT_TTS))
        self._tts = SpeakingEngine(
            get_url=lambda: self._tts_cfg.get("url", storage.DEFAULT_TTS["url"]),
            get_cmd=lambda: self._tts_cfg.get("server_cmd", ""),
            get_ref=lambda: {
                "ref_audio_path": self._tts_cfg.get("ref_audio_path", ""),
                "prompt_text": self._tts_cfg.get("prompt_text", ""),
                "prompt_lang": self._tts_cfg.get("prompt_lang", "zh"),
            },
        )
        self._tts.status_changed.connect(self._on_tts_status)
        if self._tts_cfg.get("enabled"):
            self._tts.set_enabled(True)   # 恢复上次状态；后台拉起服务，关着就零占用

    # ---------- 生命周期：状态条/输入框跟随桌宠显示隐藏 ----------
    def showEvent(self, event):
        super().showEvent(event)
        if not self._anim_timer.isActive():
            self._anim_timer.start(self._clips[self._anim_name].delay_ms)
        self._status.show()
        self._bar.show()
        self._reposition_attached()
        if self._caption_cfg.get("enabled"):
            self._engine.start()
            self._blackboard.show_near(self)
        self.raise_()

    def hide(self):
        self._anim_timer.stop()
        self._status.hide()
        self._bar.hide()
        self._close_bubble()
        self._close_preview()
        self._engine.stop()
        self._blackboard.hide()
        super().hide()

    def moveEvent(self, event):
        super().moveEvent(event)
        if not self._suppress_reposition:
            self._reposition_attached()

    def closeEvent(self, event):
        # 防止 Windows 模态对话框怪癖把桌宠关掉导致程序退出
        event.ignore()

    def _reposition_attached(self):
        if self.isVisible():
            self._status._reposition()
            self._bar._reposition()

    def refresh_status(self):
        """任务被桌宠改动后立刻刷新头顶状态条。"""
        self._status.refresh()

    # ---------- 动画 ----------
    def _set_anim(self, name, oneshot=False):
        """切换到某组动画；oneshot=True 表示播完自动回到常态/对话态。"""
        if name not in self._clips:
            return
        if self._anim_name == name and self._oneshot == oneshot:
            return
        self._anim_name = name
        self._oneshot = oneshot
        self._frame_idx = 0
        self._anim_timer.start(self._clips[name].delay_ms)
        self.update()

    def _anim_tick(self):
        clip = self._clips[self._anim_name]
        n = clip.frame_count
        if n <= 1:
            return
        self._frame_idx = (self._frame_idx + 1) % n
        if self._oneshot and self._frame_idx == 0:
            self._oneshot = False
            self._return_to_base()
        self.update()

    def _return_to_base(self):
        self._set_anim(self._base_anim())

    def _base_anim(self):
        """基底动画：对话中 → talking；太久没互动 → 趴睡；否则 → 常态。"""
        if self._bar is not None and self._bar.input.hasFocus():
            return "talking"
        if self._idle_too_long():
            return "sleep"
        return "normal"

    def _idle_too_long(self):
        return (time.monotonic() - self._last_interaction) * 1000 >= IDLE_TOO_LONG_MS

    def _touch(self):
        """用户互动（点击/拖动/输入）：重置闲置计时并唤醒趴睡。"""
        self._last_interaction = time.monotonic()
        if self._anim_name == "sleep":
            self._return_to_base()

    def _check_idle(self):
        """定期检查：太久没互动且处于常态 → 进入趴睡；反之（已唤醒）回常态。"""
        if not self.isVisible():
            return
        if self._idle_too_long():
            if self._anim_name == "normal" and not self._oneshot:
                self._set_anim("sleep")
        elif self._anim_name == "sleep":
            self._return_to_base()

    def react_to_reply(self, reply):
        """收到回复时：任务完成 → 点赞；被夸 → 高兴；普通回答 → 说话(talking_1)。"""
        ut = self._last_user_text or ""
        self._last_user_text = ""
        if any(m in (reply or "") for m in _DONE_REPLY_MARKS):
            self._set_anim("present", oneshot=True)
        elif _is_praise(ut):
            self._set_anim("happy", oneshot=True)
        else:
            self._set_anim("talking")  # 回答/思考中：持续说话动画，直到气泡消失回常态

    def _caption_speak(self):
        """字幕生成时：艾莲进入“说话”表情（会唤醒趴睡）。字幕持续到达则保持说话，
        停止约 1.6s 后回常态。纯 UI 动画，不影响识别速度。"""
        if self._anim_name != "talking":
            self._set_anim("talking")
        self._caption_timer.start()

    def _caption_speak_done(self):
        if self._anim_name == "talking":
            self._return_to_base()

    # ---------- 外观 ----------
    def _make_fallback_clip(self):
        pm = QPixmap(PET_WIDTH, PET_WIDTH)
        pm.fill(Qt.transparent)
        p = QPainter(pm)
        p.setRenderHint(QPainter.Antialiasing)
        p.setBrush(QColor("#3B82F6"))
        p.setPen(Qt.NoPen)
        p.drawEllipse(PET_WIDTH // 4, PET_WIDTH // 4, PET_WIDTH // 2, PET_WIDTH // 2)
        p.setPen(QColor("#FFFFFF"))
        p.setFont(QFont("Microsoft YaHei UI", 26, QFont.Bold))
        p.drawText(pm.rect(), Qt.AlignCenter, "宠")
        p.end()
        return AniClip([pm], 83)

    def paintEvent(self, event):
        clip = self._clips.get(self._anim_name)
        if clip is None:
            return
        p = QPainter(self)
        p.drawPixmap(0, 0, clip.frames[self._frame_idx])
        p.end()

    def position_bottom_right(self):
        screen = QApplication.primaryScreen()
        geo = screen.availableGeometry()
        self.move(geo.right() - self.width() - 16, geo.bottom() - self.height() - 16)

    # ---------- 交互 ----------
    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            self._press_pos = event.pos()
            self._drag_offset = event.globalPos() - self.frameGeometry().topLeft()
            self._close_bubble()
            self._touch()

    def mouseMoveEvent(self, event):
        if self._drag_offset is not None and event.buttons() & Qt.LeftButton:
            self.move(event.globalPos() - self._drag_offset)
            self._touch()

    def mouseReleaseEvent(self, event):
        # 单击不再打开主窗口（避免误触弹窗）；打开/设置等都在右键菜单里
        self._drag_offset = None
        self._press_pos = None

    def contextMenuEvent(self, event):
        menu = QMenu(self)
        restore_action = menu.addAction("回到主窗口")
        settings_action = menu.addAction("设置…")
        chat_action = menu.addAction("打开聊天记录 💬")
        menu.addSeparator()
        cap_action = menu.addAction("启用字幕")
        cap_action.setCheckable(True)
        cap_action.setChecked(bool(self._caption_cfg.get("enabled")))
        cap_settings_action = menu.addAction("字幕设置…")
        menu.addSeparator()
        voice_action = menu.addAction("语音输入 🎤")
        voice_action.setCheckable(True)
        voice_action.setChecked(bool(self._voice_cfg.get("enabled")))
        menu.addSeparator()
        tts_action = menu.addAction("语音播报 🔊")
        tts_action.setCheckable(True)
        tts_action.setChecked(bool(self._tts_cfg.get("enabled")))
        tts_cfg_action = menu.addAction("语音播报设置…")
        menu.addSeparator()
        quit_action = menu.addAction("退出程序")
        chosen = menu.exec_(event.globalPos())
        if chosen == restore_action:
            self._win.restore_from_circle()
        elif chosen == settings_action:
            from widgets import SettingsDialog
            SettingsDialog(self._win).exec_()
        elif chosen == chat_action:
            self.open_chat()
        elif chosen == cap_action:
            self._toggle_caption(cap_action.isChecked())
        elif chosen == cap_settings_action:
            from captions import CaptionSettingsDialog
            CaptionSettingsDialog(self._win).exec_()
            self._apply_caption_settings()
        elif chosen == voice_action:
            self._toggle_voice(voice_action.isChecked())
        elif chosen == tts_action:
            self._toggle_tts(tts_action.isChecked())
        elif chosen == tts_cfg_action:
            self._set_tts_cmd()
        elif chosen == quit_action:
            self._win.quit_now()

    # ---------- 实时字幕（黑板） ----------
    def _toggle_caption(self, on):
        """右键菜单「启用字幕」开关：开→采集+识别+黑板；关→全部停掉。"""
        self._caption_cfg["enabled"] = bool(on)
        self._win.save()
        if on:
            self._engine.start()
            if self.isVisible():
                self._blackboard.show_near(self)
        else:
            self._engine.stop()
            self._blackboard.hide()

    # ---------- 语音输入 ----------
    def _toggle_voice(self, on):
        """右键菜单「语音输入」开关：开→显示麦克风按钮；关→停掉录音并隐藏。"""
        self._voice_cfg["enabled"] = bool(on)
        self._win.save()
        if not on:
            self._voice.stop()
        self._bar.set_voice_enabled(on)

    def _on_voice_text(self, text):
        """语音识别结果：直接塞进输入框并发送（加计划/对话都走正常路径）。"""
        self._bar.input.setText(text)
        self._bar._send()

    def _on_voice_status(self, status):
        """识别状态：错误提示用气泡告诉主人。"""
        if status.startswith("⚠"):
            self.speak_bubble(status)

    # ---------- 语音播报 ----------
    def _toggle_tts(self, on):
        """右键「语音播报」开关：开→后台拉起本地语音服务；关→停播并终止服务（零占用）。"""
        self._tts_cfg["enabled"] = bool(on)
        self._win.save()
        self._tts.set_enabled(bool(on))

    def _set_tts_cmd(self):
        """语音播报设置：服务启动命令 + 参考音频（音色来源）等。"""
        dlg = QDialog(self)
        dlg.setWindowTitle("语音播报设置")
        form = QFormLayout(dlg)
        cmd_edit = QLineEdit(dlg)
        cmd_edit.setText(self._tts_cfg.get("server_cmd", ""))
        cmd_edit.setPlaceholderText('例：cmd /c "cd /d C:\\Users\\21495\\gsvi && C:\\Users\\21495\\gsvi_venv\\Scripts\\python.exe api_v2.py -p 9880"')
        ref_edit = QLineEdit(dlg)
        ref_edit.setText(self._tts_cfg.get("ref_audio_path", ""))
        ref_edit.setPlaceholderText("例：C:/Users/21495/gsvi/custom_refs/ref_sapi_zh.wav（艾莲音色的参考音频）")
        prompt_edit = QLineEdit(dlg)
        prompt_edit.setText(self._tts_cfg.get("prompt_text", ""))
        prompt_edit.setPlaceholderText("参考音频里说的话（转写，填了音色更稳）")
        lang_edit = QLineEdit(dlg)
        lang_edit.setText(self._tts_cfg.get("prompt_lang", "zh"))
        form.addRow("服务启动命令", cmd_edit)
        form.addRow("参考音频路径", ref_edit)
        form.addRow("参考音频文字", prompt_edit)
        form.addRow("参考音频语言", lang_edit)
        hint = QLabel("服务命令留空 = 你手动启动服务，宠物只发文字。\n"
                      "填了命令 = 开启语音播报时宠物自动拉起、关闭/退出时终止（零占用）。")
        hint.setWordWrap(True)
        hint.setStyleSheet("color:#888;")
        form.addRow(hint)
        btns = QHBoxLayout()
        ok_btn = QPushButton("保存", dlg)
        cancel_btn = QPushButton("取消", dlg)
        btns.addStretch(1)
        btns.addWidget(ok_btn)
        btns.addWidget(cancel_btn)
        form.addRow(btns)
        ok_btn.clicked.connect(dlg.accept)
        cancel_btn.clicked.connect(dlg.reject)
        if dlg.exec_() == QDialog.Accepted:
            self._tts_cfg["server_cmd"] = cmd_edit.text().strip()
            self._tts_cfg["ref_audio_path"] = ref_edit.text().strip()
            self._tts_cfg["prompt_text"] = prompt_edit.text().strip()
            lang = lang_edit.text().strip() or "zh"
            self._tts_cfg["prompt_lang"] = lang
            self._win.save()

    def _tts_speak(self, text):
        """艾莲的回复/闲话 → 合成语音播报（开关关着时内部直接返回，零占用）。"""
        if self._tts_cfg.get("enabled"):
            self._tts.speak(text)

    def _on_tts_status(self, status):
        """播报状态：服务未就绪等提示用气泡告诉主人（节流后）。"""
        if status.startswith("⚠"):
            self.speak_bubble(status)

    def _on_caption_text(self, text):
        self._caption_speak()   # 字幕生成时艾莲进入“说话”表情
        # 定稿：替换预览行入幕（无预览行则直接追加）
        self._blackboard.finalize_interim(text)

    def _on_caption_interim(self, text):
        self._caption_speak()   # 字幕生成时艾莲进入“说话”表情
        # 半成品：替换当前预览行，不新增
        self._blackboard.show_interim(text)

    def _on_caption_status(self, status):
        self._blackboard.set_status(status)

    def _apply_caption_settings(self):
        """字幕设置对话框确定后：刷新黑板外观/语言，必要时重新定位。"""
        self._caption_cfg.setdefault("language", "auto")
        self._blackboard.set_language_label(lang_label(self._caption_cfg.get("language", "auto")))
        self._blackboard.apply_settings()
        if self._caption_cfg.get("enabled") and self.isVisible():
            self._blackboard.show_near(self)

    # ---------- 预览（由头顶状态条取代，保留空实现以兼容调用） ----------
    def _close_preview(self):
        if self._preview is not None:
            self._preview.close()
            self._preview = None

    # ---------- 气泡 ----------
    def speak_bubble(self, text, pixmap=None):
        if not self.isVisible():
            return
        self._close_bubble()
        self._bubble = PetBubble(self, text=text, pixmap=pixmap)
        self._bubble.closed.connect(self._on_bubble_closed)
        self._bubble.show()

    def _on_bubble_closed(self):
        """气泡自动消失：回答结束，若没在对话则回到常态（闲置过久则趴睡）。"""
        self._bubble = None
        if self._anim_name == "talking" and not self._oneshot:
            base = self._base_anim()
            if base != "talking":
                self._set_anim(base)

    def _close_bubble(self):
        if self._bubble is not None:
            self._bubble.close()
            self._bubble = None

    def _idle_speak(self):
        if not self.isVisible():
            return
        if self._chat is not None and self._chat.isVisible():
            return
        if self._anim_name == "sleep":  # 趴睡中不主动冒泡
            return
        cfg = self._win.data.get("pet_idle", {})
        if not cfg.get("enabled", True):
            return
        tasks = self._win.data["daily"].get(storage.today_str(), [])
        pending = [t for t in tasks if not t.get("done")]
        if pending and random.random() < 0.6:
            text = f"还有 {len(pending)} 项计划没完成哦，一起加油！"
        else:
            text = random.choice(chat.IDLE_PHRASES)
        self.speak_bubble(text)
        self._tts_speak(text)   # 语音播报（开的话，艾莲把闲话念出来）
        self._set_anim("happy", oneshot=True)  # 鼓励 → 高兴动作

    # ---------- 聊天记录窗 ----------
    def open_chat(self):
        self._close_bubble()
        if self._chat is None:
            self._chat = ChatWindow(self._win, self)
        self._chat.show()
        self._chat.raise_()
        self._chat.activateWindow()
        self._chat.input.setFocus()
