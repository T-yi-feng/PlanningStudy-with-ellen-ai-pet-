using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace KaoyanPlanner.WPF.Services;

/// <summary>
/// 桌宠 AI 对话：OpenAI 兼容免费 API（移植 chat.py ChatService）+ 离线话术兜底 + 闲话池。
/// 只读 secret.json / data.json 的 pet_chat 配置，绝不写盘；全部失败回退本地话术。
/// 走 HttpClient（后台线程），调用方负责把结果封送回 UI 线程。
/// </summary>
public sealed class PetChatService
{
    public const string DefaultBaseUrl = "https://open.bigmodel.cn/api/paas/v4/chat/completions";
    public const string DefaultModel = "glm-4.7";

    /// <summary>
    /// 回退链。2026-08-31 实测：该账号可用 glm-4.7 / glm-4-flash / glm-4v-flash；
    /// glm-4.6v-flash 已下线（持续 429「访问量过大」且不在可用模型列表）、glm-5.3-flash 等要余额。
    /// 链里必须放能用的模型，绝不只依赖配置里的单一模型——否则配置模型一挂对话就全变本地话术。
    /// </summary>
    private static readonly string[] TxtFallbackModels = { "glm-4.7", "glm-4-flash" };
    private static readonly string[] VisionFallbackModels = { "glm-4v-flash" };

    private readonly DataStore _store;
    private readonly Func<string>? _taskContext;
    private static readonly HttpClient Http = CreateClient();

    public PetChatService(DataStore store, Func<string>? taskContext = null)
    {
        _store = store;
        _taskContext = taskContext;
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        c.DefaultRequestHeaders.Add("User-Agent", "KaoyanPlanner.WPF");
        return c;
    }

    public const string SystemPrompt =
        "你是《绝区零》里的艾莲·乔，现在被我请来当陪伴考研学生复习的桌面宠物。" +
        "人设：慵懒清冷、奉行「节能主义」，怕麻烦、爱摸鱼，说话三言两语、一切从简，" +
        "常把「麻烦」「累了」「困」「想下班」挂在嘴边；嘴上嫌弃，其实很在意主人，" +
        "是嘴硬心软的反差萌；爱叼棒棒糖，偶尔冒点鲨鱼梗。" +
        "请用这种慵懒、简短、带点嫌弃又藏不住关心的中文回复，每句不超过 50 字，" +
        "多用省略号、少用感叹号；要鼓励主人时也是那种「就这？不过…还不错」的语气。" +
        "就以艾莲身份说话，不要透露你是 AI 模型。" +
        "如果用户发来图片，先简短描述图片里的内容，再给出相关回应。";

    // ------------------------------------------------------------ 配置

    private JsonObject? PetChatCfg => DataStore.GetObj(_store.Data, "pet_chat");

    /// <summary>从 secret.json 读 API Key（只读，不入库）：exe 目录 → 逐级向上找。</summary>
    public static string LocalApiKey()
    {
        string? dir = AppPaths.BaseDir;
        for (int up = 0; up < 6 && dir is not null; up++)
        {
            string p = Path.Combine(dir, "secret.json");
            try
            {
                if (File.Exists(p))
                {
                    var node = JsonNode.Parse(File.ReadAllText(p, Encoding.UTF8));
                    if (node is JsonObject obj && obj["api_key"] is JsonValue v && v.TryGetValue<string>(out var key))
                        return (key ?? "").Trim();
                    return "";
                }
            }
            catch
            {
                // 忽略读取失败，继续向上找
            }
            dir = Path.GetDirectoryName(dir);
        }
        return "";
    }

    public bool IsConfigured()
    {
        var cfg = PetChatCfg;
        if (cfg is null || !DataStore.GetBool(cfg["enabled"], true)) return false;
        return (DataStore.GetString(cfg["api_key"]) ?? "").Trim().Length > 0 || LocalApiKey().Length > 0;
    }

    private string ApiKey()
    {
        var cfg = PetChatCfg;
        string inline = cfg is null ? "" : DataStore.GetString(cfg["api_key"]).Trim();
        return inline.Length > 0 ? inline : LocalApiKey();
    }

    // ------------------------------------------------------------ 对话

