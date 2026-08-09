"""桌宠对话：本地指令解析（可修改计划）+ 免费 API 闲聊 + 本地兜底回复。

指令让桌宠能直接改计划（添加/划掉/删除/列出），不依赖网络；
闲聊走 OpenAI 兼容的免费模型（默认智谱 GLM-4-Flash），未配 Key 时用本地话术。
"""
import json
import os
import random
import urllib.error
import urllib.request

# ---------- 配置默认值 ----------
DEFAULT_PET_CHAT = {
    "enabled": True,
    "base_url": "https://open.bigmodel.cn/api/paas/v4/chat/completions",
    "model": "glm-4.6v-flash",   # 免费，支持图像识别
    "api_key": "",  # 密钥不写死在代码里：从同目录 secret.json 读取（secret.json 已 gitignore，不入库）
}

# 备用模型链：主模型限流(HTTP 429)或无权限(HTTP 403)时逐个尝试，仍失败才回退本地话术。
# 视觉请求只能降级到视觉模型；纯文字可降级到普通文本模型。
_TXT_FALLBACK_MODELS = ("glm-4-flash", "glm-4v-flash")
_VISION_FALLBACK_MODELS = ("glm-4v-flash",)


def _local_api_key():
    """从同目录 secret.json 读取 API Key（不入库），未配置返回 ''。"""
    try:
        p = os.path.join(os.path.dirname(os.path.abspath(__file__)), "secret.json")
        with open(p, "r", encoding="utf-8") as f:
            return (json.load(f).get("api_key") or "").strip()
    except Exception:
        return ""

SYSTEM_PROMPT = (
    "你是一只陪伴考研学生复习的桌面宠物，名字叫「艾莲」。"
    "请用简短、轻松、鼓励的中文回复，尽量不超过60字；"
    "就以宠物身份说话，不要透露你是 AI 模型；偶尔鼓励一下主人。"
    "如果用户发来图片，先简短描述图片里的内容，再给出相关回应。"
)

# ---------- 兜底话术 ----------
IDLE_PHRASES = [
    "加油呀，今天的专注时间就是分数！",
    "背单词累了就喝口水歇一下～",
    "我看好你哦，任务快完成啦！",
    "一点点进步也是进步，别小看自己 💪",
    "今天的你也在努力，真棒！",
    "要不要先休息五分钟，回来更有劲？",
    "嘿，我在呢，随时可以找我聊天～",
]

_GENERIC_REPLIES = [
    "嗯嗯，我在听～（可以跟我说「添加 背单词」「划掉 背单词」「列出计划」）",
    "这个我不太懂，不过我可以帮你管计划：试试「添加」「划掉」「列出计划」吧。",
    "我在哦！想聊天我陪你聊，想管计划就告诉我要添加/划掉哪一项～",
    "收到～要是想让我改计划，就跟我说「添加 XXX」或「划掉 XXX」。",
]


def local_reply(text):
    """无 API 或调用失败时的本地回复。"""
    t = text.strip().lower()
    if any(k in t for k in ("你好", "您好", "嗨", "hi", "hello", "哈喽", "在吗", "早上好", "晚上好", "中午好")):
        return "你好呀～我在陪你复习，有需要就喊我！"
    if any(k in t for k in ("谢谢", "多谢", "辛苦了", "感谢")):
        return "不客气～我们一起加油！"
    if any(k in t for k in ("晚安", "睡觉", "睡了")):
        return "晚安～早点休息，明天才有精神复习！"
    if any(k in t for k in ("加油", "好难", "坚持", "累", "努力")):
        return "你可以的！慢慢来，稳稳地学，量变会变成质变 💪"
    return random.choice(_GENERIC_REPLIES)


# ---------- 指令解析 ----------
ADD_KEYS = (
    "添加计划", "添加任务", "新增计划", "新增任务", "加计划", "加一个", "加一条",
    "帮我加", "加任务", "新增", "添加", "加个", "加入", "加上", "安排",
)
DEL_KEYS = ("删除", "移除", "删掉", "去掉")
DONE_KEYS = ("划掉", "勾掉", "标记完成", "标记为完成", "做完了", "完成一下", "已完成", "完成", "搞定")
LIST_KEYS = (
    "列出计划", "今日计划", "查看计划", "看看计划", "显示计划", "有哪些任务",
    "有什么任务", "现在有哪些任务", "任务列表", "计划清单", "还有任务", "还剩",
    "任务都有什么", "帮我看看计划",
)
ALL_DONE_HINTS = ("全部完成", "全完成", "全部划掉", "全部勾掉", "都完成了", "都完成")
FOCUS_START_KEYS = ("开始专注", "开始计时", "进入专注", "开始番茄", "专注一下")
FOCUS_PAUSE_KEYS = ("暂停专注", "暂停计时", "暂停")
FOCUS_RESUME_KEYS = ("继续专注", "继续计时", "继续")
FOCUS_END_KEYS = ("结束专注", "停止专注", "停止计时", "结束计时", "重置专注", "重置计时")

_GENERIC = {"计划", "今日计划", "任务", "日程", "列表", "清单", "今天", "里", "现在", "一下", "点"}


def _after(text, key):
    return text[text.find(key) + len(key):].strip(" ：:，,。.!！?？～~、\"'「」()（）")


