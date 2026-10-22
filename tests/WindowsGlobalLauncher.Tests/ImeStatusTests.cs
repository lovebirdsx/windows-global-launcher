using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests
{
    /// <summary>
    /// 输入法状态判定纯逻辑测试（<see cref="ImeStatusResolver"/>）。
    /// 只锁定规则本身，不触碰 ImeStatusReader 的 Win32 查询。
    /// </summary>
    public class ImeStatusTests
    {
        // ---------- IsChineseLanguageId ----------

        [Theory]
        [InlineData(0x0804)] // 中文(简体，中国)
        [InlineData(0x0404)] // 中文(繁体，台湾)
        [InlineData(0x0C04)] // 中文(繁体，香港)
        [InlineData(0x1004)] // 中文(简体，新加坡)
        public void IsChineseLanguageId_ChineseLayouts_ReturnsTrue(int languageId)
        {
            Assert.True(ImeStatusResolver.IsChineseLanguageId((ushort)languageId));
        }

        [Theory]
        [InlineData(0x0409)] // 英语(美国)
        [InlineData(0x0411)] // 日语
        public void IsChineseLanguageId_NonChineseLayouts_ReturnsFalse(int languageId)
        {
            Assert.False(ImeStatusResolver.IsChineseLanguageId((ushort)languageId));
        }

        // ---------- Resolve：中文布局 ----------

        [Fact]
        public void Resolve_ChineseIme_NativeMode_ReturnsChinese()
        {
            ImeDisplay display = ImeStatusResolver.Resolve(
                languageId: 0x0804,
                isIme: true,
                hasChineseLayout: true,
                conversionMode: ImeStatusResolver.ImeCmodeNative,
                modeKnown: true);

            Assert.Equal(ImeDisplay.Chinese, display);
        }

        [Fact]
        public void Resolve_ChineseIme_NonNativeMode_ReturnsEnglish()
        {
            ImeDisplay display = ImeStatusResolver.Resolve(
                languageId: 0x0804,
                isIme: true,
                hasChineseLayout: true,
                conversionMode: 0,
                modeKnown: true);

            Assert.Equal(ImeDisplay.English, display);
        }

        [Fact]
        public void Resolve_ChineseIme_ModeUnknown_ReturnsNone()
        {
            // 即便转换模式里带着 native 位，查询失败也不能当作有效结果
            ImeDisplay display = ImeStatusResolver.Resolve(
                languageId: 0x0804,
                isIme: true,
                hasChineseLayout: true,
                conversionMode: ImeStatusResolver.ImeCmodeNative,
                modeKnown: false);

            Assert.Equal(ImeDisplay.None, display);
        }

        [Fact]
        public void Resolve_ChineseLayoutWithoutIme_ReturnsEnglish()
        {
            // 「中文(简体) - 美式键盘」：中文布局但不是 IME
            ImeDisplay display = ImeStatusResolver.Resolve(
                languageId: 0x0804,
                isIme: false,
                hasChineseLayout: true,
                conversionMode: 0,
                modeKnown: true);

            Assert.Equal(ImeDisplay.English, display);
        }

        // ---------- Resolve：非中文布局 ----------

        [Fact]
        public void Resolve_JapaneseIme_ReturnsNone_EvenWithChineseLayout()
        {
            ImeDisplay display = ImeStatusResolver.Resolve(
                languageId: 0x0411,
                isIme: true,
                hasChineseLayout: true,
                conversionMode: 0,
                modeKnown: true);

            Assert.Equal(ImeDisplay.None, display);
        }

        [Theory]
        [InlineData(true, ImeDisplay.English)]  // 装了中文布局：英文键盘布局显示「英」
        [InlineData(false, ImeDisplay.None)]    // 没装中文布局：不显示
        public void Resolve_NonChineseNonIme_FollowsChineseLayoutAvailability(
            bool hasChineseLayout, ImeDisplay expected)
        {
            ImeDisplay display = ImeStatusResolver.Resolve(
                languageId: 0x0409,
                isIme: false,
                hasChineseLayout: hasChineseLayout,
                conversionMode: 0,
                modeKnown: true);

            Assert.Equal(expected, display);
        }

        // ---------- ToText ----------

        [Theory]
        [InlineData(ImeDisplay.Chinese, "中")]
        [InlineData(ImeDisplay.English, "英")]
        [InlineData(ImeDisplay.None, "")]
        public void ToText_ReturnsExpectedText(ImeDisplay display, string expected)
        {
            Assert.Equal(expected, ImeStatusResolver.ToText(display));
        }
    }
}
