using System;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KaoyanPlanner.WPF.Controls;
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
        keyBox.Password = DataStore.GetString(chat?["api_key"]);
        keyBoxPlain.Text = keyBox.Password;

        enabledCheck.Click += (_, _) => Commit();
        urlBox.LostKeyboardFocus += (_, _) => Commit();
        modelBox.LostKeyboardFocus += (_, _) => Commit();
        keyBox.LostKeyboardFocus += (_, _) => Commit();
        keyBoxPlain.LostKeyboardFocus += (_, _) => Commit();
        urlBox.KeyDown += CommitOnEnter;
        modelBox.KeyDown += CommitOnEnter;
        keyBox.KeyDown += CommitOnEnter;
        keyBoxPlain.KeyDown += CommitOnEnter;
        showKeyCheck.Click += (_, _) =>
        {
            bool show = showKeyCheck.IsChecked == true;
            keyBoxPlain.Text = keyBox.Password;   // 打开明文时同步当前密码值
            keyBoxPlain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            keyBox.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        };
        _loading = false;
    }

    private async void TestConn_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        string url = urlBox.Text.Trim().TrimEnd('/');
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            ToastHost.Show(Window.GetWindow(this), "接口地址需以 http:// 或 https:// 开头");
            return;
        }
        string apiKey = (keyBox.Visibility == Visibility.Visible ? keyBox.Password : keyBoxPlain.Text).Trim();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            if (apiKey.Length > 0)
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            var payload = new StringContent("{\"model\":\"test\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"max_tokens\":1}",
                System.Text.Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? url : url + "/chat/completions", payload);
            string body = await resp.Content.ReadAsStringAsync();
            ToastHost.Show(Window.GetWindow(this), resp.IsSuccessStatusCode
                ? "连接成功，接口与密钥可用"
                : $"接口已连通但返回 {((int)resp.StatusCode)}：{Truncate(body)}");
        }
        catch (Exception ex)
        {
            ToastHost.Show(Window.GetWindow(this), "连接失败：" + Truncate(ex.Message));
        }
    }

    private static string Truncate(string s)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length > 80 ? s[..80] + "…" : s;
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
        chat["api_key"] = (keyBox.Visibility == Visibility.Visible ? keyBox.Password : keyBoxPlain.Text).Trim();
        _store.SaveQuiet();
    }
}
