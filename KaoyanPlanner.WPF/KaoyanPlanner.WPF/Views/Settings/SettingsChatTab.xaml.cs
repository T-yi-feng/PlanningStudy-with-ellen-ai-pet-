using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KaoyanPlanner.WPF.Services;

namespace KaoyanPlanner.WPF.Views.Settings;

/// <summary>
/// AI 聊天设置：pet_chat 的启用开关 + base_url / model / api_key（文本框回车/失焦提交，立即生效）。
/// 写回 data.json 的 pet_chat 配置，不碰其它键。
/// </summary>
public partial class SettingsChatTab : UserControl
{
    private readonly DataStore _store;
    private bool _loading = true;

    public SettingsChatTab(DataStore store)
    {
        InitializeComponent();
        _store = store;

        var chat = DataStore.GetObj(_store.Data, "pet_chat");
        enabledCheck.IsChecked = DataStore.GetBool(chat?["enabled"]);
        urlBox.Text = DataStore.GetString(chat?["base_url"]);
        modelBox.Text = DataStore.GetString(chat?["model"]);
        keyBox.Text = DataStore.GetString(chat?["api_key"]);

        enabledCheck.Click += (_, _) => Commit();
        urlBox.LostKeyboardFocus += (_, _) => Commit();
        modelBox.LostKeyboardFocus += (_, _) => Commit();
        keyBox.LostKeyboardFocus += (_, _) => Commit();
        urlBox.KeyDown += CommitOnEnter;
        modelBox.KeyDown += CommitOnEnter;
        keyBox.KeyDown += CommitOnEnter;
        _loading = false;
    }

    private void CommitOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Commit();
    }

    private void Commit()
    {
        if (_loading) return;
        var chat = DataStore.GetOrCreateObj(_store.Data, "pet_chat");
        chat["enabled"] = enabledCheck.IsChecked == true;
        chat["base_url"] = urlBox.Text.Trim();
        chat["model"] = modelBox.Text.Trim();
        chat["api_key"] = keyBox.Text.Trim();
        _store.SaveQuiet();
    }
}
