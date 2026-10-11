using System;
using System.Collections.Generic;
using System.Text;

namespace CommandLauncher
{
    /// <summary>
    /// 热键字符串解析/格式化器（纯逻辑，便于单元测试）。
    /// 语法：修饰键（ctrl/alt/shift/win，不区分大小写）+ 主键，以 "+" 连接，如 "Ctrl+Shift+I"、"Alt+Q"。
    /// 主键支持 A-Z / 0-9 / F1-F12 / SPACE / ENTER / ESC / TAB / 方向键 /
    /// HOME/END/PAGEUP/PAGEDOWN/INSERT/DELETE/BACKSPACE / 常用标点（见 <see cref="KeyTable"/>）。
    /// 供 HotKeyListener（RegisterHotKey 路径）、KeyboardHook（低级钩子路径）与快捷键设置窗口共用；
    /// 解析与格式化是同一份键表的正反两个方向（TryParse/Format 互为逆运算，配套单测 round-trip）。
    /// </summary>
    public static class HotKeyParser
    {
        // 键名 → 虚拟键码的单一来源：NameToVk / VkToName 均由它派生，保证正反查结构性一致。
        private static readonly (string Name, int Vk)[] KeyTable = BuildKeyTable();

        private static readonly Dictionary<string, int> NameToVk;
        private static readonly Dictionary<int, string> VkToName;

        static HotKeyParser()
        {
            NameToVk = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            VkToName = new Dictionary<int, string>();
            foreach (var (name, vk) in KeyTable)
            {
                NameToVk[name] = vk;
                VkToName.TryAdd(vk, name);
            }

            // 历史别名：旧配置里可能写 "ESCAPE"，只允许解析进来，Format 一律产出规范名 "ESC"。
            NameToVk["ESCAPE"] = 0x1B;
        }

        /// <summary>当前支持的全部主键虚拟键码（只读视图，供设置窗口/单测枚举）。</summary>
        public static IEnumerable<int> SupportedMainKeys => VkToName.Keys;

        private static (string, int)[] BuildKeyTable()
        {
            var table = new List<(string, int)>
            {
                ("SPACE", 0x20),
                ("ENTER", 0x0D),
                ("ESC", 0x1B),
                ("TAB", 0x09),
                ("BACKSPACE", 0x08),
                ("INSERT", 0x2D),
                ("DELETE", 0x2E),
                ("HOME", 0x24),
                ("END", 0x23),
                ("PAGEUP", 0x21),
                ("PAGEDOWN", 0x22),
                ("LEFT", 0x25),
                ("UP", 0x26),
                ("RIGHT", 0x27),
                ("DOWN", 0x28),
            };

            for (int i = 1; i <= 12; i++)
                table.Add(($"F{i}", 0x6F + i)); // F1=0x70 ... F12=0x7B

            for (char c = 'A'; c <= 'Z'; c++)
                table.Add((c.ToString(), c));

            for (char c = '0'; c <= '9'; c++)
                table.Add((c.ToString(), c));

            // 标点用字面字符（美国布局的 OEM 键）。"+" 是语法分隔符，无法表达，
            // 故 VK_OEM_PLUS 一律命名为 "="（物理键位语义），小键盘加号等不支持。
            table.Add(("-", 0xBD));   // VK_OEM_MINUS
            table.Add(("=", 0xBB));   // VK_OEM_PLUS
            table.Add(("[", 0xDB));   // VK_OEM_4
            table.Add(("]", 0xDD));   // VK_OEM_6
            table.Add(("\\", 0xDC));  // VK_OEM_5
            table.Add((";", 0xBA));   // VK_OEM_1
            table.Add(("'", 0xDE));   // VK_OEM_7
            table.Add((",", 0xBC));   // VK_OEM_COMMA
            table.Add((".", 0xBE));   // VK_OEM_PERIOD
            table.Add(("/", 0xBF));   // VK_OEM_2
            table.Add(("`", 0xC0));   // VK_OEM_3

            return [.. table];
        }

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

                switch (trimmed.ToLowerInvariant())
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

            if (keyPart.Length == 0 || !NameToVk.TryGetValue(keyPart, out int vk) || vk == 0)
                return false;

            virtualKey = vk;
            return true;
        }

        /// <summary>
        /// 把主键虚拟键码 + 修饰键格式化为规范热键串（修饰键顺序 Ctrl+Alt+Shift+Win，主键大写）。
        /// 不支持的主键返回 null（录制端据此拒绝小键盘、媒体键等）。
        /// </summary>
        public static string? Format(int virtualKey, bool ctrl, bool alt, bool shift, bool win)
        {
            if (!VkToName.TryGetValue(virtualKey, out var name))
                return null;

            var sb = new StringBuilder();
            if (ctrl) sb.Append("Ctrl+");
            if (alt) sb.Append("Alt+");
            if (shift) sb.Append("Shift+");
            if (win) sb.Append("Win+");
            sb.Append(name);
            return sb.ToString();
        }

        /// <summary>该虚拟键码是否是支持的主键。纯查询，供录制端与设置窗口判定。</summary>
        public static bool IsSupportedMainKey(int virtualKey) => VkToName.ContainsKey(virtualKey);

        /// <summary>取主键的规范名（无修饰键部分），不支持返回 false。</summary>
        public static bool TryGetKeyName(int virtualKey, out string name)
        {
            if (VkToName.TryGetValue(virtualKey, out var found))
            {
                name = found;
                return true;
            }

            name = "";
            return false;
        }

        /// <summary>
        /// 归一化热键串用于冲突比较：可解析 → 规范串（大小写、修饰键顺序、ESC/ESCAPE 等写法差异被抹平）；
        /// 非空但不可解析 → trim 后大写；空/空白 → null。
        /// </summary>
        public static string? Normalize(string? hotKey)
        {
            if (string.IsNullOrWhiteSpace(hotKey))
                return null;

            if (TryParse(hotKey, out int vk, out bool ctrl, out bool alt, out bool shift, out bool win))
                return Format(vk, ctrl, alt, shift, win);

            return hotKey.Trim().ToUpperInvariant();
        }
    }
}
