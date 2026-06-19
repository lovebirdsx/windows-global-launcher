using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CommandLauncher
{
    /// <summary>
    /// 低级键盘钩子（WH_KEYBOARD_LL），用于接管系统 Alt+Tab。
    /// 在 WPF UI 线程上安装，钩子回调即运行在 UI 线程；回调本体只做轻量判定，
    /// 实际 UI 操作由订阅方通过 Dispatcher.BeginInvoke 异步执行，避免触发系统钩子超时。
    /// </summary>
    public class KeyboardHook : IDisposable
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

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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
        private const uint WM_SYSCOMMAND = 0x0112;
        private const int SC_CLOSE = 0xF060;

        private const int VK_TAB = 0x09;
        private const int VK_SHIFT = 0x10;
        private const int VK_MENU = 0x12;   // Alt
        private const int VK_ESCAPE = 0x1B;
        private const int VK_UP = 0x26;
        private const int VK_DOWN = 0x28;
        private const int VK_LEFT = 0x25;
        private const int VK_RIGHT = 0x27;
        private const int VK_J = 0x4A;
        private const int VK_K = 0x4B;
        private const int VK_N = 0x4E;
        private const int VK_P = 0x50;
        private const int VK_Q = 0x51;
        private const int VK_X = 0x58;
        private const int VK_LMENU = 0xA4;
        private const int VK_RMENU = 0xA5;

        #endregion

        /// <summary>按下 Alt+Tab（参数为是否同时按住 Shift，表示反向）。</summary>
        public event Action<bool>? AltTab;

        /// <summary>松开 Alt，确认当前选中窗口。</summary>
        public event Action? Commit;

        /// <summary>按下 Esc，取消切换。</summary>
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

        public KeyboardHook()
        {
            _proc = HookProc;
        }

        public void Install()
        {
            if (_hookId != IntPtr.Zero)
                return;

            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hookId == IntPtr.Zero)
                Logger.LogError("安装键盘钩子失败", new Win32Exception(Marshal.GetLastWin32Error()));
            else
                Logger.LogInfo("键盘钩子安装成功");
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vk = (int)data.vkCode;

                bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isKeyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                bool active = IsSwitcherActive?.Invoke() == true;

                if (isKeyDown && vk == VK_TAB && IsKeyPressed(VK_MENU))
                {
                    bool shift = IsKeyPressed(VK_SHIFT);
                    AltTab?.Invoke(shift);
                    return (IntPtr)1; // 吞掉，阻止系统原生 Alt+Tab
                }

                if (isKeyDown && vk == VK_Q && IsKeyPressed(VK_MENU))
                {
                    IntPtr hwnd = GetForegroundWindow();
                    if (hwnd != IntPtr.Zero)
                        PostMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_CLOSE, IntPtr.Zero);
                    return (IntPtr)1;
                }

                // 切换器激活态下的键盘导航（方向键 / j,k,p,n / Esc），一律吞掉
                if (active && isKeyDown)
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
                        case VK_ESCAPE:
                            Cancel?.Invoke();
                            return (IntPtr)1;
                        case VK_X:
                            Close?.Invoke();
                            return (IntPtr)1; // 吞掉，避免 x 落入目标窗口
                        case VK_LEFT:
                            MoveMonitor?.Invoke(-1);
                            return (IntPtr)1;
                        case VK_RIGHT:
                            MoveMonitor?.Invoke(1);
                            return (IntPtr)1;
                    }
                }

                if (isKeyUp && (vk == VK_MENU || vk == VK_LMENU || vk == VK_RMENU) && active)
                {
                    Commit?.Invoke();
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private static bool IsKeyPressed(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

        public void Dispose()
        {
            if (_disposed)
                return;

            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
                Logger.LogInfo("键盘钩子已卸载");
            }
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        ~KeyboardHook()
        {
            Dispose();
        }
    }
}
