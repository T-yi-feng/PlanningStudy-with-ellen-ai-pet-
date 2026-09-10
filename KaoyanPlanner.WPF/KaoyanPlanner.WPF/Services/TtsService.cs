using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using KaoyanPlanner.WPF.Native;

namespace KaoyanPlanner.WPF.Services;

/// <summary>
/// 艾莲语音播报：把回复/闲话用本地 GPT-SoVITS / GSVI 推理服务合成声音（移植 tts.py SpeakingEngine）。
/// 只读 data.json 的 tts 配置；开关关闭时 Speak 直接返回（零线程零子进程）。
/// 合成在后台线程做，合成完经 <see cref="PlayRequested"/> 把 wav 路径封回 UI 线程播放（跨线程安全）。
/// 若服务进程是本服务拉起的（server_cmd 非空），关闭/退出时一并终止释放显存。
/// </summary>
public sealed class TtsService : IDisposable
{
    private const int ReadyTimeoutMs = 90_000;    // 模型加载一般 10~30s
    private const int ReadyIntervalMs = 1_500;
    private const int WarnIntervalMs = 10_000;    // 失败提示节流
    private const int MaxText = 200;

    private readonly DataStore _store;
    private readonly string _cacheDir;

    private bool _wanted;
    private bool _enabled;      // 实际可用（服务就绪后才置 true）
    private bool _starting;
    private Process? _proc;     // 本服务拉起的进程（有才杀，手动起的不动）
    private ChildProcessJob? _job;   // 作业对象：父进程被强杀/崩溃时由内核回收整棵子进程树
    private int _seq;
    private long _lastWarnTicks;
    private readonly object _warnLock = new();

    // 合成串行化：单卡 GPU 推理服务同时收多个 /tts 会互相挤压甚至报错，排队一个个来
    private readonly SemaphoreSlim _synthGate = new(1, 1);
    // 在途合成的取消令牌：关闭/重启服务时立刻取消，避免旧请求打到正在退出的实例上
    private CancellationTokenSource _synthCts = new();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>状态提示（⚠ 开头，节流后），由 UI 层转气泡。</summary>
    public event Action<string>? StatusChanged;

    /// <summary>合成完 → 携带 wav 路径（后台线程触发，UI 层封回主线程播放）。</summary>
    public event Action<string>? PlayRequested;

    public TtsService(DataStore store)
    {
        _store = store;
        _cacheDir = Path.Combine(Path.GetTempPath(), "kaoyan_tts");
        try { Directory.CreateDirectory(_cacheDir); } catch { /* 忽略，合成时再判 */ }
        CleanCache();   // 启动时清理历史缓存：wav 只播一次，旧文件留着只会无限累积
    }

