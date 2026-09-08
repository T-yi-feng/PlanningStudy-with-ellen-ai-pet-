using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KaoyanPlanner.WPF.Controls;
using KaoyanPlanner.WPF.Services;
using Forms = System.Windows.Forms;

namespace KaoyanPlanner.WPF.Views.Settings;

/// <summary>
/// 语音播报设置：tts 的服务启动命令 + 参考音频（音色来源）+ 语言 + 服务状态灯 + 试听。
/// 写回 data.json 的 tts 配置（不碰其它键）；文本框回车/失焦提交，立即生效。
/// 注：正在运行的语音服务不会因这里改动而重启（对齐原对话框行为）。
/// </summary>
public partial class SettingsTtsTab : UserControl
{
    private readonly DataStore _store;
    private readonly MainWindow _host;
    private bool _loading = true;
    private readonly DispatcherTimer _probe;

    public SettingsTtsTab(DataStore store, MainWindow host)
    {
        InitializeComponent();
        _store = store;
        _host = host;

        var tts = DataStore.GetObj(_store.Data, "tts");
        cmdBox.Text = DataStore.GetString(tts?["server_cmd"]);
        refBox.Text = DataStore.GetString(tts?["ref_audio_path"]);
        promptBox.Text = DataStore.GetString(tts?["prompt_text"]);
        string lang = DataStore.GetString(tts?["prompt_lang"]) is { Length: > 0 } l ? l : "zh";
        bool preset = false;
        foreach (var item in langBox.Items)
            if (item is ComboBoxItem cbi && string.Equals(cbi.Content?.ToString(), lang, System.StringComparison.OrdinalIgnoreCase))
            {
                langBox.SelectedItem = item;
                preset = true;
                break;
            }
        if (!preset) langBox.Text = lang;

        cmdBox.LostKeyboardFocus += (_, _) => Commit();
        refBox.LostKeyboardFocus += (_, _) => Commit();
        promptBox.LostKeyboardFocus += (_, _) => Commit();
        langBox.LostKeyboardFocus += (_, _) => Commit();
        cmdBox.KeyDown += CommitOnEnter;
        refBox.KeyDown += CommitOnEnter;
        promptBox.KeyDown += CommitOnEnter;
        langBox.KeyDown += CommitOnEnter;

        _probe = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _probe.Tick += (_, _) => ProbeOnce();
        _probe.Start();
        ProbeOnce();

        _loading = false;
    }

    // ------------------------------------------------------------ 服务状态

    /// <summary>轻量端口探测：能 TCP 连上 9880 即认为服务在线（只读连接，不发数据）。</summary>
    private static bool IsServerUp()
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync("127.0.0.1", 9880);
            return task.Wait(500) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private void ProbeOnce()
    {
        if (!IsVisible) return;
        bool up = IsServerUp();
        statusDot.Fill = up ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("DangerBrush");
        statusLbl.Text = up ? "语音服务在线" : "语音服务未连接";
    }

    private void Test_Click(object sender, RoutedEventArgs e) => ProbeOnce();

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        if (_host.PetWindow is { } pet)
        {
            pet.PreviewTts();
            ToastHost.Show(Window.GetWindow(this), "已发送试听，稍等合成…");
        }
    }

    // ------------------------------------------------------------ 参考音频浏览

    private void BrowseRef_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Forms.OpenFileDialog
        {
            Title = "选择参考音频",
            Filter = "音频文件 (*.wav;*.mp3)|*.wav;*.mp3|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = !string.IsNullOrWhiteSpace(refBox.Text)
                ? System.IO.Path.GetDirectoryName(refBox.Text) ?? ""
                : "",
        };
        if (dlg.ShowDialog() == Forms.DialogResult.OK)
        {
            refBox.Text = dlg.FileName;
            Commit();
        }
    }

    // ------------------------------------------------------------ 提交

    private void CommitOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Commit();
    }

    private void Commit()
    {
        if (_loading) return;
        var tts = DataStore.GetOrCreateObj(_store.Data, "tts");
        tts["server_cmd"] = cmdBox.Text.Trim();
        tts["ref_audio_path"] = refBox.Text.Trim();
        tts["prompt_text"] = promptBox.Text.Trim();
        tts["prompt_lang"] = (langBox.SelectedItem is ComboBoxItem cbi ? (cbi.Content?.ToString() ?? "") : langBox.Text).Trim() is { Length: > 0 } l ? l : "zh";
        _store.SaveQuiet();
    }
}
