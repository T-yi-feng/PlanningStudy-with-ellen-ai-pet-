# -*- coding: utf-8 -*-
"""艾莲语音播报：把她的回复/闲话用本地 GPT-SoVITS 合成声音播出来。

本地跑一个 GPT-SoVITS / GSVI 推理服务（默认 http://127.0.0.1:9880，
POST /tts 返回 WAV），本引擎只负责：把文字发过去、把返回的 WAV 用
QMediaPlayer 播出来；服务没启动时安静跳过，不打扰。

「关闭即零占用」：开关关闭时 speak() 直接返回——不建线程、不轮询、
不占性能；若服务进程是本引擎拉起的，关闭/退出时一并 terminate，释放显存。
"""
import json
import os
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request

import paths

from PyQt5.QtCore import QObject, QUrl, pyqtSignal
from PyQt5.QtMultimedia import QMediaContent, QMediaPlayer

# 服务就绪探测 / 拉起的超时（模型加载一般 10~30s）
_READY_TIMEOUT_S = 90.0
_READY_INTERVAL_S = 1.5
# 失败提示节流
_WARN_INTERVAL_S = 10.0
_MAX_TEXT = 200


def _resolve_ref_audio(path):
    """参考音频路径可能随程序搬移而失效（配置里存的是旧绝对路径）。

    依次尝试：原样 → 相对程序目录（exe/项目旁） → 在 <程序目录>/model 下按文件名搜索。
    返回实际存在的路径；全找不到则原样返回，交给服务端报错。
    """
    path = (path or "").strip()
    if not path:
        return ""
    if os.path.exists(path):
        return path
    base = paths.base_dir()
    cand = os.path.join(base, path.lstrip("/\\"))
    if os.path.exists(cand):
        return cand
    fname = os.path.basename(path)
    if fname:
        model_dir = os.path.join(base, "model")
        if os.path.isdir(model_dir):
            for root, _dirs, files in os.walk(model_dir):
                if fname in files:
                    return os.path.join(root, fname)
    return path


def _prompt_from_filename(path):
    """GSVI 参考音频常命名为「【标签】转写文字.wav」，从文件名提取转写。

    SoVITS V3 模型要求 prompt_text（参考音频的转写）不能为空；配置文件里
    没填时，用文件名里带出来的转写兜底。
    """
    name = os.path.basename(path)
    if "】" in name:
        tail = name.split("】", 1)[1].rsplit(".", 1)[0].strip()
        if tail:
            return tail
    return ""


