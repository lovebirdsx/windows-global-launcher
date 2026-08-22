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

        /// <summary>共享 HttpClient：单源连接超时 15s，整体 30s（检查是小请求，无需长时间兜底）。</summary>
        private static readonly HttpClient Http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient(new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(15),
                // 静态 HttpClient 的连接会长期驻留，不设生命周期上限时 DNS 变更不会被感知
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
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

        /// <summary>纯网络查询：请求 releases/latest 并解析。不读写 AppState、不判断版本高低。</summary>
        public static async Task<UpdateCheckResult> FetchLatestAsync()
        {
            try
            {
                using var response = await Http.GetAsync(ApiUrl).ConfigureAwait(false);

                // GitHub 限流（403/429）：调用方应据此调整提示文案，且不写检查时间戳
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                    response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    var limited = new UpdateCheckResult
                    {
                        RateLimited = true,
                        Error = $"GitHub 接口访问受限（HTTP {(int)response.StatusCode}），请稍后再试",
                    };
                    Logger.LogWarning(limited.Error);
                    return limited;
                }

                // 仓库尚未发布任何 release
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    var notFound = new UpdateCheckResult { Error = "尚未发布任何正式版本" };
                    Logger.LogWarning(notFound.Error);
                    return notFound;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var failed = new UpdateCheckResult { Error = $"检查更新失败（HTTP {(int)response.StatusCode}）" };
                    Logger.LogWarning(failed.Error);
                    return failed;
                }

                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                var info = ParseRelease(json, out string? parseError);
                if (parseError != null)
                {
                    Logger.LogWarning(parseError);
                    return new UpdateCheckResult { Error = parseError };
                }

                // info 为 null 且无错误 = 最新 release 是草稿/预发布，视为无可用正式版（不是错误）
                return new UpdateCheckResult { Info = info };
            }
            catch (Exception ex)
            {
                // 断网/超时/解析失败等一律转成失败结果，绝不抛出给调用方
                var error = $"检查更新失败：{ex.Message}";
                Logger.LogWarning(error);
                return new UpdateCheckResult { Error = error };
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
