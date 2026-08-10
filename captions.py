# -*- coding: utf-8 -*-
"""实时媒体字幕：采集系统播放声音（WASAPI 环回）→ 按静音切分语句 → 云端语音识别 → 黑板字幕。

架构：
- 采集线程：pyaudiowpatch 打开「默认输出设备的 Loopback」，按 0.5s 窗口读 PCM，
  降混为单声道 + 重采样到 16kHz，连同能量一起放入队列。
- 识别线程：能量门限（VAD）把连续有声音的窗口攒成一个「语句」；连续静音 ≥1.2s 或
  语句超长(15s)时提交给硅基流动 SenseVoiceSmall 转写，结果通过 text_ready 信号发出。
- 全程后台线程，绝不阻塞 UI；Key 从 secret.json 读取（不入库）。

与聊天完全独立：字幕走 Blackboard 黑板窗口，对话走气泡/聊天窗，互不影响。
"""
import io
import json
import queue
import secrets
import threading
import time
import urllib.error
import urllib.request
import wave

import numpy as np
from PyQt5.QtCore import QObject, pyqtSignal
from PyQt5.QtWidgets import (
    QColorDialog, QComboBox, QDialog, QDialogButtonBox, QFormLayout, QFrame,
    QHBoxLayout, QLabel, QLineEdit, QPushButton, QSpinBox, QVBoxLayout,
)

import secret

# ---------- 常量 ----------
ASR_BASE_URL = "https://api.siliconflow.cn/v1/audio/transcriptions"
# 硅基流动两款免费 ASR 模型（无用量限制）：
# - SenseVoiceSmall：低延迟、实时友好，中文准、自带标点，支持中/英/日/韩/粤
# - TeleSpeechASR：中文高精度、方言/口音/嘈杂音频更强，但推理慢、不算实时
MODEL_ITEMS = [
    ("FunAudioLLM/SenseVoiceSmall", "SenseVoiceSmall · 实时低延迟（推荐）"),
    ("TeleAI/TeleSpeechASR", "TeleSpeechASR · 高精度 / 方言强"),
]
DEFAULT_MODEL = MODEL_ITEMS[0][0]
ASR_MODEL = DEFAULT_MODEL   # 兼容旧引用


def model_label(value):
    for val, label in MODEL_ITEMS:
        if val == value:
            return label.split("·")[0].strip()
    return value or DEFAULT_MODEL

SR = 16000          # 识别采样率（单声道）
CHUNK_S = 0.5       # 每窗口秒数
ENERGY_THRESH = 0.015   # 归一化 RMS 基准门限
SILENCE_END_S = 0.7     # 语句结束后静音多久提交（越小字幕出得越早）
MAX_UTTER_S = 15.0      # 语句最长秒数（防止一直说不停导致字幕迟到）
MIN_UTTER_S = 0.35      # 短于该时长的声音视为杂音/爆音，不上传识别
THRESH_MIN, THRESH_MAX = 0.008, 0.03   # 自适应 VAD 门限范围（应对声音偏小的视频）
INTERIM_S = 1.5         # 说话期间每隔这么久出一版“半成品”字幕（实时预览）
INTERIM_MIN_S = 1.0     # 攒够这么多秒才出第一版半成品，避免太碎
QUEUE_MAX = 8           # 采集→切分队列上限，超过丢旧保新（防内存积压）


def _adapt_threshold(noise_floor, thresh, energy):
    """自适应 VAD 门限：安静时把门限压下来（视频声音小也能识别），
    有持续噪音时缓慢抬高（避免把噪音当人声刷屏）。返回 (新底噪, 新门限)。"""
    if energy < thresh:            # 当前是“安静”窗口 → 用它刷新底噪
        noise_floor = noise_floor * 0.8 + energy * 0.2
    else:                          # 正在说话 → 底噪缓慢回升，防止越降越低
        noise_floor *= 1.02
    return noise_floor, max(THRESH_MIN, min(THRESH_MAX, noise_floor * 1.5))

# 语言：值传给 API，标签显示在设置/黑板
LANG_ITEMS = [
    ("auto", "自动识别"),
    ("zh", "中文"),
    ("en", "英文"),
    ("yue", "粤语"),
    ("ja", "日语"),
    ("ko", "韩语"),
]
LANG_VALUE_TO_LABEL = {v: l for v, l in LANG_ITEMS}