class SpeakingEngine(QObject):
    """把文字合成语音播报。enabled=False 时完全静默、零线程零子进程。"""

    status_changed = pyqtSignal(str)   # 状态提示（服务未就绪等）
    play_requested = pyqtSignal(str)   # 工作线程合成完 → 主线程播放（跨线程必须走信号，QTimer.singleShot 在无事件循环的线程里不触发）

    def __init__(self, get_url, get_cmd=None, get_ref=None, parent=None):
        super().__init__(parent)
        self._get_url = get_url       # () -> str 服务地址，如 http://127.0.0.1:9880
        self._get_cmd = get_cmd or (lambda: "")   # () -> str 服务启动命令（可空=外部管理）
        self._get_ref = get_ref or (lambda: {})   # () -> dict {ref_audio_path, prompt_text, prompt_lang} 参考音频配置
        self._enabled = False         # 实际可用（服务就绪后才置 True）
        self._wanted = False          # 用户想要的开关状态（后台启动中也可能被关掉）
        self._starting = False        # 后台拉服务进行中
        self._player = QMediaPlayer(self)
        self.play_requested.connect(self._play)   # 队列连接：合成线程 emit → 主线程播放
        self._proc = None             # 本引擎拉起的服务进程（有才杀，手动起的不动）
        self._seq = 0
        self._last_warn = 0.0
        self._warn_lock = threading.Lock()
        self._last_wav = None
        self._cache_dir = os.path.join(tempfile.gettempdir(), "kaoyan_tts")
        try:
            os.makedirs(self._cache_dir, exist_ok=True)
        except OSError:
            pass

    # ---------- 生命周期 ----------
    def set_enabled(self, on):
        """开关：开启→后台拉起服务、就绪后才真正可用；关闭→零占用：
        停播放、不建线程、不轮询，并终止本引擎拉起的服务进程（释放显存）。"""
        if on:
            self._wanted = True
            if not self._enabled and not self._starting:
                self._starting = True
                threading.Thread(target=self._bg_start, daemon=True).start()
        else:
            self._wanted = False
            self._starting = False
            self._enabled = False
            self._stop_play()
            self._kill_proc()

    def _bg_start(self):
        """后台：确保服务可到达（配了命令就拉起），成功后启用；中途被关则作罢。"""
        try:
            ok = self.ensure_server()
            has_cmd = bool((self._get_cmd() or "").strip())
            # 服务就绪 或 外部手动管理（无命令，speak 会静默兜底）→ 启用
            if self._wanted and (ok or not has_cmd):
                self._enabled = True
        finally:
            self._starting = False

    def ensure_server(self):
        """服务不可达且配了启动命令 → 拉起并等待就绪（后台线程里调用，不卡 UI）。

        返回 True=URL 能通（服务在）；False=不可用。失败会节流提示。
        """
        if self._reachable():
            return True
        cmd = (self._get_cmd() or "").strip()
        if not cmd:
            return False
        try:
            flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
            self._proc = subprocess.Popen(
                cmd, shell=True, creationflags=flags,
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            )
        except Exception:
            self._proc = None
            self._warn("⚠ 语音服务启动失败（请检查「语音播报设置…」里的命令）")
            return False
        # 等待就绪
        deadline = time.time() + _READY_TIMEOUT_S
        exited = False
        while time.time() < deadline:
            if self._proc is not None and self._proc.poll() is not None:
                exited = True   # 进程退出了（命令写错等）
                break
            if self._reachable():
                return True
            time.sleep(_READY_INTERVAL_S)
        if exited:
            self._kill_proc()
            self._warn("⚠ 语音服务启动失败：命令退出了（请检查「语音播报设置…」）")
            return False
        if self._reachable():
            return True
        self._kill_proc()
        self._warn("⚠ 语音服务没起来：先装好 GSVI + 艾莲模型（右键「语音播报设置…」填启动命令）")
        return False

    def stop_server(self):
        """程序退出时调用：终止本引擎拉起的服务进程（有则杀，无则不动）。"""
        self._kill_proc()

    # ---------- 播报 ----------
    def speak(self, text):
        """把一句话合成并播报。关闭时直接返回，不创建任何线程。"""
        if not self._enabled:
            return
        text = (text or "").strip()
        if not text:
            return
        if len(text) > _MAX_TEXT:
            text = text[:_MAX_TEXT]
        threading.Thread(target=self._synthesize, args=(text,),
                         daemon=True).start()

    # ---------- 内部 ----------
    def _synthesize(self, text):
        url = (self._get_url() or "http://127.0.0.1:9880").rstrip("/")
        ref = self._get_ref() or {}
        ref_path = _resolve_ref_audio(ref.get("ref_audio_path") or "")
        if not ref_path:
            self._warn("⚠ 没配置参考音频：右键「语音播报设置…」填艾莲的参考音频路径")
            return
        prompt = (ref.get("prompt_text") or "").strip()
        if not prompt:
            # SoVITS V3 要求 prompt_text：配置里没填时，从 GSVI 文件名「【…】转写」兜底
            prompt = _prompt_from_filename(ref_path)
        payload = json.dumps({
            "text": text,
            "text_lang": "auto",
            "ref_audio_path": ref_path,
            "prompt_lang": (ref.get("prompt_lang") or "zh").strip() or "zh",
            "prompt_text": prompt,
            "text_split_method": "cut5",
            "speed": 1.0,
        }).encode("utf-8")
        try:
            req = urllib.request.Request(
                url + "/tts", data=payload,
                headers={"Content-Type": "application/json"}, method="POST")
            with urllib.request.urlopen(req, timeout=60) as resp:
                data = resp.read()
            if not data or data[:4] not in (b"RIFF", b"OggS"):
                raise ValueError("unexpected response")
            path = os.path.join(self._cache_dir, "ellen_%d.wav" % self._seq)
            with open(path, "wb") as f:
                f.write(data)
            self._seq += 1
            self.play_requested.emit(path)   # 跨线程：主线程事件循环里才真正播放
        except urllib.error.HTTPError as exc:
            # 服务在但拒绝（如 ref 路径不对/语言不支持）→ 尽量把服务端真实原因带出来
            reason = ""
            try:
                body = exc.read().decode("utf-8", errors="replace")
                info = json.loads(body)
                reason = info.get("Exception") or info.get("message") or ""
            except Exception:
                pass
            low = reason.lower()
            if "prompt_text" in low:
                hint = "右键「语音播报设置…」填「参考音频文字」（SoVITS V3 必填，可看文件名【…】后的转写）"
            elif "ref" in low or "not exists" in low or "file" in low:
                hint = "检查「语音播报设置…」的参考音频路径是否正确"
            elif "text" in low:
                hint = "说点有效内容再试"
            else:
                hint = "稍后再试"
            self._warn("⚠ 语音服务拒绝了请求（%s）%s%s" % (exc.code, ("：" + reason) if reason else "", "——" + hint))
        except Exception:
            # 服务没起/出错 → 安静跳过，只节流提示
            self._warn("⚠ 艾莲的声音没响：本地语音服务未就绪")

    def _play(self, path):
        try:
            self._player.stop()
            self._player.setMedia(QMediaContent(QUrl.fromLocalFile(path)))
            self._player.play()
        except Exception:
            pass
        if self._last_wav and self._last_wav != path:
            try:
                os.remove(self._last_wav)
            except OSError:
                pass
        self._last_wav = path

    def _stop_play(self):
        try:
            self._player.stop()
        except Exception:
            pass

    def _reachable(self):
        """探测服务是否已就绪。

        POST /tts 不带参考音频会被服务拒绝（400），但“能收到 HTTP 响应”
        本身就证明服务在监听；只有连接失败/超时才视为不可达。
        """
        url = (self._get_url() or "http://127.0.0.1:9880").rstrip("/")
        try:
            req = urllib.request.Request(
                url + "/tts",
                data=b'{"text":"ping","text_lang":"auto"}',
                headers={"Content-Type": "application/json"}, method="POST")
            with urllib.request.urlopen(req, timeout=3) as resp:
                resp.read(16)
            return True
        except urllib.error.HTTPError:
            return True   # 服务回了 400/422 → 服务在
        except Exception:
            return False  # 连接失败/超时 → 不在

    def _warn(self, msg):
        with self._warn_lock:
            now = time.time()
            if now - self._last_warn < _WARN_INTERVAL_S:
                return
            self._last_warn = now
        self.status_changed.emit(msg)

    def _kill_proc(self):
        proc = self._proc
        self._proc = None
        if proc is None:
            return
        try:
            if proc.poll() is None:
                proc.terminate()
                try:
                    proc.wait(timeout=5)
                except Exception:
                    proc.kill()
        except Exception:
            pass
