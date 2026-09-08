using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KaoyanPlanner.WPF.Services;
using RadioButton = System.Windows.Controls.RadioButton;

namespace KaoyanPlanner.WPF.Views.Settings;

/// <summary>
/// 设置页宿主：主界面内的一整页（非浮窗）。左侧分栏（通用/桌宠/AI/语音/字幕/关于）+ 右侧内容。
/// 所有修改立即生效：开关/下拉/日期一改即写盘，文本框 LostFocus/回车提交，页头只有「← 返回」。
/// 分栏切换沿用主窗的 160ms 淡入 + 上移动画（尊重系统动效开关与 reduce_motion）。
/// </summary>
public partial class SettingsPage : UserControl
{
    private static readonly Dictionary<string, string> Titles = new()
    {
        ["general"] = "通用",
        ["pet"] = "桌宠",
        ["chat"] = "AI 聊天",
        ["tts"] = "语音播报",
        ["caption"] = "实时字幕",
        ["about"] = "关于",
    };

    private readonly DataStore _store;
    private readonly MainWindow _host;

    private SettingsGeneralTab? _general;
    private SettingsPetTab? _pet;
    private SettingsChatTab? _chat;
    private SettingsTtsTab? _tts;
    private SettingsCaptionTab? _caption;
    private SettingsAboutTab? _about;

    private bool _loading = true;

    public SettingsPage(DataStore store, MainWindow host)
    {
        InitializeComponent();
        _store = store;
        _host = host;
        ShowSection("general");
        _loading = false;
    }

    /// <summary>切换到某个分栏（默认通用）。由主窗 ⚙ 或桌宠右键跳转调用。</summary>
    public void ShowSection(string tag)
    {
        if (string.IsNullOrEmpty(tag)) tag = "general";
        UserControl tab = EnsureTab(tag);
        pageTitle.Text = Titles.TryGetValue(tag, out string? t) ? t : "设置";

        navGeneral.IsChecked = tag == "general";
        navPet.IsChecked = tag == "pet";
        navChat.IsChecked = tag == "chat";
        navTts.IsChecked = tag == "tts";
        navCaption.IsChecked = tag == "caption";
        navAbout.IsChecked = tag == "about";

        if (ReferenceEquals(sectionHost.Content, tab))
        {
            // 同栏再切：兜底恢复完全不透明（防中断动画残留）
            tab.Opacity = 1;
            if (tab.RenderTransform is TranslateTransform tt0) tt0.Y = 0;
            (tab as ISettingsSection)?.Refresh();
            return;
        }

        if (tab.RenderTransform is not TranslateTransform tt)
        {
            tt = new TranslateTransform();
            tab.RenderTransform = tt;
        }
        tab.Opacity = 0;
        tt.Y = 8;
        sectionHost.Content = tab;
        (tab as ISettingsSection)?.Refresh();

        if (SystemParameters.ClientAreaAnimation && !ReduceMotion)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            tab.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        }
        else
        {
            tab.Opacity = 1;
            tt.Y = 0;
        }
    }

    private bool ReduceMotion => DataStore.GetBool(_store.Data["reduce_motion"]);

    private UserControl EnsureTab(string tag) => tag switch
    {
        "general" => _general ??= new SettingsGeneralTab(_store, _host),
        "pet" => _pet ??= new SettingsPetTab(_store, _host),
        "chat" => _chat ??= new SettingsChatTab(_store),
        "tts" => _tts ??= new SettingsTtsTab(_store),
        "caption" => _caption ??= new SettingsCaptionTab(_store, _host),
        _ => _about ??= new SettingsAboutTab(),
    };

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is RadioButton rb && rb.Tag is string tag)
            ShowSection(tag);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _host.ReturnFromSettings();
}
