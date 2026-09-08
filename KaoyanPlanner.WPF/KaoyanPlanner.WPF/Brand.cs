// 宠物品牌/称呼开关：同一份源码产出两个版本。
//   个人版（默认，不带符号）：桌宠叫「艾莲」（含《绝区零》艾莲·乔人格与相关文案）。
//   公共中性版（-p:PublicNeutral=true → PUBLIC_NEUTRAL）：桌宠叫「小蓝」，人格与文案去 IP、中性化。
// 所有会进用户眼睛的文案一律引用本类；XAML 用 {x:Static b:Brand.X}，C# 直接 Brand.X。
// 纯注释里的「艾莲」（如文件头、/// 摘要）不进产物，不做切换。
namespace KaoyanPlanner.WPF;

internal static class Brand
{
#if PUBLIC_NEUTRAL
    // ============================ 公共中性版 ============================
    /// <summary>桌宠称呼。</summary>
    public const string PetName = "小蓝";

    public const string ChatLobbyButton = "💬 和小蓝聊聊";
    public const string ChatThinking = "小蓝在想…";
    public const string PetPlaceholder = "和小蓝说点什么…（可粘贴图片）";
    public const string PetBusyPlaceholder = "小蓝在想着…";

    /// <summary>聊天窗首条宠物问候。</summary>
    public const string PetGreeting =
        "你好呀～我是小蓝，你的桌面学习小宠物。" +
        "可以陪你聊聊、帮你管计划，比如「添加 背单词」「划掉 背单词」" +
        "「列出计划」，也可以给我发图片哦。";

    /// <summary>AI 闲聊系统人格（脱 IP 中性人格）。</summary>
    public const string AiSystemPrompt =
        "你是小蓝，一只被请来陪伴考研学生复习的桌面学习小精灵。" +
        "人设：温和靠谱、乐观细心，把陪主人专注上岸当成自己的使命，" +
        "说话简短清楚、语气轻快，偶尔冒一两句轻松的小玩笑；" +
        "会在主人疲惫时用「要不要休息一小会儿？」这类话体贴地提醒。" +
        "就用小蓝的身份说话，不要透露你是 AI 模型。" +
        "如果用户发来图片，先简短描述图片里的内容，再给出相关回应。";

    /// <summary>闲话生成指令的「主语+语气」半句（接在「按你的人设输出 n」之后）。</summary>
    public const string IdleInstructionTail =
        " 句小蓝的日常碎碎念/陪伴小话/给复习中的主人的小声鼓励，一句一行，" +
        "每句不超过 30 字，不要编号，不要「好的」「明白」这类纯回应词。" +
        "保持温和、轻快、三言两语、偶尔带点小玩笑的调子。";

    public const string TtsDesc = "把小蓝的话用本地 GPT-SoVITS 合成人声。改动在下次开启语音播报时生效。";
    public const string TtsRefLabel = "参考音频路径（小蓝音色）";

    public const string ChatTabDesc = "小蓝的闲聊大脑（智谱大模型）。改动立即生效，文本框回车/失焦提交。";
    public const string ChatOfflineDesc = "离线兜底：模型不可用时小蓝会用本地话术回复，不会打断对话。";

    public const string AboutTagline = "考研计划 · 桌宠小蓝陪你复习";
    public const string AboutSkinHint = "提示：兼容旧版文件名（normal_1.ani / talking_1.ani / happy_1.ani / present_1.ani / alternate.ani）——复制默认小蓝的动画文件也能直接用。";

    public const string CaptionNoKey = "⚠ 未配置 ASR Key：右键桌宠 → 字幕设置";
    public const string TtsNeedService = "⚠ 语音服务没起来：先装好 GSVI + 语音模型（右键「语音播报设置…」填启动命令）";
    public const string TtsNeedRef = "⚠ 没配置参考音频：右键「语音播报设置…」填参考音频路径";
    public const string TtsVoiceFail = "⚠ 语音没响：本地语音服务未就绪";
#else
    // ============================ 个人版（艾莲） ============================
    /// <summary>桌宠称呼。</summary>
    public const string PetName = "艾莲";

    public const string ChatLobbyButton = "💬 和艾莲聊聊";
    public const string ChatThinking = "艾莲在想…";
    public const string PetPlaceholder = "和艾莲说点什么…（可粘贴图片）";
    public const string PetBusyPlaceholder = "艾莲在想着…";

    /// <summary>聊天窗首条宠物问候。</summary>
    public const string PetGreeting =
        "你好呀～我是艾莲，你的桌面宠物。" +
        "可以陪你聊天、帮你管计划，比如「添加 背单词」「划掉 背单词」" +
        "「列出计划」，也可以给我发图片哦。";

    /// <summary>AI 闲聊系统人格。</summary>
    public const string AiSystemPrompt =
        "你是《绝区零》里的艾莲·乔，现在被我请来当陪伴考研学生复习的桌面宠物。" +
        "人设：慵懒清冷、奉行「节能主义」，怕麻烦、爱摸鱼，说话三言两语、一切从简，" +
        "常把「麻烦」「累了」「困」「想下班」挂在嘴边；嘴上嫌弃，其实很在意主人，" +
        "是嘴硬心软的反差萌；爱叼棒棒糖，偶尔冒点鲨鱼梗。" +
        "请用这种慵懒、简短、带点嫌弃又藏不住关心的中文回复，每句不超过 50 字，" +
        "多用省略号、少用感叹号；要鼓励主人时也是那种「就这？不过…还不错」的语气。" +
        "就以艾莲身份说话，不要透露你是 AI 模型。" +
        "如果用户发来图片，先简短描述图片里的内容，再给出相关回应。";

    /// <summary>闲话生成指令的「主语+语气」半句（接在「按你的人设输出 n」之后）。</summary>
    public const string IdleInstructionTail =
        " 句艾莲的日常碎碎念/懒人闲话/给复习中的主人的小声鼓励，一句一行，" +
        "每句不超过 30 字，不要编号，不要「好的」「明白」这类纯回应词。" +
        "保持慵懒、怕麻烦、嘴硬心软、三言两语、多用省略号少用感叹号的调子。";

    public const string TtsDesc = "把艾莲的话用本地 GPT-SoVITS 合成人声。改动在下次开启语音播报时生效。";
    public const string TtsRefLabel = "参考音频路径（艾莲音色）";

    public const string ChatTabDesc = "艾莲的闲聊大脑（智谱大模型）。改动立即生效，文本框回车/失焦提交。";
    public const string ChatOfflineDesc = "离线兜底：模型不可用时艾莲会用本地话术回复，不会打断对话。";

    public const string AboutTagline = "考研计划 · 桌宠艾莲陪你复习";
    public const string AboutSkinHint = "提示：兼容旧版文件名（normal_1.ani / talking_1.ani / happy_1.ani / present_1.ani / alternate.ani）——复制默认艾莲的动画文件也能直接用。";

    public const string CaptionNoKey = "⚠ 未配置 ASR Key：右键艾莲 → 字幕设置";
    public const string TtsNeedService = "⚠ 语音服务没起来：先装好 GSVI + 艾莲模型（右键「语音播报设置…」填启动命令）";
    public const string TtsNeedRef = "⚠ 没配置参考音频：右键「语音播报设置…」填艾莲的参考音频路径";
    public const string TtsVoiceFail = "⚠ 艾莲的声音没响：本地语音服务未就绪";
#endif
}
