using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using NAudio.Wave;

namespace KaoyanPlanner.WPF.Services;

/// <summary>
/// 实时媒体字幕：采集系统播放声音（WASAPI 环回）→ 能量门限切句 → 云端语音识别（移植 captions.py）。
/// 用 NAudio 的 WasapiLoopbackCapture 拿默认输出设备的环回 PCM，降混 + 重采样 16k 后进队列；
/// VAD 线程攒句子（支持自适应门限 + 半成品预览），识别线程 multipart 上传硅基流动 SenseVoiceSmall。
/// 全程后台线程；结果经 TextReady/InterimReady/StatusChanged 事件封回主线程。
/// </summary>
public sealed class CaptionService : IDisposable
{
    /// <summary>
    /// 默认识别模型。硅基流动的两个语音识别模型会「轮流」故障——
    /// 2026-08-18：FunAudioLLM/SenseVoiceSmall 对任意音频稳定 500，TeleAI/TeleSpeechASR 正常；
    /// 2026-08-20：反过来了，TeleSpeechASR 500、SenseVoiceSmall 正常。所以：
    /// 1) 默认模型随当前可用者走（现在 SenseVoice 活着）；
    /// 2) 真正靠 TranscribeAsync 的回退链兜底——链里必须同时含两个模型，绝不能被去重成单点。
    /// </summary>
    public const string DefaultModel = "FunAudioLLM/SenseVoiceSmall";
    private const string AsrBaseUrl = "https://api.siliconflow.cn/v1/audio/transcriptions";
    private const int SR = 16000;
    private const double EnergyThresh = 0.015;
    private const double SilenceEndS = 0.7;
    private const double MaxUtterS = 15.0;
    private const double MinUtterS = 0.35;
    private const double ThreshMin = 0.008, ThreshMax = 0.03;
    private const double InterimS = 1.5;
    private const double InterimMinS = 1.0;
    private const int QueueMax = 8;

    private readonly Func<string> _getKey;
    private readonly Func<string> _getLang;
    private readonly Func<string> _getModel;

    private WasapiLoopbackCapture? _capture;
    private readonly BlockingCollection<(float[] Samples, double Energy)> _queue = new();
    private readonly BlockingCollection<(bool Final, byte[] Wav)> _transQ = new();
    private volatile bool _running;
    private int _gen;
    private string _lastFinal = "";
    private string _lastInterim = "";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>定稿字幕（整句）。</summary>
    public event Action<string>? TextReady;
    /// <summary>半成品字幕（实时预览）。</summary>
    public event Action<string>? InterimReady;
    /// <summary>状态提示（聆听中/错误/未配 Key）。</summary>
    public event Action<string>? StatusChanged;

    public bool Running => _running;

    public CaptionService(Func<string> getKey, Func<string> getLang, Func<string>? getModel = null)
    {
        _getKey = getKey;
        _getLang = getLang;
        _getModel = getModel ?? (() => DefaultModel);
    }

    private static HttpClient CreateClient()
    {
        // 连接池显式调优：建连 10s 快失败、连接复用防握手堆积；整体超时 40s（弱网上传 16k 音频够用）。
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 4,
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
        c.DefaultRequestHeaders.Add("User-Agent", "PetPlanner/1.0");
        return c;
    }

    // ------------------------------------------------------------ 生命周期

    public void Start()
    {
        if (_running) return;
        _running = true;
        _gen++;
        Drain();
        _ = Task.Run(() => VadLoop(_gen));
        _ = Task.Run(() => TransLoop(_gen));
        StartCapture();
        StatusChanged?.Invoke("🎙 聆听中…");
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        StopCapture();
        _queue.Add((Array.Empty<float>(), 0.0));   // 哨兵唤醒 VAD 退出
    }

