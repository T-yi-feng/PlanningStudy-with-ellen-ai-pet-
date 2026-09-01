using System.IO;
using System.Threading;
using KaoyanPlanner.WPF.Services;
using Xunit;
using Xunit.Sdk;

namespace KaoyanPlanner.WPF.Tests;

/// <summary>
/// AniClip 解析器测试：读取构建分发的 desk_pet/*.ani 真实资源（只读，不写任何数据）。
/// WPF Freezable 需在 STA 线程创建，故所有用例包一层 STA。
/// </summary>
public class AniClipTests
{
    private static void RunSta(Action action)
    {
        Exception? err = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { err = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err is not null) throw new XunitException("STA 线程异常: " + err.Message, err);
    }

    private static string AniPath(string name) => Path.Combine(AppContext.BaseDirectory, "desk_pet", name);

    [Fact]
    public void LoadAni_RealAsset_DecodesFrames()
    {
        RunSta(() =>
        {
            var clip = AniLoader.LoadAni(AniPath("normal_1.ani"));
            Assert.NotNull(clip);
            Assert.True(clip!.FrameCount > 0, "应有至少一帧");
            Assert.True(clip.DelayMs >= 20, "帧延迟应 >= 20ms");
            var f0 = clip.Frames[0];
            Assert.True(f0.PixelWidth > 0 && f0.PixelHeight > 0, "帧应有有效尺寸");
        });
    }

    [Fact]
    public void LoadAni_AllFiveClips_Load()
    {
        RunSta(() =>
        {
            foreach (string name in new[] { "normal_1.ani", "talking_1.ani", "happy_1.ani", "present_1.ani", "alternate.ani" })
            {
                var clip = AniLoader.LoadAni(AniPath(name));
                Assert.NotNull(clip);
                Assert.True(clip!.FrameCount > 0, name + " 应能解码");
            }
        });
    }

    [Fact]
    public void LoadAni_MissingFile_ReturnsNull()
    {
        RunSta(() => Assert.Null(AniLoader.LoadAni(AniPath("nope.ani"))));
    }

    [Fact]
    public void MakeFallbackClip_SingleFrameAtTargetWidth()
    {
        RunSta(() =>
        {
            var clip = AniLoader.MakeFallbackClip(190);
            Assert.Equal(1, clip.FrameCount);
            Assert.Equal(190, clip.Frames[0].PixelWidth);
            Assert.Equal(190, clip.Frames[0].PixelHeight);
        });
    }
}
