using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaoyanPlanner.WPF.Services;
using Cursors = System.Windows.Input.Cursors;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using Forms = System.Windows.Forms;

namespace KaoyanPlanner.WPF.Views.Settings;

/// <summary>
/// 桌宠设置：形象选择器（每个形象 = desk_pet/<名称>/ 子文件夹）+ 导入（浏览/拖拽）+ 闲话开关与间隔。
/// 选择/导入立即生效：写 data.json 的 pet_skin（惰性键），并让活体桌宠 ReloadSkin 换装。
/// </summary>
public partial class SettingsPetTab : UserControl, ISettingsSection
{
    private readonly DataStore _store;
    private readonly MainWindow _host;
    private bool _loading = true;
    private string _current = "";   // 当前选中形象名（"" = 默认顶层艾莲）

    public SettingsPetTab(DataStore store, MainWindow host)
    {
        InitializeComponent();
        _store = store;
        _host = host;

        _current = DataStore.GetString(_store.Data["pet_skin"]);

        var petIdle = DataStore.GetObj(_store.Data, "pet_idle");
        idleCheck.IsChecked = DataStore.GetBool(petIdle?["enabled"], true);
        idleInterval.Text = DataStore.GetInt(petIdle?["interval_min"], 8).ToString();

        idleCheck.Click += IdleCheck_Click;
        idleInterval.KeyDown += IdleInterval_KeyDown;
        idleInterval.LostKeyboardFocus += IdleInterval_LostFocus;

        RefreshSkins();
        _loading = false;
    }

    /// <summary>重新枚举皮肤（外部在磁盘改了形象文件夹后，回到本分栏时刷新）。</summary>
    public void Refresh() => RefreshSkins();

    // ------------------------------------------------------------ 皮肤卡片

    private void RefreshSkins()
    {
        skinList.Children.Clear();
        string root = PetSkinService.SkinRootDir;
        foreach (var skin in PetSkinService.ListSkins(root))
            skinList.Children.Add(BuildSkinCard(skin));
    }

    private Border BuildSkinCard(SkinInfo skin)
    {
        string selName = skin.IsDefault ? "" : skin.Name;
        bool selected = _current == selName;

        var card = new Border
        {
            Width = 150,
            Margin = new Thickness(0, 0, 12, 12),
            CornerRadius = new CornerRadius(10),
            Background = (Brush)FindResource("SurfaceBrush"),
            BorderBrush = selected ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(selected ? 2 : 1),
            Padding = new Thickness(10, 12, 10, 10),
            Cursor = skin.Valid ? Cursors.Hand : Cursors.Arrow,
            IsEnabled = skin.Valid,
            Opacity = skin.Valid ? 1.0 : 0.55,
            Tag = selName,
        };
        card.MouseLeftButtonDown += (_, _) => { if (skin.Valid) SelectSkin(selName); };

        // 预览图带底色的取景框（Image 无 Background，外包一层 Border）
        var imgBox = new Border
        {
            Width = 120,
            Height = 120,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("InputBrush"),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var img = new Image { Stretch = Stretch.Uniform };
        img.Source = LoadPreview(skin);
        imgBox.Child = img;

        var nameText = new TextBlock
        {
            Text = skin.Name + (skin.IsDefault ? "（默认）" : ""),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 130,
        };
        var note = new TextBlock
        {
            Text = skin.Valid ? (selected ? "✓ 当前形象" : "点击切换") : "缺核心动画",
            FontSize = 11,
            Foreground = skin.Valid ? (Brush)FindResource("AccentStrongBrush") : (Brush)FindResource("DangerBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var panel = new StackPanel();
        panel.Children.Add(imgBox);
        panel.Children.Add(nameText);
        panel.Children.Add(note);
        card.Child = panel;
        return card;
    }

    /// <summary>卡片预览图：preview.png 优先，缺省用常态第一帧。</summary>
    private ImageSource? LoadPreview(SkinInfo skin)
    {
        string? preview = PetSkinService.PreviewFile(skin.Directory);
        if (preview is not null && File.Exists(preview))
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(preview);
                bi.DecodePixelWidth = 120;
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch
            {
                // 预览图损坏 → 退回常态第一帧
            }
        }
        var files = PetSkinService.ResolveClipFiles(skin.Directory);
        if (files.TryGetValue("normal", out string? normal))
        {
            var clip = AniLoader.LoadAni(normal);
            if (clip is not null && clip.Frames.Count > 0) return clip.Frames[0];
        }
        return null;
    }

    /// <summary>选择形象：与当前一致则跳过；空名=切回默认（移除键恢复字节形状）；否则写 pet_skin。</summary>
    private void SelectSkin(string name)
    {
        string cur = DataStore.GetString(_store.Data["pet_skin"]);
        if (cur == name) return;                       // 无变化不落盘
        if (name.Length == 0)
        {
            if (_store.Data.ContainsKey("pet_skin"))
                _store.Data.Remove("pet_skin");
        }
        else
        {
            _store.Data["pet_skin"] = name;
        }
        _store.SaveQuiet();
        _current = name;
        RefreshSkins();                                 // 更新选中高亮
        _host.PetWindow?.ReloadSkin();                  // 立即换装
    }

    // ------------------------------------------------------------ 导入 / 打开

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new Forms.FolderBrowserDialog
        {
            Description = "选择桌宠形象文件夹（内含 normal/talking/happy/present 四个 .ani）",
            ShowNewFolderButton = true,
        };
        if (dlg.ShowDialog() == Forms.DialogResult.OK)
            TryImport(dlg.SelectedPath);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(PetSkinService.SkinRootDir) { UseShellExecute = true }); }
        catch { /* 目录不存在时静默 */ }
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        // 目录拖放：必须先设 Effects=Copy + Handled，否则光标是禁止符
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            e.Effects = paths is not null && paths.Length > 0 && Directory.Exists(paths[0])
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (paths is not null && paths.Length > 0 && Directory.Exists(paths[0]))
            TryImport(paths[0]);
    }

    private void TryImport(string src)
    {
        string? name = PetSkinService.ImportSkin(src, PetSkinService.SkinRootDir, out string? error);
        if (name is null)
        {
            importMsg.Text = "⚠ " + error;
            importMsg.Visibility = Visibility.Visible;
            return;
        }
        importMsg.Visibility = Visibility.Collapsed;
        SelectSkin(name);
    }

    // ------------------------------------------------------------ 闲话

    private void IdleCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var petIdle = DataStore.GetOrCreateObj(_store.Data, "pet_idle");
        petIdle["enabled"] = idleCheck.IsChecked == true;
        _store.SaveQuiet();
    }

    private void IdleInterval_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitIdleInterval();
    }

    private void IdleInterval_LostFocus(object sender, RoutedEventArgs e) => CommitIdleInterval();

    private void CommitIdleInterval()
    {
        if (_loading) return;
        if (int.TryParse(idleInterval.Text, out int n))
        {
            n = Math.Max(2, n);
            idleInterval.Text = n.ToString();
            var petIdle = DataStore.GetOrCreateObj(_store.Data, "pet_idle");
            petIdle["interval_min"] = n;
            _store.SaveQuiet();
        }
    }
}
