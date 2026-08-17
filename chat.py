"""桌宠对话：本地指令解析（可修改计划）+ 免费 API 闲聊 + 本地兜底回复。

指令让桌宠能直接改计划（添加/划掉/删除/列出），不依赖网络；
闲聊走 OpenAI 兼容的免费模型（默认智谱 GLM-4-Flash），未配 Key 时用本地话术。
"""
import json
import os
import random
import re
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


def is_configured(cfg):
    """API 是否可用：开关开着且已配 Key。未配置 = 纯离线，闲话直接走本地话术。"""
    cfg = cfg or {}
    if not cfg.get("enabled", True):
        return False
    return bool((cfg.get("api_key") or _local_api_key() or "").strip())

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

# 长期任务欠卡时的专属话术
DEBT_PHRASES = [
    "喂…你那个长期任务几天没打卡了，今天多打一次能补一天，别攒着。",
    "欠卡不补，进度只会越拖越远…今天补上吧。",
    "……有个任务欠卡了，当天再打卡一次就抵消一天，记得哦。",
    "打卡漏一天没关系，补回来就行。说吧，是不是想偷懒？",
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

# 固定任务指令（长期任务）：冻结 / 解冻 / 查询欠卡 / 打卡 / 添加
FREEZE_KEYS = (
    "冻结任务", "任务冻结", "冻结计划", "先冻结", "暂时冻结", "暂停一下",
    "暂停计划", "暂停任务", "最近有事", "冻结",
)
UNFREEZE_KEYS = ("解冻任务", "解冻", "解除冻结", "取消冻结", "恢复计划", "恢复任务", "继续任务")
DEBT_KEYS = (
    "欠卡", "打卡情况", "长期任务进度", "固定任务进度", "长期任务情况", "固定任务情况",
    "我欠了多少", "欠了多少天", "打卡了吗", "打卡了没", "今天打卡", "要不要补卡",
    "补卡情况", "欠卡情况",
)
PUNCH_KEYS = ("打卡", "补卡")
ADD_FIXED_KEYS = ("长期任务", "长期计划", "固定任务", "固定计划")

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


def _extract_fixed(text, keys):
    """解析「添加长期任务 名 N天 简介：…」→ (name, desc, days) 或 None。

    需几天完成可省略（默认 1）；简介以「简介/说明/备注」引出。
    """
    for key in keys:
        if key in text:
            rest = _after(text, key)
            if not rest or rest in _GENERIC:
                rest = _before(text, key)
            rest = (rest or "").strip(" ：:，,。.！!？?～~、\"'")
            if not rest:
                continue
            # 提取简介
            desc = ""
            for tag in ("简介", "说明", "备注"):
                if tag in rest:
                    head, _, tail = rest.partition(tag)
                    rest = head.strip(" ：:，,。.！!～~、\"'")
                    desc = tail.strip(" ：:，,。.！!？?～~、\"'（）()")
                    break
            # 提取天数：N天
            days = 1
            m = re.search(r"(\d+)\s*天", rest)
            if m:
                days = int(m.group(1))
                rest = rest[:m.start()].strip(" ：:，,。.！!～~、\"'")
            name = _clean_task(rest.strip(" ：:，,。.！!～~、\"'"))
            if name:
                return name, desc, days
    return None


# ---------- 固定任务状态上下文（喂给 AI，让它能回答任务类问题） ----------
_TASK_TOPIC_HINTS = (
    "任务", "计划", "长期", "固定", "欠卡", "补卡", "打卡", "进度",
    "冻结", "解冻", "复习",
)


def _has_task_topic(text):
    return any(h in text for h in _TASK_TOPIC_HINTS)


def task_context(win):
    """把当前固定任务状态拼成一段文字，作为 AI 对话前缀上下文。"""
    data = getattr(win, "data", None) if win is not None else None
    if data is None:
        return ""
    parts = []
    if data.get("frozen"):
        since = data.get("frozen_since") or ""
        parts.append("（长期任务已冻结" + (f"，自 {since}" if since else "") + "）")
    fixed = [t for t in data.get("tasks", []) if not t.get("done")]
    if fixed:
        lines = ["长期任务进度："]
        for t in fixed:
            line = f"  {t.get('text', '')} {t.get('progress', 0)}/{t.get('target_days', 1)}"
            if t.get("owed", 0):
                line += f"（欠{t.get('owed', 0)}天，当天再打卡一次可抵消）"
            lines.append(line)
        parts.append("\n".join(lines))
    if not parts:
        return ""
    return "\n".join(parts)


def help_text():
    return ("我可以帮你管计划哦～试试：\n"
            "· 添加 背单词\n"
            "· 添加长期任务 背单词 30天 简介：每天50个\n"
            "· 打卡 背单词\n"
            "· 划掉 背单词\n"
            "· 删除 背单词\n"
            "· 列出计划\n"
            "· 长期任务进度\n"
            "· 冻结任务 / 解冻任务\n"
            "· 全部完成\n"
            "也可以随便跟我聊天～")


def handle_command(text, win):
    """本地指令解析。返回 (handled, reply)；handled=False 表示交给 AI 闲聊。"""
    t = text.strip()
    if not t:
        return True, ""
    if t in ("帮助", "help", "你能做什么", "你会什么", "你都能干嘛", "怎么用", "怎么用你"):
        return True, help_text()

    # 冻结 / 解冻长期任务（放在专注控制前，避免「暂停计划」误判成暂停专注）
    if any(k in t for k in FREEZE_KEYS):
        return True, win.pet_freeze_tasks(True)
    if any(k in t for k in UNFREEZE_KEYS):
        return True, win.pet_freeze_tasks(False)

    # 专注计时控制
    if any(k in t for k in FOCUS_START_KEYS):
        return True, win.pet_start_focus()
    if any(k in t for k in FOCUS_PAUSE_KEYS):
        return True, win.pet_pause_focus()
    if any(k in t for k in FOCUS_RESUME_KEYS):
        return True, win.pet_resume_focus()
    if any(k in t for k in FOCUS_END_KEYS):
        return True, win.pet_reset_focus()

    # 欠卡 / 长期任务进度查询
    if any(k in t for k in DEBT_KEYS):
        return True, win.pet_debt_report()

    # 打卡 / 补卡（当天第 2 次 = 补卡）
    if any(k in t for k in PUNCH_KEYS) and not any(q in t for q in ("？", "?", "吗")):
        task = _extract_task(t, PUNCH_KEYS)
        if task:
            return True, win.pet_punch_task(task)
        return True, "要打卡哪个长期任务？说「打卡 背单词」～"

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

    # 添加固定任务（长期目标）：含「长期任务/固定任务」等词
    if any(k in t for k in ADD_FIXED_KEYS) and any(k in t for k in ADD_KEYS):
        parsed = _extract_fixed(t, ADD_FIXED_KEYS)
        if parsed:
            name, desc, days = parsed
            return True, win.pet_add_fixed_task(name, desc, days)

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

    def __init__(self, cfg, context_fn=None):
        self.cfg = cfg or {}
        self._context_fn = context_fn   # () -> str：固定任务状态，供任务类提问时喂给 AI

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

        messages = [{"role": "system", "content": SYSTEM_PROMPT}]
        if (not is_vision) and self._context_fn and isinstance(content, str):
            ctx = self._context_fn()
            if ctx and _has_task_topic(content):
                # 任务类提问：先给一段「当前任务状态」，AI 才能答得上
                messages.append({"role": "user", "content": "【当前任务状态】\n" + ctx})
        messages.append({"role": "user", "content": content})
        return self._send(base, key, models, messages)

    def respond_idle_lines(self, n=5, context=""):
        """联网生成 n 句符合艾莲人设的闲话（一句一行），失败抛 ChatUnavailable。

        供桌宠闲话批量用：一次性生成多句、逐行清洗（去编号/引号），最多取 n 句。
        温度调高一点，让每次出来的话尽量不一样。
        """
        if not self.cfg.get("enabled", True):
            raise ChatUnavailable("已关闭 AI 对话")
        key = (self.cfg.get("api_key") or _local_api_key() or "").strip()
        if not key:
            raise ChatUnavailable("未配置 API Key")
        base = (self.cfg.get("base_url") or "").strip() or DEFAULT_PET_CHAT["base_url"]
        primary = (self.cfg.get("model") or "").strip() or DEFAULT_PET_CHAT["model"]
        # 闲话是纯文字，优先用文本模型（主模型可能是视觉模型，对这种提示词会回空）
        models = [m for m in _TXT_FALLBACK_MODELS]
        if primary not in models:
            models.append(primary)

        ctx_hint = ("可偶尔提起当前状态：" + context + "（只说闲话，不要给操作建议）。"
                    if context else "")
        prompt = (
            "现在不要回答我、不要提问、不要打招呼。只按你的人设输出 %d 句"
            "艾莲的日常碎碎念/懒人闲话/给复习中的主人的小声鼓励，一句一行，"
            "每句不超过 30 字，不要编号，不要「好的」「明白」这类纯回应词。"
            "保持慵懒、怕麻烦、嘴硬心软、三言两语、多用省略号少用感叹号的调子。%s"
            % (n, ctx_hint)
        )
        messages = [
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": prompt},
        ]
        content = self._send(base, key, models, messages, temperature=1.0)
        lines = []
        for ln in content.splitlines():
            ln = re.sub(r"^[\s\-*•]*\d+[.、）)]?\s*", "", ln.strip())
            ln = ln.strip(" 　“”\"'《》「」")
            if ln:
                lines.append(ln[:40])
        return lines[:n]

    def _send(self, base, key, models, messages, temperature=0.8):
        """逐个模型尝试发请求，返回文本内容；全部失败抛 ChatUnavailable。"""
        last_err = None
        for model in models:
            payload = {
                "model": model,
                "messages": messages,
                "max_tokens": 300,
                "temperature": temperature,
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
                content = body["choices"][0]["message"]["content"].strip()
            except (KeyError, IndexError, TypeError):
                last_err = "返回格式异常"
                continue
            if not content:
                last_err = "空回复"
                continue
            return content
        raise ChatUnavailable(last_err or "所有模型均不可用")
