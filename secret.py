# -*- coding: utf-8 -*-
"""本地密钥文件 secret.json 的读写（已 .gitignore，绝不入仓库）。

存放所有不希望进仓库的密钥/凭据：
- "api_key"：智谱 GLM 对话
- "asr_api_key"：硅基流动语音识别
"""
import json
import os

_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "secret.json")


def get(key, default=""):
    """读取某个密钥；文件不存在/损坏/无该键 → 返回 default。"""
    try:
        with open(_PATH, "r", encoding="utf-8") as f:
            return json.load(f).get(key, default)
    except Exception:
        return default


def set(key, value):
    """写入某个密钥（保留其他键）。失败静默忽略（只在有写权限时成功）。"""
    try:
        with open(_PATH, "r", encoding="utf-8") as f:
            data = json.load(f)
    except Exception:
        data = {}
    data[key] = value
    try:
        with open(_PATH, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
    except OSError:
        pass