def lang_label(value):
    return LANG_VALUE_TO_LABEL.get(value, "自动识别")


class CaptionUnavailable(Exception):
    """字幕不可用（未配 Key / 网络失败 / 采集失败）。"""


# ---------- 音频处理 ----------
def _resample(x, src, dst):
    """线性重采样到目标采样率（够用且稳）。"""
    if src == dst or len(x) < 2:
        return x
    n_out = max(1, int(round(len(x) * dst / src)))
    if n_out == len(x):
        return x
    xi = np.linspace(0, len(x) - 1, n_out)
    return np.interp(xi, np.arange(len(x)), x).astype(np.float32)


def _samples_to_wav(bufs):
    """一串 16k 单声道 float 数组 → WAV 字节（16bit PCM）。

    先裁掉首尾近静音，让云端模型只听到干净人声（省流量、提高识别精度）。
    """
    concat = bufs[0] if len(bufs) == 1 else np.concatenate(bufs)
    peak = float(np.abs(concat).max()) if len(concat) else 0.0
    if peak > 0:
        cut = max(peak * 0.02, 0.003)
        nz = np.nonzero(np.abs(concat) > cut)[0]
        if len(nz) > 1:
            concat = concat[int(nz[0]):int(nz[-1]) + 1]
    pcm = np.clip(concat * 32767.0, -32768.0, 32767.0).astype(np.int16)
    bio = io.BytesIO()
    with wave.open(bio, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())
    return bio.getvalue()


def _transcribe(api_key, wav_bytes, language, model=ASR_MODEL, base_url=ASR_BASE_URL,
                timeout=30, retries=3):
    """multipart 上传 WAV 到硅基流动转写，返回识别文本。失败抛 CaptionUnavailable。

    瞬时网络抖动/服务端过载(5xx)/限流(429)都会自动重试，退避递增；
    只有 401/403（Key 无效）立即失败不重试。
    """
    boundary = "----KaoyanCaption" + secrets.token_hex(4)
    fields = [("model", model), ("response_format", "json")]
    if language and language != "auto":
        fields.append(("language", language))
    crlf = b"\r\n"
    body = bytearray()
    for k, v in fields:
        body += b"--" + boundary.encode() + crlf
        body += ('Content-Disposition: form-data; name="%s"' % k).encode() + crlf + crlf
        body += v.encode("utf-8") + crlf
    body += b"--" + boundary.encode() + crlf
    body += b'Content-Disposition: form-data; name="file"; filename="audio.wav"' + crlf
    body += b"Content-Type: audio/wav" + crlf + crlf
    body += wav_bytes + crlf
    body += b"--" + boundary.encode() + b"--" + crlf

    last_err = None
    for attempt in range(max(1, retries)):
        req = urllib.request.Request(
            base_url, data=bytes(body),
            headers={
                "Authorization": "Bearer " + api_key,
                "Content-Type": "multipart/form-data; boundary=" + boundary,
                "User-Agent": "KaoyanPlanner/1.0",
            },
            method="POST",
        )
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                raw = resp.read().decode("utf-8", errors="replace")
            break
        except urllib.error.HTTPError as exc:
            if exc.code in (401, 403):
                raise CaptionUnavailable("⚠ ASR Key 无效，请在字幕设置里检查")
            if exc.code == 429 or 500 <= exc.code < 600:
                last_err = "服务端繁忙(%d)" % exc.code
            else:
                last_err = "识别失败 (HTTP %d)" % exc.code
            if attempt + 1 < max(1, retries):
                time.sleep(1.2 * (attempt + 1))
                continue
            raise CaptionUnavailable("⚠ " + last_err + "，稍等再试")
        except Exception:
            last_err = "网络不可用"
            if attempt + 1 < max(1, retries):
                time.sleep(1.2 * (attempt + 1))
                continue
            raise CaptionUnavailable("⚠ 网络不可用，已重试 %d 次仍失败" % max(1, retries))
    try:
        data = json.loads(raw)
        text = data.get("text", "") or data.get("data", "")
        if not isinstance(text, str):
            text = ""
    except Exception:
        text = raw
    return (text or "").strip()


