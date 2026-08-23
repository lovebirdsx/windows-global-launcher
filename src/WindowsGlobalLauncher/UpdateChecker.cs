using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace CommandLauncher
{
    /// <summary>一次可用更新的完整描述（由 GitHub Release JSON 解析而来）。</summary>
    public sealed class UpdateInfo
    {
        public Version Version { get; init; } = new(0, 0, 0);  // 从 tag_name 去掉 v 前缀解析
        public string TagName { get; init; } = "";             // 如 "v1.2.3"
        public string ReleaseNotes { get; init; } = "";        // release body 原文（可能为空）
        public string HtmlUrl { get; init; } = "";             // release 页面 URL
        public string AssetName { get; init; } = "";           // 如 WindowsGlobalLauncher-v1.2.3-win-x64.zip
        public string AssetUrl { get; init; } = "";            // 资产 browser_download_url
        public long AssetSize { get; init; }                   // 资产字节数，未知为 0
        public string Sha256 { get; init; } = "";              // 来自资产 digest 字段（"sha256:<hex>"）取出的小写 hex，取不到为空串
        public string Sha256Url { get; init; } = "";           // 同名 + ".sha256" 校验资产的下载 URL，没有为空串
    }

    /// <summary>一次检查的结果：区分「成功拿到 release」「限流」「其它失败」，便于调用方决定提示文案与是否写检查时间戳。</summary>
    public sealed class UpdateCheckResult
    {
        public UpdateInfo? Info { get; init; }        // 成功解析到最新 release 时非空（无论它是否比当前版本新）
        public string? Error { get; init; }           // 失败原因（中文短句），成功为 null
        public bool RateLimited { get; init; }        // 是否被 GitHub 限流（HTTP 403/429）
    }

    /// <summary>
    /// GitHub Release 更新检查：请求 releases/latest 并解析出可用更新。
    /// 只做纯网络查询与解析，不读写 AppState、不判断版本高低；版本比较与状态读写由其它公开方法承担。
    /// </summary>
    public static class UpdateChecker
    {
        /// <summary>release 页面 URL（手动查看/手动下载入口）。</summary>
        public const string ReleasePageUrl = "https://github.com/lovebirdsx/windows-global-launcher/releases/latest";

        /// <summary>GitHub REST API 的 latest release 端点（对 api.github.com 不做镜像回退，镜像只可靠代理资产下载）。</summary>
        private const string ApiUrl = "https://api.github.com/repos/lovebirdsx/windows-global-launcher/releases/latest";

        /// <summary>走系统代理的 HttpClient（默认路径，尊重用户的代理配置）：单源连接超时 15s，整体 30s。</summary>
        private static readonly HttpClient HttpViaProxy = CreateHttpClient(useProxy: true);

        /// <summary>强制直连的 HttpClient（UseProxy=false）：代理被限流/拦截时的回退路径。</summary>
        private static readonly HttpClient HttpDirect = CreateHttpClient(useProxy: false);

        private static HttpClient CreateHttpClient(bool useProxy)
        {
            var client = new HttpClient(new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(15),
                // 静态 HttpClient 的连接会长期驻留，不设生命周期上限时 DNS 变更不会被感知
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                // useProxy=false 时彻底绕过系统代理：代理出口 IP 被 GitHub 限流（共享节点 60 次/小时被撞满）
                // 或中间设备拦截时，回退到用户自己的直连出口再试一次
                UseProxy = useProxy,
            })
            {
                Timeout = TimeSpan.FromSeconds(30),
            };

            // GitHub API 要求必须带 User-Agent，否则直接返回 403
            client.DefaultRequestHeaders.Add("User-Agent", $"WindowsGlobalLauncher/{App.AppVersionString}");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            return client;
        }

        /// <summary>
        /// 纯网络查询：请求 releases/latest 并解析。不读写 AppState、不判断版本高低。
        /// 先走系统代理；拿到 403/429 或网络异常时自动用强制直连重试一次——
        /// 代理出口 IP 多为共享节点，易被 GitHub 限流（60 次/小时被撞满），而用户的直连出口通常干净。
        /// </summary>
        public static async Task<UpdateCheckResult> FetchLatestAsync()
        {
            // 第一跳：走系统代理（尊重用户代理配置；没配代理时 UseProxy=true 也等价直连）
            var first = await FetchOnceAsync(HttpViaProxy, "代理").ConfigureAwait(false);
            if (first.Result != null)
                return first.Result;

            // 第一跳失败（限流/网络异常）：换强制直连再试一次
            Logger.LogInfo($"经代理请求失败（{first.FailureReason}），尝试直连重试");
            var second = await FetchOnceAsync(HttpDirect, "直连").ConfigureAwait(false);
            if (second.Result != null)
                return second.Result;

            // 两跳都失败：优先报告「更像真因」的那个——限流比网络异常更有诊断价值，故优先报第一跳的限流
            // Error 不带「检查更新失败：」前缀（前缀由 UpdateCoordinator 弹窗时统一加）
            var report = first.Result == null && first.WasRateLimited ? first : second;
            Logger.LogWarning($"检查更新失败：{report.FailureReason}（代理与直连均已尝试）");
            return new UpdateCheckResult { Error = report.FailureReason, RateLimited = report.WasRateLimited };
        }

        /// <summary>单次请求的结果包装：成功/业务失败时 Result 非空；可回退的失败（限流/网络异常）时填充 FailureReason。</summary>
        private sealed class FetchAttempt
        {
            public UpdateCheckResult? Result { get; init; }   // 成功、404、解析失败等「不必换路重试」的终态
            public string FailureReason { get; init; } = "";  // 可回退失败的中文原因（限流/网络异常）
            public bool WasRateLimited { get; init; }         // 本次失败是否是 403/429 限流
        }

        /// <summary>用指定 client 请求一次 releases/latest。终态（含成功）返回 Result，可回退失败返回 FailureReason。</summary>
        private static async Task<FetchAttempt> FetchOnceAsync(HttpClient client, string pathLabel)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await client.GetAsync(ApiUrl).ConfigureAwait(false);

                // GitHub 限流（403/429）：可回退失败——换条路（直连/代理）可能就通了
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                    response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    string reason = await DescribeRateLimitAsync(response).ConfigureAwait(false);
                    Logger.LogWarning($"经{pathLabel}请求被限流（HTTP {(int)response.StatusCode}）：{reason}");
                    return new FetchAttempt { FailureReason = reason, WasRateLimited = true };
                }

                // 仓库尚未发布任何 release（终态，换路无意义）
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    const string notFound = "尚未发布任何正式版本";
                    Logger.LogWarning(notFound);
                    return new FetchAttempt { Result = new UpdateCheckResult { Error = notFound } };
                }

                // 其它 HTTP 错误（5xx 等）：也允许换路重试——可能是代理节点到 GitHub 的链路问题
                if (!response.IsSuccessStatusCode)
                {
                    string reason = $"GitHub 接口返回 HTTP {(int)response.StatusCode}";
                    Logger.LogWarning($"经{pathLabel}请求失败：{reason}");
                    return new FetchAttempt { FailureReason = reason };
                }

                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                var info = ParseRelease(json, out string? parseError);
                if (parseError != null)
                {
                    Logger.LogWarning(parseError);
                    return new FetchAttempt { Result = new UpdateCheckResult { Error = parseError } };
                }

                // info 为 null 且无错误 = 最新 release 是草稿/预发布，视为无可用正式版（不是错误）
                return new FetchAttempt { Result = new UpdateCheckResult { Info = info } };
            }
            catch (Exception ex)
            {
                // 断网/超时/DNS 失败等：可回退失败，换条路再试
                string reason = FriendlyNetworkError(ex);
                Logger.LogWarning($"经{pathLabel}请求异常：{ex.GetType().Name}: {ex.Message}");
                return new FetchAttempt { FailureReason = reason };
            }
            finally
            {
                response?.Dispose();
            }
        }

        /// <summary>
        /// 把 403/429 响应翻译成给用户看的中文原因：读 X-RateLimit-Remaining 区分
        /// 「GitHub 官方限流」（Remaining=0，附本地重置时间）与「中间设备拦截的 403」（无限流头）。
        /// </summary>
        private static async Task<string> DescribeRateLimitAsync(HttpResponseMessage response)
        {
            string? remaining = GetHeader(response, "X-RateLimit-Remaining");
            string? reset = GetHeader(response, "X-RateLimit-Reset");
            string? server = GetHeader(response, "Server");
            string bodyPreview = await ReadBodyPreviewAsync(response).ConfigureAwait(false);
            Logger.LogWarning($"403 详情：X-RateLimit-Remaining={remaining ?? "无"} Server={server ?? "无"}；响应体摘要：{bodyPreview}");

            // GitHub 官方限流：响应头带 X-RateLimit-Remaining: 0
            if (remaining == "0")
            {
                if (long.TryParse(reset, out long resetUnix))
                {
                    var localReset = DateTimeOffset.FromUnixTimeSeconds(resetUnix).LocalDateTime;
                    return $"GitHub 接口访问受限（当前网络出口 IP 请求过于频繁），预计 {localReset:HH:mm} 后恢复，也可切换代理节点后重试";
                }
                return "GitHub 接口访问受限（当前网络出口 IP 请求过于频繁），请稍后再试，也可切换代理节点后重试";
            }

            // 没有限流头的 403：多半是防火墙/代理软件返回的拦截页
            return "GitHub 接口访问被拦截（HTTP 403），可能是当前网络或代理节点屏蔽了 GitHub，请切换网络或代理节点后重试";
        }

        /// <summary>把网络异常翻译成给用户看的中文短句。</summary>
        private static string FriendlyNetworkError(Exception ex)
        {
            // HttpRequestException 的 Message 通常已包含底层原因（DNS 失败/连接被拒绝/超时）
            if (ex is TaskCanceledException || ex is TimeoutException)
                return "连接 GitHub 超时，请检查网络或代理后重试";
            if (ex is HttpRequestException httpEx)
                return $"无法连接 GitHub：{httpEx.Message}";
            return ex.Message;
        }

        /// <summary>防御式取响应头第一个值：头不存在返回 null，绝不抛异常。</summary>
        private static string? GetHeader(HttpResponseMessage response, string name)
        {
            if (response.Headers.TryGetValues(name, out var values))
            {
                foreach (var v in values)
                    return v;
            }
            return null;
        }

        /// <summary>读取 403 响应体前 300 字符用于排查（限流页与防火墙拦截页内容完全不同），失败返回占位串。</summary>
        private static async Task<string> ReadBodyPreviewAsync(HttpResponseMessage response)
        {
            try
            {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                body = body.Replace("\r", " ").Replace("\n", " ").Trim();
                return body.Length > 300 ? body.Substring(0, 300) + "…" : body;
            }
            catch (Exception ex)
            {
                return $"(读取响应体失败：{ex.Message})";
            }
        }

        /// <summary>该版本是否比当前运行版本新。</summary>
        public static bool IsNewerThanCurrent(UpdateInfo info)
        {
            return CompareVersion(info.Version, App.AppVersion) > 0;
        }

        /// <summary>距上次自动检查是否已满 24 小时（供启动时的自动检查节流；手动检查不调用它）。</summary>
        public static bool ShouldAutoCheck()
        {
            DateTime last = AppState.Instance.GetLastUpdateCheckUtc();
            // 未来时间（时钟回拨或手改 JSON）时也允许检查，避免「时钟回拨后永远不检查」
            if (last > DateTime.UtcNow)
                return true;
            return DateTime.UtcNow - last >= TimeSpan.FromHours(24);
        }

        /// <summary>记录本次检查时间（UTC now）。</summary>
        public static void MarkChecked()
        {
            AppState.Instance.SetLastUpdateCheckUtc(DateTime.UtcNow);
        }

        /// <summary>该版本是否被用户「跳过」。</summary>
        public static bool IsSkipped(UpdateInfo info)
        {
            string skipped = AppState.Instance.GetSkippedUpdateVersion();
            if (string.IsNullOrWhiteSpace(skipped))
                return false;
            // 解析失败视为未跳过（用户手改 JSON 写坏时不应误判为已跳过）
            if (!Version.TryParse(skipped.Trim(), out var skippedVersion) || skippedVersion == null)
                return false;
            return CompareVersion(info.Version, skippedVersion) == 0;
        }

        /// <summary>记录「跳过此版本」。</summary>
        public static void SkipVersion(UpdateInfo info)
        {
            AppState.Instance.SetSkippedUpdateVersion(info.Version.ToString());
            Logger.LogInfo($"已跳过版本 {info.Version} 的更新提醒");
        }

        /// <summary>
        /// 解析 release JSON。返回 null 有两种含义：草稿/预发布（无可用正式版，error 为 null）
        /// 或解析失败（error 非 null）。两者都绝不抛出异常。
        /// </summary>
        private static UpdateInfo? ParseRelease(string json, out string? error)
        {
            error = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    error = "GitHub 返回的 release 数据格式异常";
                    return null;
                }

                // 草稿或预发布不算正式版，视为无可用更新（不是错误）
                if (TryGetBool(root, "draft") == true || TryGetBool(root, "prerelease") == true)
                    return null;

                string? tagName = TryGetString(root, "tag_name");
                if (string.IsNullOrWhiteSpace(tagName))
                {
                    error = "release 缺少 tag_name";
                    return null;
                }

                // tag_name 去掉前导 v/V 后解析版本号
                string versionText = tagName.Trim();
                if (versionText.Length > 0 && (versionText[0] == 'v' || versionText[0] == 'V'))
                    versionText = versionText.Substring(1);
                if (!Version.TryParse(versionText, out var version) || version == null)
                {
                    error = $"无法解析版本号：{tagName}";
                    return null;
                }

                string body = TryGetString(root, "body") ?? "";
                string htmlUrl = TryGetString(root, "html_url") ?? "";

                // 资产选择：优先完全匹配 WindowsGlobalLauncher-<tag_name>-win-x64.zip，退而取第一个 zip + win-x64
                JsonElement? selectedAsset = SelectAsset(root, tagName);
                if (selectedAsset == null)
                {
                    error = "最新版本缺少 win-x64 安装包资产";
                    return null;
                }
                JsonElement asset = selectedAsset.Value;

                string assetName = TryGetString(asset, "name") ?? "";
                string assetUrl = TryGetString(asset, "browser_download_url") ?? "";
                long assetSize = TryGetInt64(asset, "size") ?? 0;
                string sha256 = TryGetSha256(asset);
                string sha256Url = FindSha256Url(root, assetName);

                return new UpdateInfo
                {
                    Version = version,
                    TagName = tagName,
                    ReleaseNotes = body,
                    HtmlUrl = htmlUrl,
                    AssetName = assetName,
                    AssetUrl = assetUrl,
                    AssetSize = assetSize,
                    Sha256 = sha256,
                    Sha256Url = sha256Url,
                };
            }
            catch (Exception ex)
            {
                // 防御：任何字段缺失/格式不符都不应抛出，统一转为失败
                error = $"解析 release 数据失败：{ex.Message}";
                return null;
            }
        }

        /// <summary>从 assets 数组中选择安装包资产：优先精确名匹配，否则取第一个 zip + win-x64。</summary>
        private static JsonElement? SelectAsset(JsonElement root, string tagName)
        {
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("assets", out var assets) ||
                assets.ValueKind != JsonValueKind.Array)
                return null;

            string exactName = $"WindowsGlobalLauncher-{tagName}-win-x64.zip";
            JsonElement? fallback = null;

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object)
                    continue;
                string? name = TryGetString(asset, "name");
                if (string.IsNullOrEmpty(name))
                    continue;
                if (name == exactName)
                    return asset;
                if (fallback == null &&
                    name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                    name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
                    fallback = asset;
            }

            return fallback;
        }

        /// <summary>在 assets 数组中找「选中的 zip 名 + .sha256」的校验资产，返回其下载 URL；没有为空串。</summary>
        private static string FindSha256Url(JsonElement root, string assetName)
        {
            if (string.IsNullOrEmpty(assetName))
                return "";
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("assets", out var assets) ||
                assets.ValueKind != JsonValueKind.Array)
                return "";

            string shaName = assetName + ".sha256";
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object)
                    continue;
                if (TryGetString(asset, "name") == shaName)
                    return TryGetString(asset, "browser_download_url") ?? "";
            }
            return "";
        }

        /// <summary>
        /// 解析资产 digest（形如 "sha256:abc123..."），取冒号后的小写 hex；字段不存在或格式不符返回空串。
        /// 只接受标准的 64 位十六进制：格式异常时返回空串让调用方退到 .sha256 文件，
        /// 而不是拿一个坏值去比对导致更新永远失败。
        /// </summary>
        private static string TryGetSha256(JsonElement asset)
        {
            string? digest = TryGetString(asset, "digest");
            if (string.IsNullOrEmpty(digest))
                return "";
            int idx = digest.IndexOf(':');
            if (idx < 0)
                return "";

            string hex = digest.Substring(idx + 1).Trim().ToLowerInvariant();
            if (hex.Length != 64 || !IsHex(hex))
            {
                Logger.LogWarning($"资产 digest 格式异常，忽略该校验值：{digest}");
                return "";
            }

            return hex;
        }

        private static bool IsHex(string text)
        {
            foreach (char c in text)
            {
                bool isHexDigit = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!isHexDigit)
                    return false;
            }
            return true;
        }

        /// <summary>防御式取字符串属性：字段不存在或类型不符返回 null，绝不抛异常。</summary>
        private static string? TryGetString(JsonElement obj, string name)
        {
            if (obj.ValueKind == JsonValueKind.Object &&
                obj.TryGetProperty(name, out var el) &&
                el.ValueKind == JsonValueKind.String)
                return el.GetString();
            return null;
        }

        /// <summary>防御式取布尔属性：字段不存在或类型不符返回 null。</summary>
        private static bool? TryGetBool(JsonElement obj, string name)
        {
            if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el))
            {
                if (el.ValueKind == JsonValueKind.True)
                    return true;
                if (el.ValueKind == JsonValueKind.False)
                    return false;
            }
            return null;
        }

        /// <summary>防御式取整型属性：字段不存在或类型不符返回 null。</summary>
        private static long? TryGetInt64(JsonElement obj, string name)
        {
            if (obj.ValueKind == JsonValueKind.Object &&
                obj.TryGetProperty(name, out var el) &&
                el.ValueKind == JsonValueKind.Number &&
                el.TryGetInt64(out var v))
                return v;
            return null;
        }

        /// <summary>
        /// 版本比较：只比较 Major/Minor/Build，且把负的 Build 视为 0。
        /// System.Version 的 "1.2.3"（Revision=-1）与 "1.2.3.0"（Revision=0）不相等且后者更大，
        /// 直接比较会在三段/四段混用时误判，故此处刻意忽略 Revision 并归一化 Build。
        /// </summary>
        private static int CompareVersion(Version a, Version b)
        {
            int compare = a.Major.CompareTo(b.Major);
            if (compare != 0)
                return compare;

            compare = a.Minor.CompareTo(b.Minor);
            if (compare != 0)
                return compare;

            int aBuild = a.Build < 0 ? 0 : a.Build;
            int bBuild = b.Build < 0 ? 0 : b.Build;
            return aBuild.CompareTo(bBuild);
        }
    }
}
