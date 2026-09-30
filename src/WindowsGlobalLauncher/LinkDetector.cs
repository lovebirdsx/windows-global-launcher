using System;
using System.Collections.Generic;

namespace CommandLauncher
{
    /// <summary>文本中识别出的一处链接：原文区间 [Start, Length) 与打开用的规范化 URL。</summary>
    public readonly record struct TextLink(int Start, int Length, string Url);

    /// <summary>
    /// 文本便签里的链接识别（无状态、可单测）。
    /// 只认 <c>http://</c>、<c>https://</c>、<c>www.</c> 三种起点，不识别裸域名、邮箱、ftp://、file://。
    /// 刻意用手写扫描而非正则：50k 字符文本下行为更可控（只在起点命中处才做子串/Uri 校验），
    /// 且尾标点剥离与括号平衡这类后处理本来就得手写。
    /// </summary>
    public static class LinkDetector
    {
        /// <summary>
        /// 扫描文本中的链接，按出现顺序返回；无链接返回空列表。
        /// 返回的区间互不重叠（识别成功后从区间末尾继续扫描），且 Start/Length 指向原文，
        /// 而 Url 是可用于打开的规范化串（www. 起点会补上 https://，故两者可能不同）。
        /// </summary>
        public static IReadOnlyList<TextLink> Detect(string text)
        {
            if (string.IsNullOrEmpty(text))
                return Array.Empty<TextLink>();

            var links = new List<TextLink>();
            int i = 0;
            while (i < text.Length)
            {
                if (!TryMatchStart(text, i, out int markerLength))
                {
                    i++; // 起点不匹配：逐字符推进，不跳步（跳步会漏掉紧邻它的下一个起点）
                    continue;
                }

                // 主体扫描：吃掉所有 URL 允许字符；空格、中文、全角标点、引号、尖括号等一律终止
                int end = i + markerLength;
                while (end < text.Length && IsUrlChar(text[end]))
                    end++;

                // 先在候选串上剥离尾标点、再定区间：保证 Length 与最终 URL 覆盖的是同一段原文
                string candidate = StripTrailingPunctuation(text.Substring(i, end - i));
                string? url = NormalizeUrl(candidate);
                if (url == null)
                {
                    // 形如 "https://"（无主机）的假起点：逐字符推进，继续找它之后真正的链接。
                    // 不用「跳到扫描末尾」是因为有效链接可能紧跟在假起点内部（如 https:///xhttps://a.com）。
                    i++;
                    continue;
                }

                links.Add(new TextLink(i, candidate.Length, url));
                i += candidate.Length; // 从区间末尾继续，保证结果互不重叠
            }

            return links;
        }

