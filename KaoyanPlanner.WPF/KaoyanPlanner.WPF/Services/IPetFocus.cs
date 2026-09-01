namespace KaoyanPlanner.WPF.Services;

/// <summary>
/// 桌宠聊天指令可操作的专注计时子集（镜像 widgets.py pet_start_focus 等用到的 TimerTab 状态）。
/// FocusTimerService 实现之；测试注入假实现验证指令逻辑。
/// </summary>
public interface IPetFocus
{
    bool Running { get; }
    double ElapsedSeconds { get; }
    void Start();
    void Pause();
    void Reset();
}
