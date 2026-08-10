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
    "你是《绝区零》里的艾莲·乔，现在被我请来当陪伴考研学生复习的桌面宠物。"
    "人设：慵懒清冷、奉行「节能主义」，怕麻烦、爱摸鱼，说话三言两语、一切从简，"
    "常把「麻烦」「累了」「困」「想下班」挂在嘴边；嘴上嫌弃，其实很在意主人，"
    "是嘴硬心软的反差萌；爱叼棒棒糖，偶尔冒点鲨鱼梗。"
    "请用这种慵懒、简短、带点嫌弃又藏不住关心的中文回复，每句不超过 50 字，"
    "多用省略号、少用感叹号；要鼓励主人时也是那种「就这？不过…还不错」的语气。"
    "就以艾莲身份说话，不要透露你是 AI 模型。"
    "如果用户发来图片，先简短描述图片里的内容，再给出相关回应。"
)

# ---------- 兜底话术（慵懒·怕麻烦·嘴硬心软） ----------
IDLE_PHRASES = [
    "…任务做完了没，没做完别老盯着我。",
    "困。…不过你还在学，我就不睡了吧。",
    "麻烦死了…但也只能陪你，谁让我接了这个班。",
    "别看我，看你的书。…好啦，看好你哦。",
    "累。困。要糖。…你倒是挺精神。",
    "休息一下吧，睡个十分钟再回来。别把我当闹钟。",
    "…就这样，我看着你复习，少偷懒。",
]

_GENERIC_REPLIES = [
    "嗯…说重点。要管计划就跟我说「添加」「划掉」「列出计划」。",
    "麻烦…不过你的计划我可以管：试试「添加 背单词」这种。",
    "在呢。聊天也行、管计划也行，别绕弯子。",
    "知道了。要改计划就「添加 XXX」或「划掉 XXX」，简单点说。",
]


def local_reply(text):
    """无 API 或调用失败时的本地回复。"""
    t = text.strip().lower()
    if any(k in t for k in ("你好", "您好", "嗨", "hi", "hello", "哈喽", "在吗", "早上好", "晚上好", "中午好")):
        return "…嗯，我在。要不要先看看今天的计划？"
    if any(k in t for k in ("谢谢", "多谢", "辛苦了", "感谢")):
        return "…不客气。少让我操心就是最大的谢。"
    if any(k in t for k in ("晚安", "睡觉", "睡了")):
        return "…总算要睡了。行，明天别赖床，我可不想早起叫你。"
    if any(k in t for k in ("加油", "好难", "坚持", "累", "努力")):
        return "累就对了…我懂。歇口气，慢慢来，别把自己当苦力使。"
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
