using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KaoyanPlanner.WPF.Services;

namespace KaoyanPlanner.WPF.Views.Settings;

/// <summary>
/// 通用设置：考研日期 + 窗口置顶 + 开机自启 + 降低动效 + 字体个性化
/// （字体族 ui.font_family / 字重 ui.font_weight，全站 DynamicResource 即时生效）。
/// </summary>
public partial class SettingsGeneralTab : UserControl, ISettingsSection
{
    private sealed record FontOption(string Display, string Value, FontFamily Family);
    private sealed record WeightOption(string Display, string Value);

    // 候选字体（本机没装的自动隐藏）：带圆润标注的优先，满足「偏好圆角字体」。
    private static readonly (string Display, string Value)[] FontCandidates =
    {
        ("跟随系统默认", ""),
        ("微软雅黑 UI", "Microsoft YaHei UI"),
        ("幼圆（圆润）", "YouYuan"),
        ("MiSans（圆润）", "MiSans"),
        ("等线", "DengXian"),
        ("微软雅黑", "Microsoft YaHei"),
        ("黑体", "SimHei"),
        ("楷体", "KaiTi"),
        ("宋体", "SimSun"),
        ("仿宋", "FangSong"),
        ("Segoe UI", "Segoe UI"),
    };

    private static readonly (string Display, string Value)[] WeightCandidates =
    {
        ("常规", "normal"),
        ("中等", "medium"),
        ("半粗", "semibold"),
        ("加粗", "bold"),
    };

    private readonly DataStore _store;
    private readonly MainWindow _host;
    private bool _loading = true;

    public SettingsGeneralTab(DataStore store, MainWindow host)
    {
        InitializeComponent();
        _store = store;
        _host = host;

        string exam = DataStore.GetString(_store.Data["exam_date"]);
        if (DateTime.TryParseExact(exam, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ed))
            examPicker.SelectedDate = ed;
        topmostCheck.IsChecked = DataStore.GetBool(_store.Data["window_topmost"], true);
        autostartCheck.IsChecked = AutostartService.IsEnabled();
        reduceMotionCheck.IsChecked = DataStore.GetBool(_store.Data["reduce_motion"]);

        InitFontControls();
        _loading = false;

        examPicker.SelectedDateChanged += Exam_Changed;
        topmostCheck.Click += Topmost_Click;
        autostartCheck.Click += Autostart_Click;
        reduceMotionCheck.Click += ReduceMotion_Click;
        fontBox.SelectionChanged += Font_Changed;
        weightBox.SelectionChanged += Weight_Changed;
    }

    public void Refresh() { }

    // ------------------------------------------------------------ 字体

    private void InitFontControls()
    {
        var ui = DataStore.GetObj(_store.Data, "ui");
        string currentFamily = DataStore.GetString(ui?["font_family"]);
        string currentWeight = DataStore.GetString(ui?["font_weight"]);
        if (currentWeight.Length == 0)
            currentWeight = DataStore.GetBool(ui?["codex_font"], true) ? "semibold" : "normal";

        // 字体族（按本机已装过滤）
        var installed = new HashSet<string>(
            Fonts.SystemFontFamilies.Select(f => f.Source), StringComparer.OrdinalIgnoreCase);
        var options = new List<FontOption>();
        foreach (var (display, value) in FontCandidates)
        {
            if (value.Length > 0 && !installed.Contains(value)) continue;
            options.Add(new FontOption(display, value,
                value.Length == 0 ? new FontFamily(App.DefaultUiFont) : new FontFamily(value)));
        }
        fontBox.ItemsSource = options;
        fontBox.SelectedItem = options.FirstOrDefault(o =>
            string.Equals(o.Value, currentFamily, StringComparison.OrdinalIgnoreCase)) ?? options[0];

        // 字重
        var weights = WeightCandidates.Select(w => new WeightOption(w.Display, w.Value)).ToList();
        weightBox.ItemsSource = weights;
        weightBox.SelectedItem = weights.FirstOrDefault(w => w.Value == currentWeight) ?? weights[2];
    }

    private void Font_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || fontBox.SelectedItem is not FontOption fo) return;
        var ui = DataStore.GetOrCreateObj(_store.Data, "ui");
        if (fo.Value.Length == 0)
            ui.Remove("font_family");                       // 默认 → 恢复缺省键（字节契约）
        else
            ui["font_family"] = fo.Value;
        _store.SaveQuiet();
        App.ApplyFontSettings(ui);
    }

    private void Weight_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || weightBox.SelectedItem is not WeightOption wo) return;
        var ui = DataStore.GetOrCreateObj(_store.Data, "ui");
        ui["font_weight"] = wo.Value;
        _store.SaveQuiet();
        App.ApplyFontSettings(ui);
    }

    // ------------------------------------------------------------ 其他

    private void Exam_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (examPicker.SelectedDate is DateTime dt)
            _store.Data["exam_date"] = dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        else
            _store.Data["exam_date"] = "";   // 清空 → 倒计时退回显示今日日期
        _store.Save();                        // Changed → 标题倒计时刷新
    }

    private void Topmost_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool top = topmostCheck.IsChecked == true;
        _store.Data["window_topmost"] = top;
        _store.SaveQuiet();
        _host.ApplyTopmost(top);
    }

    private void Autostart_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool want = autostartCheck.IsChecked == true;
        if (want == AutostartService.IsEnabled()) return;
        if (AutostartService.SetEnabled(want))
            return;
        autostartCheck.IsChecked = !want;   // 失败回滚勾选态
        MessageBox.Show("设置开机自启失败（注册表写入出错）", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ReduceMotion_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _store.Data["reduce_motion"] = reduceMotionCheck.IsChecked == true;
        _store.SaveQuiet();
    }
}
