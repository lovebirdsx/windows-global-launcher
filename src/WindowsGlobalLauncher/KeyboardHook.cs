using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;

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
    /// 可配置窗口动作热键（WH_KEYBOARD_LL）+ 框选选中态的全局 Esc/Del。
    /// Alt+Tab 已由 <see cref="AltTabHook"/> 专用钩子接管，本类不再处理。
    /// 钩子安装在 WPF UI 线程上，回调运行在 UI 线程；回调本体只做轻量判定，
    /// 实际 UI 操作由订阅方通过 Dispatcher.BeginInvoke 异步执行，避免触发系统钩子超时。
    /// 为缩短钩链、降低系统判定超时（LowLevelHooksTimeout）的概率，本钩子采用条件安装：
    /// 没有任何动作绑定、且框选选中态守卫未订阅时，不安装钩子。
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

        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;   // Alt
        private const int VK_ESCAPE = 0x1B;
        private const int VK_DELETE = 0x2E;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_RSHIFT = 0xA1;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_LMENU = 0xA4;
        private const int VK_RMENU = 0xA5;

        #endregion

        private Func<bool>? _shouldCancelSelectionOnEscape;
        /// <summary>由 PinWindow 提供：当前是否需要在框选选中态下用全局 Esc 取消选中（非切换器激活时）。</summary>
        public Func<bool>? ShouldCancelSelectionOnEscape
        {
            get => _shouldCancelSelectionOnEscape;
            set
            {
                _shouldCancelSelectionOnEscape = value;
                RefreshInstallState();
            }
        }

        /// <summary>取消框选选中（由订阅方在 UI 线程安全执行，须轻量）。</summary>
        public Action? CancelSelection { get; set; }

        private Func<bool>? _shouldDeleteSelection;
        /// <summary>由 PinWindow 提供：当前是否需要在框选选中态下用全局 Del 删除选中贴图（非切换器激活时）。</summary>
        public Func<bool>? ShouldDeleteSelection
        {
            get => _shouldDeleteSelection;
            set
            {
                _shouldDeleteSelection = value;
                RefreshInstallState();
            }
        }

        /// <summary>删除框选选中的贴图（由订阅方在 UI 线程安全执行，须轻量）。</summary>
        public Action? DeleteSelection { get; set; }

        // 可配置的「热键 → 动作」绑定表（整体替换，仅在 UI 线程读写，与钩子回调同线程，无需加锁）
        private IReadOnlyList<HotKeyActionBinding> _actionBindings = [];

        /// <summary>整体替换动作绑定表（配置热更新时调用，须在 UI 线程）。</summary>
        public void SetActionBindings(IReadOnlyList<HotKeyActionBinding>? bindings)
        {
            _actionBindings = bindings ?? [];
            Logger.LogInfo($"动作热键绑定已更新，共 {_actionBindings.Count} 条");
            RefreshInstallState();
        }

        #region 按键录制（快捷键设置窗口用）

        private Action<int, bool, bool, bool, bool>? _recordingCallback;

        // 录制期间仍处于按下状态的非修饰键（keydown 时不存在才回调，用于抑制自动重复）
        private readonly HashSet<int> _recordingDownKeys = [];

        // 录制期间各修饰键族（Shift/Ctrl/Alt/Win）按下的物理键数量（左右变体分别计数）
        private readonly Dictionary<int, int> _recordingModifierHeld = [];

        /// <summary>是否处于按键录制模式。</summary>
        public bool IsRecordingKey => _recordingCallback != null;

        /// <summary>
        /// 进入按键录制模式：此后所有非注入按键（含 keyup，避免前台收到不配对的松键）都被吞掉，
        /// 非修饰键的首次按下以 (vk, ctrl, alt, shift, win) 回调给订阅方；录制期间不触发动作绑定。
        /// <para>
        /// 回调运行在钩子线程（UI 线程）内、钩子栈上：订阅方必须只做 Dispatcher.BeginInvoke 转发，
        /// 绝不能在回调里同步调用 <see cref="EndKeyRecording"/>——那会触发钩子卸载/重装，属重入风险。
        /// </para>
        /// </summary>
        public void BeginKeyRecording(Action<int, bool, bool, bool, bool> onKeyDown)
        {
            _recordingCallback = onKeyDown ?? throw new ArgumentNullException(nameof(onKeyDown));
            _recordingDownKeys.Clear();

            // 快照播种：用户可能先按住修饰键再点「录制」，此时钩子还没见过这些按下事件
            _recordingModifierHeld.Clear();
            SeedModifierSnapshot();

            RefreshInstallState();
            Logger.LogInfo("键盘钩子进入按键录制模式（所有按键将被吞掉）");
        }

        /// <summary>退出按键录制模式，恢复正常热键判定。须在 UI 线程、且不在钩子回调栈上调用。</summary>
        public void EndKeyRecording()
        {
            if (_recordingCallback == null)
                return;

            _recordingCallback = null;
            _recordingDownKeys.Clear();
            _recordingModifierHeld.Clear();
            RefreshInstallState();
            Logger.LogInfo("键盘钩子退出按键录制模式");
        }

        private void SeedModifierSnapshot()
        {
            CountModifierIfPressed(VK_LSHIFT);
            CountModifierIfPressed(VK_RSHIFT);
            CountModifierIfPressed(VK_LCONTROL);
            CountModifierIfPressed(VK_RCONTROL);
            CountModifierIfPressed(VK_LMENU);
            CountModifierIfPressed(VK_RMENU);
            CountModifierIfPressed(VK_LWIN);
            CountModifierIfPressed(VK_RWIN);
        }

        private void CountModifierIfPressed(int vk)
        {
            if (IsKeyPressed(vk))
                AddModifierHeld(vk);
        }

        /// <summary>把修饰键的左右变体/通用码归一到族码（Shift/Ctrl/Alt/Win）；非修饰键返回 0。</summary>
        private static int ModifierFamily(int vk) => vk switch
        {
            VK_SHIFT or VK_LSHIFT or VK_RSHIFT => VK_SHIFT,
            VK_CONTROL or VK_LCONTROL or VK_RCONTROL => VK_CONTROL,
            VK_MENU or VK_LMENU or VK_RMENU => VK_MENU,
            VK_LWIN or VK_RWIN => VK_LWIN,
            _ => 0,
        };

        private void AddModifierHeld(int vk)
        {
            int family = ModifierFamily(vk);
            _recordingModifierHeld[family] = _recordingModifierHeld.GetValueOrDefault(family) + 1;
        }

        private void ReleaseModifierHeld(int vk)
        {
            int family = ModifierFamily(vk);
            int count = _recordingModifierHeld.GetValueOrDefault(family) - 1;
            if (count > 0)
                _recordingModifierHeld[family] = count;
            else
                _recordingModifierHeld.Remove(family);
        }

        private bool IsModifierHeld(int family) => _recordingModifierHeld.GetValueOrDefault(family) > 0;

        /// <summary>录制模式下的按键处理：更新按下状态，非修饰键首次按下时回调。调用方负责吞键。</summary>
        private void HandleRecordingEvent(int vk, bool isKeyDown)
        {
            if (ModifierFamily(vk) != 0)
            {
                if (isKeyDown)
                    AddModifierHeld(vk);
                else
                    ReleaseModifierHeld(vk);
                return; // 修饰键本身不是录制对象
            }

            if (isKeyDown)
            {
                // 自动重复：同一主键未松开前重复的 keydown 直接忽略
                if (!_recordingDownKeys.Add(vk))
                    return;

                try
                {
                    _recordingCallback?.Invoke(vk,
                        IsModifierHeld(VK_CONTROL),
                        IsModifierHeld(VK_MENU),
                        IsModifierHeld(VK_SHIFT),
                        IsModifierHeld(VK_LWIN));
                }
                catch (Exception ex)
                {
                    Logger.LogError("按键录制回调异常，已自动结束录制", ex);
                    // 不能在钩子栈上同步卸载钩子，延迟到 dispatcher 上下文执行
                    Dispatcher.CurrentDispatcher.BeginInvoke(new Action(EndKeyRecording));
                }
            }
            else
            {
                _recordingDownKeys.Remove(vk);
            }
        }

        #endregion

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

        /// <summary>条件安装/卸载：有动作绑定、录制态或选中态守卫时安装，否则卸载，缩短全局钩链。</summary>
        private void RefreshInstallState()
        {
            bool needHook = _actionBindings.Count > 0
                || _recordingCallback != null
                || ShouldCancelSelectionOnEscape != null
                || ShouldDeleteSelection != null;

            if (needHook && _hookId == IntPtr.Zero)
            {
                Install();
            }
            else if (!needHook && _hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
                Logger.LogInfo("键盘钩子已卸载（无绑定/守卫，缩短钩链）");
            }
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vk = (int)data.vkCode;

                bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;

                // 本程序自身注入的按键（模拟粘贴、媒体键、Shift+F10、Alt 解锁、掩码键等）直接透传、
                // 不参与任何判定：注入序列若参与绑定匹配会命中用户自定义绑定造成递归/误触发。
                if ((data.flags & 0x10u) != 0) // LLKHF_INJECTED
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);

                // 按键录制模式：吞掉一切非注入按键（含 keyup），只把非修饰键的首次按下回调出去。
                // 必须放在动作绑定匹配之前：否则录制时按到已有绑定的组合会真的触发动作。
                if (_recordingCallback != null)
                {
                    HandleRecordingEvent(vk, isKeyDown);
                    return (IntPtr)1;
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

                // 框选选中态下的全局 Esc：取消选中（空白处按 Esc 也能取消）。
                if (isKeyDown && vk == VK_ESCAPE && ShouldCancelSelectionOnEscape?.Invoke() == true)
                {
                    CancelSelection?.Invoke();
                    return (IntPtr)1;
                }

                // 框选选中态下的全局 Del：删除选中的贴图/文字便签（含多选）。仅裸 Del 触发。
                if (isKeyDown && vk == VK_DELETE
                    && !IsKeyPressed(VK_CONTROL) && !IsKeyPressed(VK_MENU) && !IsKeyPressed(VK_SHIFT)
                    && !IsKeyPressed(VK_LWIN) && !IsKeyPressed(VK_RWIN)
                    && ShouldDeleteSelection?.Invoke() == true)
                {
                    DeleteSelection?.Invoke();
                    return (IntPtr)1;
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

            // 兜底：进程退出/钩子释放时录制态不得残留
            _recordingCallback = null;
            _recordingDownKeys.Clear();
            _recordingModifierHeld.Clear();

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
