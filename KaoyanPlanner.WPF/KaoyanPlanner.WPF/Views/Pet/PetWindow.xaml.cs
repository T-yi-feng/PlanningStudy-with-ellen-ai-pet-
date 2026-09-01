using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KaoyanPlanner.WPF.Services;
// UseWindowsForms 会隐式导入 System.Windows.Forms + System.Drawing → 命名冲突全局别名
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace KaoyanPlanner.WPF.Views.Pet;

/// <summary>
/// 桌宠「艾莲」主窗口：.ani 动画播放 + 头顶状态条 + 常驻输入框 + 对话气泡 + 右键菜单。
/// 移植 pet.py PetWindow/PetStatus/PetChatBar 的核心交互：
/// - 单击仅用于拖动（不再打开主窗口）；右键菜单：回到主窗口 / 设置 / 聊天记录 / 退出
/// - 动画状态机：normal/talking/happy/present/sleep，一次性动画播完回常态；太久没互动 → 趴睡
/// - 对话：本地指令改计划 / AI 闲聊（离线兜底），气泡 Q 弹回复，图片粘贴走视觉模型
/// - 头顶状态条每秒刷新任务进度 + 专注状态；闲话按配置间隔主动冒泡
/// TTS / 语音输入 / 实时字幕留 P2/P3（右键菜单项暂为占位）。
/// </summary>
public partial class PetWindow : Window
{
    private const int PetWidth = 190;
    private const int IdleTooLongMs = 5 * 60 * 1000;
    private const int IdleAiBatch = 5;
    private const long AiIdleFailCooldownMs = 30L * 60 * 1000;
    private const int BubbleTimeoutMs = 10000;

    private static readonly string[] DoneReplyMarks = { "搞定", "划掉", "已勾掉", "全部完成", "完成啦", "太棒了" };
    private static readonly string[] PraiseKeys =
        { "夸", "表扬", "好棒", "真棒", "很棒", "棒棒", "棒", "厉害", "可爱", "聪明", "漂亮", "乖", "喜欢", "爱你", "欣赏", "👍", "❤", "😍" };

    public DataStore Store => _store;
    public MainWindow? MainWindow => _mainWindow;

    private readonly DataStore _store;
    private readonly MainWindow _mainWindow;
    private readonly FocusTimerService _focusTimer;
    private readonly PetCommandService _commands;
    private readonly PetChatService _chat;
    private readonly TtsService _tts;
    private MediaPlayer? _ttsPlayer;
    private readonly CaptionService _caption;
    private BlackboardWindow? _blackboard;
    private readonly DispatcherTimer _captionAnimTimer;

    private readonly Dictionary<string, AniClip> _clips = new();
    private readonly DispatcherTimer _animTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _idleCheckTimer;
    private readonly DispatcherTimer _idleSpeakTimer;
    private readonly Queue<string> _aiIdleLines = new();

    private string _animName = "normal";
    private int _frameIdx;
    private bool _oneshot;
    private long _lastInteractionTicks;
    private Point _dragOffset;
    private bool _dragging;
    private string? _pendingB64;
    private string _lastUserText = "";
    private PetBubbleWindow? _bubble;
    private ChatWindow? _chatWindow;
    private bool _busy;
    private bool _aiIdleBusy;
    private long _aiIdleCooldownUntil;

