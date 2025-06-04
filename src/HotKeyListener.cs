using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CommandLauncher
{
    /// <summary>
    /// 这个类内部创建了一个“消息窗口”（HwndSource），
    /// 立刻用它的 HWND 去调用 RegisterHotKey，
    /// 负责接收 WM_HOTKEY，一旦按下 Ctrl+Space，就触发 HotKeyPressed 事件。
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
                var parts = hotKeyString.Split('+');
                if (parts.Length < 2) return false;

                uint modifiers = 0;
                string keyPart = "";

                foreach (var part in parts)
                {
                    var trimmedPart = part.Trim();
                    switch (trimmedPart.ToLower())
                    {
                        case "ctrl":
                            modifiers |= MOD_CONTROL;
                            break;
                        case "alt":
                            modifiers |= MOD_ALT;
                            break;
                        case "shift":
                            modifiers |= MOD_SHIFT;
                            break;
                        case "win":
                            modifiers |= MOD_WIN;
                            break;
                        default:
                            keyPart = trimmedPart;
                            break;
                    }
                }

                if (string.IsNullOrEmpty(keyPart))
                {
                    return false;
                }

                uint virtualKey = GetVirtualKey(keyPart);
                if (virtualKey == 0)
                {
                    return false;
                }

                bool result = RegisterHotKey(_window.Handle, HOTKEY_ID, modifiers, virtualKey);
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

        private uint GetVirtualKey(string key)
        {
            // 处理常用按键
            switch (key.ToUpper())
            {
                case "SPACE": return 0x20;
                case "ENTER": return 0x0D;
                case "ESC": case "ESCAPE": return 0x1B;
                case "TAB": return 0x09;
                case "F1": return 0x70;
                case "F2": return 0x71;
                case "F3": return 0x72;
                case "F4": return 0x73;
                case "F5": return 0x74;
                case "F6": return 0x75;
                case "F7": return 0x76;
                case "F8": return 0x77;
                case "F9": return 0x78;
                case "F10": return 0x79;
                case "F11": return 0x7A;
                case "F12": return 0x7B;
            }

            // 字母和数字
            if (key.Length == 1)
            {
                char c = key.ToUpper()[0];
                if (c >= 'A' && c <= 'Z')
                    return (uint)c;
                if (c >= '0' && c <= '9')
                    return (uint)c;
            }

            return 0;
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
