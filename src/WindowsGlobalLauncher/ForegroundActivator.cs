using System;
using System.Runtime.InteropServices;

namespace CommandLauncher
{
    /// <summary>
    /// 把本进程的窗口强行切到前台的公共实现。
    /// <para>
    /// 本程序的弹出式窗口（命令启动器、剪贴板历史）都由全局热键唤出，
    /// 而热键路径上的输入并没有真正进入本进程的消息队列（低级键盘钩子转发 / WM_HOTKEY 经
    /// Dispatcher 异步派发），因此直接 <c>Activate</c>／<c>SetForegroundWindow</c> 会被
    /// 系统的「前台锁定」间歇性拒绝——表现为窗口弹出却拿不到焦点，或者刚显示就因失焦被自己隐藏。
    /// </para>
    /// <para>
    /// 这里集中实现 <see cref="WindowEnumerator"/>.Activate 同款的 AttachThreadInput 绕过技巧，
    /// 供各弹出窗口复用；各窗口自己保留「宽限期 + 重试次数」这类与交互相关的策略。
    /// </para>
    /// </summary>
    public static class ForegroundActivator
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam,
            IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        private const byte VK_MENU = 0x12;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private const uint WM_NULL = 0x0000;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const uint HungProbeTimeoutMs = 200;
        private const int ERROR_TIMEOUT = 1460;

        /// <summary>当前前台窗口句柄（弹出自己的窗口之前调用，用于记录「原本在前台的是谁」）。</summary>
        public static IntPtr GetForeground() => GetForegroundWindow();

        /// <summary>
        /// 尝试把 <paramref name="hwnd"/> 切到前台一次（不含重试，重试策略由调用方决定）。
        /// </summary>
        /// <param name="hwnd">要切到前台的本进程窗口。</param>
        /// <param name="previousForeground">
        /// 弹出本窗口之前记录的前台窗口。用它取线程做 AttachThreadInput，
        /// 而不是优先用此刻的 <c>GetForegroundWindow</c>——后者可能已经是我们自己，会附加错线程。
        /// 传 <see cref="IntPtr.Zero"/> 表示当时没有前台窗口，此时自动回退到此刻的前台窗口。
        /// </param>
        /// <param name="ownerName">仅用于日志的窗口名称，例如「命令启动器」。</param>
        /// <returns>是否成功切到前台。</returns>
        public static bool ForceForeground(IntPtr hwnd, IntPtr previousForeground, string ownerName)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            bool ok = false;
            try
            {
                uint thisThread = GetCurrentThreadId();

                // 附加目标优先用「弹出前的前台窗口」，取不到才回退到此刻的前台窗口。
                // previousForeground 为 0 是合法情况（记录那一刻系统没有前台窗口），照直用 0 就没有
                // 线程可附加，SetForegroundWindow 只能裸调、更容易被前台锁定拒绝。
                // 附加的目的只是借一个别的输入队列来解锁，用此刻的前台窗口同样成立，排除自己即可。
                IntPtr attachTarget = previousForeground;
                if (attachTarget == IntPtr.Zero || attachTarget == hwnd)
                {
                    IntPtr current = GetForegroundWindow();
                    if (current != hwnd)
                        attachTarget = current;
                }

                uint foreThread = attachTarget == IntPtr.Zero
                    ? 0
                    : GetWindowThreadProcessId(attachTarget, out _);

                // 附加前先探测目标线程是否挂起：AttachThreadInput 是共享输入队列，
                // 若目标窗口无响应，附加会把本线程（同时也是全局键盘钩子所在线程）一起拖死。
                bool hung = IsWindowHung(attachTarget);
                bool attached = false;
                try
                {
                    if (!hung && foreThread != 0 && foreThread != thisThread)
                        attached = AttachThreadInput(thisThread, foreThread, true);

                    if (hung)
                        Logger.LogWarning($"跳过 AttachThreadInput：{ownerName}要附加的前台窗口 {attachTarget} 无响应，改用兜底激活路径");

                    BringWindowToTop(hwnd);
                    ok = SetForegroundWindow(hwnd);

                    // 前台锁定仍拒绝时：模拟一次 Alt 击发解锁（经典 SetForegroundWindow 解锁手段），再重试一次
                    if (!ok)
                    {
                        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        ok = SetForegroundWindow(hwnd);
                    }

                    // 挂起、或压根没有可附加的窗口时，SetForegroundWindow 可能仍被拒绝，
                    // 改用不依赖输入队列附加的兜底 API 切到前台
                    if (!ok && (hung || foreThread == 0))
                    {
                        SwitchToThisWindow(hwnd, true);
                        ok = GetForegroundWindow() == hwnd;
                    }
                }
                finally
                {
                    // 严格与 attach 配对：异常路径也必须解除附加，否则本线程会永久处于 attached 状态
                    if (attached)
                        AttachThreadInput(thisThread, foreThread, false);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"激活{ownerName}失败: {ex.Message}");
            }

            return ok;
        }

        /// <summary>
        /// 探测窗口是否无响应（挂起）。WM_NULL 的返回值恒为 0，无法用返回值区分「处理成功」与「超时」，
        /// 故用 SetLastError + GetLastError == ERROR_TIMEOUT 判定；SMTO_ABORTIFHUNG 确保
        /// 目标挂起时尽快返回，最坏情况只阻塞 <see cref="HungProbeTimeoutMs"/> 毫秒。
        /// </summary>
        public static bool IsWindowHung(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            IntPtr result = SendMessageTimeout(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero,
                SMTO_ABORTIFHUNG, HungProbeTimeoutMs, out _);
            if (result != IntPtr.Zero)
                return false; // 消息被处理且返回非零 → 目标响应正常

            return Marshal.GetLastWin32Error() == ERROR_TIMEOUT;
        }
    }
}
