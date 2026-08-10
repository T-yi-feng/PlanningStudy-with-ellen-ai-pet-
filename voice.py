# -*- coding: utf-8 -*-
"""语音输入：按住麦克风按钮说话，松开后识别成文字，直接进聊天输入框执行。

用的是**系统默认麦克风**（不是播放的媒体声音），识别复用 captions 的硅基流动 ASR，
与字幕黑板完全独立。可配合对话/指令使用：说「添加 背单词」就能直接加计划。
"""
import threading

import numpy as np
from PyQt5.QtCore import QObject, pyqtSignal

from captions import (
    CaptionUnavailable, SR, _resample, _samples_to_wav, _transcribe,
)


class VoiceUnavailable(Exception):
    """麦克风不可用。"""


class VoiceInputEngine(QObject):
    """按住说话：start() 开始采集，stop() 结束并在后台识别。"""

    text_ready = pyqtSignal(str)        # 识别出的文本（主线程收到后可当打字发出去）
    status_changed = pyqtSignal(str)    # 状态提示（聆听中/识别中/待命/错误）

    def __init__(self, get_key, get_lang, get_model=None, parent=None):
        super().__init__(parent)
        self._get_key = get_key         # () -> str ASR Key
        self._get_lang = get_lang       # () -> str 语言
        self._get_model = get_model or (lambda: "FunAudioLLM/SenseVoiceSmall")
        self._bufs = []                 # 本次录音的 16k 单声道窗口
        self._recording = False
        self._thread = None

    def is_recording(self):
        return self._recording

    # ---------- 生命周期 ----------
    def start(self):
        """按住说话：开始采集。"""
        if self._recording:
            return
        self._recording = True
        self._bufs = []
        self._thread = threading.Thread(target=self._record_loop, daemon=True)
        self._thread.start()
        self.status_changed.emit("🎙 聆听中…（松开结束）")

    def stop(self):
        """松开：结束采集，后台识别并把文本发给 text_ready。"""
        if not self._recording:
            return
        self._recording = False
        if self._thread is not None:
            self._thread.join(timeout=2.0)
            self._thread = None
        if self._bufs:
            wav = _samples_to_wav(self._bufs)
            self._bufs = []
            threading.Thread(target=self._transcribe_thread, args=(wav,),
                             daemon=True).start()
        self.status_changed.emit("🔇 待命")

    # ---------- 采集 ----------
    def _open_mic(self):
        import pyaudiowpatch as pyaudio
        pa = pyaudio.PyAudio()
        try:
            info = pa.get_default_input_device_info()
        except Exception:
            info = None
        if info is None:
            for i in range(pa.get_device_count()):
                d = pa.get_device_info_by_index(i)
                if d.get("maxInputChannels", 0) > 0:
                    info = d
                    break
        if info is None:
            pa.terminate()
            raise VoiceUnavailable("⚠ 找不到麦克风")
        sr = int(info["defaultSampleRate"])
        ch = int(info["maxInputChannels"]) or 1
        try:
            stream = pa.open(
                format=pyaudio.paInt16, channels=ch, rate=sr, input=True,
                input_device_index=int(info["index"]),
                frames_per_buffer=max(1, int(sr * 0.5)),
            )
            return pa, stream, sr, ch
        except Exception:
            try:
                pa.terminate()
            except Exception:
                pass
            raise

    def _record_loop(self):
        pa = stream = None
        try:
            pa, stream, sr, ch = self._open_mic()
            n = int(sr * 0.5)
            while self._recording:
                data = stream.read(n, exception_on_overflow=False)
                a = np.frombuffer(data, dtype=np.int16).astype(np.float32) / 32768.0
                if ch > 1:
                    a = a.reshape(-1, ch).mean(axis=1)
                mono = _resample(a, sr, SR)
                self._bufs.append(mono)
        except Exception:
            if self._recording:
                self.status_changed.emit("⚠ 麦克风采集出错")
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

    # ---------- 识别（后台线程，不卡 UI） ----------
    def _transcribe_thread(self, wav):
        key = (self._get_key() or "").strip()
        if not key:
            self.status_changed.emit("⚠ 未配置 ASR Key：右键艾莲 → 字幕设置")
            return
        self.status_changed.emit("🤔 识别中…")
        try:
            text = _transcribe(key, wav, self._get_lang(), model=self._get_model())
        except CaptionUnavailable as exc:
            self.status_changed.emit(str(exc))
            return
        except Exception:
            self.status_changed.emit("⚠ 识别失败")
            return
        text = (text or "").strip()
        if text:
            self.text_ready.emit(text)
