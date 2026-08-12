using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CommandLauncher
{
    /// <summary>
    /// 这个类内部创建了一个“消息窗口”（HwndSource），
    /// 立刻用它的 HWND 去调用 RegisterHotKey，
    /// 负责接收 WM_HOTKEY，一旦按下配置的热键，就触发 HotKeyPressed 事件。
    /// </summary>
    public class HotKeyListener : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID = 9000;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;

        private readonly NativeWindow _window;
        private bool _disposed = false;

        public event Action? HotKeyPressed;

        public HotKeyListener()
        {
            _window = new NativeWindow();
            _window.SetParent(this);
            _window.CreateHandle(new CreateParams());
        }

        public bool RegisterHotKey(string hotKeyString)
        {
            try
            {
                if (!HotKeyParser.TryParse(hotKeyString, out int virtualKey,
                        out bool ctrl, out bool alt, out bool shift, out bool win))
                {
                    return false;
                }

                // 注册全局热键必须至少带一个修饰键（保持原有行为，避免独占普通按键）
                if (!ctrl && !alt && !shift && !win)
                {
                    return false;
                }

                uint modifiers = 0;
                if (ctrl) modifiers |= MOD_CONTROL;
                if (alt) modifiers |= MOD_ALT;
                if (shift) modifiers |= MOD_SHIFT;
                if (win) modifiers |= MOD_WIN;

                bool result = RegisterHotKey(_window.Handle, HOTKEY_ID, modifiers, (uint)virtualKey);
                if (!result)
                {
                    Logger.LogError($"注册热键失败: {hotKeyString}", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
                    return false;
                }

                Logger.LogInfo($"成功注册热键: {hotKeyString}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"注册热键异常: {hotKeyString}", ex);
                return false;
            }
        }

        public bool UnregisterHotKey()
        {
            try
            {
                bool result = UnregisterHotKey(_window.Handle, HOTKEY_ID);
                if (!result)
                {
                    Logger.LogError("注销热键失败", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
                    return false;
                }

                Logger.LogInfo("成功注销热键");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("注销热键异常", ex);
                return false;
            }
        }

        private void HandleMessage(ref Message m)
        {
            const int WM_HOTKEY = 0x0312;

            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            {
                HotKeyPressed?.Invoke();
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                UnregisterHotKey(_window.Handle, HOTKEY_ID);
                _window?.DestroyHandle();
                _disposed = true;
                Logger.LogInfo("热键监听器已释放");
            }
        }

        private class NativeWindow : System.Windows.Forms.NativeWindow
        {
            private HotKeyListener? _parent;

            public void SetParent(HotKeyListener parent)
            {
                _parent = parent;
            }

            protected override void WndProc(ref Message m)
            {
                _parent?.HandleMessage(ref m);
                base.WndProc(ref m);
            }
        }
    }
}
