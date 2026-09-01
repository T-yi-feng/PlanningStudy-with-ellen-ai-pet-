// UseWindowsForms=true 会让 SDK 全局引入 System.Windows.Forms / System.Drawing，
// 与 WPF 同名类型（UserControl/TextBox/Application…）冲突。
// 这里用全局别名把简单名统一指向 WPF 版本；需要 WinForms 类型时用 Forms. 前缀。
global using UserControl = System.Windows.Controls.UserControl;
global using TextBox = System.Windows.Controls.TextBox;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
global using Brush = System.Windows.Media.Brush;
global using Color = System.Windows.Media.Color;
global using FontFamily = System.Windows.Media.FontFamily;
global using Button = System.Windows.Controls.Button;
global using CheckBox = System.Windows.Controls.CheckBox;
global using ComboBox = System.Windows.Controls.ComboBox;
global using Label = System.Windows.Controls.Label;
global using Image = System.Windows.Controls.Image;
// 桌宠/气泡/剪辑解码也用到的冲突名 → 全局别名统一指向 WPF
global using Point = System.Windows.Point;
global using Vector = System.Windows.Vector;
global using Brushes = System.Windows.Media.Brushes;
global using Orientation = System.Windows.Controls.Orientation;
global using HorizontalAlignment = System.Windows.HorizontalAlignment;
global using FlowDirection = System.Windows.FlowDirection;
global using DataObject = System.Windows.DataObject;
global using DataFormats = System.Windows.DataFormats;
