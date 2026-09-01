namespace KaoyanPlanner.WPF.Views.Settings;

/// <summary>设置分栏页共用的刷新钩子：每次进入该分栏时由 SettingsPage 调用（皮肤列表等需要重读磁盘）。</summary>
public interface ISettingsSection
{
    void Refresh();
}
