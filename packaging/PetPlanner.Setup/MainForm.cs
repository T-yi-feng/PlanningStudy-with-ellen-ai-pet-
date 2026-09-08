using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PetPlanner.Setup;

/// <summary>安装向导单窗口：选安装目录 → 快捷方式选项 → 安装/重新安装/卸载。</summary>
internal sealed class MainForm : Form
{
    private readonly TextBox _dirBox = new();
    private readonly Label _stateLabel = new();
    private readonly Label _warnLabel = new();
    private readonly CheckBox _cbDesktop = new() { Text = "创建桌面快捷方式", Checked = true };
    private readonly CheckBox _cbMenu = new() { Text = "创建开始菜单快捷方式", Checked = true };
    private readonly CheckBox _cbLaunch = new() { Text = "安装后立即运行", Checked = true };
    private readonly Button _btnBrowse = new() { Text = "浏览…", AutoSize = true };
    private readonly Button _btnUninstall = new() { Text = "卸载" };
    private readonly Button _btnInstall = new() { Text = "安装" };
    private readonly Button _btnCancel = new() { Text = "取消" };
    private readonly ProgressBar _progress = new() { Maximum = 100, Style = ProgressBarStyle.Continuous };
    private readonly Label _status = new() { AutoEllipsis = true };

    private bool _busy;

    public MainForm()
    {
        Text = "PetPlanner 安装向导";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(560, 380);

        int x = 28, w = ClientSize.Width - 2 * x;

        var title = NewLabel("PetPlanner 安装向导", new Font(Font, FontStyle.Bold), 15F);
        title.Location = new Point(x, 18);
        title.Size = new Size(w, 30);
        Controls.Add(title);

        var desc = NewLabel("考研计划 · 专注计时 · 提醒 · 统计 的桌面应用。", null, 9F);
        desc.Location = new Point(x, 50);
        desc.Size = new Size(w, 20);
        Controls.Add(desc);

        var dirLabel = NewLabel("安装位置", null, 9F);
        dirLabel.Location = new Point(x, 86);
        dirLabel.Size = new Size(w, 20);
        Controls.Add(dirLabel);

        _dirBox.Location = new Point(x, 108);
        _dirBox.Size = new Size(w - 84, 26);
        _dirBox.TextChanged += (_, _) => RefreshState();
        Controls.Add(_dirBox);

        _btnBrowse.Location = new Point(x + _dirBox.Width + 6, 107);
        _btnBrowse.Click += (_, _) => BrowseDir();
        Controls.Add(_btnBrowse);

        _warnLabel.ForeColor = Color.Crimson;
        _warnLabel.Location = new Point(x, 140);
        _warnLabel.Size = new Size(w, 20);
        Controls.Add(_warnLabel);

        _stateLabel.ForeColor = Color.DimGray;
        _stateLabel.Location = new Point(x, 168);
        _stateLabel.Size = new Size(w, 22);
        Controls.Add(_stateLabel);

        int cy = 200;
        _cbDesktop.Location = new Point(x, cy);
        _cbDesktop.AutoSize = true;
        Controls.Add(_cbDesktop);
        cy += 28;
        _cbMenu.Location = new Point(x, cy);
        _cbMenu.AutoSize = true;
        Controls.Add(_cbMenu);
        cy += 28;
        _cbLaunch.Location = new Point(x, cy);
        _cbLaunch.AutoSize = true;
        Controls.Add(_cbLaunch);
        cy += 34;

        _progress.Location = new Point(x, cy);
        _progress.Size = new Size(w, 18);
        _progress.Visible = false;
        Controls.Add(_progress);
        cy += 24;
        _status.Location = new Point(x, cy);
        _status.Size = new Size(w, 18);
        _status.Visible = false;
        Controls.Add(_status);
        cy += 30;

        int btnY = ClientSize.Height - 40;
        _btnCancel.Location = new Point(ClientSize.Width - x - 92, btnY);
        _btnCancel.Size = new Size(92, 30);
        _btnCancel.Click += (_, _) => Close();
        Controls.Add(_btnCancel);

        _btnUninstall.Location = new Point(_btnCancel.Left - 98, btnY);
        _btnUninstall.Size = new Size(92, 30);
        _btnUninstall.Click += async (_, _) => await UninstallAsync();
        Controls.Add(_btnUninstall);

        _btnInstall.Location = new Point(_btnUninstall.Left - 98, btnY);
        _btnInstall.Size = new Size(92, 30);
        _btnInstall.Click += async (_, _) => await InstallAsync();
        Controls.Add(_btnInstall);

        _dirBox.Text = InstallerEngine.DefaultInstallDir();
        RefreshState();
    }

