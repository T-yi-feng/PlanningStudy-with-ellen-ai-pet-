using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace PetPlanner.Setup;

/// <summary>
/// 安装 / 卸载核心：从嵌入资源（payload/*，见 csproj）写出应用，
/// 建快捷方式（WScript.Shell 晚绑定，零 COM interop 引用，适合单文件），
/// 卸载 = 杀进程 → 删快捷方式 → 删安装目录。
/// 应用是便携式：data.json 就放 exe 旁，故安装目录必须可写（默认 %LOCALAPPDATA%\Programs\PetPlanner）。
/// </summary>
internal static class InstallerEngine
{
    public const string AppExeName = "PetPlanner.exe";

    /// <summary>默认安装目录（用户可写，避开 Program Files）。</summary>
    public static string DefaultInstallDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "PetPlanner");

    /// <summary>目标目录是否已安装（据此切「重新安装 / 卸载」）。</summary>
    public static bool IsInstalled(string dir) => File.Exists(Path.Combine(dir, AppExeName));

    /// <summary>目录可写性试探（建/删临时文件）。</summary>
    public static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".petplanner-setup-write-probe");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>校验安装位置合理性；返回 null=OK，否则为错误文案。</summary>
    public static string? ValidateDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return "请先填写安装位置。";
        string full = Path.GetFullPath(dir);
        string root = Path.GetPathRoot(full) ?? string.Empty;
        if (root.Length > 0 && string.Equals(full.TrimEnd('\\', '/') + "\\",
                Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
            return "安装位置不能是磁盘根目录，请选一个子目录（如 D:\\PetPlanner）。";
        if (!IsWritable(full)) return "该目录不可写。应用便携式、数据写 exe 旁，请选一个普通目录（不要装进 Program Files）。";
        return null;
    }

    /// <summary>执行安装。回调 progress 0..100、status 过程文案；抛异常=失败。</summary>
    public static void Install(string dir, bool desktopShortcut, bool startMenuShortcut,
        IProgress<int> progress, IProgress<string> status)
    {
        dir = Path.GetFullPath(dir);
        Directory.CreateDirectory(dir);
        KillApp(dir);   // 只杀本安装目录内的进程（版本隔离：不动用户个人版）

        var asm = Assembly.GetExecutingAssembly();
        string[] res = asm.GetManifestResourceNames();
        status.Report("正在解包应用文件…");
        // 白名单已在 csproj；这里按 payload/ 前缀逐个写出，未来加旁置资源只需加 csproj 一行。
        int wrote = 0;
        foreach (string name in res)
        {
            const string prefix = "payload/";
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string leaf = name.Substring(prefix.Length);
            if (leaf.Length == 0 || leaf.Contains('\\')) continue; // 只允许扁平文件名
            string dest = Path.Combine(dir, Path.GetFileName(leaf));
            // 覆盖安装 / 升级：若目录里已有 data.json（用户数据），绝不覆盖——只首次安装写干净默认。
            bool isData = string.Equals(Path.GetFileName(leaf), "data.json", StringComparison.OrdinalIgnoreCase);
            if (isData && File.Exists(dest)) { status.Report("发现已有 data.json，保留你的数据。"); continue; }
            WriteResource(asm, name, dest);
            wrote++;
            progress.Report(30 + wrote * 20 / Math.Max(1, res.Length)); // ~50%
        }
        if (wrote == 0 && !File.Exists(Path.Combine(dir, AppExeName)))
            throw new InvalidOperationException("安装包内部缺少应用文件（payload 未嵌入？）。");

        // 空 desk_pet 目录：公共用户「打开皮肤文件夹 / 拖放导入」可用（无素材=中性圆球兜底）。
        Directory.CreateDirectory(Path.Combine(dir, "desk_pet"));
        progress.Report(70);

        string exePath = Path.Combine(dir, AppExeName);
        bool scFail = false;
        if (desktopShortcut)
            scFail |= !CreateShortcut(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "PetPlanner.lnk"),
                exePath);
        if (startMenuShortcut)
        {
            string menuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "PetPlanner");
            Directory.CreateDirectory(menuDir);
            scFail |= !CreateShortcut(Path.Combine(menuDir, "PetPlanner.lnk"), exePath);
        }
        progress.Report(90);
        status.Report(scFail ? "快捷方式创建失败（将自动忽略）。" : "快捷方式已创建。");
    }

    /// <summary>卸载：杀本目录进程 → 删桌面/开始菜单快捷方式 → 删安装目录。返回剩余未删路径。</summary>
    public static List<string> Uninstall(string dir)
    {
        var leftover = new List<string>();
        KillApp(dir);   // 只杀本安装目录内的进程（版本隔离：不动用户个人版）

        TryDeleteFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "PetPlanner.lnk"));
        string menuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "PetPlanner");
        TryDeleteFile(Path.Combine(menuDir, "PetPlanner.lnk"));
        TryDeleteDirectory(menuDir);

        if (!TryDeleteDirectory(Path.GetFullPath(dir)))
        {
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) leftover.Add(f);
                foreach (string d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)) leftover.Add(d);
            }
            catch { /* 目录已删 */ }
        }
        return leftover;
    }

    public static void Launch(string dir)
    {
        string exe = Path.Combine(dir, AppExeName);
        if (File.Exists(exe))
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir });
    }

    // ---------- 内部工具 ----------

    private static void WriteResource(Assembly asm, string resName, string destPath)
    {
        using Stream? s = asm.GetManifestResourceStream(resName)
                          ?? throw new InvalidOperationException($"资源缺失：{resName}");
        using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
        s.CopyTo(fs); // 64KB 分块流式，150MB 也不整块进内存
    }

    /// <summary>WScript.Shell 晚绑定建 .lnk：不经任何 COM interop 程序集，单文件友好。公开以便 CLI 自测。</summary>
    public static bool CreateShortcut(string lnkPath, string targetExe)
    {
        try
        {
            Type? t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return false;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic sc = shell.CreateShortcut(lnkPath);
            sc.TargetPath = targetExe;
            sc.WorkingDirectory = Path.GetDirectoryName(targetExe);
            sc.IconLocation = targetExe + ",0";
            sc.Description = "PetPlanner 考研计划器";
            sc.Save();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 只结束「目标目录内」的 PetPlanner 进程——个人版与测试版进程同名但安装位置不同，
    /// 按路径过滤后卸载/重装测试版绝不会误杀正在跑的个人版（版本隔离）。
    /// </summary>
    private static void KillApp(string dir)
    {
        string root;
        try { root = Path.GetFullPath(dir).TrimEnd('\\', '/') + "\\"; }
        catch { return; }
        try
        {
            foreach (Process p in Process.GetProcessesByName("PetPlanner"))
            {
                try
                {
                    string? exe = p.MainModule?.FileName;
                    if (string.IsNullOrEmpty(exe)) continue;
                    if (!exe.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // 不在本目录 → 跳过
                    try { p.Kill(); p.WaitForExit(3000); } catch { /* 已退出/无权限 */ }
                }
                catch { /* 进程已退出 / 无权限读 MainModule */ }
            }
        }
        catch { /* 遍历失败忽略 */ }
    }

    private static bool TryDeleteFile(string path)
    {
        for (int i = 0; i < 5; i++)
        {
            try { if (File.Exists(path)) File.Delete(path); return true; }
            catch { Thread.Sleep(250); }
        }
        return false;
    }

    private static bool TryDeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return true;
        for (int i = 0; i < 8; i++)
        {
            try { Directory.Delete(dir, recursive: true); return true; }
            catch { Thread.Sleep(300); }
        }
        return false;
    }
}
