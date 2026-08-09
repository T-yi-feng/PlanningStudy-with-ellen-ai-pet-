# -*- coding: utf-8 -*-
"""Windows 动画光标（.ani）解析器：把桌宠动画帧解码为 QPixmap 序列。

格式：RIFF 'ACON' 容器 → anih 元信息（JifRate 帧率）+ LIST fram 里每帧的 icon 数据。
每个 icon 数据 = 若干字节前置 + BITMAPINFOHEADER(biSize=40, biHeight=2*h) +
                32bpp XOR 图像（h 行，DIB 自底向上）+ 1bpp AND 掩码。
实际解码：真实图像高 = biHeight/2（本项目的文件即 128），只取前 h 行 32bpp 像素，
按 DIB 自底向上翻转后直接作为 ARGB32 QImage（像素本就是 BGRA，无需预乘）。
"""
import os
import struct

from PyQt5.QtCore import Qt
from PyQt5.QtGui import QImage, QPixmap


class AniClip(object):
    """一组动画帧 + 每帧延迟（毫秒）。frames 已按目标宽度缩放好。"""
    __slots__ = ("frames", "delay_ms", "width", "height")

    def __init__(self, frames, delay_ms):
        self.frames = list(frames)
        self.delay_ms = int(delay_ms)
        self.width = self.frames[0].width() if self.frames else 0
        self.height = self.frames[0].height() if self.frames else 0

    @property
    def frame_count(self):
        return len(self.frames)


def _iter_chunks(data, start=12):
    """遍历 RIFF 一级块，产出 (fourcc, payload)。"""
    i = start
    while i + 8 <= len(data):
        fcc = data[i:i+4]
        sz = struct.unpack("<I", data[i+4:i+8])[0]
        yield fcc, data[i+8:i+8+sz]
        i += 8 + sz + (sz & 1)


def _fram_payloads(data):
    """取出 LIST 'fram' 里所有 icon 子块的数据。"""
    out = []
    for fcc, body in _iter_chunks(data):
        if fcc == b"LIST" and body[:4] == b"fram":
            j = 4
            while j + 8 <= len(body):
                s2 = struct.unpack("<I", body[j+4:j+8])[0]
                out.append(body[j+8:j+8+s2])
                j += 8 + s2 + (s2 & 1)
    return out


def _jifrate(data):
    """anih 的 JifRate（帧速率，单位 1/60 秒）；缺省 5。"""
    for fcc, body in _iter_chunks(data):
        if fcc == b"anih" and len(body) >= 36:
            return struct.unpack("<I", body[28:32])[0]
    return 5


def _decode_icon_payload(payload):
    """单个 icon 数据 → QImage（128×128 32bpp，透明背景）。失败返回 None。"""
    off = payload.find(b"\x28\x00\x00\x00")  # biSize == 40
    if off < 0:
        return None
    w = struct.unpack("<i", payload[off+4:off+8])[0]
    h = struct.unpack("<i", payload[off+8:off+12])[0]
    bpp = struct.unpack("<H", payload[off+14:off+16])[0]
    if bpp != 32 or w <= 0 or h <= 0:
        return None
    stride = ((w * bpp + 31) // 32) * 4
    pixels = payload[off + 40:]
    img_h = h // 2 if h > w else h          # 真实图像高（图标 DIB 高度会翻倍）
    need = img_h * stride
    if len(pixels) < need:
        return None
    buf = bytearray(need)
    for y in range(img_h):                   # DIB 自底向上 → 翻转成自顶向下
        src = (img_h - 1 - y) * stride
        buf[y * w * 4:(y + 1) * w * 4] = pixels[src:src + w * 4]
    img = QImage(bytes(buf), w, img_h, w * 4, QImage.Format_ARGB32).copy()
    return None if img.isNull() else img


def load_ani(path, target_width):
    """加载 .ani → AniClip（帧已缩放到 target_width 宽）。失败返回 None。"""
    try:
        if not os.path.exists(path):
            return None
        with open(path, "rb") as f:
            data = f.read()
        if data[:4] != b"RIFF" or data[8:12] != b"ACON":
            return None
        qimgs = []
        for payload in _fram_payloads(data):
            img = _decode_icon_payload(payload)
            if img is not None:
                qimgs.append(img)
        if not qimgs:
            return None
        delay_ms = max(20, int(_jifrate(data) * 1000.0 / 60.0 + 0.5))
        w = qimgs[0].width()
        scale = target_width / float(w)
        th = max(1, int(qimgs[0].height() * scale + 0.5))
        frames = [
            QPixmap.fromImage(img).scaled(
                target_width, th, Qt.IgnoreAspectRatio, Qt.SmoothTransformation
            )
            for img in qimgs
        ]
        return AniClip(frames, delay_ms)
    except Exception:
        return None
