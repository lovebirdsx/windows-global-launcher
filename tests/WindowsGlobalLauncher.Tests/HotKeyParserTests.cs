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
}
