using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CommandLauncher
{
    /// <summary>
    /// 一条「热键 → 动作」绑定：主键 + 修饰键组合命中时触发 Callback 并吞掉按键。
    /// 修饰键为精确匹配（配置 Alt+Q 时，Alt+Shift+Q 不会触发），避免组合键互相干扰。
    /// Callback 运行在钩子回调线程（有 LowLevelHooksTimeout 限制），必须轻量，
    /// 实际动作应由订阅方通过 Dispatcher.BeginInvoke 异步执行。
    /// </summary>
    public sealed class HotKeyActionBinding
    {
        public int VirtualKey { get; set; }
        public bool Ctrl { get; set; }
        public bool Alt { get; set; }
        public bool Shift { get; set; }
        public bool Win { get; set; }
        public required Action Callback { get; set; }

        /// <summary>判定按键是否与绑定匹配（vk 相等且修饰键精确一致）。纯函数，便于单测。</summary>
        public bool Matches(int vk, bool ctrl, bool alt, bool shift, bool win)
            => vk == VirtualKey && ctrl == Ctrl && alt == Alt && shift == Shift && win == Win;
    }

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
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const uint KEYEVENTF_KEYUP = 0x0002;
        // 未映射的虚拟键，用作掩码键：Win 组合键的主键被吞掉后，系统只看到 Win 按下+松开会弹出开始菜单，
        // 注入一次该键让系统认为 Win 按住期间有其它按键发生（与 AutoHotkey 的 mask key 做法相同）。
        private const byte VK_MASK = 0xFF;

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
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
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
        private const int VK_X = 0x58;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
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

        /// <summary>由 PinWindow 提供：当前是否需要在框选选中态下用全局 Esc 取消选中（非切换器激活时）。</summary>
        public Func<bool>? ShouldCancelSelectionOnEscape { get; set; }

        /// <summary>取消框选选中（由订阅方在 UI 线程安全执行，须轻量）。</summary>
        public Action? CancelSelection { get; set; }

        // 可配置的「热键 → 动作」绑定表（整体替换，仅在 UI 线程读写，与钩子回调同线程，无需加锁）
        private IReadOnlyList<HotKeyActionBinding> _actionBindings = [];

        /// <summary>整体替换动作绑定表（配置热更新时调用，须在 UI 线程）。</summary>
        public void SetActionBindings(IReadOnlyList<HotKeyActionBinding>? bindings)
        {
            _actionBindings = bindings ?? [];
            Logger.LogInfo($"动作热键绑定已更新，共 {_actionBindings.Count} 条");
        }

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

                // 可配置的动作热键（如 Alt+Q 关闭前台窗口）：修饰键精确匹配，命中即吞掉
                if (isKeyDown && _actionBindings.Count > 0)
                {
                    bool ctrl = IsKeyPressed(VK_CONTROL);
                    bool alt = IsKeyPressed(VK_MENU);
                    bool shift = IsKeyPressed(VK_SHIFT);
                    bool win = IsKeyPressed(VK_LWIN) || IsKeyPressed(VK_RWIN);

                    foreach (var binding in _actionBindings)
                    {
                        if (binding.Matches(vk, ctrl, alt, shift, win))
                        {
                            if (binding.Win)
                                SendMaskKey();
                            binding.Callback();
                            return (IntPtr)1;
                        }
                    }
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

                // 框选选中态下、切换器未激活时的全局 Esc：取消选中（空白处按 Esc 也能取消）。
                // 副作用（已接受）：选中态是长期状态，期间前台是外部应用/桌面时的 Esc 都会被吞，
                // 用户点空白即取消、Esc 立即恢复透传。ShouldCancelSelectionOnEscape 已排除编辑态
                // （编辑中的 Esc 须留给贴图 TextBox 的 PreviewKeyDown 取消编辑）与本进程前台窗口
                // （命令面板/剪贴板历史/截图遮罩/框选遮罩等的 Esc 交给其自身窗口级处理）。
                if (isKeyDown && vk == VK_ESCAPE && !active && ShouldCancelSelectionOnEscape?.Invoke() == true)
                {
                    CancelSelection?.Invoke();
                    return (IntPtr)1; // 吞掉，避免 Esc 落入前台应用
                }

                if (isKeyUp && (vk == VK_MENU || vk == VK_LMENU || vk == VK_RMENU) && active)
                {
                    Commit?.Invoke();
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private static bool IsKeyPressed(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

        /// <summary>注入一次无映射的掩码键，避免 Win 组合键被吞后松开 Win 弹出开始菜单。</summary>
        private static void SendMaskKey()
        {
            keybd_event(VK_MASK, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MASK, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

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
