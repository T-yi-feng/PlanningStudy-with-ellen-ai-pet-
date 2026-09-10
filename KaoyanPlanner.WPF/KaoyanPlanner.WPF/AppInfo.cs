namespace KaoyanPlanner.WPF;

/// <summary>
/// 版本身份（版本隔离的唯一来源）：同一份源码按编译开关产出两个版本——
///   · 个人版（默认，不带参数）：桌宠艾莲，自用。
///   · 安装包测试版（-p:TestBuild=true → TEST_BUILD）：随 PetPlanner-Setup.exe 分发的公共中性版（小蓝）。
/// 单实例管道、开机自启注册表值、关于页版本、托盘提示全部据此区分：
/// 两版进程名都叫 PetPlanner.exe 但身份互不相同 → 可同时运行、数据各自独立（数据目录 = exe 所在目录）、
/// 自启项互不覆盖、卸载测试版不会误杀个人版进程。
/// </summary>
internal static class AppInfo
{
    /// <summary>应用版本号（关于页 / 文件属性展示；改动版本时改这里）。</summary>
    public const string Version = "2.2.7";

#if TEST_BUILD
    /// <summary>版本名：随安装包分发的测试版（公共中性版）。</summary>
    public const string Edition = "测试版";
    public const string EditionTag = "Test";
#else
    /// <summary>版本名：个人专用自用版。</summary>
    public const string Edition = "个人版";
    public const string EditionTag = "Personal";
#endif

    /// <summary>单实例命名管道名：不同版本用不同管道 → 两版可同时运行、各自唤醒自己。</summary>
    public static string SingleInstancePipeName => "PetPlanner_" + EditionTag + "_SingleInstance";

    /// <summary>开机自启注册表值名（HKCU\...\Run）：两版各占一项，勾选互不覆盖。</summary>
    public static string AutostartValueName => "PetPlanner" + EditionTag;

    /// <summary>托盘悬浮提示（63 字符上限内带出版本，便于区分正在跑哪个）。</summary>
    public static string TrayText => "考研复习计划 · " + Edition;
}
