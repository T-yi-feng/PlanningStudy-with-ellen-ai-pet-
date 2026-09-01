using System.IO;
using KaoyanPlanner.WPF.Services;
using Xunit;

namespace KaoyanPlanner.WPF.Tests;

/// <summary>
/// 桌宠形象管理测试：全部用临时目录 + 占位 .ani 文件（服务只做文件发现/校验/导入，
/// 不解码动画，无需真实 RIFF 内容）。每个用例建根目录，finally 清理。
/// </summary>
public class PetSkinServiceTests
{
    private static string NewRoot()
    {
        string d = Path.Combine(Path.GetTempPath(), "kp_skin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
    }

    private static void Write(string path) => File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });

    private static void MakeCore(string dir, string suffix = ".ani", string[]? kinds = null)
    {
        foreach (string kind in kinds ?? new[] { "normal", "talking", "happy", "present" })
            Write(Path.Combine(dir, kind + suffix));
    }

    // ------------------------------------------------------------ ListSkins

    [Fact]
    public void ListSkins_EmptyRoot_OnlyDefaultEntry()
    {
        string root = NewRoot();
        try
        {
            var skins = PetSkinService.ListSkins(root);
            Assert.Single(skins);
            Assert.True(skins[0].IsDefault);
            Assert.Equal(root, skins[0].Directory);
            Assert.False(skins[0].Valid);   // 根目录没动画
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ListSkins_Subfolders_ValidFlagsAndDotSkip()
    {
        string root = NewRoot();
        try
        {
            MakeCore(root);                       // 顶层有动画 → 默认有效
            Directory.CreateDirectory(Path.Combine(root, "小蓝"));
            MakeCore(Path.Combine(root, "小蓝"));
            Directory.CreateDirectory(Path.Combine(root, "broken"));
            MakeCore(Path.Combine(root, "broken"), kinds: new[] { "normal", "talking" });   // 缺核心
            Directory.CreateDirectory(Path.Combine(root, ".hidden"));

            var skins = PetSkinService.ListSkins(root);
            Assert.Equal(3, skins.Count);          // 默认 + 小蓝 + broken（.hidden 跳过）
            Assert.True(skins[0].IsDefault && skins[0].Valid);
            Assert.Equal("小蓝", skins[1].Name);
            Assert.True(skins[1].Valid);
            Assert.Equal("broken", skins[2].Name);
            Assert.False(skins[2].Valid);
        }
        finally { Cleanup(root); }
    }

    // ------------------------------------------------------------ ResolveClipFiles

    [Fact]
    public void ResolveClipFiles_LegacyAliasLayout_ResolvesDefaults()
    {
        // 顶层默认艾莲布局（旧名）：证明不改名也能解析
        string root = NewRoot();
        try
        {
            foreach (string f in new[] { "normal_1.ani", "talking.ani", "happy_1.ani", "present_1.ani", "alternate.ani" })
                Write(Path.Combine(root, f));

            var files = PetSkinService.ResolveClipFiles(root);
            Assert.Equal(Path.Combine(root, "normal_1.ani"), files["normal"]);
            Assert.Equal(Path.Combine(root, "talking.ani"), files["talking"]);
            Assert.Equal(Path.Combine(root, "happy_1.ani"), files["happy"]);
            Assert.Equal(Path.Combine(root, "present_1.ani"), files["present"]);
            Assert.Equal(Path.Combine(root, "alternate.ani"), files["sleep"]);
            Assert.True(PetSkinService.IsValidSkinFolder(root));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ResolveClipFiles_CanonicalSubfolderLayout_Resolves()
    {
        string root = NewRoot();
        try
        {
            string dir = Path.Combine(root, "小蓝");
            Directory.CreateDirectory(dir);
            foreach (string f in new[] { "normal.ani", "talking.ani", "happy.ani", "present.ani", "sleep.ani" })
                Write(Path.Combine(dir, f));

            var files = PetSkinService.ResolveClipFiles(dir);
            Assert.Equal(5, files.Count);
            Assert.Equal(Path.Combine(dir, "normal.ani"), files["normal"]);
            Assert.Equal(Path.Combine(dir, "sleep.ani"), files["sleep"]);
            Assert.True(PetSkinService.IsValidSkinFolder(dir));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void IsValidSkinFolder_MissingCore_False()
    {
        string root = NewRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "x"));
            MakeCore(Path.Combine(root, "x"), kinds: new[] { "normal", "talking", "happy" });
            Assert.False(PetSkinService.IsValidSkinFolder(Path.Combine(root, "x")));
            Assert.False(PetSkinService.IsValidSkinFolder(Path.Combine(root, "nope")));
        }
        finally { Cleanup(root); }
    }

    // ------------------------------------------------------------ ImportSkin

    [Fact]
    public void ImportSkin_AliasSource_CopiesCanonicalNames()
    {
        string root = NewRoot();
        try
        {
            string src = Path.Combine(Path.GetTempPath(), "kp_src_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(src);
            try
            {
                foreach (string f in new[] { "normal_1.ani", "talking_1.ani", "happy_1.ani", "present_1.ani", "alternate.ani" })
                    Write(Path.Combine(src, f));
                Write(Path.Combine(src, "preview.png"));

                string? name = PetSkinService.ImportSkin(src, root, out string? err);
                Assert.Null(err);
                Assert.Equal(Path.GetFileName(src), name);

                string dest = Path.Combine(root, name!);
                Assert.True(File.Exists(Path.Combine(dest, "normal.ani")));    // 规范名落盘
                Assert.True(File.Exists(Path.Combine(dest, "talking.ani")));
                Assert.True(File.Exists(Path.Combine(dest, "sleep.ani")));
                Assert.True(File.Exists(Path.Combine(dest, "preview.png")));
                Assert.True(PetSkinService.IsValidSkinFolder(dest));
            }
            finally { Cleanup(src); }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ImportSkin_Collision_AppendsSuffix()
    {
        string root = NewRoot();
        try
        {
            string src = Path.Combine(Path.GetTempPath(), "kp_src_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(src);
            try
            {
                MakeCore(src);
                string? n1 = PetSkinService.ImportSkin(src, root, out _);
                string? n2 = PetSkinService.ImportSkin(src, root, out _);
                Assert.Equal(n1 + "_2", n2);   // 同名冲突 → 第二次自动 _2（返回实际落盘名）
                Assert.True(File.Exists(Path.Combine(root, n1!, "normal.ani")));
                Assert.True(File.Exists(Path.Combine(root, n2!, "normal.ani")));
            }
            finally { Cleanup(src); }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ImportSkin_ReimportSameFolder_NoOp()
    {
        string root = NewRoot();
        try
        {
            string dest = Path.Combine(root, "已存在");
            Directory.CreateDirectory(dest);
            MakeCore(dest);
            // 源就是目标目录 → 直接返回同名，不重复复制、不产生 _2
            string? name = PetSkinService.ImportSkin(dest, root, out string? err);
            Assert.Null(err);
            Assert.Equal("已存在", name);
            Assert.False(Directory.Exists(Path.Combine(root, "已存在_2")));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ImportSkin_InvalidSource_Errors()
    {
        string root = NewRoot();
        try
        {
            string src = Path.Combine(Path.GetTempPath(), "kp_src_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(src);
            try
            {
                Write(Path.Combine(src, "normal.ani"));   // 只有 1 个
                string? name = PetSkinService.ImportSkin(src, root, out string? err);
                Assert.Null(name);
                Assert.Contains("缺少核心动画", err);
                Assert.Null(PetSkinService.ImportSkin(Path.Combine(Path.GetTempPath(), "kp_none_" + Guid.NewGuid().ToString("N")), root, out err));
                Assert.Contains("不存在", err);
            }
            finally { Cleanup(src); }
        }
        finally { Cleanup(root); }
    }

    // ------------------------------------------------------------ Sanitize / SkinDirFor

    [Theory]
    [InlineData("小蓝", "小蓝")]
    [InlineData("a/b*:c?\"<>|", "a_b__c_____")]
    [InlineData("CON", "_CON")]
    [InlineData("COM1", "_COM1")]
    [InlineData("LPT9", "_LPT9")]
    [InlineData(".", "skin")]
    [InlineData("..", "skin")]
    [InlineData("  ", "skin")]
    [InlineData("名字. ", "名字")]
    [InlineData("正常名字", "正常名字")]
    public void SanitizeSkinName_Rules(string raw, string expected)
    {
        Assert.Equal(expected, PetSkinService.SanitizeSkinName(raw));
    }

    [Fact]
    public void SkinDirFor_EmptyIsRoot_AndGuardsTraversal()
    {
        string root = NewRoot();
        try
        {
            Assert.Equal(root, PetSkinService.SkinDirFor(root, ""));
            Assert.Equal(root, PetSkinService.SkinDirFor(root, null));
            Assert.Equal(root, PetSkinService.SkinDirFor(root, ".."));
            Assert.Equal(root, PetSkinService.SkinDirFor(root, "a/b"));
            Assert.Equal(Path.Combine(root, "小蓝"), PetSkinService.SkinDirFor(root, "小蓝"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void PreviewFile_OnlyWhenExists()
    {
        string root = NewRoot();
        try
        {
            Assert.Null(PetSkinService.PreviewFile(root));
            Write(Path.Combine(root, "preview.png"));
            Assert.Equal(Path.Combine(root, "preview.png"), PetSkinService.PreviewFile(root));
        }
        finally { Cleanup(root); }
    }
}