def _before(text, key):
    return text[:text.find(key)].strip(" ：:，,。.!！?？～~、\"'「」()（）")


def _clean_task(s):
    for w in ("一个新的", "一条", "一个新", "一个", "个", "条", "项"):
        if s.startswith(w):
            s = s[len(w):]
    return s.strip(" ，,。！!")


def _extract_task(text, keys):
    """取关键字后的内容作任务名；若关键字后是通用词，退回取关键字前的内容。"""
    for key in keys:
        if key in text:
            after = _after(text, key)
            if after and after not in _GENERIC and len(after) >= 2:
                return _clean_task(after)
            before = _before(text, key)
            if before:
                return _clean_task(before.lstrip("把，将，请，帮我，给"))
    return ""


def help_text():
    return ("我可以帮你管计划哦～试试：\n"
            "· 添加 背单词\n"
            "· 划掉 背单词\n"
            "· 删除 背单词\n"
            "· 列出计划\n"
            "· 全部完成\n"
            "也可以随便跟我聊天～")


def handle_command(text, win):
    """本地指令解析。返回 (handled, reply)；handled=False 表示交给 AI 闲聊。"""
    t = text.strip()
    if not t:
        return True, ""
    if t in ("帮助", "help", "你能做什么", "你会什么", "你都能干嘛", "怎么用", "怎么用你"):
        return True, help_text()

    # 专注计时控制
    if any(k in t for k in FOCUS_START_KEYS):
        return True, win.pet_start_focus()
    if any(k in t for k in FOCUS_PAUSE_KEYS):
        return True, win.pet_pause_focus()
    if any(k in t for k in FOCUS_RESUME_KEYS):
        return True, win.pet_resume_focus()
    if any(k in t for k in FOCUS_END_KEYS):
        return True, win.pet_reset_focus()

    # 全部完成
    if any(k in t for k in ALL_DONE_HINTS):
        return True, win.pet_complete_all()

    # 列出计划
    if any(k in t for k in LIST_KEYS):
        return True, win.pet_list_tasks()

    # 删除
    if any(k in t for k in DEL_KEYS):
        task = _extract_task(t, DEL_KEYS)
        if task:
            return True, win.pet_delete_task(task)

    # 划掉 / 完成
    if any(k in t for k in DONE_KEYS):
        task = _extract_task(t, DONE_KEYS)
        if task:
            return True, win.pet_complete_task(task)

    # 添加
    if any(k in t for k in ADD_KEYS):
        task = _extract_task(t, ADD_KEYS)
        if task:
            return True, win.pet_add_task(task)

    return False, ""


# ---------- AI 闲聊 ----------
class ChatUnavailable(Exception):
    """AI 对话不可用（未启用 / 未配 Key / 网络失败）。"""


class ChatService:
    """OpenAI 兼容的免费 API 客户端（智谱 GLM-4-Flash / 硅基流动等）。"""

    def __init__(self, cfg):
        self.cfg = cfg or {}

    def respond(self, user_text):
        """纯文字对话。"""
        return self._post(user_text)

    def respond_vision(self, user_text, image_b64):
        """图文对话（glm-4.6v-flash 支持图像识别）。"""
        content = [
            {"type": "text", "text": user_text or "请描述一下这张图片"},
            {"type": "image_url", "image_url": {"url": "data:image/png;base64," + image_b64}},
        ]
        return self._post(content)

    def _post(self, content):
        if not self.cfg.get("enabled", True):
            raise ChatUnavailable("已关闭 AI 对话")
        key = (self.cfg.get("api_key") or _local_api_key() or "").strip()
        if not key:
            raise ChatUnavailable("未配置 API Key")
        base = (self.cfg.get("base_url") or "").strip() or DEFAULT_PET_CHAT["base_url"]
        primary = (self.cfg.get("model") or "").strip() or DEFAULT_PET_CHAT["model"]

        is_vision = isinstance(content, list)
        fallbacks = _VISION_FALLBACK_MODELS if is_vision else _TXT_FALLBACK_MODELS
        models = [primary] + [m for m in fallbacks if m != primary]

        last_err = None
        for model in models:
            payload = {
                "model": model,
                "messages": [
                    {"role": "system", "content": SYSTEM_PROMPT},
                    {"role": "user", "content": content},
                ],
                "max_tokens": 300,
                "temperature": 0.8,
            }
            req = urllib.request.Request(
                base,
                data=json.dumps(payload).encode("utf-8"),
                headers={
                    "Content-Type": "application/json",
                    "Authorization": "Bearer " + key,
                },
                method="POST",
            )
            try:
                with urllib.request.urlopen(req, timeout=25) as resp:
                    body = json.loads(resp.read().decode("utf-8"))
            except urllib.error.HTTPError as exc:
                # 限流(429)或无权限(403) → 换备用模型；其余 HTTP 错误直接失败
                if exc.code in (429, 403):
                    last_err = "HTTP %d" % exc.code
                    continue
                raise ChatUnavailable("API HTTP %d" % exc.code)
            except Exception as exc:  # 超时/网络错误
                raise ChatUnavailable(str(exc))
            try:
                return body["choices"][0]["message"]["content"].strip()
            except (KeyError, IndexError, TypeError):
                last_err = "返回格式异常"
                continue
        raise ChatUnavailable(last_err or "所有模型均不可用")