    private void Drain()
    {
        while (_queue.TryTake(out _)) { }
        while (_transQ.TryTake(out _)) { }
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------ 采集（NAudio 后台线程）

    private void StartCapture()
    {
        try
        {
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += (_, _) =>
            {
                if (_running) StatusChanged?.Invoke("⚠ 音频采集出错，正在重试…");
            };
            _capture.StartRecording();
        }
        catch
        {
            _capture?.Dispose();
            _capture = null;
            StatusChanged?.Invoke("⚠ 找不到可用的音频输出设备");
        }
    }

    private void StopCapture()
    {
        try { _capture?.StopRecording(); } catch { /* 忽略 */ }
        try { _capture?.Dispose(); } catch { /* 忽略 */ }
        _capture = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_running || _capture is null) return;
        WaveFormat fmt = _capture.WaveFormat;
        float[] mono = BytesToMono(e.Buffer, e.BytesRecorded, fmt);
        if (mono.Length == 0) return;
        float[] resampled = Resample(mono, fmt.SampleRate, SR);
        double energy = Energy(resampled);
        if (_queue.Count >= QueueMax) _queue.TryTake(out _);   // 丢最旧保新
        _queue.Add((resampled, energy));
    }

    /// <summary>字节 → 单声道 float（降混）。32bit=float、16bit=PCM，其余跳过。</summary>
    private static float[] BytesToMono(byte[] bytes, int recorded, WaveFormat fmt)
    {
        int ch = Math.Max(1, fmt.Channels);
        int total;
        float[] buf;
        if (fmt.BitsPerSample == 32)
        {
            total = recorded / 4;
            buf = new float[total];
            Buffer.BlockCopy(bytes, 0, buf, 0, recorded);   // WASAPI 环回共享模式 = IEEE float
        }
        else if (fmt.BitsPerSample == 16)
        {
            total = recorded / 2;
            buf = new float[total];
            for (int i = 0; i < total; i++) buf[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        }
        else
        {
            return Array.Empty<float>();
        }
        if (ch <= 1) return buf;
        int n = total / ch;
        var mono = new float[n];
        for (int i = 0; i < n; i++)
        {
            float sum = 0;
            for (int c = 0; c < ch; c++) sum += buf[i * ch + c];
            mono[i] = sum / ch;
        }
        return mono;
    }

    private static float[] Resample(float[] x, int src, int dst)
    {
        if (src == dst || x.Length < 2) return x;
        int nOut = Math.Max(1, (int)Math.Round(x.Length * (double)dst / src));
        if (nOut == x.Length) return x;
        if (nOut == 1) return new[] { x[0] };
        var outArr = new float[nOut];
        double step = (double)(x.Length - 1) / (nOut - 1);
        for (int i = 0; i < nOut; i++)
        {
            double pos = i * step;
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, x.Length - 1);
            double frac = pos - i0;
            outArr[i] = (float)(x[i0] * (1 - frac) + x[i1] * frac);
        }
        return outArr;
    }

    private static double Energy(float[] x)
    {
        if (x.Length == 0) return 0;
        double sum = 0;
        foreach (float v in x) sum += v * v;
        return Math.Sqrt(sum / x.Length);
    }

    // ------------------------------------------------------------ 语句切分（VAD）

    private static (double NoiseFloor, double Thresh) AdaptThreshold(double noiseFloor, double thresh, double energy)
    {
        if (energy < thresh) noiseFloor = noiseFloor * 0.8 + energy * 0.2;
        else noiseFloor *= 1.02;
        return (noiseFloor, Math.Max(ThreshMin, Math.Min(ThreshMax, noiseFloor * 1.5)));
    }

