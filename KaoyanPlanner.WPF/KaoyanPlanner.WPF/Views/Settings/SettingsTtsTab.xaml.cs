using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KaoyanPlanner.WPF.Services;

namespace KaoyanPlanner.WPF.Views.Settings;

/// <summary>
/// 语音播报设置：tts 的服务启动命令 + 参考音频（音色来源）等（从 TtsSettingsWindow 迁入）。
/// 写回 data.json 的 tts 配置（不碰其它键）；文本框回车/失焦提交，立即生效。
/// 注：正在运行的语音服务不会因这里改动而重启（对齐原对话框行为）。
/// </summary>
public partial class SettingsTtsTab : UserControl
{
    private readonly DataStore _store;
    private bool _loading = true;

    public SettingsTtsTab(DataStore store)
    {
        InitializeComponent();
        _store = store;

        var tts = DataStore.GetObj(_store.Data, "tts");
        cmdBox.Text = DataStore.GetString(tts?["server_cmd"]);
        refBox.Text = DataStore.GetString(tts?["ref_audio_path"]);
        promptBox.Text = DataStore.GetString(tts?["prompt_text"]);
        langBox.Text = DataStore.GetString(tts?["prompt_lang"]) is { Length: > 0 } l ? l : "zh";

        cmdBox.LostKeyboardFocus += (_, _) => Commit();
        refBox.LostKeyboardFocus += (_, _) => Commit();
        promptBox.LostKeyboardFocus += (_, _) => Commit();
        langBox.LostKeyboardFocus += (_, _) => Commit();
        cmdBox.KeyDown += CommitOnEnter;
        refBox.KeyDown += CommitOnEnter;
        promptBox.KeyDown += CommitOnEnter;
        langBox.KeyDown += CommitOnEnter;
        _loading = false;
    }

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
        tts["prompt_lang"] = langBox.Text.Trim() is { Length: > 0 } l ? l : "zh";
        _store.SaveQuiet();
    }
}