# ---------- 引擎 ----------
class CaptionEngine(QObject):
    """系统声音 → 字幕 的完整管线。start()/stop() 控制；信号都跨线程发到主线程。

    识别分“半成品”与“定稿”两档：说话过程中每 INTERIM_S 出一版实时预览
    （interim_ready，黑板上持续替换当前行）；整句说完再出定稿（text_ready）。
    线程带世代号：stop/start 反复切换时旧线程立即让位，不会有两套线程同时
    消费同一个队列导致同一段话被识别两遍。
    """

    text_ready = pyqtSignal(str)        # 定稿字幕（整句，黑板上替换/入幕）
    interim_ready = pyqtSignal(str)     # 半成品字幕（实时预览，黑板上替换当前行）
    status_changed = pyqtSignal(str)    # 状态提示（聆听中/错误/未配 Key）

    def __init__(self, get_key, get_lang, get_model=None, parent=None):
        super().__init__(parent)
        self._get_key = get_key     # () -> str 当前 ASR Key
        self._get_lang = get_lang   # () -> str 当前语言值
        self._get_model = get_model or (lambda: DEFAULT_MODEL)
        self._queue = queue.Queue()     # 采集线程 → 切分线程（音频窗口）
        self._trans_q = queue.Queue()   # 切分线程 → 识别线程（整段语句）
        self._running = False
        self._gen = 0                  # 世代号：防 stop/start 后旧线程复活
        self._last_final = ""          # 上一条定稿，用于去重
        self._last_interim = ""        # 上一版半成品，用于去重

    # ---------- 生命周期 ----------
    def start(self):
        if self._running:
            return
        self._running = True
        self._gen += 1
        self._drain()   # 清掉残留队列，避免重复/旧数据被新线程消费
        # 采集 / 切分 / 识别 三条线程并行：这句话在上传时，下一句仍在切分，字幕不积压
        for target in (self._capture_loop, self._vad_loop, self._trans_loop):
            threading.Thread(target=target, args=(self._gen,), daemon=True).start()
        self.status_changed.emit("🎙 聆听中…")

    def stop(self):
        if not self._running:
            return
        self._running = False
        for q in (self._queue, self._trans_q):
            try:
                q.put(None)   # 唤醒切分/识别线程退出
            except Exception:
                pass

    def _drain(self):
        for q in (self._queue, self._trans_q):
            while True:
                try:
                    q.get_nowait()
                except queue.Empty:
                    break

    # ---------- 采集 ----------
    def _open_stream(self):
        import pyaudiowpatch as pyaudio
        pa = pyaudio.PyAudio()
        try:
            loop = pa.get_default_wasapi_loopback()
        except Exception:
            loop = None
        if loop is None:
            loop = next(pa.get_loopback_device_info_generator(), None)
        if loop is None:
            pa.terminate()
            raise CaptionUnavailable("⚠ 找不到可用的音频输出设备")
        try:
            sr = int(loop["defaultSampleRate"])
            ch = int(loop["maxInputChannels"])
            stream = pa.open(
                format=pyaudio.paInt16, channels=ch, rate=sr, input=True,
                input_device_index=int(loop["index"]),
                frames_per_buffer=max(1, int(sr * CHUNK_S)),
            )
            return pa, stream, sr, ch
        except Exception:
            try:
                pa.terminate()   # 打开失败也要释放 PortAudio 句柄
            except Exception:
                pass
            raise

    def _capture_loop(self, gen):
        while self._running and gen == self._gen:
            pa = stream = None
            try:
                pa, stream, sr, ch = self._open_stream()
                n = int(sr * CHUNK_S)
                while self._running and gen == self._gen:
                    data = stream.read(n, exception_on_overflow=False)
                    a = np.frombuffer(data, dtype=np.int16).astype(np.float32) / 32768.0
                    if ch > 1:
                        a = a.reshape(-1, ch).mean(axis=1)
                    mono = _resample(a, sr, SR)
                    energy = float(np.sqrt(np.mean(mono * mono)))
                    if self._queue.qsize() >= QUEUE_MAX:
                        try:
                            self._queue.get_nowait()   # 丢最旧，保证内存有上限
                        except queue.Empty:
                            pass
                    self._queue.put((mono, energy))
            except Exception:
                if self._running and gen == self._gen:
                    self.status_changed.emit("⚠ 音频采集出错，正在重试…")
                    time.sleep(2.0)
            finally:
                if stream is not None:
                    try:
                        stream.close()
                    except Exception:
                        pass
                if pa is not None:
                    try:
                        pa.terminate()
                    except Exception:
                        pass

    # ---------- 语句切分 ----------
    def _vad_loop(self, gen):
        """能量门限把连续人声攒成一句话 → 塞进识别队列。

        说话过程中按 INTERIM_S 间隔发“半成品”作实时预览（黑板上替换当前行，
        不新增）；整句说完发“定稿”。自适应门限让声音偏小的视频也能识别；
        切分与识别线程分离，切分不阻塞。
        """
        buf = []            # 当前语句的 16k 单声道窗口
        collecting = False
        silence_s = 0.0
        since_interim = 0.0
        noise_floor, thresh = ENERGY_THRESH, ENERGY_THRESH
        while self._running and gen == self._gen:
            try:
                item = self._queue.get(timeout=1.0)
            except queue.Empty:
                if collecting:
                    self._enqueue(buf, final=True)
                    buf, collecting, silence_s, since_interim = [], False, 0.0, 0.0
                continue
            if item is None:
                break
            mono, energy = item
            noise_floor, thresh = _adapt_threshold(noise_floor, thresh, energy)
            if energy >= thresh:
                buf.append(mono)
                if not collecting:
                    collecting = True
                    since_interim = 0.0
                else:
                    since_interim += len(mono) / SR
                silence_s = 0.0
                total = sum(len(x) for x in buf) / SR
                if total >= MAX_UTTER_S:
                    self._enqueue(buf, final=True)
                    buf, collecting, silence_s, since_interim = [], False, 0.0, 0.0
                elif since_interim >= INTERIM_S and total >= INTERIM_MIN_S:
                    self._enqueue(buf, final=False)
                    since_interim = 0.0
            elif collecting:
                silence_s += len(mono) / SR
                if silence_s >= SILENCE_END_S:
                    self._enqueue(buf, final=True)
                    buf, collecting, silence_s, since_interim = [], False, 0.0, 0.0
            # 空闲静音直接丢弃

    def _enqueue(self, buf, final):
        if not buf:
            return
        total = sum(len(x) for x in buf) / SR
        if final:
            if total < MIN_UTTER_S:
                return   # 太短，多半是杂音/爆音
        elif total < INTERIM_MIN_S:
            return   # 半成品至少攒够一小段，避免太碎
        if self._trans_q.qsize() >= 4:
            return   # 网络卡顿积压时丢弃旧句，保证新字幕及时（内存有上限）
        try:
            self._trans_q.put_nowait((final, _samples_to_wav(buf)))
        except Exception:
            pass

    # ---------- 识别（独立线程，不阻塞切分） ----------
    def _trans_loop(self, gen):
        while self._running and gen == self._gen:
            try:
                item = self._trans_q.get(timeout=1.0)
            except queue.Empty:
                continue
            if item is None:
                break
            final, wav = item
            key = (self._get_key() or "").strip()
            if not key:
                self.status_changed.emit("⚠ 未配置 ASR Key：右键艾莲 → 字幕设置")
                continue
            try:
                text = _transcribe(key, wav, self._get_lang(), model=self._get_model())
            except CaptionUnavailable as exc:
                self.status_changed.emit(str(exc))
                continue
            except Exception:
                self.status_changed.emit("⚠ 识别失败")
                continue
            text = (text or "").strip()
            if final:
                if text and text != self._last_final:
                    self._last_final = text
                    self.text_ready.emit(text)
                    self.status_changed.emit("🎙 聆听中…")
                # 与上一条定稿完全相同的句子：跳过，不重复入幕
            else:
                if text and text != self._last_interim:
                    self._last_interim = text
                    self.interim_ready.emit(text)
            wav = None   # 立即释放本次上传的音频字节


