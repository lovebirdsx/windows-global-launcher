using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CommandLauncher
{
    /// <summary>
    /// Alt+Tab 专用低级键盘钩子（WH_KEYBOARD_LL）。
    /// 与通用 KeyboardHook 分离：回调路径极短（命中即吞，未命中立即 CallNextHookEx），
    /// 最大限度降低系统判定钩子超时（LowLevelHooksTimeout）而把我们踢出钩链的概率。
    /// 只在 UI 线程安装/卸载，回调运行在 UI 线程；实际 UI 操作由订阅方 Dispatcher.BeginInvoke 异步执行。
    /// </summary>
    public class AltTabHook : IDisposable
    {
        #region Win32 P/Invoke

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const int VK_TAB = 0x09;
        private const int VK_MENU = 0x12;   // Alt
        private const int VK_LMENU = 0xA4;
        private const int VK_RMENU = 0xA5;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_UP = 0x26;
        private const int VK_DOWN = 0x28;
        private const int VK_LEFT = 0x25;
        private const int VK_RIGHT = 0x27;
        private const int VK_J = 0x4A;
        private const int VK_K = 0x4B;
        private const int VK_N = 0x4E;
        private const int VK_P = 0x50;
        private const int VK_X = 0x58;

        #endregion

        /// <summary>按下 Alt+Tab（参数为是否同时按住 Shift，表示反向）。</summary>
        public event Action<bool>? AltTab;

        /// <summary>松开 Alt，确认当前选中窗口。</summary>
        public event Action? Commit;

        /// <summary>切换器激活态下按下 Esc，取消切换。</summary>
        public event Action? Cancel;

        /// <summary>切换器激活态下用方向键 / j,k,p,n 移动选择（-1 上，+1 下）。</summary>
        public event Action<int>? Navigate;

        /// <summary>切换器激活态下按 x，关闭当前选中窗口。</summary>
        public event Action? Close;

        /// <summary>切换器激活态下按左/右方向键，将选中窗口移到相邻显示器（-1 左，+1 右）。</summary>
        public event Action<int>? MoveMonitor;

        /// <summary>由切换器提供：当前切换器是否处于激活态（决定是否吞掉 Esc / 触发 Commit）。</summary>
        public Func<bool>? IsSwitcherActive { get; set; }

        private readonly LowLevelKeyboardProc _proc; // 字段强引用，防止委托被 GC 回收
        private IntPtr _hookId = IntPtr.Zero;
        private bool _disposed;

        // Alt 物理按下状态（仅跟踪非注入事件，注入事件直接透传）。
        // 不用 GetAsyncKeyState：钩子回调在按键真正进入系统队列前触发，
        // GetAsyncKeyState 在极端时序下可能读到旧状态；自己跟踪最可靠且零 P/Invoke 开销。
        private bool _altDown;

        public AltTabHook()
        {
            _proc = HookProc;
        }

        public void Install()
        {
            if (_hookId != IntPtr.Zero)
                return;

            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hookId == IntPtr.Zero)
                Logger.LogError("安装 Alt+Tab 专用钩子失败", new Win32Exception(Marshal.GetLastWin32Error()));
            else
                Logger.LogInfo("Alt+Tab 专用钩子安装成功");
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
                return CallNextHookEx(_hookId, nCode, wParam, lParam);

            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            // 注入事件（包括本程序模拟的 Alt 击发、Shift+F10 等）直接透传，不更新状态也不参与判定
            if ((data.flags & 0x10u) != 0) // LLKHF_INJECTED
                return CallNextHookEx(_hookId, nCode, wParam, lParam);

            int msg = (int)wParam;
            int vk = (int)data.vkCode;

            // 跟踪 Alt 按下/松开
            if (vk == VK_MENU || vk == VK_LMENU || vk == VK_RMENU)
            {
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                if (isDown)
                {
                    _altDown = true;
                }
                else if (isUp)
                {
                    bool wasDown = _altDown;
                    _altDown = false;
                    if (wasDown && IsSwitcherActive?.Invoke() == true)
                        Commit?.Invoke();
                }
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            // Tab：Alt 按住时触发切换并吞掉（阻止系统原生 Alt+Tab）
            if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && vk == VK_TAB && _altDown)
            {
                // 自愈：若钩子曾错过 Alt 松开（系统超时踢出钩链、安全桌面切换等），
                // _altDown 会粘滞为 true；这里用 GetAsyncKeyState 读真实物理状态校验，不一致则复位。
                if ((GetAsyncKeyState(VK_MENU) & 0x8000) == 0)
                {
                    _altDown = false;
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                // Shift 状态无法可靠从钩子链自身跟踪（Shift+Tab 是常见路径，但 Shift 也可单独按下松开），
                // 这里用 GetAsyncKeyState 是唯一需要外部状态的地方；Alt+Tab 场景下 Shift 与 Tab 几乎同时按下，
                // 时序窗口极小，实测可靠。若未来出现 Shift 状态误判，可改为跟踪 Shift 键的按下/松开。
                bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0; // VK_SHIFT
                AltTab?.Invoke(shift);
                return (IntPtr)1; // 吞掉
            }

            // 切换器激活态下的导航/关闭/移动显示器/Esc：吞掉并派发
            if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && IsSwitcherActive?.Invoke() == true)
            {
                switch (vk)
                {
                    case VK_UP:
                    case VK_K:
                    case VK_P:
                        Navigate?.Invoke(-1);
                        return (IntPtr)1;
                    case VK_DOWN:
                    case VK_J:
                    case VK_N:
                        Navigate?.Invoke(1);
                        return (IntPtr)1;
                    case VK_X:
                        Close?.Invoke();
                        return (IntPtr)1;
                    case VK_LEFT:
                        MoveMonitor?.Invoke(-1);
                        return (IntPtr)1;
                    case VK_RIGHT:
                        MoveMonitor?.Invoke(1);
                        return (IntPtr)1;
                    case VK_ESCAPE:
                        Cancel?.Invoke();
                        return (IntPtr)1;
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        public void Dispose()
        {
            if (_disposed)
                return;

            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
                Logger.LogInfo("Alt+Tab 专用钩子已卸载");
            }
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        ~AltTabHook()
        {
            Dispose();
        }
    }
}
