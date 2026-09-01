using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace KaoyanPlanner.WPF.Views.Dialogs;

/// <summary>
/// 添加/编辑固定任务：内容 + 可选简介 + 需几天完成（1–9999），镜像 widgets.py 的 FixedTaskDialog。
/// </summary>
public partial class FixedTaskDialog : Window
{
    public string ResultText => textBox.Text.Trim();
    public string ResultDesc => descBox.Text.Trim();

    public int ResultDays
    {
        get
        {
            int v = int.TryParse(daysBox.Text, out var n) ? n : 1;
            return Math.Clamp(v, 1, 9999);
        }
    }

    public FixedTaskDialog(string text = "", string desc = "", int days = 1)
    {
        InitializeComponent();
        textBox.Text = text;
        descBox.Text = desc;
        daysBox.Text = days.ToString();
        if (text.Length > 0) dlgTitle.Text = "编辑固定任务";
        textBox.Focus();
        textBox.SelectAll();
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e)
    {
        if (textBox.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, "任务内容不能为空。", "固定任务", MessageBoxButton.OK, MessageBoxImage.Information);
            textBox.Focus();
            return;
        }
        DialogResult = true;
    }

    private void DaysBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !Regex.IsMatch(e.Text, @"^\d$");

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }
}
