using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;

namespace KaoyanPlanner.WPF.Controls;

/// <summary>
/// HH:mm 掩码输入：只收数字、自动补冒号、退格吃冒号、Enter 提交。
/// 镜像 widgets.py 的 QTimeEdit(HH:mm)；默认 = 当前时间 +1 分钟。
/// </summary>
public partial class TimeBox : UserControl
{
    /// <summary>按 Enter 时触发（是否提交由调用方决定）。</summary>
    public event Action? EnterPressed;

    public TimeBox()
    {
        InitializeComponent();
        Reset();
    }

    /// <summary>重置为当前时间 +1 分钟（用于添加完清空 / 非法输入回退）。</summary>
    public void Reset()
        => tb.Text = DateTime.Now.AddMinutes(1).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>解析当前输入为合法 HH:mm；非法返回 null。</summary>
    public string? ValidTime()
    {
        var parts = tb.Text.Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out int h) || !int.TryParse(parts[1], out int m)) return null;
        if (h < 0 || h > 23 || m < 0 || m > 59) return null;
        return $"{h:00}:{m:00}";
    }

    private void Tb_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        foreach (char c in e.Text)
            if (!char.IsDigit(c)) { e.Handled = true; return; }

        // 用键入数字替换选中区间，再把数字流重排成 HH:mm（光标保持跟在刚键入的数字后）
        string cur = tb.Text;
        if (tb.SelectionLength > 0)
            cur = cur.Remove(tb.SelectionStart, tb.SelectionLength);
        int selStart = Math.Min(tb.SelectionStart, cur.Length);

        string before = string.Concat(cur[..selStart].Where(char.IsDigit));
        string after = string.Concat(cur[selStart..].Where(char.IsDigit));
        string digits = before + e.Text + after;
        if (digits.Length > 4) digits = digits[..4];

        e.Handled = true;
        tb.Text = FormatHm(digits);
        int di = before.Length + e.Text.Length;   // 光标前的数字个数
        tb.CaretIndex = Math.Min(tb.Text.Length, di + (di >= 2 ? 1 : 0));
    }

    private void Tb_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back)
        {
            // 退格时连冒号一起吃，避免光标卡在冒号后面按两次
            int caret = tb.CaretIndex;
            if (caret > 0 && tb.Text[caret - 1] == ':')
            {
                tb.Text = tb.Text.Remove(caret - 1, 1);
                tb.CaretIndex = caret - 1;
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Enter)
        {
            EnterPressed?.Invoke();
            e.Handled = true;
        }
    }

    private static string FormatHm(string digits) => digits.Length switch
    {
        0 => "",
        1 => digits,
        2 => digits + ":",
        3 => digits[..2] + ":" + digits[2],
        _ => digits[..2] + ":" + digits[2..],
    };
}
