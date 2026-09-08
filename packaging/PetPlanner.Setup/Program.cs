using System;
using System.Windows.Forms;

namespace PetPlanner.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 静默模式：自动化/分发脚本用（GUI 向导照常双击运行）。
        //  --silent-install  <dir>     静默安装（不建快捷方式、不启动）
        //  --silent-uninstall <dir>    静默卸载（杀进程→删快捷方式→删目录）
        //  --shortcut        <lnk> <目标exe>   仅测 .lnk 创建（返回 0=成功）
        if (args.Length > 0) return RunCli(args);

        Application.Run(new MainForm());
        return 0;
    }

    private static int RunCli(string[] args)
    {
        var noneInt = new Progress<int>(_ => { });
        var noneStr = new Progress<string>(_ => { });
        try
        {
            switch (args[0])
            {
                case "--silent-install":
                    if (args.Length < 2) return 2;
                    string? err = InstallerEngine.ValidateDir(args[1]);
                    if (err is not null) return 2;
                    InstallerEngine.Install(args[1], desktopShortcut: false, startMenuShortcut: false, noneInt, noneStr);
                    return 0;

                case "--silent-uninstall":
                    if (args.Length < 2) return 2;
                    return InstallerEngine.Uninstall(args[1]).Count == 0 ? 0 : 2;

                case "--shortcut":
                    if (args.Length < 3) return 2;
                    return InstallerEngine.CreateShortcut(args[1], args[2]) ? 0 : 3;

                default:
                    return 2;
            }
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine("PetPlanner.Setup: " + ex.Message); } catch { /* WinExe 无控制台 */ }
            return 1;
        }
    }
}
