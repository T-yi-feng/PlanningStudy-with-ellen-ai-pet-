using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace KaoyanPlanner.WPF.Services;

/// <summary>
/// 开机自启：HKCU\Software\Microsoft\Windows\CurrentVersion\Run 值（按版本区分）→ exe 路径
/// （autostart.py 移植，无需管理员权限）。值名按版本隔离（AppInfo）——个人版与测试版各占一项，
/// 勾选互不覆盖。IsEnabled 只在条目确实指向当前 exe 时算开启
/// （项目移动/exe 挪位后视为未开启，勾上会重写正确路径）；SetEnabled 开=写入、关=删除。
/// </summary>
public static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string ValueName => AppInfo.AutostartValueName;

    private static string? CurrentExe
    {
        get
        {
            // 单文件发布下 Assembly.Location 为空串 → 用进程路径（= 单文件 exe 自身）
            string? path = Environment.ProcessPath;
            return string.IsNullOrEmpty(path) ? null : path;
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            string? value = key?.GetValue(ValueName) as string;
            if (string.IsNullOrEmpty(value)) return false;
            string? exe = CurrentExe;
            if (exe is null) return false;
            foreach (string token in ExtractQuoted(value))
                if (string.Equals(token, exe, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>开→写入自启命令；关→删除自启项。返回是否成功（失败=注册表不可写等）。</summary>
    public static bool SetEnabled(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return false;
            if (on)
            {
                string? exe = CurrentExe;
                if (exe is null) return false;
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>取出带引号路径串（兼容 "pythonw" "main.py" 与 "exe" 两种旧写法）。</summary>
    private static IEnumerable<string> ExtractQuoted(string s)
    {
        foreach (Match m in Regex.Matches(s, "\"([^\"]*)\""))
            yield return m.Groups[1].Value;
    }
}
