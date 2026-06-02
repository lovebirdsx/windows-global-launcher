using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CommandLauncher
{
    /// <summary>
    /// 以「普通用户 / 中等完整性级别」启动子进程。
    ///
    /// launcher 自身以管理员（高完整性）运行，直接 Process.Start 出的子进程会继承
    /// 管理员令牌。这里借用桌面 Shell（explorer.exe）的令牌，通过 CreateProcessWithTokenW
    /// 创建进程，使子进程等同于用户在桌面双击启动的普通权限。
    ///
    /// 该路径等价于 UseShellExecute=false 的直接 CreateProcess，不支持 URL / 文档关联启动。
    /// </summary>
    public static class MediumIntegrityProcess
    {
        #region Win32 P/Invoke

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateTokenEx(
            IntPtr hExistingToken,
            uint dwDesiredAccess,
            IntPtr lpTokenAttributes,
            int ImpersonationLevel,
            int TokenType,
            out IntPtr phNewToken);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessWithTokenW(
            IntPtr hToken,
            uint dwLogonFlags,
            string? lpApplicationName,
            StringBuilder lpCommandLine,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        // OpenProcess 访问权限
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        // 令牌访问权限
        private const uint TOKEN_DUPLICATE = 0x0002;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
        private const uint TOKEN_ADJUST_DEFAULT = 0x0080;
        private const uint TOKEN_ADJUST_SESSIONID = 0x0100;

        private const uint TOKEN_DESIRED_ACCESS =
            TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY |
            TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID;

        // SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation
        private const int SecurityImpersonation = 2;

        // TOKEN_TYPE.TokenPrimary
        private const int TokenPrimary = 1;

        // CreateProcessWithTokenW dwLogonFlags
        private const uint LOGON_WITH_PROFILE = 0x00000001;

        #endregion

        /// <summary>
        /// 借用 explorer 令牌，以普通用户权限启动子进程。失败时抛出异常（由上层报错、不启动）。
        /// </summary>
        public static void Start(string fileName, string arguments, string workingDirectory)
        {
            // 1. 找到桌面 Shell（explorer.exe）窗口，进而拿到它的进程
            IntPtr shellWindow = GetShellWindow();
            if (shellWindow == IntPtr.Zero)
            {
                throw new InvalidOperationException("无法获取桌面 Shell 窗口（explorer 可能未运行），无法以普通权限启动。");
            }

            GetWindowThreadProcessId(shellWindow, out uint shellPid);
            if (shellPid == 0)
            {
                throw new InvalidOperationException("无法获取桌面 Shell 进程 ID，无法以普通权限启动。");
            }

            IntPtr hProcess = IntPtr.Zero;
            IntPtr hToken = IntPtr.Zero;
            IntPtr hPrimaryToken = IntPtr.Zero;
            var pi = new PROCESS_INFORMATION();

            try
            {
                // 2. 打开 explorer 进程并获取其令牌
                hProcess = OpenProcess(PROCESS_QUERY_INFORMATION, false, shellPid);
                if (hProcess == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "打开 explorer 进程失败。");
                }

                if (!OpenProcessToken(hProcess, TOKEN_DESIRED_ACCESS, out hToken))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "打开 explorer 进程令牌失败。");
                }

                // 3. 复制为可用于创建进程的主令牌
                if (!DuplicateTokenEx(hToken, TOKEN_DESIRED_ACCESS, IntPtr.Zero,
                        SecurityImpersonation, TokenPrimary, out hPrimaryToken))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "复制 explorer 令牌失败。");
                }

                // 4. 拼接命令行（可执行路径加引号以容忍空格），用该令牌创建进程
                var commandLine = new StringBuilder();
                commandLine.Append('"').Append(fileName).Append('"');
                if (!string.IsNullOrEmpty(arguments))
                {
                    commandLine.Append(' ').Append(arguments);
                }

                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf<STARTUPINFO>();

                string? currentDirectory = string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory;

                if (!CreateProcessWithTokenW(
                        hPrimaryToken,
                        LOGON_WITH_PROFILE,
                        null,
                        commandLine,
                        0,
                        IntPtr.Zero,
                        currentDirectory,
                        ref si,
                        out pi))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "以普通权限创建进程失败。");
                }
            }
            finally
            {
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                if (hPrimaryToken != IntPtr.Zero) CloseHandle(hPrimaryToken);
                if (hToken != IntPtr.Zero) CloseHandle(hToken);
                if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
            }
        }
    }
}
