using System.Windows.Controls;
using KaoyanPlanner.WPF.Services;

namespace KaoyanPlanner.WPF.Views.Settings;

/// <summary>关于页：版本（含版本名：个人版/测试版）+ 数据/桌宠目录说明 + 添加形象教程（纯展示）。</summary>
public partial class SettingsAboutTab : UserControl
{
    public SettingsAboutTab()
    {
        InitializeComponent();
        versionText.Text = $"版本 {AppInfo.Version} · {AppInfo.Edition}" +
                           $"　·　数据目录：{AppPaths.BaseDir}";
    }
}