    private void VadLoop(int gen)
    {
        var buf = new List<float[]>();
        bool collecting = false;
        double silenceS = 0, sinceInterim = 0;
        double noiseFloor = EnergyThresh, thresh = EnergyThresh;
        while (_running && gen == _gen)
        {
            if (!_queue.TryTake(out var item, TimeSpan.FromSeconds(1)))
            {
                if (collecting)   // 1s 拿不到新窗口 → 采集停滞，flush 当前句
                {
                    Enqueue(buf, final: true);
                    buf = new List<float[]>(); collecting = false; silenceS = 0; sinceInterim = 0;
                }
                continue;
            }
            if (item.Samples.Length == 0) break;   // 哨兵
            var (mono, energy) = item;
            (noiseFloor, thresh) = AdaptThreshold(noiseFloor, thresh, energy);
            if (energy >= thresh)
            {
                buf.Add(mono);
                if (!collecting) { collecting = true; sinceInterim = 0; }
                else sinceInterim += mono.Length / (double)SR;
                silenceS = 0;
                double total = buf.Sum(x => x.Length) / (double)SR;
                if (total >= MaxUtterS)
                {
                    Enqueue(buf, final: true);
                    buf = new List<float[]>(); collecting = false; silenceS = 0; sinceInterim = 0;
                }
                else if (sinceInterim >= InterimS && total >= InterimMinS)
                {
                    Enqueue(buf, final: false);
                    sinceInterim = 0;
                }
            }
            else if (collecting)
            {
                silenceS += mono.Length / (double)SR;
                if (silenceS >= SilenceEndS)
                {
                    Enqueue(buf, final: true);
                    buf = new List<float[]>(); collecting = false; silenceS = 0; sinceInterim = 0;
                }
            }
        }
    }

    private void Enqueue(List<float[]> buf, bool final)
    {
        if (buf.Count == 0) return;
        double total = buf.Sum(x => x.Length) / (double)SR;
        if (final)
        {
            if (total < MinUtterS) return;   // 太短，多半是杂音
        }
        else if (total < InterimMinS)
        {
            return;
        }
        if (_transQ.Count >= 4) return;   // 网络积压丢旧句
        _transQ.Add((final, SamplesToWav(buf)));
    }

    // ------------------------------------------------------------ 识别（独立线程）

    private void TransLoop(int gen)
    {
        while (_running && gen == _gen)
        {
            if (!_transQ.TryTake(out var item, TimeSpan.FromSeconds(1))) continue;
            var (final, wav) = item;
            string key = (_getKey() ?? "").Trim();
            if (key.Length == 0)
            {
                StatusChanged?.Invoke(Brand.CaptionNoKey);
                continue;
            }
            string text;
            try
            {
                text = TranscribeAsync(key, wav, _getLang(), _getModel()).GetAwaiter().GetResult();
            }
            catch (CaptionUnavailable ex)
            {
                StatusChanged?.Invoke(ex.Message);
                continue;
            }
            catch
            {
                StatusChanged?.Invoke("⚠ 识别失败");
                continue;
            }
            text = (text ?? "").Trim();
            if (final)
            {
                if (text.Length > 0 && text != _lastFinal)
                {
                    _lastFinal = text;
                    TextReady?.Invoke(text);
                    StatusChanged?.Invoke("🎙 聆听中…");
                }
            }
            else
            {
                if (text.Length > 0 && text != _lastInterim)
                {
                    _lastInterim = text;
                    InterimReady?.Invoke(text);
                }
            }
        }
    }