# ---------- 字幕设置对话框 ----------
_COLOR_PRESETS = [
    ("粉笔白", "#F5F0E6"),
    ("亮黄", "#FFE873"),
    ("粉红", "#FFB6C1"),
    ("青绿", "#A8E6CF"),
    ("橙黄", "#FFB26B"),
]


class CaptionSettingsDialog(QDialog):
    """字幕设置：ASR Key（写入 secret.json，不入库）+ 语言 + 字号 + 颜色。"""

    def __init__(self, win, parent=None):
        super().__init__(parent)
        self._win = win
        cfg = win.data.setdefault("caption", dict())
        self.setWindowTitle("字幕设置")
        self.setModal(True)
        self.setMinimumWidth(360)
        self._color = cfg.get("color", "#F5F0E6")

        root = QVBoxLayout(self)
        root.setContentsMargins(16, 16, 16, 16)
        root.setSpacing(10)

        card = QFrame()
        card.setObjectName("card")
        lay = QVBoxLayout(card)
        lay.setContentsMargins(12, 10, 12, 10)
        lay.setSpacing(8)
        title = QLabel("🎙 实时媒体字幕")
        title.setObjectName("title")
        lay.addWidget(title)

        form = QFormLayout()
        form.setSpacing(8)
        self._key = QLineEdit(secret.get("asr_api_key"))
        self._key.setEchoMode(QLineEdit.Password)
        self._key.setPlaceholderText("申请硅基流动 Key：cloud.siliconflow.cn")
        form.addRow("识别 Key", self._key)

        self._model = QComboBox()
        cur_model = cfg.get("model", DEFAULT_MODEL)
        for idx, (val, label) in enumerate(MODEL_ITEMS):
            self._model.addItem(label, val)
            if val == cur_model:
                self._model.setCurrentIndex(idx)
        form.addRow("识别模型", self._model)

        self._lang = QComboBox()
        cur = cfg.get("language", "auto")
        for idx, (val, label) in enumerate(LANG_ITEMS):
            self._lang.addItem(label, val)
            if val == cur:
                self._lang.setCurrentIndex(idx)
        form.addRow("字幕语言", self._lang)

        self._size = QSpinBox()
        self._size.setRange(12, 40)
        self._size.setValue(int(cfg.get("font_size", 18)))
        form.addRow("字体大小", self._size)

        self._color_btns = []
        color_row = QHBoxLayout()
        color_row.setSpacing(6)
        for name, val in _COLOR_PRESETS:
            btn = QPushButton(name)
            btn.setObjectName("preset")
            btn.setFixedHeight(26)
            btn.setStyleSheet("QPushButton { color: %s; }" % val)
            btn.clicked.connect(lambda _, v=val: self._pick_color(v))
            color_row.addWidget(btn)
            self._color_btns.append(btn)
        custom = QPushButton("自定义…")
        custom.setObjectName("secondary")
        custom.setFixedHeight(26)
        custom.clicked.connect(self._custom_color)
        color_row.addWidget(custom)
        color_row.addStretch(1)
        form.addRow("字幕颜色", color_row)
        lay.addLayout(form)

        tip = QLabel(
            "识别的是「电脑播放的声音」（视频/网课/音乐），不是麦克风。"
            "首次使用需在右上角设置里填入硅基流动识别 Key（免费申请，无用量限制）。"
            "语言选「自动识别」即可中英文混用。"
        )
        tip.setObjectName("subtitle")
        tip.setWordWrap(True)
        lay.addWidget(tip)
        root.addWidget(card)

        buttons = QDialogButtonBox(QDialogButtonBox.Ok | QDialogButtonBox.Cancel)
        buttons.button(QDialogButtonBox.Ok).setText("确定")
        buttons.button(QDialogButtonBox.Cancel).setText("取消")
        buttons.accepted.connect(self._accept)
        buttons.rejected.connect(self.reject)
        root.addWidget(buttons)

    def _pick_color(self, value):
        self._color = value
        self._highlight()

    def _custom_color(self):
        color = QColorDialog.getColor()
        if color.isValid():
            self._color = color.name().upper()

    def _highlight(self):
        for btn in self._color_btns:
            btn.setProperty("checked", False)
            btn.style().unpolish(btn)
            btn.style().polish(btn)

    def _accept(self):
        cfg = self._win.data.setdefault("caption", dict())
        cfg["model"] = self._model.currentData()
        cfg["language"] = self._lang.currentData()
        cfg["font_size"] = self._size.value()
        cfg["color"] = self._color
        secret.set("asr_api_key", self._key.text().strip())
        self._win.save()
        self.accept()