    public async Task<string> RespondAsync(string text, CancellationToken ct = default)
    {
        try
        {
            var messages = new JsonArray();
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = SystemPrompt });
            if (_taskContext is not null && HasTaskTopic(text))
            {
                string ctx = _taskContext();
                if (ctx.Length > 0)
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = "【当前任务状态】\n" + ctx });
            }
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });
            return await SendAsync(messages, isVision: false, ct: ct);
        }
        catch
        {
            return LocalReply(text);
        }
    }

    public async Task<string> RespondVisionAsync(string text, string imageB64, CancellationToken ct = default)
    {
        try
        {
            var content = new JsonArray();
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text.Length > 0 ? text : "请描述一下这张图片" });
            content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + imageB64 } });
            var messages = new JsonArray();
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = SystemPrompt });
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
            return await SendAsync(messages, isVision: true, ct: ct);
        }
        catch
        {
            return LocalReply(text);
        }
    }

    /// <summary>联网生成 n 句艾莲式闲话（一句一行）；失败返回空列表（调用方走冷却）。</summary>
    public async Task<List<string>> RespondIdleLinesAsync(int n, string context, CancellationToken ct = default)
    {
        try
        {
            var cfg = PetChatCfg;
            if (cfg is null || !DataStore.GetBool(cfg["enabled"], true)) return new List<string>();
            if (ApiKey().Length == 0) return new List<string>();
            string baseUrl = DataStore.GetString(cfg["base_url"]).Trim();
            if (baseUrl.Length == 0) baseUrl = DefaultBaseUrl;
            string primary = DataStore.GetString(cfg["model"]).Trim();
            if (primary.Length == 0) primary = DefaultModel;

            var models = new List<string>();
            foreach (string m in TxtFallbackModels) models.Add(m);
            if (!models.Contains(primary)) models.Add(primary);

            string ctxHint = context.Length > 0
                ? "（可偶尔提起当前状态：" + context + "（只说闲话，不要给操作建议）。"
                : "";
            string prompt =
                "现在不要回答我、不要提问、不要打招呼。只按你的人设输出 " + n +
                " 句艾莲的日常碎碎念/懒人闲话/给复习中的主人的小声鼓励，一句一行，" +
                "每句不超过 30 字，不要编号，不要「好的」「明白」这类纯回应词。" +
                "保持慵懒、怕麻烦、嘴硬心软、三言两语、多用省略号少用感叹号的调子。" + ctxHint;

            var messages = new JsonArray();
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = SystemPrompt });
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = prompt });

            string content = await SendCoreAsync(baseUrl, ApiKey(), models, messages, 1.0, ct);
            var lines = new List<string>();
            foreach (string raw in content.Split('\n'))
            {
                string ln = System.Text.RegularExpressions.Regex.Replace(raw.Trim(), "^[\\s\\-*•]*\\d+[.、）)]?\\s*", "");
                ln = ln.Trim(" 　“”\"'《》「」".ToCharArray());
                if (ln.Length > 0) lines.Add(ln.Length > 40 ? ln[..40] : ln);
            }
            return lines.Take(n).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private async Task<string> SendAsync(JsonArray messages, bool isVision, CancellationToken ct)
    {
        var cfg = PetChatCfg;
        string baseUrl = cfg is null ? DefaultBaseUrl : DataStore.GetString(cfg["base_url"]).Trim();
        if (baseUrl.Length == 0) baseUrl = DefaultBaseUrl;
        string primary = cfg is null ? DefaultModel : DataStore.GetString(cfg["model"]).Trim();
        if (primary.Length == 0) primary = DefaultModel;

        var fallbacks = isVision ? VisionFallbackModels : TxtFallbackModels;
        var models = new List<string> { primary };
        foreach (string m in fallbacks) if (m != primary) models.Add(m);

        return await SendCoreAsync(baseUrl, ApiKey(), models, messages, 0.8, ct);
    }

    private static async Task<string> SendCoreAsync(string baseUrl, string key, IReadOnlyList<string> models,
        JsonArray messages, double temperature, CancellationToken ct)
    {
        Exception? lastErr = null;
        foreach (string model in models)
        {
            var payload = new JsonObject
            {
                ["model"] = model,
                ["messages"] = messages,
                ["max_tokens"] = 300,
                ["temperature"] = temperature,
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            req.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            try
            {
                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    // 任何 HTTP 错误都换下一个模型：模型下线/访问量过大(429)/余额不足/格式错误(400)等。
                    // 坑（2026-08-31）：glm-4.6v-flash 已下线持续 429，且 vision 主模型 glm-4.7 收
                    // 图片会 400——这两种都必须能滚到下一个模型，绝不能 throw 中断整条链。
                    lastErr = new InvalidOperationException("API HTTP " + (int)resp.StatusCode);
                    continue;
                }
                string body = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                JsonElement el = doc.RootElement;
                string content;
                try
                {
                    content = el.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
                }
                catch
                {
                    lastErr = new InvalidOperationException("返回格式异常");
                    continue;
                }
                content = content.Trim();
                if (content.Length == 0) { lastErr = new InvalidOperationException("空回复"); continue; }
                return content;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                throw;   // 网络/超时：整体失败（与 Python 一致，不逐个换模型）
            }
        }
        throw lastErr ?? new InvalidOperationException("所有模型均不可用");
    }

    // ------------------------------------------------------------ 任务上下文

    private static readonly string[] TaskTopicHints = { "任务", "计划", "长期", "固定", "欠卡", "补卡", "打卡", "进度", "冻结", "解冻", "复习" };

    private static bool HasTaskTopic(string text)
    {
        foreach (string h in TaskTopicHints) if (text.Contains(h, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>把当前固定任务状态拼成一段文字，作为 AI 对话前缀上下文（镜像 chat.py task_context）。</summary>
    public string TaskContext()
    {
        var data = _store.Data;
        var parts = new List<string>();
        if (DataStore.GetBool(data["frozen"]))
        {
            string since = DataStore.GetString(data["frozen_since"]);
            parts.Add("（长期任务已冻结" + (since.Length > 0 ? $"，自 {since}" : "") + "）");
        }
        var fixedTasks = new List<JsonObject>();
        if (data["tasks"] is JsonArray tasks)
            foreach (var n in tasks)
                if (n is JsonObject t && !DataStore.GetBool(t["done"]))
                    fixedTasks.Add(t);
        if (fixedTasks.Count > 0)
        {
            var lines = new List<string> { "长期任务进度：" };
            foreach (var t in fixedTasks)
            {
                string line = $"  {DataStore.GetString(t["text"])} {DataStore.GetInt(t["progress"])}/{DataStore.GetInt(t["target_days"], 1)}";
                long owe = DataStore.GetInt(t["owed"]);
                if (owe > 0) line += $"（欠{owe}天，当天点「补卡」打一次可抵消）";
                lines.Add(line);
            }
            parts.Add(string.Join("\n", lines));
        }
        return parts.Count > 0 ? string.Join("\n", parts) : "";
    }

    // ------------------------------------------------------------ 本地话术兜底（移植 chat.py）

    public static readonly string[] IdlePhrases =
    {
        "…任务做完了没，没做完别老盯着我。",
        "困。…不过你还在学，我就不睡了吧。",
        "麻烦死了…但也只能陪你，谁让我接了这个班。",
        "别看我，看你的书。…好啦，看好你哦。",
        "累。困。要糖。…你倒是挺精神。",
        "休息一下吧，睡个十分钟再回来。别把我当闹钟。",
        "…就这样，我看着你复习，少偷懒。",
    };

    public static readonly string[] DebtPhrases =
    {
        "喂…你那个长期任务几天没打卡了，今天点「补卡」打卡一次能补一天，别攒着。",
        "欠卡不补，进度只会越拖越远…今天补上吧。",
        "……有个任务欠卡了，今天点「补卡」打一次就抵消一天，记得哦。",
        "打卡漏一天没关系，补回来就行。说吧，是不是想偷懒？",
    };

    private static readonly string[] GenericReplies =
    {
        "嗯…说重点。要管计划就跟我说「添加」「划掉」「列出计划」。",
        "麻烦…不过你的计划我可以管：试试「添加 背单词」这种。",
        "在呢。聊天也行、管计划也行，别绕弯子。",
        "知道了。要改计划就「添加 XXX」或「划掉 XXX」，简单点说。",
    };

    public static string LocalReply(string text)
    {
        string t = (text ?? "").Trim().ToLowerInvariant();
        if (ContainsAny(t, "你好", "您好", "嗨", "hi", "hello", "哈喽", "在吗", "早上好", "晚上好", "中午好"))
            return "…嗯，我在。要不要先看看今天的计划？";
        if (ContainsAny(t, "谢谢", "多谢", "辛苦了", "感谢"))
            return "…不客气。少让我操心就是最大的谢。";
        if (ContainsAny(t, "晚安", "睡觉", "睡了"))
            return "…总算要睡了。行，明天别赖床，我可不想早起叫你。";
        if (ContainsAny(t, "加油", "好难", "坚持", "累", "努力"))
            return "累就对了…我懂。歇口气，慢慢来，别把自己当苦力使。";
        return GenericReplies[Random.Shared.Next(GenericReplies.Length)];
    }

    private static bool ContainsAny(string s, params string[] keys)
    {
        foreach (string k in keys) if (s.Contains(k, StringComparison.Ordinal)) return true;
        return false;
    }
}
