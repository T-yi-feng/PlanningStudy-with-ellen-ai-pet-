using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KaoyanPlanner.WPF.Services;
using ColorConverter = System.Windows.Media.ColorConverter;
using Forms = System.Windows.Forms;

namespace KaoyanPlanner.WPF.Views.Settings;

/// <summary>
/// 实时字幕设置（从 CaptionSettingsWindow 迁入）：ASR Key（写 secret.json）+ 识别模型 + 语言 + 字号 + 颜色。
/// 每次改动立即写回 data.json 的 caption 配置并让黑板实时刷新（host.PetWindow.ApplyCaptionSettings）。
/// </summary>
public partial class SettingsCaptionTab : UserControl
{
    private static readonly (string Name, string Hex)[] ColorPresets =
    {
        ("粉笔白", "#F5F0E6"),
        ("亮黄", "#FFE873"),
        ("粉红", "#FFB6C1"),
        ("青绿", "#A8E6CF"),
        ("橙黄", "#FFB26B"),
    };

    private readonly DataStore _store;
    private readonly MainWindow _host;
    private readonly List<Button> _swatches = new();
    private string _color;
    private bool _loading = true;

    public SettingsCaptionTab(DataStore store, MainWindow host)
    {
        InitializeComponent();
        _store = store;
        _host = host;

        var cfg = DataStore.GetObj(store.Data, "caption");
        _color = DataStore.GetString(cfg?["color"]);
        if (string.IsNullOrEmpty(_color)) _color = "#F5F0E6";

        keyBox.Password = SecretService.Get("asr_api_key");
        SelectByTag(modelBox, DataStore.GetString(cfg?["model"]), "FunAudioLLM/SenseVoiceSmall");
        SelectByTag(langBox, DataStore.GetString(cfg?["language"]), "auto");
        sizeBox.Text = DataStore.GetInt(cfg?["font_size"], 18).ToString();

        BuildSwatches();
        UpdateSwatchSelection();

        keyBox.LostKeyboardFocus += (_, _) => Commit();
        modelBox.SelectionChanged += (_, _) => Commit();
        langBox.SelectionChanged += (_, _) => Commit();
        sizeBox.LostKeyboardFocus += (_, _) => Commit();
        sizeBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) Commit(); };
        _loading = false;
    }

    private static void SelectByTag(ComboBox box, string? value, string def)
    {
        string target = string.IsNullOrEmpty(value) ? def : value;
        foreach (var item in box.Items)
            if (item is ComboBoxItem cbi && (string)cbi.Tag == target)
            {
                box.SelectedItem = cbi;
                return;
            }
        box.SelectedIndex = 0;
    }

    private void BuildSwatches()
    {
        var surface = (Brush)FindResource("SurfaceBrush");
        var border = (Brush)FindResource("BorderBrush");
        foreach (var (name, hex) in ColorPresets)
        {
            var btn = new Button
            {
                Content = name,
                Tag = hex,
                Width = 62,
                Height = 28,
                Margin = new Thickness(0, 0, 6, 0),
                Background = surface,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                BorderBrush = border,
                BorderThickness = new Thickness(1),
            };
            btn.Click += (_, _) => { _color = hex; UpdateSwatchSelection(); Commit(); };
            _swatches.Add(btn);
            colorPanel.Children.Add(btn);
        }
    }

    private void UpdateSwatchSelection()
    {
        var accent = (Brush)FindResource("AccentBrush");
        var border = (Brush)FindResource("BorderBrush");
        foreach (var btn in _swatches)
        {
            bool sel = (string)btn.Tag == _color;
            btn.BorderThickness = sel ? new Thickness(2) : new Thickness(1);
            btn.BorderBrush = sel ? accent : border;
        }
    }

    private void Custom_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new Forms.ColorDialog { FullOpen = true };
        try { dlg.Color = System.Drawing.ColorTranslator.FromHtml(_color); } catch { /* 非法颜色则用默认 */ }
        if (dlg.ShowDialog() == Forms.DialogResult.OK)   // 无 owner（对齐 QColorDialog.getColor）
        {
            _color = "#" + dlg.Color.R.ToString("X2") + dlg.Color.G.ToString("X2") + dlg.Color.B.ToString("X2");
            UpdateSwatchSelection();
            Commit();
        }
    }

    private void Commit()
    {
        if (_loading) return;
        var cfg = DataStore.GetOrCreateObj(_store.Data, "caption");
        if (modelBox.SelectedItem is ComboBoxItem m) cfg["model"] = (string)m.Tag;
        if (langBox.SelectedItem is ComboBoxItem l) cfg["language"] = (string)l.Tag;
        int sz = int.TryParse(sizeBox.Text, out int n) ? Math.Clamp(n, 12, 40) : 18;
        sizeBox.Text = sz.ToString();
        cfg["font_size"] = sz;
        cfg["color"] = _color;
        SecretService.Set("asr_api_key", keyBox.Password.Trim());
        _store.SaveQuiet();
        _host.PetWindow?.ApplyCaptionSettings();   // 黑板实时刷新外观/语言
    }
}