    /// <summary>
    /// 清理 1 小时前的合成缓存（每次播报都新写文件，旧 wav 播完即无价值）。
    /// 启动时执行一次即可：运行期间文件只在生成后几秒内被播放，不会被误删。
    /// </summary>
    private void CleanCache()
    {
        try
        {
            long cutoff = DateTime.UtcNow.AddHours(-1).Ticks;
            foreach (string f in Directory.EnumerateFiles(_cacheDir, "ellen_*.wav"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f).Ticks < cutoff)
                        File.Delete(f);
                }
                catch { /* 单文件失败忽略（可能正被播放占用），不阻塞清理 */ }
            }
        }
        catch { /* 目录不可用/被清 → 忽略 */ }
    }

    public bool Enabled => _enabled;

    private JsonObject? TtsCfg => DataStore.GetObj(_store.Data, "tts");
    private string Url => (DataStore.GetString(TtsCfg?["url"]) is { Length: > 0 } u ? u : "http://127.0.0.1:9880").TrimEnd('/');
    private string ServerCmd => DataStore.GetString(TtsCfg?["server_cmd"]);
    private string RefAudioPath => DataStore.GetString(TtsCfg?["ref_audio_path"]);
    private string PromptText => DataStore.GetString(TtsCfg?["prompt_text"]);
    private string PromptLang => DataStore.GetString(TtsCfg?["prompt_lang"]) is { Length: > 0 } l ? l : "zh";

    // ------------------------------------------------------------ 生命周期

    /// <summary>开关：开启→后台拉起服务、就绪后才真正可用；关闭→零占用并终止本服务拉起的进程。</summary>
    public void SetEnabled(bool on)
    {
        if (on)
        {
            _wanted = true;
            // 重新开启时换一枚新令牌（上一枚可能在关闭时被 Cancel 过）
            if (_synthCts.IsCancellationRequested)
            {
                var old = _synthCts;
                _synthCts = new CancellationTokenSource();
                old.Dispose();
            }
            if (!_enabled && !_starting)
            {
                _starting = true;
                _ = Task.Run(BgStartAsync);
            }
        }
        else
        {
            _wanted = false;
            _starting = false;
            _enabled = false;
            _synthCts.Cancel();   // 在途合成立刻终止，别再往正在关的服务发请求
            KillProc();
        }
    }

    private async Task BgStartAsync()
    {
        try
        {
            bool ok = await EnsureServerAsync();
            bool hasCmd = !string.IsNullOrWhiteSpace(ServerCmd);
            // 服务就绪，或外部手动管理（无命令，speak 会静默兜底）→ 启用
            if (_wanted && (ok || !hasCmd))
            {
                _enabled = true;
                WarmUp();   // 预热：消除 CUDA 首句惩罚（首次推理 ~9s → 预热后 ~1.5s）
            }
        }
        catch
        {
            // 后台启动不该让进程挂掉：任何意外都静默（下次 Speak 会再节流提示）
        }
        finally
        {
            _starting = false;
        }
    }

    private async Task<bool> EnsureServerAsync()
    {
        if (await ReachableAsync())
            return true;
        string cmd = ServerCmd.Trim();
        if (cmd.Length == 0)
            return false;
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + cmd)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // 坑（2.2.3 定论，承接 2.2.2）：GSVI 的 fastapi 装在「user site」（%APPDATA%\Python\Python312\site-packages）。
            // 从豆包/自动化会话启动本程序时，子进程会继承三个污染源，必须逐个净化：
            //   1) PYTHONNOUSERSITE=1 → 禁用 user site → fastapi 找不到 → import 秒退；
            //   2) PYTHONPATH / PYTHONHOME → 干扰包定位（早期 §14 误诊为 PYTHONPATH，实为 1)）；
            //   3) PATH 前置的豆包沙箱目录（含 python314.dll / python3.dll / vcruntime140.dll）→
            //      torch 按 PATH 搜 DLL 时加载到错误版本 → 模型加载（s1v3.ckpt）永久卡住 → 90s 超时被杀。
            // 另外 APPDATA 必须保留/补齐（user site 定位依赖它）：
            psi.Environment["PYTHONNOUSERSITE"] = "";
            psi.Environment["PYTHONPATH"] = "";
            psi.Environment["PYTHONHOME"] = "";
            if (!psi.Environment.TryGetValue("APPDATA", out string? appData) || string.IsNullOrEmpty(appData))
                psi.Environment["APPDATA"] = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming");
            if (psi.Environment.TryGetValue("Path", out string? path) && !string.IsNullOrEmpty(path))
            {
                string[] kept = path.Split(';')
                    .Where(p => !string.IsNullOrWhiteSpace(p)
                        && !p.Contains("Doubao", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                psi.Environment["Path"] = string.Join(";", kept);
            }
            _proc = Process.Start(psi);
            if (_proc is not null)
            {
                // 纳入「随父同死」作业：即使本程序被任务管理器强杀/崩溃，cmd 及其 python 孙进程
                // 也会被内核一并回收，不会留下占着 9880 端口与显存的孤儿（线路占用的根因）。
                _job ??= new ChildProcessJob();
                _job.Assign(_proc);
                // 坑：重定向了 stdout/stderr 就必须持续排空——GSVI 加载模型时日志量很大，
                // 匿名管道缓冲区（默认约 4KB）一满子进程的写就阻塞，服务永远起不来、
                // 90s 超时后被杀。原版 Python 用的是 DEVNULL；这里异步读掉丢弃（零副作用）。
                _proc.OutputDataReceived += (_, _) => { };
                _proc.ErrorDataReceived += (_, _) => { };
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
            }
        }
        catch
        {
            _proc = null;
            Warn("⚠ 语音服务启动失败（请检查「语音播报设置…」里的命令）");
            return false;
        }
        // 等待就绪
        long deadline = Environment.TickCount64 + ReadyTimeoutMs;
        bool exited = false;
        while (Environment.TickCount64 < deadline)
        {
            if (_proc is not null && _proc.HasExited)
            {
                exited = true;   // 命令写错等导致进程退出
                break;
            }
            if (await ReachableAsync())
                return true;
            await Task.Delay(ReadyIntervalMs);
        }
        if (exited)
        {
            // 命令写的服务进程很快退出了：大概率是端口已被别的 GSVI 实例占住（bind 失败即退出），
            // 但也有可能是命令本身写错。探一下端口是否还有响应，好把话说明白。
            bool occupied = await ReachableAsync();
            KillProc();
            Warn(occupied
                ? "⚠ 语音服务没起来：语音服务端口已被别的程序占着（先关掉其它 GSVI / 语音服务再试）"
                : "⚠ 语音服务启动失败：命令退出了（请检查「语音播报设置…」）");
            return false;
        }
        if (await ReachableAsync())
            return true;
        KillProc();
        Warn(Brand.TtsNeedService);
        return false;
    }

    /// <summary>程序退出时调用：终止本服务拉起的服务进程（有则杀，无则不动）。</summary>
    public void StopServer() => KillProc();

    /// <summary>
    /// 预热：服务就绪后静默合成一次短句，把 CUDA kernel / 权重页缓存热起来。
    /// GSVI 冷启动首次推理约 9s，预热后约 1.5s；不写文件、不播放、不提示，失败静默。
    /// </summary>
    private void WarmUp()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500);   // 等 uvicorn 完全就绪（就绪探测与监听可能差几百 ms）
                string refPath = ResolveRefAudio(RefAudioPath);
                if (refPath.Length == 0) return;
                string prompt = PromptText.Trim();
                if (prompt.Length == 0) prompt = PromptFromFilename(refPath);
                var payload = new JsonObject
                {
                    ["text"] = "我在呢。",
                    ["text_lang"] = "auto",
                    ["ref_audio_path"] = refPath,
                    ["prompt_lang"] = PromptLang,
                    ["prompt_text"] = prompt,
                    ["text_split_method"] = "cut5",
                    ["speed"] = 1.0,
                };
                using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
                using var cts = new CancellationTokenSource(40_000);
                using var resp = await Http.PostAsync(Url + "/tts", content, cts.Token);
                // 结果丢弃：预热只为让后续合成更快，不播放不落盘
            }
            catch
            {
                // 预热失败完全静默（服务可能刚起还在忙 / 端口被占等，不影响主链路）
            }
        });
    }

    public void Dispose()
    {
        try { _synthCts.Cancel(); } catch { /* 已释放 */ }
        KillProc();
        _job?.Dispose();
        _synthGate.Dispose();
        _synthCts.Dispose();
    }

    // ------------------------------------------------------------ 播报

    /// <summary>把一句话合成并播报。关闭时直接返回，不创建任何线程。</summary>
    public void Speak(string text)
    {
        if (!_enabled)
            return;
        text = (text ?? "").Trim();
        if (text.Length == 0)
            return;
        if (text.Length > MaxText)
            text = text[..MaxText];
        _ = Task.Run(() => SynthesizeAsync(text));
    }

    /// <summary>试听：忽略开关状态强制合成一次（设置页「试听」用），关闭时返回。</summary>
    public void Preview(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        if (text.Length > MaxText) text = text[..MaxText];
        _ = Task.Run(() => SynthesizeAsync(text));
    }

    private async Task SynthesizeAsync(string text)
    {
        CancellationToken ct = _synthCts.Token;
        // 排队等上一句合成完（单卡推理服务并发会互相挤压）；等待期间被关闭则直接放弃
        try { await _synthGate.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (ct.IsCancellationRequested) return;
            string url = Url;
            string refPath = ResolveRefAudio(RefAudioPath);
            if (refPath.Length == 0)
            {
                Warn(Brand.TtsNeedRef);
                return;
            }
            string prompt = PromptText.Trim();
            if (prompt.Length == 0)
                // SoVITS V3 要求 prompt_text：配置没填时，从 GSVI 文件名「【…】转写」兜底
                prompt = PromptFromFilename(refPath);

            var payload = new JsonObject
            {
                ["text"] = text,
                ["text_lang"] = "auto",
                ["ref_audio_path"] = refPath,
                ["prompt_lang"] = PromptLang,
                ["prompt_text"] = prompt,
                ["text_split_method"] = "cut5",
                ["speed"] = 1.0,
            };

            try
            {
                using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
                using var resp = await Http.PostAsync(url + "/tts", content, ct);
                byte[] data = await resp.Content.ReadAsByteArrayAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    // 服务在但拒绝（如 ref 路径不对 / 语言不支持）→ 尽量带出服务端真实原因
                    Warn(RejectReason((int)resp.StatusCode, data));
                    return;
                }
                if (data.Length == 0 || !(IsRiff(data) || IsOgg(data)))
                {
                    Warn(Brand.TtsVoiceFail);
                    return;
                }
                string path = Path.Combine(_cacheDir, "ellen_" + Interlocked.Increment(ref _seq) + ".wav");
                await File.WriteAllBytesAsync(path, data, ct);
                PlayRequested?.Invoke(path);   // 后台线程 → UI 层封回主线程播放
            }
            catch (OperationCanceledException)
            {
                // 服务被关闭/重启时主动取消，不算故障，不提示
            }
            catch
            {
                // 服务没起/网络出错 → 安静跳过，只节流提示
                Warn(Brand.TtsVoiceFail);
            }
        }
        finally
        {
            _synthGate.Release();
        }
    }

    private static string RejectReason(int status, byte[] data)
    {
        string reason = "";
        try
        {
            var body = JsonNode.Parse(Encoding.UTF8.GetString(data));
            if (body is JsonObject obj)
                reason = (obj["Exception"] ?? obj["message"])?.GetValue<string>() ?? "";
        }
        catch
        {
            // 忽略，走兜底
        }
        string low = reason.ToLowerInvariant();
        string hint;
        if (low.Contains("prompt_text"))
            hint = "右键「语音播报设置…」填「参考音频文字」（SoVITS V3 必填，可看文件名【…】后的转写）";
        else if (low.Contains("errno") || low.Contains("invalid") || low.Contains("tts failed"))
            hint = "可能端口被旧的语音服务实例占用：关掉其它 GSVI 语音服务，再在「语音播报设置」里关掉重开一次";
        else if (low.Contains("ref") || low.Contains("not exists") || low.Contains("file"))
            hint = "检查「语音播报设置…」的参考音频路径是否正确";
        else if (low.Contains("text"))
            hint = "说点有效内容再试";
        else
            hint = "稍后再试";
        return $"⚠ 语音服务拒绝了请求（{status}）{(reason.Length > 0 ? "：" + reason : "")}——{hint}";
    }

    // ------------------------------------------------------------ 内部

    /// <summary>探测服务是否就绪：能收到 HTTP 响应（含 400/422）即视为在监听。</summary>
    private async Task<bool> ReachableAsync()
    {
        string url = Url + "/tts";
        try
        {
            using var cts = new CancellationTokenSource(3000);
            using var content = new StringContent("{\"text\":\"ping\",\"text_lang\":\"auto\"}", Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(url, content, cts.Token);
            return true;   // 任何 HTTP 响应都说明服务在
        }
        catch
        {
            return false;  // 连接失败/超时 → 不在
        }
    }

    /// <summary>参考音频路径可能随程序搬移失效（配置里存的是旧绝对路径）→ 依次回退解析。</summary>
    private static string ResolveRefAudio(string path)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0)
            return "";
        if (File.Exists(path))
            return path;
        string base_ = AppPaths.BaseDir;
        string cand = Path.Combine(base_, path.TrimStart('/', '\\'));
        if (File.Exists(cand))
            return cand;
        string fname = Path.GetFileName(path);
        if (fname.Length > 0)
        {
            string modelDir = Path.Combine(base_, "model");
            if (Directory.Exists(modelDir))
                foreach (string f in Directory.EnumerateFiles(modelDir, fname, SearchOption.AllDirectories))
                    return f;
        }
        return path;
    }

    /// <summary>GSVI 参考音频常命名为「【标签】转写.wav」→ 从文件名提取转写兜底。</summary>
    private static string PromptFromFilename(string path)
    {
        string name = Path.GetFileName(path);
        int idx = name.IndexOf('】');
        if (idx < 0)
            return "";
        string tail = name[(idx + 1)..];
        int dot = tail.LastIndexOf('.');
        if (dot >= 0)
            tail = tail[..dot];
        return tail.Trim();
    }

    private static bool IsRiff(byte[] d) => d.Length >= 4 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F';
    private static bool IsOgg(byte[] d) => d.Length >= 4 && d[0] == 'O' && d[1] == 'g' && d[2] == 'g' && d[3] == 'S';

    private void Warn(string msg)
    {
        lock (_warnLock)
        {
            long now = Environment.TickCount64;
            if (now - _lastWarnTicks < WarnIntervalMs)
                return;
            _lastWarnTicks = now;
        }
        StatusChanged?.Invoke(msg);
    }

    private void KillProc()
    {
        Process? proc = _proc;
        _proc = null;
        if (proc is null)
            return;
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                // Kill 是异步的：等它真正退出（最多 3s），否则紧接着重启会撞上
                // 还没释放 9880 端口的旧进程，表现为「端口被占/线路占用」。
                proc.WaitForExit(3000);
            }
        }
        catch
        {
            // 已退出/无权限 → 忽略
        }
        proc.Dispose();
    }
}
