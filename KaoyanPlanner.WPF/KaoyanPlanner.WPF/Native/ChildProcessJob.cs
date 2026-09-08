using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KaoyanPlanner.WPF.Native;

/// <summary>
/// 子进程「随父同死」作业对象（KILL_ON_JOB_CLOSE）。
///
/// 解决的问题：TtsService 用 cmd /c 拉起本地 GPT-SoVITS/GSVI 推理服务（python，占 9880 端口与显存）。
/// 正常退出时 App.OnExit 会 KillProc；但一旦 PetPlanner 被任务管理器/Stop-Process 强杀、崩溃或异常断电式退出，
/// 托管 OnExit 根本来不及执行，python 服务就成了孤儿，继续占着 9880 端口——下次启动要么连到半死的旧实例、
/// 要么新实例 bind 失败退出，气泡报「线路占用 / 端口被占」。
///
/// 把拉起的子进程 Assign 进作业后：父进程句柄随进程死亡被内核关闭的瞬间，Windows 会杀掉作业内全部进程
/// （cmd 及其 python 孙进程整棵树），无需父进程跑任何托管代码。Win8+ 支持嵌套作业，分配失败静默降级
/// （退回原来的显式 Kill 路径），不影响主流程。
/// </summary>
internal sealed class ChildProcessJob : IDisposable
{
    // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE：作业最后一个句柄关闭时杀掉作业内所有进程
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;

    private IntPtr _handle;

    public ChildProcessJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) return;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };
        SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref info,
            Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        // 句柄一直留到本类 Dispose（随 TtsService 生命周期，进程退出时由 OS 兜底关闭→整棵树被杀）
    }

    /// <summary>把子进程纳入作业。失败返回 false（嵌套作业受限等），调用方按旧路径兜底即可。</summary>
    public bool Assign(Process process)
    {
        if (_handle == IntPtr.Zero || process is null) return false;
        try
        {
            return AssignProcessToJobObject(_handle, process.Handle);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        IntPtr h = System.Threading.Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (h != IntPtr.Zero) CloseHandle(h);   // 关闭即杀作业内全部进程（正常退出路径的双保险）
    }

    // ------------------------------------------------------------ P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public IntPtr PerProcessUserTimeLimit;
        public IntPtr PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION jobObjectInfo, int jobObjectInformationLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
