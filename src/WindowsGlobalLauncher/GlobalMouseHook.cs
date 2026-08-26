using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CommandLauncher
{
    /// <summary>
    /// 低级鼠标钩子（WH_MOUSE_LL），用于「框选选中态下点击空白处取消选中」——贴图是
    /// ShowActivated=false 弹出的、无键盘焦点，窗口级事件收不到「点空白」，只能靠全局钩子监听。
    /// 在 WPF UI 线程上安装（与 KeyboardHook 同一机制），钩子回调即运行在 UI 线程。
    /// 刻意做成懒加载常驻（第一次框选时安装、进程结束自动清理），而不是随选中态动态
    /// 安装/卸载：回调在非左键按下或未选中时只做一次布尔判断即返回，开销可忽略，
    /// 免去安装/卸载的边界状态。
    /// 回调**不吞鼠标**——点空白照常落在桌面/下层窗口，本钩子只旁路地取消选中。
    /// </summary>
    internal sealed class GlobalMouseHook : IDisposable
    {
        #region Win32 P/Invoke

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        // MSLLHOOKSTRUCT：pt 为屏幕物理像素坐标（与 Cursor.Position、PhysicalBounds 同坐标系）。
        // dwExtraInfo 是 ULONG_PTR（指针宽度），必须声明为 IntPtr（x64 下 8 字节）；LayoutKind.Sequential
        // 会自动在它前面补齐 padding，保证结构体总大小与 Win32 一致。
        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public System.Drawing.Point pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;

        #endregion

        private readonly LowLevelMouseProc _proc; // 字段强引用，防止委托被 GC 回收导致钩子失效
        private IntPtr _hookId = IntPtr.Zero;
        private bool _disposed;

        public GlobalMouseHook()
        {
            _proc = HookProc;
        }

        public void Install()
        {
            if (_hookId != IntPtr.Zero)
                return;

            _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
            if (_hookId == IntPtr.Zero)
                Logger.LogError("安装鼠标钩子失败", new Win32Exception(Marshal.GetLastWin32Error()));
            else
                Logger.LogInfo("鼠标钩子安装成功");
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // 仅在「有框选选中」时才关心左键按下：其余消息（含高频鼠标移动）快速放行，
            // 避免常态拦截带来的性能开销与 LowLevelHooksTimeout 风险
            if (nCode >= 0 && (int)wParam == WM_LBUTTONDOWN && PinWindow.IsAnySelected)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                PinWindow.OnGlobalLeftButtonDown(data.pt);
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
                Logger.LogInfo("鼠标钩子已卸载");
            }
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        ~GlobalMouseHook()
        {
            Dispose();
        }
    }
}
