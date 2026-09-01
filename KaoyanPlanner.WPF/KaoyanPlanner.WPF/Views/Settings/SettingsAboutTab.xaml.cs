using System.Reflection;
using System.Windows.Controls;
using KaoyanPlanner.WPF.Services;

namespace KaoyanPlanner.WPF.Views.Settings;

/// <summary>关于页：版本 + 数据/桌宠目录说明 + 添加形象教程（纯展示）。</summary>
public partial class SettingsAboutTab : UserControl
{
    public SettingsAboutTab()
    {
        InitializeComponent();
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        versionText.Text = $"版本 {v?.Major}.{v?.Minor}.{v?.Build}" +
                           $"　·　数据目录：{AppPaths.BaseDir}";
    }
}
