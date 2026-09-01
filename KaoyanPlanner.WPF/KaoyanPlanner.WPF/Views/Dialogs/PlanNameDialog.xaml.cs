using System.Windows;
using System.Windows.Input;

namespace KaoyanPlanner.WPF.Views.Dialogs;

/// <summary>
/// 新建 / 重命名计划：单字段名称输入框。
/// </summary>
public partial class PlanNameDialog : Window
{
    public string ResultName => nameBox.Text.Trim();

    public PlanNameDialog(string title, string initial = "")
    {
        InitializeComponent();
        dlgTitle.Text = title;
        nameBox.Text = initial;
        nameBox.Focus();
        nameBox.SelectAll();
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ResultName.Length == 0)
        {
            MessageBox.Show(this, "计划名称不能为空。", "计划", MessageBoxButton.OK, MessageBoxImage.Information);
            nameBox.Focus();
            return;
        }
        DialogResult = true;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }
}
