using System.IO;
using System.IO.Pipes;
using System.Text;

namespace KaoyanPlanner.WPF.Services;

/// <summary>
/// 单实例锁：NamedPipe 对齐旧版 Python 的 QLocalServer（同名管道，与 PyQt exe 天然互斥）。
/// 管道名按版本区分（AppInfo）——个人版与安装包测试版互不冲突，两版可同时运行；
/// 同一版本内第二实例启动 → 连上管道发 "show" → 退出；主实例收到 "show" → OnWake（还原窗口）。
/// 处理完重新监听，后续第二实例仍能唤醒。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>管道名：按版本隔离（个人版 / 测试版各用各的管道）。</summary>
    public static string PipeName => AppInfo.SingleInstancePipeName;

    private readonly Action _onWake;
    private NamedPipeServerStream? _server;
    private bool _disposed;

    public SingleInstance(Action onWake) => _onWake = onWake;

    /// <summary>尝试成为主实例。返回 true=本实例继续运行，false=已有实例（本实例应退出）。</summary>
    public bool TryAcquire()
    {
        // 第二实例探测：能连上说明已有主实例在跑 → 发唤醒信号后退出
        try
        {
            using var probe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            probe.Connect(300);
            Write(probe, "show");
            return false;
        }
        catch (TimeoutException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var server = CreateServer();
        if (server is null) return false;
        try
        {
            server.BeginWaitForConnection(OnConnected, server);
            _server = server;
            return true;
        }
        catch (IOException)
        {
            // 监听冲突：已有实例持有 → 不重复启动
            server.Dispose();
            return false;
        }
    }

    private static NamedPipeServerStream? CreateServer()
    {
        try
        {
            return new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void OnConnected(IAsyncResult ar)
    {
        var server = (NamedPipeServerStream)ar.AsyncState!;
        try { server.EndWaitForConnection(ar); }
        catch (IOException) { server.Dispose(); ReArm(); return; }
        catch (ObjectDisposedException) { return; }

        try
        {
            var buf = new byte[64];
            int n = server.Read(buf, 0, buf.Length);
            if (n > 0 && Encoding.ASCII.GetString(buf, 0, n).Contains("show"))
            {
                try { Write(server, "ok"); } catch (IOException) { }
                try { _onWake(); } catch { /* 唤醒失败不应拖垮主进程（历史崩溃：null this 委托） */ }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            server.Dispose();
            ReArm();
        }
    }

    /// <summary>处理完一个第二实例后重新监听，后续启动仍能唤醒（对齐 Python 信号槽持续接收）。</summary>
    private void ReArm()
    {
        if (_disposed) return;
        var next = CreateServer();
        if (next is null) return;
        try
        {
            next.BeginWaitForConnection(OnConnected, next);
            _server = next;
        }
        catch (IOException)
        {
            next.Dispose();
        }
    }

    private static void Write(NamedPipeClientStream s, string text)
    {
        byte[] b = Encoding.ASCII.GetBytes(text);
        s.Write(b, 0, b.Length);
        s.Flush();
    }

    private static void Write(NamedPipeServerStream s, string text)
    {
        byte[] b = Encoding.ASCII.GetBytes(text);
        s.Write(b, 0, b.Length);
        s.Flush();
    }

    public void Dispose()
    {
        _disposed = true;
        _server?.Dispose();
    }
}
