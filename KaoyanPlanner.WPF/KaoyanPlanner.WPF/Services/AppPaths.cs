using System.IO;

namespace KaoyanPlanner.WPF.Services;

/// <summary>
/// 路径解析（对应 paths.py）：用户数据目录 = exe 所在目录（便携）。
/// 开发/测试可用环境变量 KAOYAN_DATA_DIR 覆盖（例如指向仓库根的 data.json）。
/// </summary>
public static class AppPaths
{
    public static string BaseDir
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("KAOYAN_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(env))
                return env;
            string? p = Environment.ProcessPath;
            return p is null ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(p)!;
        }
    }

    public static string DataFile => Path.Combine(BaseDir, "data.json");

    public static string SecretFile => Path.Combine(BaseDir, "secret.json");
}
