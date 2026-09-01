using System.IO;

namespace KaoyanPlanner.WPF.Services;

/// <summary>一个桌宠形象的描述：显示名 + 目录 + 是否默认 + 是否有效（4 核心动画齐全）。</summary>
public sealed record SkinInfo(string Name, string Directory, bool IsDefault, bool Valid);

/// <summary>
/// 桌宠形象管理：每个形象 = desk_pet 下的一个子文件夹，含 5 组动画（.ani）。
/// 文件名兼容两套：规范名（normal.ani/talking.ani/happy.ani/present.ani/sleep.ani）与
/// 旧版顶层默认名（normal_1.ani/talking_1.ani/happy_1.ani/present_1.ani/alternate.ani）。
/// 默认艾莲 = desk_pet 顶层文件（皮肤名为空串），不迁移、不改动。
/// 纯 C#（不碰 WPF），方便单测：目录一律由调用方传入，方便用临时目录验证。
/// </summary>
public static class PetSkinService
{
    /// <summary>生产环境的形象根目录（exe 旁的 desk_pet）。测试传临时目录。</summary>
    public static string SkinRootDir { get; } = Path.Combine(AppPaths.BaseDir, "desk_pet");

    /// <summary>动画动作种类及加载顺序。</summary>
    public static readonly string[] KindOrder = { "normal", "talking", "happy", "present", "sleep" };

    /// <summary>每个动作种类的候选文件名：规范名在前，遗留别名在后。</summary>
    public static readonly IReadOnlyDictionary<string, string[]> KindCandidates =
        new Dictionary<string, string[]>
        {
            ["normal"] = new[] { "normal.ani", "normal_1.ani" },
            ["talking"] = new[] { "talking.ani", "talking_1.ani" },
            ["happy"] = new[] { "happy.ani", "happy_1.ani" },
            ["present"] = new[] { "present.ani", "present_1.ani" },
            ["sleep"] = new[] { "sleep.ani", "alternate.ani" },
        };

    /// <summary>形象目录：皮肤名为空串 → 根目录（默认艾莲 = 顶层文件）；否则 root/name（防路径穿越）。</summary>
    public static string SkinDirFor(string root, string? skinName)
    {
        string name = (skinName ?? "").Trim();
        if (name.Length == 0) return root;
        if (name.IndexOfAny(new[] { '/', '\\' }) >= 0 || name == "." || name == "..")
            return root;   // 只允许单层文件夹名
        return Path.Combine(root, name);
    }

    /// <summary>解析该形象目录里每个 kind 实际存在的文件（逐个候选取第一个命中）。</summary>
    public static IReadOnlyDictionary<string, string> ResolveClipFiles(string skinDir)
    {
        var result = new Dictionary<string, string>();
        if (!Directory.Exists(skinDir)) return result;
        foreach (string kind in KindOrder)
        {
            foreach (string cand in KindCandidates[kind])
            {
                string path = Path.Combine(skinDir, cand);
                if (File.Exists(path))
                {
                    result[kind] = path;
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>4 个核心动画（normal/talking/happy/present）全部可解析才算有效形象。</summary>
    public static bool IsValidSkinFolder(string skinDir)
    {
        if (!Directory.Exists(skinDir)) return false;
        var files = ResolveClipFiles(skinDir);
        return files.ContainsKey("normal") && files.ContainsKey("talking")
            && files.ContainsKey("happy") && files.ContainsKey("present");
    }

    /// <summary>枚举形象：默认（顶层）在最前，子目录按名排序。跳过以 . 开头的目录。</summary>
    public static IReadOnlyList<SkinInfo> ListSkins(string root)
    {
        var list = new List<SkinInfo>
        {
            new("默认", root, IsDefault: true, Valid: IsValidSkinFolder(root)),
        };
        if (Directory.Exists(root))
        {
            foreach (string dir in Directory.EnumerateDirectories(root)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
            {
                list.Add(new SkinInfo(Path.GetFileName(dir), dir, IsDefault: false, Valid: IsValidSkinFolder(dir)));
            }
        }
        return list;
    }

    /// <summary>形象目录里的 preview.png；无则返回 null（设置里缺省用常态第一帧）。</summary>
    public static string? PreviewFile(string skinDir)
    {
        string p = Path.Combine(skinDir, "preview.png");
        return File.Exists(p) ? p : null;
    }

    /// <summary>把任意文件夹名清洗成合法的单层形象名（去非法字符/设备名/首尾点空格，兜底 "skin"）。</summary>
    public static string SanitizeSkinName(string raw)
    {
        string name = (raw ?? "").Trim();
        if (name.Length == 0) return "skin";
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        // Windows 保留设备名（CON/PRN/AUX/NUL/COM1-9/LPT1-9）→ 前缀下划线绕开
        string stem = name.Split('.')[0].ToUpperInvariant();
        bool device = stem is "CON" or "PRN" or "AUX" or "NUL"
            || (stem.Length > 3 && stem.StartsWith("COM") && stem[3..].All(char.IsDigit))
            || (stem.Length > 3 && stem.StartsWith("LPT") && stem[3..].All(char.IsDigit));
        if (device) name = "_" + name;
        name = name.Trim().TrimStart('.').TrimEnd('.', ' ');
        if (name.Length == 0) return "skin";
        return name.Length > 40 ? name[..40] : name;
    }

    /// <summary>
    /// 导入形象：把源文件夹复制为 root/&lt;名字&gt;/（文件按规范名落盘，preview.png 若有则带过来）。
    /// 已存在同名 → 自动追加 _2/_3（绝不覆盖已有形象）；同文件夹重导 → no-op。
    /// 成功返回形象名，失败返回 null 并给出 error。
    /// </summary>
    public static string? ImportSkin(string sourceDir, string root, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir))
        {
            error = "选中的文件夹不存在";
            return null;
        }
        if (!IsValidSkinFolder(sourceDir))
        {
            error = "这个文件夹缺少核心动画（需要 normal/talking/happy/present 四个 .ani）";
            return null;
        }
        string name = SanitizeSkinName(Path.GetFileName(sourceDir.TrimEnd('\\', '/')));
        string destDir = SkinDirFor(root, name);
        // 重导同一个文件夹（源就是目标）→ 直接成功，不复制
        if (string.Equals(Path.GetFullPath(sourceDir), Path.GetFullPath(destDir), StringComparison.OrdinalIgnoreCase))
            return name;
        int n = 2;
        while (Directory.Exists(destDir))
        {
            destDir = Path.Combine(root, name + "_" + n);
            n++;
        }
        var src = ResolveClipFiles(sourceDir);
        try
        {
            Directory.CreateDirectory(destDir);
            foreach (string kind in KindOrder)
            {
                if (src.TryGetValue(kind, out string? path))
                    File.Copy(path, Path.Combine(destDir, KindCandidates[kind][0]), overwrite: true);
            }
            string? preview = PreviewFile(sourceDir);
            if (preview is not null)
                File.Copy(preview, Path.Combine(destDir, "preview.png"), overwrite: true);
            return Path.GetFileName(destDir);
        }
        catch (Exception ex)
        {
            error = "导入失败：" + ex.Message;
            try { if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true); }
            catch { /* 清理失败忽略 */ }
            return null;
        }
    }
}
