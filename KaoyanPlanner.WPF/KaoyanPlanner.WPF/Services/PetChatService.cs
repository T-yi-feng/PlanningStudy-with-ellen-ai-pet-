using System.Globalization;
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
    public const string DefaultBaseUrl = "https://ark.cn-beijing.volces.com/api/v3/chat/completions";
    public const string DefaultModel = "doubao-seed-2-1-turbo-260628";

    /// <summary>
    /// 回退链（火山方舟协作奖励授权档）。2026-09-11 切方舟：主模型 doubao-seed-2-1-turbo
    /// （快、多模态），兜底同代 pro；两者都需在方舟控制台「开通管理→模型授权」里点「立即授权」
    /// （授权后每日最高可得免费 200 万 token）。链里必须放能用的模型，绝不只依赖配置里的单一模型——
    /// 否则配置模型一挂对话就全变本地话术。
    /// </summary>
    private static readonly string[] TxtFallbackModels = { "doubao-seed-2-1-turbo-260628", "doubao-seed-2-1-pro-260628" };
    private static readonly string[] VisionFallbackModels = { "doubao-seed-2-1-turbo-260628" };

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
        // 连接池显式调优：DNS/建连超时 10s（快失败）、连接复用防 TLS 握手堆积、
        // 单域名连接上限防并发打爆。整体超时放宽到 45s——大模型长回复在弱网下
        // 常要 20~40s，25s 太容易误杀（这是「API 不稳定/经常掉回本地话术」的常见来源）。
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 8,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        c.DefaultRequestHeaders.Add("User-Agent", "KaoyanPlanner.WPF");
        return c;
    }

    /// <summary>AI 闲聊系统人格（个人版=艾莲·乔，公共中性版=小蓝，见 Brand）。</summary>
    public static string SystemPrompt => Brand.AiSystemPrompt;

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

    // 多轮上下文：最近 6 轮（12 条）消息进请求，让 AI 记得住主人说过什么（「对话太短/不智能」的根因之一）。
    private const int MaxHistoryMessages = 12;
    private readonly List<(string Role, string Text)> _history = new();

    private void AppendHistory(string role, string text)
    {
        _history.Add((role, text));
        if (_history.Count > MaxHistoryMessages) _history.RemoveAt(0);
    }

    public async Task<string> RespondAsync(string text, CancellationToken ct = default)
    {
        AppendHistory("user", text);
        try
        {
            var messages = new JsonArray();
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = SystemPrompt });
            // 状态注入 + 话题条件：只有主人主动聊计划/提醒/日程/学习时才参考；
            // 纯闲聊时让 AI 忘记状态，以艾莲人设轻松聊天，不主动把话题拉回计划。
            string state = StateContext();
            if (state.Length > 0)
                messages.Add(new JsonObject { ["role"] = "user", ["content"] =
                    "【当前状态】\n" + state +
                    "\n（仅当主人这句话涉及计划、提醒、日程或学习安排时才参考以上状态；" +
                    "主人纯闲聊时请忘记这些状态，以艾莲人设轻松聊天，不要主动提起任何计划、提醒或学习内容，也别把话题绕回计划。）" });
            // 历史：不包含刚追加的最后一条 user（它作为本轮提问放最后）
            foreach (var (role, t) in _history.Take(_history.Count - 1))
                messages.Add(new JsonObject { ["role"] = role, ["content"] = t });
            string reply = await SendAsync(messages, isVision: false, ct: ct);
            AppendHistory("assistant", reply);
            return reply;
        }
        catch
        {
            string reply = LocalReply(text);
            AppendHistory("assistant", reply);
            return reply;
        }
    }

    public async Task<string> RespondVisionAsync(string text, string imageB64, CancellationToken ct = default)
    {
        AppendHistory("user", text.Length > 0 ? text : "（发来一张图片）");
        try
        {
            var content = new JsonArray();
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text.Length > 0 ? text : "请描述一下这张图片" });
            content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + imageB64 } });
            var messages = new JsonArray();
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = SystemPrompt });
            // 视觉请求也带上最近文本历史（图片本身不重放），保持对话连贯
            foreach (var (role, t) in _history.Take(_history.Count - 1))
                messages.Add(new JsonObject { ["role"] = role, ["content"] = t });
            string reply = await SendAsync(messages, isVision: true, ct: ct);
            AppendHistory("assistant", reply);
            return reply;
        }
        catch
        {
            string reply = LocalReply(text);
            AppendHistory("assistant", reply);
            return reply;
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

            // 回退链顺序：配置的主模型优先，再补兜底模型（去重）——此前把兜底放前面，
            // 用户配置了自定义模型时反而先打兜底，顺序反了。
            var models = new List<string>();
            if (primary.Length > 0 && !models.Contains(primary)) models.Add(primary);
            foreach (string m in TxtFallbackModels) if (!models.Contains(m)) models.Add(m);

            string ctxHint = context.Length > 0
                ? "（可偶尔提起当前状态：" + context + "（只说闲话，不要给操作建议）。"
                : "";
            string prompt =
                "现在不要回答我、不要提问、不要打招呼。只按你的人设输出 " + n +
                Brand.IdleInstructionTail + ctxHint;

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
        for (int idx = 0; idx < models.Count; idx++)
        {
            string model = models[idx];
            var payload = new JsonObject
            {
                ["model"] = model,
                ["messages"] = messages,
                ["max_tokens"] = 600,
                ["temperature"] = temperature,
                // 火山方舟：关闭深度思考，聊天更跟手（桌宠要快，不需要长推理）
                ["thinking"] = new JsonObject { ["type"] = "disabled" },
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
                    if (idx < models.Count - 1)
                    {
                        // 429/5xx 是服务端瞬时过载：稍等再试下一个模型，避免连续打爆
                        if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                            await Task.Delay(600, ct).ConfigureAwait(false);
                    }
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
                throw;   // 调用方主动取消（关窗/退出）→ 不再试，直接向上抛
            }
            catch (Exception ex)
            {
                // 网络/超时（TaskCanceledException、HttpRequestException、SocketException 等）：
                // 绝不中断整条回退链——记下原因，退避一下换下一个模型。
                // 坑（2026-09-08 修复）：此前这里直接 throw，单点网络抖动/慢响应就会让
                // 整个对话掉进本地话术，体验上就是「艾莲/小蓝经常不回话」。与 AI_GUIDE §10.8
                // 「回退链必须无条件多模型（任意 HTTP 错误都 continue）」同一理由，网络错误同样 continue。
                lastErr = ex;
                if (idx < models.Count - 1)
                    await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
        throw lastErr ?? new InvalidOperationException("所有模型均不可用");
    }

    // ------------------------------------------------------------ 任务上下文

    /// <summary>给 AI 的当前状态：任务进度 + 提醒概览（今天起 3 天内 + 每日提醒）。</summary>
    public string StateContext()
    {
        var parts = new List<string>();
        string tasks = TaskContext();
        if (tasks.Length > 0) parts.Add(tasks);
        string reminders = ReminderContext(DateTime.Now);
        if (reminders.Length > 0) parts.Add(reminders);
        return parts.Count > 0 ? string.Join("\n", parts) : "";
    }

    /// <summary>提醒概览：每日型（旧版 time 键）+ 今天起 3 天内的日期型，附紧急程度。</summary>
    private string ReminderContext(DateTime now)
    {
        var data = _store.Data;
        if (data["reminders"] is not JsonArray arr || arr.Count == 0) return "";
        var lines = new List<string>();
        var horizon = new HashSet<string>();
        for (int i = 0; i <= 3; i++)
            horizon.Add(now.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        foreach (var n in arr)
        {
            if (n is not JsonObject r || !DataStore.GetBool(r["enabled"], true)) continue;
            string label = ReminderService.LabelOf(r);
            string priTxt = ReminderService.PriorityOf(r) switch { 2 => "（紧急）", 1 => "（重要）", _ => "" };
            if (ReminderService.IsLegacy(r))
            {
                string time = DataStore.GetString(r["time"]);
                if (ReminderService.TryParseHm(time, out _, out _))
                    lines.Add($"每日 {time} {label}{priTxt}");
            }
            else
            {
                string date = DataStore.GetString(r["date"]);
                if (!horizon.Contains(date)) continue;
                long interval = DataStore.GetInt(r["interval_min"], 0);
                string range = interval > 0
                    ? $"{DataStore.GetString(r["start"])}~{DataStore.GetString(r["end"])}（每{interval}分钟）"
                    : DataStore.GetString(r["start"]);
                lines.Add($"{date} {range} {label}{priTxt}");
            }
        }
        return lines.Count > 0 ? "提醒：" + string.Join(" · ", lines) : "";
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