    private static Label NewLabel(string text, Font? font, float em) => new()
    {
        Text = text,
        Font = font ?? new Font("Microsoft YaHei UI", em),
    };

    private string TargetDir => Path.GetFullPath(_dirBox.Text.Trim());

    private void RefreshState()
    {
        if (_busy) return;
        string dir = TargetDir;
        bool installed = InstallerEngine.IsInstalled(dir);
        string? err = InstallerEngine.ValidateDir(dir);

        if (err is not null)
        {
            _warnLabel.Text = err;
            _btnInstall.Enabled = false;
        }
        else
        {
            _warnLabel.Text = "";
            _btnInstall.Enabled = true;
        }

        _btnInstall.Text = installed ? "重新安装" : "安装";
        _btnUninstall.Visible = installed;
        _stateLabel.Text = installed
            ? "已在此目录检测到 PetPlanner。点「重新安装」升级（将保留你的 data.json），或点「卸载」移除。"
            : "将安装到以上目录（便携式：你的数据会保存在这个目录里）。";
    }

    private void BrowseDir()
    {
        using var fbd = new FolderBrowserDialog();
        fbd.Description = "选择安装位置";
        try { fbd.SelectedPath = TargetDir; } catch { /* 忽略非法路径 */ }
        if (fbd.ShowDialog(this) == DialogResult.OK)
            _dirBox.Text = fbd.SelectedPath;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _btnInstall.Enabled = !busy && _warnLabel.Text.Length == 0;
        _btnBrowse.Enabled = !busy;
        _btnCancel.Enabled = !busy;
        _btnUninstall.Enabled = !busy;
        _dirBox.Enabled = !busy;
        _cbDesktop.Enabled = !busy;
        _cbMenu.Enabled = !busy;
        _cbLaunch.Enabled = !busy;
        _progress.Visible = busy;
        _status.Visible = busy;
    }

    private async Task InstallAsync()
    {
        if (_busy) return;
        string? err = InstallerEngine.ValidateDir(TargetDir);
        if (err is not null) { MessageBox.Show(this, err, "无法安装", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        bool isReinstall = InstallerEngine.IsInstalled(TargetDir);
        if (isReinstall && MessageBox.Show(this, "该目录已安装 PetPlanner。重新安装会覆盖程序文件（你的 data.json 会被保留）。\n继续？",
                "重新安装", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        SetBusy(true);
        _progress.Value = 0;
        var progress = new Progress<int>(v => _progress.Value = Math.Clamp(v, 0, 100));
        var statusText = new Progress<string>(s => _status.Text = s);
        try
        {
            await Task.Run(() => InstallerEngine.Install(TargetDir,
                _cbDesktop.Checked, _cbMenu.Checked, progress, statusText));
            _progress.Value = 100;
            _status.Text = "安装完成。";
            if (_cbLaunch.Checked)
            {
                try { InstallerEngine.Launch(TargetDir); } catch { /* 启动失败可忽略 */ }
            }
            MessageBox.Show(this, "PetPlanner 已安装完成。", "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "安装失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
            RefreshState();
        }
    }

    private async Task UninstallAsync()
    {
        if (_busy) return;
        string dir = TargetDir;
        if (!InstallerEngine.IsInstalled(dir)) return;
        if (MessageBox.Show(this, $"确定要卸载 PetPlanner 吗？\n将删除目录：\n{dir}\n及其中的学习数据。",
                "卸载确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        SetBusy(true);
        _progress.Value = 30;
        _status.Text = "正在卸载…";
        try
        {
            var leftover = await Task.Run(() => InstallerEngine.Uninstall(dir));
            _progress.Value = 100;
            _status.Text = leftover.Count == 0 ? "已卸载。" : $"部分文件未能删除：{string.Join("；", leftover)}";
            MessageBox.Show(this, leftover.Count == 0 ? "PetPlanner 已卸载。" : "卸载完成，但以下文件仍被占用未能删除：\n" + string.Join("\n", leftover),
                "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "卸载失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
            RefreshState();
        }
    }
}
