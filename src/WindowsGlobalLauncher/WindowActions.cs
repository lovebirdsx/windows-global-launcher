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

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const byte VK_VOLUME_MUTE = 0xAD;
        private const byte VK_VOLUME_DOWN = 0xAE;
        private const byte VK_VOLUME_UP = 0xAF;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        /// <summary>全部可用动作（动作名不区分大小写）。</summary>
        public static readonly IReadOnlyDictionary<string, Action> All =
            new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase)
            {
                ["CloseWindow"] = CloseForegroundWindow,
                ["VolumeUp"] = () => PressMediaKey(VK_VOLUME_UP),
                ["VolumeDown"] = () => PressMediaKey(VK_VOLUME_DOWN),
                ["ToggleMute"] = () => PressMediaKey(VK_VOLUME_MUTE),
                ["ShowClipboardHistory"] = () => App.ClipboardHistoryWindow?.ShowHistory(),
                ["Screenshot"] = ScreenshotManager.StartCapture,
                ["PinClipboard"] = ScreenshotManager.PinFromClipboard,
                ["TogglePinVisibility"] = PinWindow.ToggleAllVisibility, // 切换所有贴图（图片贴图与文字便签）的显示/隐藏
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

        /// <summary>模拟一次多媒体键点击（增大/减小音量、静音），等同按键盘上的对应媒体键。</summary>
        private static void PressMediaKey(byte vk)
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }
}
