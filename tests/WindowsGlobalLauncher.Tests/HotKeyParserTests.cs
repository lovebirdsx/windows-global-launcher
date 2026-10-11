using System.Collections.Generic;
using System.Linq;
using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests
{
    public class HotKeyParserTests
    {
        [Theory]
        [InlineData("Alt+Q", 0x51, false, true, false, false)]
        [InlineData("alt+q", 0x51, false, true, false, false)]           // 不区分大小写
        [InlineData("ALT + Q", 0x51, false, true, false, false)]         // 允许空格
        [InlineData("Ctrl+Shift+F4", 0x73, true, false, true, false)]
        [InlineData("Win+E", 0x45, false, false, false, true)]
        [InlineData("Ctrl+Alt+Shift+Win+1", 0x31, true, true, true, true)]
        [InlineData("F12", 0x7B, false, false, false, false)]            // 允许不带修饰键（由调用方自行约束）
        [InlineData("Ctrl+Space", 0x20, true, false, false, false)]
        [InlineData("Shift+Tab", 0x09, false, false, true, false)]
        [InlineData("Shift+F7", 0x76, false, false, true, false)]        // 默认绑定：切换贴图整体显示/隐藏
        [InlineData("Ctrl+Alt+Enter", 0x0D, true, true, false, false)]   // 默认绑定：模拟 Shift+F10 弹出右键菜单
        [InlineData("ESCAPE", 0x1B, false, false, false, false)]         // 历史别名，仍可解析
        public void TryParse_ValidHotKey_ParsesCorrectly(
            string hotKey, int expectedVk, bool ctrl, bool alt, bool shift, bool win)
        {
            bool result = HotKeyParser.TryParse(hotKey, out int vk,
                out bool actualCtrl, out bool actualAlt, out bool actualShift, out bool actualWin);

            Assert.True(result);
            Assert.Equal(expectedVk, vk);
            Assert.Equal(ctrl, actualCtrl);
            Assert.Equal(alt, actualAlt);
            Assert.Equal(shift, actualShift);
            Assert.Equal(win, actualWin);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Alt+")]        // 空段
        [InlineData("Ctrl+Alt")]    // 缺少主键
        [InlineData("Alt+Unknown")] // 未知键名
        [InlineData("Alt+A+B")]     // 主键出现多次
        public void TryParse_InvalidHotKey_ReturnsFalse(string? hotKey)
        {
            bool result = HotKeyParser.TryParse(hotKey, out _, out _, out _, out _, out _);

            Assert.False(result);
        }

        [Theory]
        [InlineData("UP", 0x26)]
        [InlineData("DOWN", 0x28)]
        [InlineData("LEFT", 0x25)]
        [InlineData("RIGHT", 0x27)]
        [InlineData("HOME", 0x24)]
        [InlineData("END", 0x23)]
        [InlineData("PAGEUP", 0x21)]
        [InlineData("PAGEDOWN", 0x22)]
        [InlineData("INSERT", 0x2D)]
        [InlineData("DELETE", 0x2E)]
        [InlineData("BACKSPACE", 0x08)]
        [InlineData(";", 0xBA)]
        [InlineData("'", 0xDE)]
        [InlineData(",", 0xBC)]
        [InlineData(".", 0xBE)]
        [InlineData("/", 0xBF)]
        [InlineData("\\", 0xDC)]
        [InlineData("[", 0xDB)]
        [InlineData("]", 0xDD)]
        [InlineData("-", 0xBD)]
        [InlineData("=", 0xBB)]
        [InlineData("`", 0xC0)]
        public void TryParse_ExtendedKeys_ParsesCorrectly(string hotKey, int expectedVk)
        {
            bool result = HotKeyParser.TryParse(hotKey, out int vk,
                out bool ctrl, out bool alt, out bool shift, out bool win);

            Assert.True(result);
            Assert.Equal(expectedVk, vk);
            Assert.False(ctrl);
            Assert.False(alt);
            Assert.False(shift);
            Assert.False(win);
        }

        [Fact]
        public void TryParse_ExtendedKeyWithModifiers_ParsesCorrectly()
        {
            bool result = HotKeyParser.TryParse("Ctrl+Alt+/", out int vk,
                out bool ctrl, out bool alt, out bool shift, out bool win);

            Assert.True(result);
            Assert.Equal(0xBF, vk);
            Assert.True(ctrl);
            Assert.True(alt);
            Assert.False(shift);
            Assert.False(win);
        }
    }

    public class HotKeyParserFormatTests
    {
        /// <summary>全部受支持主键 × 代表性修饰键组合（round-trip 覆盖全键表）。</summary>
        public static IEnumerable<object[]> AllKeysWithModifiers()
        {
            (bool Ctrl, bool Alt, bool Shift, bool Win)[] combos =
            [
                (false, false, false, false),
                (true, false, false, false),
                (false, true, true, false),
                (true, true, true, true),
            ];

            foreach (var vk in HotKeyParser.SupportedMainKeys)
                foreach (var combo in combos)
                    yield return new object[] { vk, combo.Ctrl, combo.Alt, combo.Shift, combo.Win };
        }

        [Theory]
        [MemberData(nameof(AllKeysWithModifiers))]
        public void Format_RoundTripsThroughTryParse(int vk, bool ctrl, bool alt, bool shift, bool win)
        {
            string? text = HotKeyParser.Format(vk, ctrl, alt, shift, win);
            Assert.NotNull(text);

            bool result = HotKeyParser.TryParse(text, out int parsedVk,
                out bool actualCtrl, out bool actualAlt, out bool actualShift, out bool actualWin);

            Assert.True(result);
            Assert.Equal(vk, parsedVk);
            Assert.Equal(ctrl, actualCtrl);
            Assert.Equal(alt, actualAlt);
            Assert.Equal(shift, actualShift);
            Assert.Equal(win, actualWin);
        }

        [Fact]
        public void Format_UsesCanonicalModifierOrderAndCase()
        {
            Assert.Equal("Ctrl+Alt+Shift+Win+Q",
                HotKeyParser.Format(0x51, ctrl: true, alt: true, shift: true, win: true));
            Assert.Equal("Shift+F7", HotKeyParser.Format(0x76, ctrl: false, alt: false, shift: true, win: false));
            Assert.Equal("F4", HotKeyParser.Format(0x73, ctrl: false, alt: false, shift: false, win: false));
        }

        [Fact]
        public void Format_OemPlusKey_ProducesEqualsNotPlus()
        {
            // "+" 是语法分隔符，无法在热键串里表达，VK_OEM_PLUS 一律命名为 "="
            Assert.Equal("=", HotKeyParser.Format(0xBB, ctrl: false, alt: false, shift: false, win: false));
            Assert.Equal("Ctrl+=", HotKeyParser.Format(0xBB, ctrl: true, alt: false, shift: false, win: false));
        }

        [Theory]
        [InlineData(0x60)] // 小键盘 0
        [InlineData(0x6B)] // 小键盘 +
        [InlineData(0x7C)] // F13
        [InlineData(0x13)] // PAUSE
        [InlineData(0x5D)] // APPS 菜单键
        [InlineData(0x10)] // 单独的 SHIFT（修饰键本身不可作为主键）
        [InlineData(0x00)]
        public void Format_UnsupportedKey_ReturnsNull(int vk)
        {
            Assert.Null(HotKeyParser.Format(vk, ctrl: true, alt: false, shift: false, win: false));
            Assert.False(HotKeyParser.IsSupportedMainKey(vk));
            Assert.False(HotKeyParser.TryGetKeyName(vk, out _));
        }

        [Fact]
        public void SupportedMainKeys_TryGetKeyName_RoundTripsThroughTryParse()
        {
            foreach (var vk in HotKeyParser.SupportedMainKeys.ToList())
            {
                Assert.True(HotKeyParser.IsSupportedMainKey(vk));
                Assert.True(HotKeyParser.TryGetKeyName(vk, out var name));
                Assert.True(HotKeyParser.TryParse(name, out int parsedVk, out _, out _, out _, out _));
                Assert.Equal(vk, parsedVk);
            }
        }

        [Fact]
        public void SupportedMainKeys_AreUniqueAndKnown()
        {
            var keys = HotKeyParser.SupportedMainKeys.ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
            // 常见键抽样（字母/数字/功能键/编辑键/标点）
            Assert.Contains(0x41, keys);
            Assert.Contains(0x31, keys);
            Assert.Contains(0x70, keys);
            Assert.Contains(0x26, keys);
            Assert.Contains(0xBF, keys);
        }

        [Theory]
        [InlineData("alt + q", "Alt+Q")]
        [InlineData("ALT+Q", "Alt+Q")]
        [InlineData("Ctrl+Alt+Shift+Win+A", "Ctrl+Alt+Shift+Win+A")]
        [InlineData("escape", "ESC")]     // 别名归一为规范名
        [InlineData("F7", "F7")]
        public void Normalize_ParseableKey_ReturnsCanonicalString(string input, string expected)
        {
            Assert.Equal(expected, HotKeyParser.Normalize(input));
        }

        [Fact]
        public void Normalize_UnparseableKey_ReturnsTrimmedUpper()
        {
            Assert.Equal("CTRL+CTRL", HotKeyParser.Normalize("  ctrl+ctrl "));
            Assert.Equal("NOTAKEY", HotKeyParser.Normalize("notakey"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Normalize_EmptyInput_ReturnsNull(string? input)
        {
            Assert.Null(HotKeyParser.Normalize(input));
        }
    }

    public class HotKeyActionBindingTests
    {
        private static HotKeyActionBinding CreateAltQ()
            => new() { VirtualKey = 0x51, Alt = true, Callback = () => { } };

        [Fact]
        public void Matches_ExactModifiers_ReturnsTrue()
        {
            Assert.True(CreateAltQ().Matches(0x51, ctrl: false, alt: true, shift: false, win: false));
        }

        [Theory]
        [InlineData(0x51, false, true, true, false)]   // Alt+Shift+Q 不应触发 Alt+Q
        [InlineData(0x51, true, true, false, false)]   // Ctrl+Alt+Q 不应触发
        [InlineData(0x51, false, false, false, false)] // 单独 Q 不应触发
        [InlineData(0x58, false, true, false, false)]  // Alt+X 不应触发
        public void Matches_MismatchedInput_ReturnsFalse(int vk, bool ctrl, bool alt, bool shift, bool win)
        {
            Assert.False(CreateAltQ().Matches(vk, ctrl, alt, shift, win));
        }
    }

    public class WindowActionsTests
    {
        [Theory]
        [InlineData("CloseWindow")]
        [InlineData("VolumeUp")]
        [InlineData("VolumeDown")]
        [InlineData("ToggleMute")]
        [InlineData("volumeup")] // 动作名不区分大小写
        [InlineData("TogglePinVisibility")]
        [InlineData("togglepinvisibility")] // 动作名不区分大小写
        [InlineData("ShowContextMenu")]
        [InlineData("EyeCareNormal")]
        [InlineData("eyecareoffice")] // 新增动作：护眼模式（默认不绑定）
        [InlineData("OpenHotkeySettings")]
        public void All_ContainsBuiltInActions(string actionName)
        {
            Assert.True(WindowActions.All.ContainsKey(actionName));
        }

        [Fact]
        public void AllActions_StartsWithLegacyDefaultsInOrder()
        {
            string[] legacyOrder =
            [
                "CloseWindow", "VolumeUp", "VolumeDown", "ToggleMute", "ShowClipboardHistory",
                "Screenshot", "PinClipboard", "TogglePinVisibility", "PinBoxSelect", "ShowContextMenu",
            ];

            Assert.Equal(legacyOrder, WindowActions.AllActions.Take(legacyOrder.Length).Select(a => a.Name));
        }

        [Fact]
        public void AllActions_NamesUniqueAndDescriptorsComplete()
        {
            var names = WindowActions.AllActions.Select(a => a.Name).ToList();
            Assert.Equal(names.Count, names.Distinct(System.StringComparer.OrdinalIgnoreCase).Count());

            foreach (var descriptor in WindowActions.AllActions)
            {
                Assert.False(string.IsNullOrWhiteSpace(descriptor.DisplayName));
                Assert.False(string.IsNullOrWhiteSpace(descriptor.Description));
                Assert.NotNull(descriptor.Execute);
            }
        }

        [Fact]
        public void All_MatchesAllActions()
        {
            Assert.Equal(WindowActions.AllActions.Length, WindowActions.All.Count);
            foreach (var descriptor in WindowActions.AllActions)
            {
                Assert.True(WindowActions.All.TryGetValue(descriptor.Name, out var found));
                Assert.Same(descriptor, found);
            }
        }

        [Fact]
        public void DefaultWindowActions_CoverAllRegistryActions()
        {
            var defaults = AppConfig.DefaultWindowActions().Select(a => a.Action).ToList();
            foreach (var descriptor in WindowActions.AllActions)
                Assert.Contains(descriptor.Name, defaults);

            // 注册表里没有默认表之外的条目（防拼写错导致的孤儿条目）
            foreach (var action in defaults)
                Assert.True(WindowActions.All.ContainsKey(action));
        }

        [Fact]
        public void DefaultWindowActions_LegacyBindingsUnchanged()
        {
            var defaults = AppConfig.DefaultWindowActions();
            Assert.Equal(10 + 3, defaults.Count);

            var map = defaults.ToDictionary(a => a.Action, a => a.HotKey, System.StringComparer.OrdinalIgnoreCase);
            Assert.Equal("Alt+Q", map["CloseWindow"]);
            Assert.Equal("Win+F12", map["VolumeUp"]);
            Assert.Equal("Win+F11", map["VolumeDown"]);
            Assert.Equal("Win+F10", map["ToggleMute"]);
            Assert.Equal("Ctrl+Alt+C", map["ShowClipboardHistory"]);
            Assert.Equal("F4", map["Screenshot"]);
            Assert.Equal("F7", map["PinClipboard"]);
            Assert.Equal("Shift+F7", map["TogglePinVisibility"]);
            Assert.Equal("Win+Q", map["PinBoxSelect"]);
            Assert.Equal("Ctrl+Alt+Enter", map["ShowContextMenu"]);

            // 新增动作默认不绑定
            Assert.Equal("", map["EyeCareNormal"]);
            Assert.Equal("", map["EyeCareOffice"]);
            Assert.Equal("", map["OpenHotkeySettings"]);
        }
    }
}
