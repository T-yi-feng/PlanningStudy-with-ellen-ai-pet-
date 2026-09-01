using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KaoyanPlanner.WPF.Services;

/// <summary>
/// 一组动画帧 + 每帧延迟（毫秒）。帧为原生尺寸 BitmapSource（显示缩放由 Image 控制）。
/// 对应 ani.py 的 AniClip。
/// </summary>
public sealed class AniClip
{
    public IReadOnlyList<BitmapSource> Frames { get; }
    public int DelayMs { get; }
    public int FrameCount => Frames.Count;

    public AniClip(IReadOnlyList<BitmapSource> frames, int delayMs)
    {
        Frames = frames;
        DelayMs = delayMs;
    }
}

/// <summary>
/// Windows 动画光标（.ani）解析器：把桌宠动画帧解码为 BitmapSource 序列。
/// 移植 ani.py：RIFF 'ACON' 容器 → anih(JifRate) + LIST fram 里每帧 icon 数据；
/// icon 数据 = BITMAPINFOHEADER(biSize=40, biHeight=2*h) + 32bpp XOR 图(h 行，DIB 自底向上)。
/// 解码后经 FormatConvertedBitmap 预乘 alpha（WPF 渲染要求 Pbgra32），帧一律 Freeze 供跨线程用。
/// </summary>
public static class AniLoader
{
    /// <summary>加载 .ani → AniClip（原生尺寸帧）；文件缺失/损坏返回 null。</summary>
    public static AniClip? LoadAni(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 12) return null;
            if (data[0] != (byte)'R' || data[1] != (byte)'I' || data[2] != (byte)'F' || data[3] != (byte)'F') return null;
            if (data[8] != (byte)'A' || data[9] != (byte)'C' || data[10] != (byte)'O' || data[11] != (byte)'N') return null;

            var frames = new List<BitmapSource>();
            foreach (byte[] payload in FramPayloads(data))
            {
                BitmapSource? img = DecodeIconPayload(payload);
                if (img is not null) frames.Add(img);
            }
            if (frames.Count == 0) return null;

            int jif = JifRate(data);
            int delayMs = Math.Max(20, (int)Math.Round(jif * 1000.0 / 60.0));
            return new AniClip(frames, delayMs);
        }
        catch
        {
            return null;   // 任一环节出错 → 整组退回兜底（镜像 ani.py load_ani）
        }
    }

    /// <summary>兜底帧：蓝色圆球 + 白色「宠」（镜像 pet.py _make_fallback_clip）。</summary>
    public static AniClip MakeFallbackClip(int targetWidth)
    {
        var bmp = new RenderTargetBitmap(targetWidth, targetWidth, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            var fill = new SolidColorBrush(Color.FromRgb(0x33, 0x9C, 0xFF));
            dc.DrawEllipse(fill, null,
                new Point(targetWidth / 2.0, targetWidth / 2.0),
                targetWidth / 4.0, targetWidth / 4.0);
            // 兜底位图按 96 DPI 渲染 → PixelsPerDip=1.0（用带 PixelsPerDip 的重载避免 CS0618）
            var ft = new FormattedText("宠", CultureInfo.GetCultureInfo("zh-CN"),
                FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"),
                targetWidth / 7.3, Brushes.White, 1.0);
            dc.DrawText(ft, new Point((targetWidth - ft.Width) / 2, (targetWidth - ft.Height) / 2));
        }
        bmp.Render(dv);
        bmp.Freeze();
        return new AniClip(new[] { (BitmapSource)bmp }, 83);
    }

    // ------------------------------------------------------------ RIFF 解析

    /// <summary>遍历 RIFF 一级块（从 12 起跳过 RIFF 头），产出 (fourcc, payload)。</summary>
    private static IEnumerable<(string FourCc, byte[] Payload)> IterChunks(byte[] data, int start)
    {
        int i = start;
        while (i + 8 <= data.Length)
        {
            string fcc = Encoding.ASCII.GetString(data, i, 4);
            int sz = BitConverter.ToInt32(data, i + 4);
            if (sz < 0 || i + 8 + sz > data.Length) break;
            var payload = new byte[sz];
            Buffer.BlockCopy(data, i + 8, payload, 0, sz);
            yield return (fcc, payload);
            i += 8 + sz + (sz & 1);
        }
    }

    /// <summary>取出 LIST 'fram' 里所有 icon 子块的数据。</summary>
    private static List<byte[]> FramPayloads(byte[] data)
    {
        var out_ = new List<byte[]>();
        foreach ((string fcc, byte[] body) in IterChunks(data, 12))
        {
            if (fcc == "LIST" && body.Length >= 4 &&
                body[0] == (byte)'f' && body[1] == (byte)'r' && body[2] == (byte)'a' && body[3] == (byte)'m')
            {
                int j = 4;
                while (j + 8 <= body.Length)
                {
                    int sz = BitConverter.ToInt32(body, j + 4);
                    if (sz < 0 || j + 8 + sz > body.Length) break;
                    var payload = new byte[sz];
                    Buffer.BlockCopy(body, j + 8, payload, 0, sz);
                    out_.Add(payload);
                    j += 8 + sz + (sz & 1);
                }
            }
        }
        return out_;
    }

    /// <summary>anih 的 JifRate（帧速率，单位 1/60 秒）；缺省 5。</summary>
    private static int JifRate(byte[] data)
    {
        foreach ((string fcc, byte[] body) in IterChunks(data, 12))
            if (fcc == "anih" && body.Length >= 36)
                return BitConverter.ToInt32(body, 28);
        return 5;
    }

    /// <summary>单个 icon 数据 → 预乘 alpha 的 BitmapSource。失败返回 null。</summary>
    private static BitmapSource? DecodeIconPayload(byte[] payload)
    {
        int off = IndexOfBytes(payload, new byte[] { 0x28, 0x00, 0x00, 0x00 });   // biSize == 40
        if (off < 0 || off + 40 > payload.Length) return null;
        int w = BitConverter.ToInt32(payload, off + 4);
        int h = BitConverter.ToInt32(payload, off + 8);
        short bpp = BitConverter.ToInt16(payload, off + 14);
        if (bpp != 32 || w <= 0 || h <= 0) return null;

        int stride = ((w * 32 + 31) / 32) * 4;
        int imgH = h > w ? h / 2 : h;             // 图标 DIB 高度翻倍 → 真实图像高
        int need = imgH * stride;
        int pixelsOff = off + 40;
        if (pixelsOff + need > payload.Length) return null;

        var buf = new byte[need];
        for (int y = 0; y < imgH; y++)            // DIB 自底向上 → 翻转成自顶向下
        {
            int src = (imgH - 1 - y) * stride;
            Buffer.BlockCopy(payload, pixelsOff + src, buf, y * w * 4, w * 4);
        }

        var raw = BitmapSource.Create(w, imgH, 96, 96, PixelFormats.Bgra32, null, buf, stride);
        var pm = new FormatConvertedBitmap(raw, PixelFormats.Pbgra32, null, 0);
        pm.Freeze();
        return pm;
    }

    private static int IndexOfBytes(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }
}
