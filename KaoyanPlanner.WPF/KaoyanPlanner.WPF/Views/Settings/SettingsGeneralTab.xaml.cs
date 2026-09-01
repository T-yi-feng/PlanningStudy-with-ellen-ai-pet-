using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using KaoyanPlanner.WPF.Services;

namespace KaoyanPlanner.WPF.Views.Settings;

/// <summary>
/// 通用设置：考研日期（驱动标题倒计时）+ 窗口置顶 + 开机自启 + 降低动效。立即生效。
/// </summary>
public partial class SettingsGeneralTab : UserControl, ISettingsSection
{
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

        examPicker.SelectedDateChanged += Exam_Changed;
        topmostCheck.Click += Topmost_Click;
        autostartCheck.Click += Autostart_Click;
        reduceMotionCheck.Click += ReduceMotion_Click;
        _loading = false;
    }

    public void Refresh() { }

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
