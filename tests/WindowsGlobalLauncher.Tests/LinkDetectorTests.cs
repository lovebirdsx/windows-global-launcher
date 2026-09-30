using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests
{
    public class LinkDetectorTests
    {
        // 统一断言：区间定位回原文片段（Substring）、长度、以及规范化后的打开用 URL。
        // expectedFragment 同时用于校验 Start/Length 与 Url 的差异（www. 起点补全后两者不同）。
        private static void AssertSingleLink(string text, int expectedStart, string expectedFragment, string expectedUrl)
        {
            TextLink link = Assert.Single(LinkDetector.Detect(text));

            Assert.Equal(expectedStart, link.Start);
            Assert.Equal(expectedFragment.Length, link.Length);
            Assert.Equal(expectedFragment, text.Substring(link.Start, link.Length)); // 区间必须落在原文的同一片段上
            Assert.Equal(expectedUrl, link.Url);
        }

        [Theory]
        [InlineData("https://example.com")]                                  // 最简 https
        [InlineData("http://a.cn/path?x=1&y=2#frag")]                        // query + fragment 完整保留
        [InlineData("https://a.cn:8080/x")]                                  // 端口号
        [InlineData("https://a.cn/x%20y")]                                   // 百分号编码
        [InlineData("https://a.cn/x/y_z-w~1.html")]                          // 路径里的 - _ ~ 均属允许字符
        [InlineData("https://a.cn/?a=1&b=%2F")]                              // 转义斜杠
        public void Detect_BasicUrl_ReturnsWholeUrlAsSingleLink(string text)
        {
            // 无尾标点、无 www. 补全：原文片段即最终 URL
            AssertSingleLink(text, 0, text, text);
        }

        [Theory]
        [InlineData("www.baidu.com", 0, "www.baidu.com", "https://www.baidu.com")]      // 无 scheme：补 https://
        [InlineData("WWW.BAIDU.COM", 0, "WWW.BAIDU.COM", "https://WWW.BAIDU.COM")]      // 大小写不改写，Url 保留原文
        [InlineData("见 www.a.cn/x 然后", 2, "www.a.cn/x", "https://www.a.cn/x")]        // 中文 / 空格包裹
        [InlineData("参考www.a.cn然后", 2, "www.a.cn", "https://www.a.cn")]              // 前一字符是中文：非 ASCII 字母数字，放行
        public void Detect_WwwPrefix_PrependsHttpsAndKeepsOriginalRange(
            string text, int expectedStart, string expectedFragment, string expectedUrl)
        {
            AssertSingleLink(text, expectedStart, expectedFragment, expectedUrl);
        }

        [Theory]
        [InlineData("abcwww.foo.com")]      // 前一字符是 ASCII 字母
        [InlineData("123www.foo.com")]      // 前一字符是数字
        [InlineData("a@www.foo.com")]       // 前一字符是 @
        public void Detect_WwwPrefixWithAsciiWordBefore_NoLink(string text)
        {
            Assert.Empty(LinkDetector.Detect(text));
        }

        [Theory]
        [InlineData("参考https://a.com/x然后", 2, "https://a.com/x", "https://a.com/x")]   // 中文处终止
        [InlineData("见 https://a.com/x 然后", 2, "https://a.com/x", "https://a.com/x")]   // 空格处终止
        [InlineData("“https://a.com”", 1, "https://a.com", "https://a.com")]              // 全角引号包裹
        [InlineData("【https://a.com】", 1, "https://a.com", "https://a.com")]            // 全角方括号包裹
        [InlineData("链接：https://a.com/x。", 3, "https://a.com/x", "https://a.com/x")]   // 全角冒号前置、全角句号收尾
        public void Detect_SurroundedByChineseOrFullWidth_StopsAtNonAscii(
            string text, int expectedStart, string expectedFragment, string expectedUrl)
        {
            AssertSingleLink(text, expectedStart, expectedFragment, expectedUrl);
        }

        [Theory]
        [InlineData("https://a.com。")]   // 全角句号
        [InlineData("https://a.com，")]   // 全角逗号
        [InlineData("https://a.com、")]   // 全角顿号
        [InlineData("https://a.com；")]   // 全角分号
        [InlineData("https://a.com：")]   // 全角冒号
        [InlineData("https://a.com！")]   // 全角叹号
        [InlineData("https://a.com？")]   // 全角问号
        [InlineData("https://a.com）")]   // 全角右括号
        [InlineData("https://a.com】")]   // 全角右方括号
        [InlineData("https://a.com》")]   // 全角书名号
        [InlineData("https://a.com」")]   // 全角右引号（直角）
        [InlineData("https://a.com』")]   // 全角右单引号
        [InlineData("https://a.com…")]   // 省略号
        [InlineData("https://a.com.")]    // 半角句点
        [InlineData("https://a.com,")]    // 半角逗号
        [InlineData("https://a.com;")]    // 半角分号
        [InlineData("https://a.com:")]    // 半角冒号
        [InlineData("https://a.com!")]    // 半角叹号
        [InlineData("https://a.com?")]    // 半角问号
        public void Detect_TrailingPunctuation_StrippedFromLink(string text)
        {
            // 这些用例剥离后的原文片段就是最终 URL（起点在 0、无 www. 补全），故期望值统一
            AssertSingleLink(text, 0, "https://a.com", "https://a.com");
        }

        [Theory]
        [InlineData("https://a.com/).", "https://a.com/")]        // 连续标点：循环剥离到稳定，尾部 '/' 保留
        [InlineData("https://a.com/!,;:", "https://a.com/")]      // 连续半角标点全剥掉
        [InlineData("https://a.com/。，！", "https://a.com/")]     // 连续全角标点全剥掉
        public void Detect_ConsecutiveTrailingPunctuation_StrippedUntilStable(string text, string expectedUrl)
        {
            AssertSingleLink(text, 0, expectedUrl, expectedUrl);
        }

        [Theory]
        [InlineData("(https://a.com)", 1, "https://a.com", "https://a.com")]   // 左括号在候选串之外 → 尾 ) 多余，剥离
        [InlineData("[https://a.com]", 1, "https://a.com", "https://a.com")]   // 方括号同理
        [InlineData("https://a.com/)", 0, "https://a.com/", "https://a.com/")]  // 候选串内全是右括号：剥离
        [InlineData("https://en.wikipedia.org/wiki/A_(b)", 0, "https://en.wikipedia.org/wiki/A_(b)",
            "https://en.wikipedia.org/wiki/A_(b)")]                            // 括号成对：保留
        [InlineData("https://a.com/a_(b))", 0, "https://a.com/a_(b)", "https://a.com/a_(b)")] // 两个右括号：只剥多余的那个
        public void Detect_UnbalancedTrailingBracket_StrippedOnlyWhenUnpaired(
            string text, int expectedStart, string expectedFragment, string expectedUrl)
        {
            AssertSingleLink(text, expectedStart, expectedFragment, expectedUrl);
        }

        [Theory]
        [InlineData("a@b.com")]                  // 邮箱：不认裸域名
        [InlineData("版本 1.0.0")]                // 数字版本号
        [InlineData("ftp://a.com")]              // 非 http/https 协议
        [InlineData("file:///C:/x")]             // file 协议
        [InlineData("https://")]                 // 有 scheme 无主机
        [InlineData("http:///path")]             // 空主机
        [InlineData("http:/a.com")]              // 只有一个斜杠
        [InlineData("https:example.com")]        // 缺少 //
        [InlineData("www.")]                     // 剥掉尾点后不再是 www. 起点
        [InlineData("WWW.")]                     // 同上（大小写无关）
        [InlineData("www")]                      // 只有 www 三个字母
        [InlineData("")]                         // 空串
        [InlineData("纯中文文本，没有任何链接。")] // 纯中文（含全角标点）
        [InlineData("详见 README 文档")]          // 普通中文 + 英文单词
        public void Detect_NoValidLink_ReturnsEmpty(string text)
        {
            Assert.Empty(LinkDetector.Detect(text));
        }

        [Fact]
        public void Detect_NullText_ReturnsEmpty()
        {
            Assert.Empty(LinkDetector.Detect(null!));
        }

        [Fact]
        public void Detect_MultipleUrls_ReturnsInOrderWithoutOverlap()
        {
            const string text = "https://a.com 与 https://b.com";

            var links = LinkDetector.Detect(text);

            Assert.Equal(2, links.Count);
            Assert.Equal(new TextLink(0, 13, "https://a.com"), links[0]);
            Assert.Equal(new TextLink(16, 13, "https://b.com"), links[1]);
            Assert.Equal("https://a.com", text.Substring(links[0].Start, links[0].Length));
            Assert.Equal("https://b.com", text.Substring(links[1].Start, links[1].Length));
            // 互不重叠且按出现顺序
            Assert.True(links[0].Start + links[0].Length <= links[1].Start);
        }

        [Fact]
        public void Detect_AdjacentUrlsWithPunctuation_SeparatesBoth()
        {
            // 中间只隔一个全角顿号：两段 URL 各自成条，标点归前者剥离、不越界
            const string text = "https://a.com、https://b.com/x。";

            var links = LinkDetector.Detect(text);

            Assert.Equal(2, links.Count);
            Assert.Equal("https://a.com", text.Substring(links[0].Start, links[0].Length));
            Assert.Equal(0, links[0].Start);
            Assert.Equal("https://b.com/x", text.Substring(links[1].Start, links[1].Length));
            Assert.Equal(14, links[1].Start);
            Assert.Equal("https://b.com/x", links[1].Url);
        }

        [Theory]
        [InlineData("HTTPS://EXAMPLE.COM", "HTTPS://EXAMPLE.COM")]              // 全大写起点识别，Url 保留原文大小写
        [InlineData("HtTpS://Example.COM/Path", "HtTpS://Example.COM/Path")]    // 混合大小写
        [InlineData("hTtP://a.com", "hTtP://a.com")]                            // http 同样忽略大小写
        [InlineData("WwW.Example.COM", "https://WwW.Example.COM")]              // www. 起点大小写无关，补全后仍保留原文大小写
        public void Detect_CaseInsensitiveStart_RecognizedAndUrlKeepsOriginalCase(string text, string expectedUrl)
        {
            AssertSingleLink(text, 0, text, expectedUrl);
        }

        [Fact]
        public void Detect_ChineseParagraphWithUrls_FindsBothLinks()
        {
            const string text = "请参考 https://github.com/lovebirdsx/windows-global-launcher 的说明，或访问 www.example.com。";

            var links = LinkDetector.Detect(text);

            Assert.Equal(2, links.Count);

            Assert.Equal(4, links[0].Start);    // "请参考 " 之后
            Assert.Equal(53, links[0].Length);
            Assert.Equal("https://github.com/lovebirdsx/windows-global-launcher",
                text.Substring(links[0].Start, links[0].Length));
            Assert.Equal("https://github.com/lovebirdsx/windows-global-launcher", links[0].Url);

            Assert.Equal(66, links[1].Start);   // " 的说明，或访问 " 之后
            Assert.Equal(15, links[1].Length);  // 原文只有 www.example.com，不含收尾的全角句号
            Assert.Equal("www.example.com", text.Substring(links[1].Start, links[1].Length));
            Assert.Equal("https://www.example.com", links[1].Url);

            Assert.True(links[0].Start + links[0].Length <= links[1].Start); // 区间不重叠
        }

        [Theory]
        [InlineData("https://a.com", "https://a.com")]              // 已规范：原样返回
        [InlineData("HTTP://A.COM", "HTTP://A.COM")]                // scheme 大小写不改写
        [InlineData("www.a.com", "https://www.a.com")]              // www. 补全 https://
        [InlineData("WWW.A.com", "https://WWW.A.com")]              // 补全后仍保留原文大小写
        [InlineData("https://a.com.", "https://a.com")]             // 尾标点剥离
        [InlineData("www.a.com。", "https://www.a.com")]             // 先剥离再补全
        [InlineData("https://a.com/x?y=1#z", "https://a.com/x?y=1#z")]
        public void NormalizeUrl_ValidInput_ReturnsNormalizedUrl(string raw, string expected)
        {
            Assert.Equal(expected, LinkDetector.NormalizeUrl(raw));
        }

        [Theory]
        [InlineData("")]                  // 空串
        [InlineData("www.")]              // 剥成 www 后既无 scheme 也无主机
        [InlineData("www")]               // 只有 www
        [InlineData("https://")]          // 无主机
        [InlineData("http:///path")]      // 空主机
        [InlineData("a@b.com")]           // 邮箱 / 裸域名
        [InlineData("ftp://a.com")]       // 非 http/https
        [InlineData("file:///C:/x")]      // file 协议（Uri 合法但 scheme 不符）
        [InlineData("版本 1.0.0")]         // 普通文本
        [InlineData("纯中文，假链接。")]    // 纯中文
        public void NormalizeUrl_InvalidInput_ReturnsNull(string raw)
        {
            Assert.Null(LinkDetector.NormalizeUrl(raw));
        }

        [Fact]
        public void NormalizeUrl_NullInput_ReturnsNull()
        {
            Assert.Null(LinkDetector.NormalizeUrl(null!));
        }
    }
}