    public PetWindow(DataStore store, MainWindow mainWindow, FocusTimerService focusTimer)
    {
        InitializeComponent();
        _store = store;
        _mainWindow = mainWindow;
        _focusTimer = focusTimer;
        _commands = new PetCommandService(store, focusTimer);
        _chat = new PetChatService(store, TaskContext);
        _tts = new TtsService(store);
        // 坑：TtsService 的 StatusChanged 在后台线程发（Warn 来自 Task.Run 的
        // SynthesizeAsync/EnsureServerAsync）。SpeakBubble 是 WPF 窗口操作，
        // 直接调用会跨线程访问窗口对象抛 InvalidOperationException 崩整个进程——
        // 必须像 _caption.StatusChanged 一样用 Dispatcher 封回主线程。
        _tts.StatusChanged += status => Dispatcher.InvokeAsync(() =>
        {
            if (status.StartsWith("⚠")) SpeakBubble(status);
        });
        _tts.PlayRequested += PlayTts;

        // 实时媒体字幕（黑板）：独立于对话气泡，右键菜单开关 + 设置（移植 pet.py 字幕部分）
        _caption = new CaptionService(
            () => SecretService.Get("asr_api_key"),
            () => DataStore.GetString(DataStore.GetObj(_store.Data, "caption")?["language"]),
            () => DataStore.GetString(DataStore.GetObj(_store.Data, "caption")?["model"]));
        _caption.TextReady += text => Dispatcher.InvokeAsync(() => OnCaptionText(text));
        _caption.InterimReady += text => Dispatcher.InvokeAsync(() => OnCaptionInterim(text));
        _caption.StatusChanged += status => Dispatcher.InvokeAsync(() => OnCaptionStatus(status));
        _blackboard = new BlackboardWindow(() => DataStore.GetObj(_store.Data, "caption"));
        _blackboard.SetLanguageLabel(CaptionLabels.LangLabel(
            DataStore.GetString(DataStore.GetObj(_store.Data, "caption")?["language"])));

        // 字幕说话表情：字幕持续到达则保持说话；停约 1.6s 后回常态
        _captionAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _captionAnimTimer.Tick += (_, _) =>
        {
            _captionAnimTimer.Stop();
            if (_animName == "talking" && !_oneshot) SetAnim(BaseAnim());
        };

        LoadClips();

        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_clips["normal"].DelayMs) };
        _animTimer.Tick += (_, _) => AnimTick();
        _animTimer.Start();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        _idleCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _idleCheckTimer.Tick += (_, _) => CheckIdle();
        _idleCheckTimer.Start();

        // 闲话：启动 90s 先聊一次，之后按配置间隔（默认 8 分钟）
        _idleSpeakTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        _idleSpeakTimer.Tick += (_, _) =>
        {
            IdleSpeak();
            _idleSpeakTimer.Interval = TimeSpan.FromMinutes(IdleIntervalMin());
        };
        _idleSpeakTimer.IsEnabled = DataStore.GetBool(DataStore.GetObj(_store.Data, "pet_idle")?["enabled"], true);
        _idleSpeakTimer.Start();

        // 图片粘贴拦截：Ctrl+V / 右键粘贴图片 → 存 b64 + 气泡缩略图
        DataObject.AddPastingHandler(petInput, OnPasting);

        // 右键菜单勾选态对齐配置
        menuAutostart.IsChecked = AutostartService.IsEnabled();
        menuCaption.IsChecked = DataStore.GetBool(DataStore.GetObj(_store.Data, "caption")?["enabled"]);
        // 语音输入（voice）尚未实现：菜单做成不可勾选的「敬请期待」占位，
        // 不再读 voice.enabled（读了也无处生效，还会和 data.json 状态打架）。
        menuTts.IsChecked = DataStore.GetBool(DataStore.GetObj(_store.Data, "tts")?["enabled"]);

        // 恢复上次播报状态：开启 → 后台拉起本地语音服务（关着就零占用）
        if (DataStore.GetBool(DataStore.GetObj(_store.Data, "tts")?["enabled"]))
            _tts.SetEnabled(true);

        _lastInteractionTicks = Environment.TickCount64;
        MaybeRefillAiIdle();
    }

    /// <summary>启动后定位到屏幕右下角（镜像 pet.py position_bottom_right）。</summary>
    public void PositionBottomRight()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 16;
        Top = wa.Bottom - ActualHeight - 16;
    }

    /// <summary>气泡定位参考：状态条顶部（整个宠物窗的最上沿）。</summary>
    public double StatusBarTop() => Top + 4;

    private bool _positioned;

    /// <summary>首次加载后定位到屏幕右下角（Loaded 时才有实际尺寸）。</summary>
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_positioned) return;
        _positioned = true;
        PositionBottomRight();
        RestoreCaptionState();   // 恢复上次字幕状态：开启 → 开始采集识别 + 黑板贴到宠物旁
    }

    /// <summary>字幕开启 → 后台开始采集识别，且黑板贴到宠物右侧（只在宠物可见时贴）。</summary>
    private void RestoreCaptionState()
    {
        if (!DataStore.GetBool(DataStore.GetObj(_store.Data, "caption")?["enabled"])) return;
        _caption.Start();
        if (IsVisible) _blackboard?.ShowNear(this);
    }

    /// <summary>托盘/菜单隐藏桌宠：停动画、收气泡、停字幕并藏黑板。</summary>
    public void HidePet()
    {
        _animTimer.Stop();
        CloseBubble();
        _caption.Stop();
        _blackboard?.Hide();
        Hide();
    }

    /// <summary>从托盘还原桌宠。</summary>
    public void ShowPet()
    {
        Show();
        if (!_animTimer.IsEnabled) _animTimer.Start();
        RestoreCaptionState();
        Activate();
    }

    public bool ChatVisible => _chatWindow?.IsVisible == true;

    // ------------------------------------------------------------ 动画

    private void LoadClips()
    {
        _clips.Clear();
        // 形象目录：pet_skin 键 → desk_pet/<名字>/；空/缺省 → 顶层默认艾莲
        string dir = PetSkinService.SkinDirFor(PetSkinService.SkinRootDir,
            DataStore.GetString(_store.Data["pet_skin"]));
        var files = PetSkinService.ResolveClipFiles(dir);
        foreach (string kind in PetSkinService.KindOrder)
            if (files.TryGetValue(kind, out string? path))
            {
                var clip = AniLoader.LoadAni(path);
                if (clip is not null) _clips[kind] = clip;
            }
        // sleep 缺省复用常态（行为较现状放宽；顶层默认仍能解析 alternate.ani）
        if (!_clips.ContainsKey("sleep"))
        {
            if (_clips.TryGetValue("normal", out AniClip? normalClip) && normalClip is not null)
                _clips["sleep"] = normalClip;
            else
                _clips["sleep"] = AniLoader.MakeFallbackClip(PetWidth);
        }
        // 任一核心缺失/损坏 → 整组退回蓝色圆球（镜像 pet.py 的 fallback 逻辑）
        if (!_clips.ContainsKey("normal") || !_clips.ContainsKey("talking")
            || !_clips.ContainsKey("happy") || !_clips.ContainsKey("present"))
        {
            _clips.Clear();
            var fb = AniLoader.MakeFallbackClip(PetWidth);
            _clips["normal"] = fb; _clips["talking"] = fb; _clips["happy"] = fb;
            _clips["present"] = fb; _clips["sleep"] = fb;
        }
        var f0 = _clips["normal"].Frames[0];
        int h = (int)Math.Round(PetWidth * f0.PixelHeight / (double)f0.PixelWidth);
        petImage.Width = PetWidth;
        petImage.Height = h;
        ShowFrame();
    }

    /// <summary>设置里换了形象后立即换装：重载动画、复位状态机并回到常态。</summary>
    public void ReloadSkin()
    {
        LoadClips();
        _animName = "normal";
        _oneshot = false;
        _frameIdx = 0;
        _animTimer.Interval = TimeSpan.FromMilliseconds(_clips["normal"].DelayMs);
        _animTimer.Start();
        ShowFrame();
    }

    private void SetAnim(string name, bool oneshot = false)
    {
        if (!_clips.ContainsKey(name)) return;
        if (_animName == name && _oneshot == oneshot) return;
        _animName = name;
        _oneshot = oneshot;
        _frameIdx = 0;
        _animTimer.Interval = TimeSpan.FromMilliseconds(_clips[name].DelayMs);
        ShowFrame();
    }

    private void AnimTick()
    {
        var clip = _clips[_animName];
        int n = clip.FrameCount;
        if (n <= 1) return;
        _frameIdx = (_frameIdx + 1) % n;
        if (_oneshot && _frameIdx == 0)
        {
            _oneshot = false;
            SetAnim(BaseAnim());
            return;
        }
        ShowFrame();
    }

    private void ShowFrame()
    {
        var clip = _clips[_animName];
        if (_frameIdx < clip.Frames.Count) petImage.Source = clip.Frames[_frameIdx];
    }

    private string BaseAnim()
    {
        if (petInput.IsKeyboardFocused) return "talking";
        if (IdleTooLong) return "sleep";
        return "normal";
    }

    private bool IdleTooLong => Environment.TickCount64 - _lastInteractionTicks >= IdleTooLongMs;

    private void Touch()
    {
        _lastInteractionTicks = Environment.TickCount64;
        if (_animName == "sleep") SetAnim(BaseAnim());
    }

    private void CheckIdle()
    {
        if (!IsVisible) return;
        if (IdleTooLong)
        {
            if (_animName == "normal" && !_oneshot) SetAnim("sleep");
        }
        else if (_animName == "sleep")
        {
            SetAnim(BaseAnim());
        }
    }

    // ------------------------------------------------------------ 状态条（1s 刷新）

    public void RefreshStatus()
    {
        string day = DataStore.TodayStr();
        var daily = DataStore.GetObj(_store.Data, "daily");
        var tasks = daily?[day] as JsonArray;
        int done = 0;
        int total = tasks?.Count ?? 0;
        if (tasks is not null)
            foreach (var n in tasks)
                if (n is JsonObject t && DataStore.GetBool(t["done"])) done++;

        var fixedArr = _store.Data["tasks"] as JsonArray;
        if (fixedArr is not null)
        {
            total += fixedArr.Count;
            foreach (var n in fixedArr)
                if (n is JsonObject t &&
                    (DataStore.GetBool(t["done"]) ||
                     (DataStore.GetString(t["last_done_date"]) == day && DataStore.GetInt(t["progress"]) > 0)))
                    done++;
        }

        string focus = _focusTimer.Running
            ? "专注 " + FmtClock((long)_focusTimer.ElapsedSeconds)
            : "未专注";
        var marks = new List<string>();
        if (DataStore.GetBool(_store.Data["frozen"])) marks.Add("❄冻结");
        long owe = 0;
        if (fixedArr is not null)
            foreach (var n in fixedArr)
                if (n is JsonObject t && !DataStore.GetBool(t["done"])) owe += DataStore.GetInt(t["owed"]);
        if (owe > 0) marks.Add($"⚠欠{owe}");
        string mark = marks.Count > 0 ? " · " + string.Join(" · ", marks) : "";
        statusText.Text = $"📋 {done}/{total}　·　🎯 {focus}{mark}";
    }

    private static string FmtClock(long sec)
    {
        var ts = TimeSpan.FromSeconds(sec);
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
    }

    // ------------------------------------------------------------ 拖拽（单击仅拖动）

    private void Pet_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragOffset = e.GetPosition(this);
        _dragging = true;
        CloseBubble();
        Touch();
        petImage.CaptureMouse();
    }

    private void Pet_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        Point p = e.GetPosition(this);
        var wa = SystemParameters.WorkArea;
        Left = Math.Max(wa.Left + 4, Math.Min(Left + p.X - _dragOffset.X, wa.Right - ActualWidth - 4));
        Top = Math.Max(wa.Top + 4, Top + p.Y - _dragOffset.Y);
        Touch();
    }

    private void Pet_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        petImage.ReleaseMouseCapture();
    }

    // ------------------------------------------------------------ 输入框

    private void PetInput_TextChanged(object sender, TextChangedEventArgs e)
        => inputPlaceholder.Visibility = petInput.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;

    private void PetInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SendFromBar();
        }
    }

    private void PetInput_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Touch();
        SetAnim("talking");
    }

    private void PetInput_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        => SetAnim("normal");

    private void Send_Click(object sender, RoutedEventArgs e) => SendFromBar();

    private void SendFromBar()
    {
        string text = petInput.Text.Trim();
        string? b64 = _pendingB64;
        petInput.Clear();
        _pendingB64 = null;
        if (text.Length == 0 && b64 is null) return;
        SendChat(text, b64);
    }

    /// <summary>图片粘贴（Ctrl+V / 右键粘贴）：存 b64，气泡提示「回车发送」。</summary>
    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(DataFormats.Bitmap))
        {
            if (e.DataObject.GetData(DataFormats.Bitmap) is BitmapSource bmp)
            {
                _pendingB64 = PngToB64(bmp);
                SpeakBubble("已粘贴图片，回车发送…", bmp);
                e.CancelCommand();   // 图片不粘进文本框
            }
        }
    }

    private static string PngToB64(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    // ------------------------------------------------------------ 对话管线（命令 / AI / 视觉）

    /// <summary>发送一条消息：本地指令优先，否则 AI 闲聊；有图片直接走视觉模型。</summary>
    public void SendChat(string text, string? imageB64)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0 && imageB64 is null) return;
        Touch();
        _lastUserText = t;
        SetAnim("talking");
        if (_chatWindow is not null && _chatWindow.IsVisible)
        {
            if (imageB64 is not null) _chatWindow.AppendImage("me", imageB64);
            if (t.Length > 0) _chatWindow.AppendMessage("me", t);
        }

        if (imageB64 is not null)
        {
            SetBusy(true);
            _ = RunAsync(() => _chat.RespondVisionAsync(t, imageB64));
        }
        else
        {
            var (handled, reply) = _commands.HandleCommand(t);
            if (handled) { Deliver(reply); return; }
            SetBusy(true);
            _ = RunAsync(() => _chat.RespondAsync(t));
        }
    }

    private async Task RunAsync(Func<Task<string>> work)
    {
        string reply;
        try { reply = await Task.Run(work); }
        catch { reply = PetChatService.LocalReply(_lastUserText); }
        _ = Dispatcher.InvokeAsync(() => Deliver(reply));   // 回 UI 线程投递，无需等待
    }

    private void Deliver(string reply)
    {
        SetBusy(false);
        RefreshStatus();
        SpeakBubble(reply);
        _tts_speak(reply);
        ReactToReply(reply);
        if (_chatWindow is not null && _chatWindow.IsVisible) _chatWindow.AppendMessage("pet", reply);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        petInput.IsEnabled = !busy;
        inputPlaceholder.Text = busy ? "艾莲在想着…" : "和艾莲说点什么…（可粘贴图片）";
        if (_chatWindow is not null) _chatWindow.SetBusy(busy);
    }

    private void ReactToReply(string reply)
    {
        string ut = _lastUserText ?? "";
        _lastUserText = "";
        foreach (string m in DoneReplyMarks)
            if ((reply ?? "").Contains(m, StringComparison.Ordinal))
            {
                SetAnim("present", oneshot: true);
                return;
            }
        if (IsPraise(ut)) SetAnim("happy", oneshot: true);
        else SetAnim("talking");
    }

    private static bool IsPraise(string text)
    {
        foreach (string k in PraiseKeys)
            if ((text ?? "").Contains(k, StringComparison.Ordinal)) return true;
        return false;
    }

    private string TaskContext() => _chat.TaskContext();

    // ------------------------------------------------------------ 气泡

    public void SpeakBubble(string text, BitmapSource? image = null)
    {
        if (!IsVisible) return;
        CloseBubble();
        _bubble = new PetBubbleWindow(this, text, image, BubbleTimeoutMs, OnBubbleClosed);
        _bubble.Show();
    }

    private void CloseBubble()
    {
        if (_bubble is not null)
        {
            _bubble.Close();
            _bubble = null;
        }
    }

    private void OnBubbleClosed()
    {
        if (_animName == "talking" && !_oneshot)
        {
            string base_ = BaseAnim();
            if (base_ != "talking") SetAnim(base_);
        }
    }

    // ------------------------------------------------------------ 闲话（配置间隔主动冒泡）

    private long IdleIntervalMin()
    {
        var cfg = DataStore.GetObj(_store.Data, "pet_idle");
        return Math.Max(2, DataStore.GetInt(cfg?["interval_min"], 8));
    }

    private void IdleSpeak()
    {
        if (!IsVisible) return;
        if (_chatWindow is not null && _chatWindow.IsVisible) return;
        if (_animName == "sleep") return;
        var cfg = DataStore.GetObj(_store.Data, "pet_idle");
        if (!DataStore.GetBool(cfg?["enabled"], true)) return;

        string text;
        var debts = new List<JsonObject>();
        if (!DataStore.GetBool(_store.Data["frozen"]) && _store.Data["tasks"] is JsonArray tasks)
            foreach (var n in tasks)
                if (n is JsonObject t && !DataStore.GetBool(t["done"]) && DataStore.GetInt(t["owed"]) > 0)
                    debts.Add(t);
        if (debts.Count > 0 && Random.Shared.NextDouble() < 0.6)
        {
            var names = string.Join("、", debts.Take(2).Select(t => DataStore.GetString(t["text"])));
            string more = debts.Count > 2 ? "等" : "";
            long total = debts.Sum(t => DataStore.GetInt(t["owed"]));
            text = $"喂，「{names}{more}」欠卡共 {total} 天啦，今天点「补卡」打卡一次能补一天哦。";
            if (Random.Shared.NextDouble() < 0.4)
                text = PetChatService.DebtPhrases[Random.Shared.Next(PetChatService.DebtPhrases.Length)];
            MaybeRefillAiIdle();
        }
        else
        {
            text = PickIdleLine();
        }
        SpeakBubble(text);
        _tts_speak(text);
        SetAnim("happy", oneshot: true);   // 鼓励 → 高兴动作
    }

    private string PickIdleLine()
    {
        if (_aiIdleLines.Count > 0)
        {
            MaybeRefillAiIdle();
            return _aiIdleLines.Dequeue();
        }
        MaybeRefillAiIdle();
        string day = DataStore.TodayStr();
        var daily = DataStore.GetObj(_store.Data, "daily");
        var tasks = daily?[day] as JsonArray;
        int pending = 0;
        if (tasks is not null)
            foreach (var n in tasks)
                if (n is JsonObject t && !DataStore.GetBool(t["done"])) pending++;
        if (pending > 0 && Random.Shared.NextDouble() < 0.6)
            return $"还有 {pending} 项计划没完成哦，一起加油！";
        return PetChatService.IdlePhrases[Random.Shared.Next(PetChatService.IdlePhrases.Length)];
    }

    private void MaybeRefillAiIdle()
    {
        if (_aiIdleBusy || _aiIdleLines.Count > 0) return;
        if (Environment.TickCount64 < _aiIdleCooldownUntil) return;
        if (!_chat.IsConfigured()) return;
        _aiIdleBusy = true;
        string context = _chat.TaskContext();
        _ = Task.Run(async () =>
        {
            var lines = await _chat.RespondIdleLinesAsync(IdleAiBatch, context);
            _ = Dispatcher.InvokeAsync(() =>
            {
                _aiIdleBusy = false;
                if (lines.Count == 0)
                    _aiIdleCooldownUntil = Environment.TickCount64 + AiIdleFailCooldownMs;
                else
                    foreach (string ln in lines)
                        if (ln.Length > 0 && !_aiIdleLines.Contains(ln)) _aiIdleLines.Enqueue(ln);
            });
        });
    }

    // ------------------------------------------------------------ 聊天记录窗

    public void OpenChat()
    {
        CloseBubble();
        _chatWindow ??= new ChatWindow(this);
        _chatWindow.Show();
        _chatWindow.Activate();
        _chatWindow.FocusInput();
    }

    // ------------------------------------------------------------ 右键菜单

    private void MenuRestore_Click(object sender, RoutedEventArgs e) => _mainWindow.ShowFromTray();

    private void MenuSettings_Click(object sender, RoutedEventArgs e)
    {
        // 设置不再是浮窗：回到主窗口 → 就地转场进设置页「通用」分栏
        _mainWindow.ShowFromTray();
        _mainWindow.NavigateToSettings("general");
    }

    private void MenuChat_Click(object sender, RoutedEventArgs e) => OpenChat();

    private void MenuCaption_Click(object sender, RoutedEventArgs e)
    {
        bool on = menuCaption.IsChecked;
        var caption = DataStore.GetOrCreateObj(_store.Data, "caption");
        caption["enabled"] = on;
        _store.Save();
        if (on)
        {
            _caption.Start();
            if (IsVisible) _blackboard?.ShowNear(this);
            SpeakBubble("已开启实时字幕 📝（识别电脑播放的声音）");
        }
        else
        {
            _caption.Stop();
            _blackboard?.Hide();
            SpeakBubble("已关闭实时字幕");
        }
    }

    private void MenuCaptionSettings_Click(object sender, RoutedEventArgs e)
    {
        // 字幕设置迁入设置页「实时字幕」分栏（每次改动已实时刷新黑板）
        _mainWindow.ShowFromTray();
        _mainWindow.NavigateToSettings("caption");
    }

    // ------------------------------------------------------------ 实时字幕（黑板）

    private void OnCaptionText(string text)
    {
        CaptionSpeak();   // 字幕生成时艾莲进入“说话”表情
        _blackboard?.FinalizeInterim(text);   // 定稿：替换预览行入幕（无预览行则直接追加）
    }

    private void OnCaptionInterim(string text)
    {
        CaptionSpeak();   // 字幕生成时艾莲进入“说话”表情
        _blackboard?.ShowInterim(text);   // 半成品：替换当前预览行，不新增
    }

    private void OnCaptionStatus(string status) => _blackboard?.SetStatus(status);

    /// <summary>字幕持续到达则保持说话动画；停约 1.6s 后回常态（纯 UI，不影响识别）。</summary>
    private void CaptionSpeak()
    {
        if (_animName != "talking") SetAnim("talking");
        _captionAnimTimer.Stop();
        _captionAnimTimer.Start();
    }

    /// <summary>字幕设置改动后：刷新黑板外观/语言，必要时重新定位（设置页每次提交时调用）。</summary>
    public void ApplyCaptionSettings()
    {
        var caption = DataStore.GetObj(_store.Data, "caption");
        _blackboard?.SetLanguageLabel(CaptionLabels.LangLabel(DataStore.GetString(caption?["language"])));
        _blackboard?.ApplySettings();
        if (DataStore.GetBool(caption?["enabled"]) && IsVisible)
            _blackboard?.ShowNear(this);
    }

    private void MenuVoice_Click(object sender, RoutedEventArgs e)
        => SpeakBubble("语音输入将在后续版本加入 🎤");

    private void MenuTts_Click(object sender, RoutedEventArgs e)
    {
        bool on = menuTts.IsChecked;
        var tts = DataStore.GetOrCreateObj(_store.Data, "tts");
        tts["enabled"] = on;
        _store.Save();
        _tts.SetEnabled(on);
        SpeakBubble(on ? "已开启语音播报 🔊（本地语音服务就绪后才会出声）" : "已关闭语音播报");
    }

    private void MenuTtsSettings_Click(object sender, RoutedEventArgs e)
    {
        // 语音播报设置迁入设置页「语音播报」分栏
        _mainWindow.ShowFromTray();
        _mainWindow.NavigateToSettings("tts");
    }

    private void MenuAutostart_Click(object sender, RoutedEventArgs e)
    {
        bool on = menuAutostart.IsChecked;
        if (AutostartService.SetEnabled(on))
            SpeakBubble(on ? "已开启开机自动启动 ✓" : "已关闭开机自动启动");
        else
        {
            menuAutostart.IsChecked = !on;
            SpeakBubble("⚠ 设置开机自启失败（注册表写入出错）");
        }
    }

    private void MenuQuit_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.AllowClose = true;
        Application.Current.Shutdown();
    }

    // ------------------------------------------------------------ 语音播报（P2）

    /// <summary>艾莲的回复/闲话 → 合成语音播报（开关关着时内部直接返回，零占用）。</summary>
    private void _tts_speak(string text)
    {
        if (!DataStore.GetBool(DataStore.GetObj(_store.Data, "tts")?["enabled"])) return;
        text = (text ?? "").Trim();
        // 空文本或纯标点 → 服务端会 400「请输入有效文本」，不值得播报，直接跳过
        if (text.Length == 0 || text.All(c => "，。！？…、～~·,?!.:;\"'「」()（）".Contains(c))) return;
        _tts.Speak(text);
    }

    /// <summary>合成完的 wav → 主线程播放（PlayRequested 在后台线程触发，这里封回 UI 线程）。</summary>
    private void PlayTts(string path)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _ttsPlayer ??= new MediaPlayer();
            try
            {
                _ttsPlayer.Open(new Uri(path));
                _ttsPlayer.Play();
            }
            catch
            {
                // 播放失败静默（文件可能被服务端写坏等）
            }
        });
    }

    /// <summary>退出时调用：终止本引擎拉起的语音服务进程（有则杀，无则不动）。</summary>
    public void ShutdownTts() => _tts.StopServer();

    /// <summary>退出时调用：停采集、停识别线程、回收音频设备。</summary>
    public void ShutdownCaption() => _caption.Dispose();
}
