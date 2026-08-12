using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CommandLauncher
{
    /// <summary>
    /// 窗口动作注册表：动作名 → 实现，是「热键 → 窗口/系统动作」绑定的唯一注册点。
    /// 新增动作 = 在 All 字典加一个条目 + 实现一个方法，然后在配置文件 WindowActions 段引用动作名即可。
    /// 动作经由键盘钩子触发、由调用方 Dispatcher.BeginInvoke 到 UI 线程异步执行，这里可以做 SendMessage 级操作。
    /// </summary>
    public static class WindowActions
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        /// <summary>全部可用动作（动作名不区分大小写）。</summary>
        public static readonly IReadOnlyDictionary<string, Action> All =
            new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase)
            {
                ["CloseWindow"] = CloseForegroundWindow,
            };

        /// <summary>
        /// 关闭当前前台窗口（等同 Alt+F4）。复用 WindowEnumerator.CloseWindow
        /// （SendMessageTimeout + WM_CLOSE，优雅关闭且防卡死）。
        /// </summary>
        private static void CloseForegroundWindow()
        {
            var hwnd = GetForegroundWindow();
            if (hwnd != IntPtr.Zero)
                WindowEnumerator.CloseWindow(hwnd);
        }
    }
}