    private async Task<string> TranscribeAsync(string key, byte[] wav, string lang, string configuredModel)
    {
        // 模型回退链：先按配置/默认模型试，服务端故障（5xx）或网络错误就换另一个模型再试。
        // 坑（2026-08-18 → 08-20）：硅基流动的两个语音识别模型会「轮流」故障——
        // 18 号 SenseVoiceSmall 挂、TeleSpeechASR 正常；20 号反过来了。所以链里必须
        // 同时放上两个模型，且绝不因「恰好与配置同模型」而被去重成单点——否则那个模型
        // 一挂就无路可退，只能报「服务端繁忙(500)」。
        const string ModelA = "TeleAI/TeleSpeechASR";
        const string ModelB = "FunAudioLLM/SenseVoiceSmall";
        var models = new List<string>();
        string primary = string.IsNullOrWhiteSpace(configuredModel) ? DefaultModel : configuredModel;
        if (!models.Contains(primary)) models.Add(primary);   // 配置/默认优先
        if (!models.Contains(ModelA)) models.Add(ModelA);      // 两个候选都放进去
        if (!models.Contains(ModelB)) models.Add(ModelB);      //（谁活着用谁）

        string? lastError = null;
        foreach (string model in models)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var form = new MultipartFormDataContent();
                    form.Add(new StringContent(model), "model");
                    form.Add(new StringContent("json"), "response_format");
                    if (!string.IsNullOrEmpty(lang) && lang != "auto")
                        form.Add(new StringContent(lang), "language");
                    var fileContent = new ByteArrayContent(wav);
                    fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                    form.Add(fileContent, "file", "audio.wav");

                    using var req = new HttpRequestMessage(HttpMethod.Post, AsrBaseUrl);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    req.Content = form;
                    using var resp = await Http.SendAsync(req);
                    string body = await resp.Content.ReadAsStringAsync();
                    int code = (int)resp.StatusCode;
                    if (resp.IsSuccessStatusCode)
                    {
                        var node = JsonNode.Parse(body);
                        string text = (node?["text"] ?? node?["data"])?.GetValue<string>() ?? "";
                        return text.Trim();
                    }
                    if (code == 401 || code == 403)
                        throw new CaptionUnavailable("⚠ ASR Key 无效，请在字幕设置里检查");
                    if (code == 429 || code >= 500)
                    {
                        lastError = $"服务端繁忙({code})";
                        await Task.Delay(600);   // 服务端瞬时过载：退避一下再换下一个模型
                        break;   // 服务端故障 → 换下一个模型
                    }
                    lastError = $"识别失败 (HTTP {code})";
                }
                catch (CaptionUnavailable) { throw; }
                catch
                {
                    lastError = "网络不可用";
                    await Task.Delay(500);   // 网络抖动：退避后换下一个模型，别连续打
                    break;   // 网络错误 → 换下一个模型
                }
                if (attempt + 1 < 2)
                    await Task.Delay(1000);
            }
        }
        throw new CaptionUnavailable("⚠ " + lastError + "，稍等再试");
    }

    // ------------------------------------------------------------ WAV 编码

    private static byte[] SamplesToWav(List<float[]> bufs)
    {
        int total = bufs.Sum(b => b.Length);
        var concat = new float[total];
        int off = 0;
        foreach (var b in bufs) { Array.Copy(b, 0, concat, off, b.Length); off += b.Length; }

        // 裁首尾近静音，让云端只听到干净人声
        float peak = 0;
        foreach (float v in concat) peak = Math.Max(peak, Math.Abs(v));
        int first = -1, last = -1;
        if (peak > 0)
        {
            float cut = Math.Max(peak * 0.02f, 0.003f);
            for (int i = 0; i < concat.Length; i++)
                if (Math.Abs(concat[i]) > cut) { if (first < 0) first = i; last = i; }
        }
        if (first >= 0 && last > first)
        {
            var trimmed = new float[last - first + 1];
            Array.Copy(concat, first, trimmed, 0, trimmed.Length);
            concat = trimmed;
        }

        var pcm = new short[Math.Max(1, concat.Length)];
        for (int i = 0; i < concat.Length; i++)
        {
            float v = Math.Clamp(concat[i] * 32767f, -32768f, 32767f);
            pcm[i] = (short)Math.Round(v);
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        int dataLen = pcm.Length * 2;
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataLen);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);        // PCM
        bw.Write((short)1);        // mono
        bw.Write(SR);
        bw.Write(SR * 2);          // byte rate
        bw.Write((short)2);        // block align
        bw.Write((short)16);       // bits
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataLen);
        foreach (short s in pcm) bw.Write(s);
        return ms.ToArray();
    }

    private sealed class CaptionUnavailable : Exception
    {
        public CaptionUnavailable(string message) : base(message) { }
    }
}
