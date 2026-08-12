namespace CommandLauncher
{
    /// <summary>
    /// 热键字符串解析器（纯逻辑，便于单元测试）。
    /// 语法：修饰键（ctrl/alt/shift/win，不区分大小写）+ 主键，以 "+" 连接，如 "Ctrl+Shift+I"、"Alt+Q"。
    /// 主键支持 A-Z / 0-9 / F1-F12 / SPACE / ENTER / ESC / TAB。
    /// 供 HotKeyListener（RegisterHotKey 路径）与 KeyboardHook（低级钩子路径）共用，保证解析行为单一来源。
    /// </summary>
    public static class HotKeyParser
    {
        /// <summary>
        /// 解析热键字符串为主键虚拟键码 + 四个修饰键标志。失败（格式错误/未知键名）返回 false。
        /// </summary>
        public static bool TryParse(string? hotKey, out int virtualKey,
            out bool ctrl, out bool alt, out bool shift, out bool win)
        {
            virtualKey = 0;
            ctrl = alt = shift = win = false;

            if (string.IsNullOrWhiteSpace(hotKey))
                return false;

            string keyPart = "";
            foreach (var part in hotKey.Split('+'))
            {
                var trimmed = part.Trim();
                if (trimmed.Length == 0)
                    return false; // 形如 "Alt+" 的空段视为非法

                switch (trimmed.ToLower())
                {
                    case "ctrl": ctrl = true; break;
                    case "alt": alt = true; break;
                    case "shift": shift = true; break;
                    case "win": win = true; break;
                    default:
                        // 主键只能出现一次
                        if (keyPart.Length > 0)
                            return false;
                        keyPart = trimmed;
                        break;
                }
            }

            virtualKey = GetVirtualKey(keyPart);
            return virtualKey != 0;
        }

        private static int GetVirtualKey(string key)
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
                    return c;
                if (c >= '0' && c <= '9')
                    return c;
            }

            return 0;
        }
    }
}
