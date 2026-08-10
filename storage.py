"""数据持久化：JSON 读写、默认结构、每日归档。"""
import copy
import datetime
import json
import os

DATA_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data.json")


def today_str():
    return datetime.date.today().isoformat()


DEFAULT_CAPTION = {   # 实时媒体字幕设置
    "enabled": False,
    "model": "FunAudioLLM/SenseVoiceSmall",   # 识别模型（SenseVoiceSmall 实时 / TeleSpeechASR 高精度）
    "language": "auto",   # auto/zh/en/yue/ja/ko
    "font_size": 18,
    "color": "#F5F0E6",   # 粉笔白
}

DEFAULT_VOICE = {   # 语音输入设置
    "enabled": False,   # 右键桌宠「语音输入」开关：开→聊天栏出现按住说话的麦克风按钮
}

DEFAULT_TTS = {   # 艾莲语音播报（本地 GPT-SoVITS 服务）
    "enabled": False,
    "url": "http://127.0.0.1:9880",   # api_v2.py 默认端口（POST /tts 返回 WAV）
    "server_cmd": "",   # 宠物接管服务生命周期的启动命令（可空=外部手动管理；开启开关时宠物拉起、关闭/退出时终止）
    "ref_audio_path": "",   # 参考音频（音色来源，如 C:/Users/21495/gsvi/custom_refs/ref_sapi_zh.wav 或艾莲模型的 ref.wav）
    "prompt_text": "",   # 参考音频对应的文字（转写，填了音色更稳）
    "prompt_lang": "zh",   # 参考音频语言（zh/en/ja/ko/yue 等）
}


def default_data():
    return {
        "exam_date": "",          # 空串表示未设置，如 "2026-12-19"
        "window_pos": None,       # [x, y]
        "collapsed": False,
        "daily": {},              # {"2026-08-09": [{"text": "...", "done": false}]}
        "reminders": [],          # [{"time": "19:00", "label": "...", "enabled": true}]
        "timer": {"duration_min": 25},
        "focus_sessions": [],     # [{"date": "2026-08-09", "start": "09:00", "end": "09:25", "duration_min": 25, "period": "上午"}]（旧版倒计时遗留，保留兼容）
        "focus_history": {},      # {"2026-08-09": {"8": 12.0, "9": 5.5}} 小时 -> 专注分钟数（正计时自动累计）
        "focus_reminder": {"enabled": True, "interval_min": 60},   # 正计时过程中的喝水休息提醒
        "unfinished_reminder": {"enabled": True, "interval_min": 60},
        "pet_chat": {   # 桌宠 AI 对话（OpenAI 兼容免费模型）
            "enabled": True,
            "base_url": "https://open.bigmodel.cn/api/paas/v4/chat/completions",
            "model": "glm-4-flash",
            "api_key": "",
        },
        "pet_idle": {"enabled": True, "interval_min": 8},   # 桌宠闲话间隔（分钟）
        "caption": dict(DEFAULT_CAPTION),   # 实时媒体字幕（黑板）
        "voice": dict(DEFAULT_VOICE),       # 语音输入
        "tts": dict(DEFAULT_TTS),           # 艾莲语音播报
    }


def load_data():
    """读取 data.json，缺省字段用默认值补齐。损坏时备份并返回默认。"""
    if os.path.exists(DATA_FILE):
        try:
            with open(DATA_FILE, "r", encoding="utf-8") as f:
                data = json.load(f)
            merged = default_data()
            merged.update(data)
            migrate_focus(merged)
            return merged
        except (json.JSONDecodeError, OSError):
            try:
                os.replace(DATA_FILE, DATA_FILE + ".bak")
            except OSError:
                pass
    return default_data()


def migrate_focus(data):
    """把旧版 focus_sessions（整轮记录）迁移到 focus_history 的按小时分布。

    新正计时按秒累计到 focus_history；历史数据只迁移一次。
    """
    sessions = data.get("focus_sessions") or []
    hist = data.setdefault("focus_history", {})
    if hist or not sessions:
        return
    for s in sessions:
        day = s.get("date")
        if not day:
            continue
        try:
            start = datetime.datetime.fromisoformat(day + " " + s.get("start", ""))
            end = datetime.datetime.fromisoformat(day + " " + s.get("end", ""))
        except (TypeError, ValueError):
            continue
        minutes = float(s.get("duration_min", 0))
        if minutes <= 0 or end <= start:
            continue
        total = (end - start).total_seconds() / 60.0
        day_hist = hist.setdefault(day, {})
        # 把 duration_min 按该轮覆盖的各小时分钟占比分摊
        t = start.replace(second=0, microsecond=0)
        while t < end:
            hour_end = t.replace(minute=59, second=59, microsecond=999999)
            seg_end = min(end, hour_end)
            overlap = (seg_end - t).total_seconds() / 60.0
            key = str(t.hour)
            day_hist[key] = round(day_hist.get(key, 0.0) + minutes * overlap / total, 3)
            t = hour_end + datetime.timedelta(minutes=1)


def save_data(data):
    """原子写入 data.json。"""
    tmp = DATA_FILE + ".tmp"
    try:
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
        os.replace(tmp, DATA_FILE)
    except OSError:
        pass


def ensure_today(data):
    """保证 data['daily'][today] 存在；跨日时归档昨天已完成任务。"""
    t = today_str()
    if t in data["daily"]:
        return t

    # 找出最近一天，若其早于今天则归档到 archive 区
    if data["daily"]:
        last_day = max(data["daily"].keys())
        if last_day < t:
            data.setdefault("archive", {})[last_day] = copy.deepcopy(data["daily"][last_day])
    data["daily"][t] = []
    return t


def add_task(data, text, done=False):
    day = ensure_today(data)
    data["daily"][day].append({"text": text, "done": done})
    save_data(data)


def set_task_done(data, index, done):
    day = today_str()
    tasks = data["daily"].get(day, [])
    if 0 <= index < len(tasks):
        tasks[index]["done"] = done
        save_data(data)


def delete_task(data, index):
    day = today_str()
    tasks = data["daily"].get(day, [])
    if 0 <= index < len(tasks):
        del tasks[index]
        save_data(data)