        /// <summary>
        /// 把原始候选串规范化为可直接打开的 URL；不是合法 http/https 链接时返回 null。
        /// 1) 先剥离尾标点（同 Detect 的规则）；2) www. 开头（忽略大小写）补 https://；
        /// 3) Uri 校验且 scheme 必须是 http/https、主机名非空；4) 返回串不改写大小写（原文保留，交给浏览器处理）。
        /// </summary>
        public static string? NormalizeUrl(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return null;

            string candidate = StripTrailingPunctuation(raw);
            if (candidate.Length == 0)
                return null;

            // 顺序不能反：先剥离再判 www. 前缀。"www." 剥成 "www" 后就不再是起点，
            // 否则会补出一个 https://www 这样的无意义链接。
            if (candidate.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                candidate = "https://" + candidate;

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
                return null;

            // 只认 http/https（Uri 的 Scheme 已归一化为小写，忽略大小写只是防御性写法）
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return null;

            // 主机名非空："https://"、"http:///path" 这类假链接在这里被挡住
            if (string.IsNullOrEmpty(uri.Host))
                return null;

            return candidate;
        }

        /// <summary>
        /// 判定 text[i] 处是否为链接起点，是则输出起点标记的长度（http:// 7、https:// 8、www. 4）。
        /// </summary>
        private static bool TryMatchStart(string text, int i, out int markerLength)
        {
            if (MatchesAt(text, i, "https://")) { markerLength = 8; return true; }
            if (MatchesAt(text, i, "http://")) { markerLength = 7; return true; }
            if (MatchesAt(text, i, "www.") && IsWwwBoundary(text, i)) { markerLength = 4; return true; }

            markerLength = 0;
            return false;
        }

        /// <summary>
        /// www. 起点的额外约束：前一字符不能是 ASCII 字母、数字或 @（文本开头 i==0 视为合法），
        /// 否则 abcwww.foo.com 这种「前面接着单词」的情形会被误判成链接。
        /// </summary>
        private static bool IsWwwBoundary(string text, int i)
        {
            if (i == 0)
                return true;

            char prev = text[i - 1];
            bool isAsciiWord = (prev >= 'a' && prev <= 'z') || (prev >= 'A' && prev <= 'Z') || (prev >= '0' && prev <= '9');
            return !isAsciiWord && prev != '@';
        }

        /// <summary>在 text[i..] 处按 OrdinalIgnoreCase 比较标记（不分配子串）。</summary>
        private static bool MatchesAt(string text, int i, string marker)
        {
            return i + marker.Length <= text.Length &&
                   string.Compare(text, i, marker, 0, marker.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }

        /// <summary>
        /// URL 允许字符：ASCII 字母数字 + <c>-._~:/?#[]@!$&amp;'()*+,;=%</c>。
        /// 其余字符（空格、中文、全角标点、引号、&lt;&gt;"{}|\^ 等）一律终止主体扫描——
        /// 「参考https://a.com/x然后」这类中文相邻场景全靠它只截出 ASCII 段。
        /// </summary>
        private static bool IsUrlChar(char c)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                return true;

            switch (c)
            {
                case '-': case '.': case '_': case '~': case ':': case '/': case '?':
                case '#': case '[': case ']': case '@': case '!': case '$': case '&':
                case '\'': case '(': case ')': case '*': case '+': case ',': case ';':
                case '=': case '%':
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 循环剥离尾部标点直到稳定：半角 <c>. , ; : ! ? ' "</c> 与全角
        /// <c>。 ， 、 ； ： ！ ？ ） 】 》 」 』 “ ” ‘ ’ …</c> 一律剥离；
        /// <c>) ] }</c> 只在「当前候选串内左括号数量 &lt; 右括号数量」时剥离，
        /// 于是 (https://a.com) 剥掉尾括号，而 https://en.wikipedia.org/wiki/A_(b) 的尾括号成对、保留。
        /// 括号计数随剥离同步扣减（在已裁剪的前缀上重算），而不是始终拿原始串计数：
        /// 否则 https://a.com/a_(b)) 会把成对的那个 ) 也一起剥光。
        /// </summary>
        private static string StripTrailingPunctuation(string s)
        {
            int end = s.Length;
            int openParen = 0, closeParen = 0, openBracket = 0, closeBracket = 0, openBrace = 0, closeBrace = 0;
            for (int k = 0; k < end; k++)
            {
                switch (s[k])
                {
                    case '(': openParen++; break;
                    case ')': closeParen++; break;
                    case '[': openBracket++; break;
                    case ']': closeBracket++; break;
                    case '{': openBrace++; break;
                    case '}': closeBrace++; break;
                }
            }

            while (end > 0)
            {
                char c = s[end - 1];
                bool strip;
                switch (c)
                {
                    case '.': case ',': case ';': case ':': case '!': case '?': case '\'': case '"':
                    case '。': case '，': case '、': case '；': case '：': case '！': case '？':
                    case '）': case '】': case '》': case '」': case '』': case '“': case '”':
                    case '‘': case '’': case '…':
                        strip = true;
                        break;
                    case ')': strip = openParen < closeParen; break;
                    case ']': strip = openBracket < closeBracket; break;
                    case '}': strip = openBrace < closeBrace; break;
                    default:
                        strip = false;
                        break;
                }
                if (!strip)
                    break;

                end--;
                switch (c) // 同步扣减，使下一次判定的计数与「已裁剪的候选串」保持一致
                {
                    case ')': closeParen--; break;
                    case ']': closeBracket--; break;
                    case '}': closeBrace--; break;
                }
            }

            return end == s.Length ? s : s.Substring(0, end);
        }
    }
}
