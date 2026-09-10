using System.Windows;
using System.Windows.Media;
using KaoyanPlanner.WPF.Services;
using KaoyanPlanner.WPF.Views;
using KaoyanPlanner.WPF.Views.Pet;

namespace KaoyanPlanner.WPF;

/// <summary>
/// 组合根：主题字典已在 App.xaml 合并。
/// M6：单实例检查 → 数据加载 → 心跳 → 托盘 → 显示主窗口。
/// P1（阶段二）：桌宠艾莲启动默认显示（对齐旧版 main.py 无条件 win.show()，无开关配置），
/// 与主窗口共享同一个 FocusTimerService（状态条显示专注时长）。关闭主窗口只隐藏到托盘；
/// 托盘「退出」结束进程（宠物随进程一起结束）。
/// </summary>
public partial class App : Application
{
    private DataStore _store = null!;
    private HeartbeatService? _heartbeat;
    private FocusTimerService? _focusTimer;
    private MainWindow? _mainWindow;
    private PetWindow? _petWindow;
    private TrayService? _tray;
    private SingleInstance? _single;

    /// <summary>
    /// 应用全局字体设置（设置页「外观」）：字体族 ui.font_family（空 = 默认）+ 字重 ui.font_weight
    /// （normal/medium/semibold/bold；未显式配置时回退旧 ui.codex_font 开关）。
    /// 改 Application 级 DynamicResource（UIFont / UiFontWeight），全站文本即时刷新；
    /// 蓝底白字为固定设计原则，不随字体变化。图标字体 IconFont 不受影响。
    /// </summary>
    public const string DefaultUiFont = "Segoe UI, Microsoft YaHei UI";

    public static void ApplyFontSettings(System.Text.Json.Nodes.JsonObject? ui)
    {
        var res = Application.Current.Resources;
        string family = DataStore.GetString(ui?["font_family"]);
        res["UIFont"] = string.IsNullOrEmpty(family) ? new FontFamily(DefaultUiFont) : new FontFamily(family);
        string w = DataStore.GetString(ui?["font_weight"]);
        FontWeight weight = w switch
        {
            "bold" => FontWeights.Bold,
            "semibold" => FontWeights.SemiBold,
            "medium" => FontWeights.Medium,
            "normal" => FontWeights.Normal,
            _ => DataStore.GetBool(ui?["codex_font"], true) ? FontWeights.SemiBold : FontWeights.Normal,
        };
        res["UiFontWeight"] = weight;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例：已有实例在跑 → 唤醒它（还原窗口）并退出自己（对齐旧版 QLocalServer 语义）
        // 唤醒回调在后台线程触发，可能早于 _mainWindow 初始化完成（如开机双实例竞态）→ 空安全调用，避免 “null this” 委托崩溃
        _single = new SingleInstance(() => Dispatcher.InvokeAsync(() => _mainWindow?.ShowFromTray()));
        if (!_single.TryAcquire())
        {
            Shutdown();
            return;
        }

        _store = new DataStore();
        _store.Load();

        // 外观：从 data.json 读字体设置（字体族 + 字重，兼容旧 codex_font 开关），在窗口创建前应用
        var uiCfg = DataStore.GetObj(_store.Data, "ui");
        ApplyFontSettings(uiCfg);

        _heartbeat = new HeartbeatService(_store);
        _focusTimer = new FocusTimerService(_store);
        // CurrentPlan 默认 null = 「不分类」。专注目标 = 长期计划（固定任务），由用户在计时页点选；桌宠遥控专注继承当前选择。
        _mainWindow = new MainWindow(_store, _heartbeat, _focusTimer);
        _petWindow = new PetWindow(_store, _mainWindow, _focusTimer);
        _mainWindow.PetWindow = _petWindow;   // 设置页换皮肤/字幕设置后实时通知桌宠
        _tray = new TrayService(_mainWindow, _petWindow);

        _heartbeat.Start();
        _mainWindow.Show();
        _petWindow.Show();   // 桌宠启动默认显示（阶段二要求）
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _petWindow?.ShutdownTts();       // 终止宠物拉起的本地语音服务进程（释放显存）
        _petWindow?.ShutdownCaption();   // 停实时字幕采集/识别线程
        _tray?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
