using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CommandLauncher
{
    /// <summary>
    /// 把子进程挂入带 KILL_ON_JOB_CLOSE 的 Windows Job 对象，保证本进程（launcher）以任何方式退出
    /// （含 scripts/publish.ps1 的 Stop-Process -Force 强杀、崩溃、任务管理器结束）时，内核自动终止挂入的子进程。
    ///
    /// 背景：RapidOCR-json.exe 以常驻子进程方式运行，只有 App.OnExit 优雅退出才会 Shutdown() 杀掉它；
    /// 主程序被强杀 / 崩溃时子进程成为孤儿并累积。挂入 Job 后由内核兜底清理，不依赖任何托管清理代码。
    ///
    /// 兼容性：Win8+ 支持嵌套 Job（父进程已在 Job 中时仍可创建子 Job 并挂入子进程）；
    /// AssignProcessToJobObject 失败（如子进程已在不可嵌套的 Job 中）时只降级记 WARN，由启动时的孤儿清扫兜底。
    /// </summary>
    public static class ChildProcessJob
    {
        // JobObjectExtendedLimitInformation 信息类值（JOBOBJECTINFOCLASS 枚举）
        private const int JobObjectExtendedLimitInformation = 9;

        // 进程退出（句柄关闭）时内核自动终止 Job 内所有进程
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        // 懒初始化保护 + 失败状态缓存（_initialized 一旦置位即不再重试，失败时 _jobHandle 保持 IntPtr.Zero）
        private static readonly object _lock = new();
        private static bool _initialized;
        private static IntPtr _jobHandle;

        /// <summary>
        /// 把指定进程挂入本进程的 Job 对象。成功返回 true；任何失败记 WARN 并返回 false，绝不抛异常
        /// （调用方把失败视为降级：孤儿由下次启动的清扫兜底）。
        /// </summary>
        public static bool TryAssign(Process process)
        {
            try
            {
                IntPtr job = EnsureJob();
                if (job == IntPtr.Zero)
                    return false;

                if (!AssignProcessToJobObject(job, process.Handle))
                {
                    Logger.LogWarning($"将进程 {process.Id} 挂入 Job 对象失败（Win32 错误码 {Marshal.GetLastWin32Error()}），孤儿由下次启动清扫兜底");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"将进程挂入 Job 对象失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>懒创建 Job 句柄（进程内唯一、故意永不关闭）。创建失败记 WARN 并缓存失败状态，不再重试。</summary>
        private static IntPtr EnsureJob()
        {
            lock (_lock)
            {
                if (_initialized)
                    return _jobHandle;

                // 先置位再尝试：无论成败都只试一次，失败后不再重试
                _initialized = true;

                try
                {
                    // Job 句柄故意永不关闭：进程退出时系统关闭句柄，恰好触发 KILL_ON_JOB_CLOSE，
                    // 由内核终止所有挂入的子进程（若在此主动 CloseHandle 会立刻杀掉它们，且失去兜底意义）
                    IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
                    if (job == IntPtr.Zero)
                    {
                        Logger.LogWarning($"创建 Job 对象失败（Win32 错误码 {Marshal.GetLastWin32Error()}），子进程孤儿将由下次启动清扫兜底");
                        return IntPtr.Zero;
                    }

                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                    {
                        BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                        {
                            LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                        },
                    };

                    int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)size))
                    {
                        Logger.LogWarning($"设置 Job 对象 KILL_ON_JOB_CLOSE 失败（Win32 错误码 {Marshal.GetLastWin32Error()}），子进程孤儿将由下次启动清扫兜底");
                        return IntPtr.Zero;
                    }

                    _jobHandle = job;
                    return job;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"创建 Job 对象失败：{ex.Message}");
                    return IntPtr.Zero;
                }
            }
        }

        #region Win32 P/Invoke

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            IntPtr hJob,
            int jobObjectInformationClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation,
            uint cbJobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        // JOBOBJECT_BASIC_LIMIT_INFORMATION：内含 SIZE_T / ULONG_PTR 尺寸字段（MinimumWorkingSetSize 等），
        // x64 下为 8 字节，必须用 UIntPtr 表达，否则结构体大小与本地布局不符
        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        // IO_COUNTERS：6 个 ULONGLONG
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
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        #endregion
    }
}
