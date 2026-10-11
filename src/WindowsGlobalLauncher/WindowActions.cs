using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace CommandLauncher
{
    /// <summary>
    /// 窗口动作描述符：动作名（配置文件里的引用键）+ 展示名/描述（快捷键设置窗口展示用）+ 执行实现。
    /// </summary>
    public sealed class WindowActionDescriptor
    {
        /// <summary>动作名，配置文件 WindowActions 段的 "Action" 字段与 <see cref="WindowActions.All"/> 的键。</summary>
        public required string Name { get; init; }

        /// <summary>设置窗口里展示的中文名称。</summary>
        public required string DisplayName { get; init; }

        /// <summary>设置窗口里展示的一行说明。</summary>
        public required string Description { get; init; }

        /// <summary>动作实现（无参、UI 线程执行）。</summary>
        public required Action Execute { get; init; }
    }

    /// <summary>
    /// 窗口动作注册表：动作名 → 实现，是「热键 → 窗口/系统动作」绑定的唯一注册点。
    /// 新增动作 = 在 <see cref="AllActions"/> 数组加一个条目（含展示名/描述）+ 实现一个方法，
    /// 然后在配置文件 WindowActions 段引用动作名（或直接在快捷键设置窗口里录制）。
    /// 动作经由键盘钩子触发、由调用方 Dispatcher.BeginInvoke 到 UI 线程异步执行，这里可以做 SendMessage 级操作。
    /// 注意静态初始化阶段不得产生副作用（只挂委托，不访问配置/管理器）。
    /// </summary>
    public static class WindowActions
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const byte VK_VOLUME_MUTE = 0xAD;
        private const byte VK_VOLUME_DOWN = 0xAE;
        private const byte VK_VOLUME_UP = 0xAF;
        private const byte VK_SHIFT = 0x10;
        private const byte VK_F10 = 0x79;
        private const byte VK_CONTROL = 0x11;
        private const byte VK_MENU = 0x12;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        /// <summary>
        /// 全部可用动作（数组顺序即快捷键设置窗口的展示顺序）。
        /// 前 10 条为历史动作，顺序与默认绑定保持一致；末尾 3 条为后补动作，默认不绑定。
        /// </summary>
        public static readonly WindowActionDescriptor[] AllActions =
        [
            new()
            {
                Name = "CloseWindow",
                DisplayName = "关闭前台窗口",
                Description = "关闭当前前台窗口（等同 Alt+F4）",
                Execute = CloseForegroundWindow,
            },
            new()
            {
                Name = "VolumeUp",
                DisplayName = "增大音量",
                Description = "提高系统音量",
                Execute = () => PressMediaKey(VK_VOLUME_UP),
            },
            new()
            {
                Name = "VolumeDown",
                DisplayName = "减小音量",
                Description = "降低系统音量",
                Execute = () => PressMediaKey(VK_VOLUME_DOWN),
            },
            new()
            {
                Name = "ToggleMute",
                DisplayName = "切换静音",
                Description = "静音与取消静音之间切换",
                Execute = () => PressMediaKey(VK_VOLUME_MUTE),
            },
            new()
            {
                Name = "ShowClipboardHistory",
                DisplayName = "剪贴板历史",
                Description = "弹出剪贴板历史面板（再按一次关闭）",
                Execute = () => App.ClipboardHistoryWindow?.ShowHistory(),
            },
            new()
            {
                Name = "Screenshot",
                DisplayName = "区域截图",
                Description = "框选区域截图，可复制或识别文字",
                Execute = ScreenshotManager.StartCapture,
            },
            new()
            {
                Name = "PinClipboard",
                DisplayName = "剪贴板贴图",
                Description = "把剪贴板图片钉为屏幕贴图，无图片时钉文字便签",
                Execute = ScreenshotManager.PinFromClipboard,
            },
            new()
            {
                Name = "TogglePinVisibility",
                DisplayName = "隐藏/显示所有贴图",
                Description = "整体切换贴图（图片贴图与文字便签）的显示与隐藏",
                Execute = PinWindow.ToggleAllVisibility,
            },
            new()
            {
                Name = "PinBoxSelect",
                DisplayName = "框选移动贴图",
                Description = "框选多张贴图后整体移动（Esc 取消选中）",
                Execute = PinWindow.StartBoxSelect,
            },
            new()
            {
                Name = "ShowContextMenu",
                DisplayName = "弹出右键菜单",
                Description = "对当前焦点控件模拟 Shift+F10，弹出右键菜单",
                Execute = ShowContextMenu,
            },
            new()
            {
                Name = "EyeCareNormal",
                DisplayName = "护眼：正常",
                Description = "关闭护眼颜色效果，显示真实色彩",
                Execute = () => ApplyEyeCareMode("正常"),
            },
            new()
            {
                Name = "EyeCareOffice",
                DisplayName = "护眼：办公",
                Description = "切换到办公护眼模式（降低色温与亮度）",
                Execute = () => ApplyEyeCareMode("办公"),
            },
            new()
            {
                Name = "OpenHotkeySettings",
                DisplayName = "打开快捷键设置",
                Description = "打开快捷键设置窗口，自定义全局热键",
                Execute = HotkeySettingsWindow.ShowFor,
            },
        ];

        /// <summary>动作名 → 描述符（不区分大小写，由 <see cref="AllActions"/> 派生，保证两者一致）。</summary>
        public static readonly IReadOnlyDictionary<string, WindowActionDescriptor> All =
            AllActions.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

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

        /// <summary>
        /// 模拟 Shift+F10：向当前焦点控件弹出右键菜单（键盘路径，与真实按键等价）。
        /// 钩子吞掉组合键时真实 Ctrl/Alt 可能仍按着，先释放它们，否则应用收到的是
        /// Ctrl+Alt+Shift+F10、加速键表不匹配导致菜单弹不出来。
        /// </summary>
        private static void ShowContextMenu()
        {
            ReleaseIfPressed(VK_CONTROL);
            ReleaseIfPressed(VK_MENU);
            keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
            keybd_event(VK_F10, 0, 0, UIntPtr.Zero);
            keybd_event(VK_F10, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        /// <summary>
        /// 应用护眼模式（按模式名查找，找不到记日志）。
        /// 热键触发的动作可能在前台无窗口时执行，失败只记日志不弹窗。
        /// </summary>
        private static void ApplyEyeCareMode(string modeName)
        {
            try
            {
                var mode = EyeCareManager.FindByCommandName(EyeCareManager.CommandPrefix + modeName);
                if (mode == null)
                {
                    Logger.LogWarning($"护眼模式不存在，已忽略: {modeName}");
                    return;
                }

                EyeCareManager.ApplyMode(mode);
            }
            catch (Exception ex)
            {
                Logger.LogError($"应用护眼模式失败: {modeName}", ex);
            }
        }

        /// <summary>修饰键仍处于按下状态时注入一次 KEYUP 释放（真实按键之后松开产生的多余 KEYUP 无害）。</summary>
        private static void ReleaseIfPressed(byte vk)
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }
}
