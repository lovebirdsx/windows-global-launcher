using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace CommandLauncher
{
    /// <summary>
    /// 更新包的下载、校验与自替换重启。
    /// <para>
    /// 自替换采用「重命名运行中的 exe」方案（Chrome 等自更新程序的经典做法）：Windows 允许重命名一个正在运行的
    /// exe（只改目录项，已打开的映像句柄仍指向同一文件），但不允许删除或覆盖它。于是
    /// 「旧 exe 改名为 .old → 新 exe 写入原路径 → 启动新进程 → 旧进程退出 → 新进程删掉 .old」
    /// 就能在不落地任何外部脚本的前提下完成替换，也没有「外部脚本等待 PID 期间被杀」的竞态窗口。
    /// </para>
    /// <para>
    /// 新进程带 <c>--wait-for-pid &lt;旧进程 pid&gt;</c> 启动，在创建任何窗口/热键/钩子/颜色矩阵之前先等旧进程退出，
    /// 避免新旧实例抢占同一批全局资源（RegisterHotKey 冲突、双托盘图标、旧进程 OnExit 的 ResetEffect 抹掉新进程刚设的护眼矩阵）。
    /// </para>
    /// </summary>
    public static class UpdateInstaller
    {
        /// <summary>更新工作目录：下载的 zip 与解压出的文件都放这里，与程序安装目录隔离。</summary>
        private static string UpdateRoot => Path.Combine(App.BaseDir, "update");

        /// <summary>解压目录（每次更新前清空重建）。</summary>
        private static string StagingDir => Path.Combine(UpdateRoot, "staging");

        /// <summary>下载中的临时文件名（校验通过后才会被解压，不会直接落到安装目录）。</summary>
        private static string DownloadTempPath => Path.Combine(UpdateRoot, "download.tmp");

        /// <summary>发布包内的可执行文件名（打包契约的一部分，见 .github/workflows/release.yml）。</summary>
        private const string ExeFileName = "WindowsGlobalLauncher.exe";

        /// <summary>旧版本备份的后缀（配合 .old1 ~ .old9 编号，见 <see cref="EnumerateBackupCandidates"/>）。</summary>
        private const string BackupSuffix = ".old";

        /// <summary>备份编号上限：.old 之外还可用 .old1 ~ .old9，共 10 个槽位。</summary>
        private const int MaxBackupSlots = 9;

        /// <summary>新 exe 落位前的临时后缀：先复制成 exe.new，再用同卷 rename 换上去，避免复制中途失败污染原路径。</summary>
        private const string PendingSuffix = ".new";

        /// <summary>进度上报间隔：每 256KB 触发一次 ProgressChanged（更新包只有几 MB，比 OCR 引擎包报得密一些）。</summary>
        private const long ProgressReportInterval = 256L * 1024;

        /// <summary>新 exe 的最小合理体积（粗校验，防止解压出损坏/占位文件就去替换）。</summary>
        private const long MinValidExeSize = 256L * 1024;

        /// <summary>等待旧进程退出的上限：旧进程 OnExit 通常在 1 秒内完成，15 秒足够覆盖极端卡顿。</summary>
        private const int WaitPreviousTimeoutMs = 15_000;

        /// <summary>0 = 空闲，1 = 进行中（Interlocked 防重入，避免用户连点「立即更新」发起两次替换）。</summary>
        private static int _busy;

        /// <summary>共享 HttpClient：单源连接超时 30s，整体 10 分钟兜底（与 OcrEngineInstaller 同款理由）。</summary>
        private static readonly HttpClient Http = new(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            // 静态 HttpClient 的连接会长期驻留，不设生命周期上限时 DNS 变更（镜像换 IP、故障切换）不会被感知
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };

        /// <summary>更新流程是否正在进行（防重入）。</summary>
        public static bool IsBusy => Interlocked.CompareExchange(ref _busy, 0, 0) == 1;

        /// <summary>下载进度：(已下载字节, 总字节)，总字节未知时为 -1。从后台线程触发，订阅方自行切 UI 线程。</summary>
        public static event Action<long, long>? ProgressChanged;

        /// <summary>阶段状态文本（如「正在校验…」「正在解压…」）。从后台线程触发，订阅方自行切 UI 线程。</summary>
        public static event Action<string>? StatusChanged;

        /// <summary>
        /// 执行完整更新流程：下载 → 校验 → 解压 → 替换 exe → 启动新进程。
        /// <para>
        /// 返回 null 表示成功且新进程已启动，调用方应**立即**退出当前进程（<c>Application.Current.Shutdown()</c>）；
        /// 返回非 null 表示失败，内容是可直接展示给用户的中文原因，此时当前进程状态未被破坏，可继续正常使用。
        /// </para>
        /// </summary>
        public static async Task<string?> DownloadAndApplyAsync(UpdateInfo info)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                return "更新已在进行中";

            try
            {
                // 先做写权限预检：目录不可写时一个字节都不下载，直接引导用户手动更新
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    Logger.LogWarning($"无法确定当前程序路径（ProcessPath={exePath ?? "null"}），放弃自动更新");
                    return "无法确定当前程序路径，请手动下载更新";
                }

                string exeDir = Path.GetDirectoryName(exePath) ?? "";
                if (!CanWriteToDirectory(exeDir))
                {
                    Logger.LogWarning($"程序所在目录不可写，放弃自动更新：{exeDir}");
                    return $"程序所在目录不可写（{exeDir}），请手动下载更新，或将程序移动到有写权限的目录";
                }

                Directory.CreateDirectory(UpdateRoot);

                // 1. 下载（多源回退）
                NotifyStatus("正在下载更新包…");
                string? downloadError = await DownloadWithFallbackAsync(info).ConfigureAwait(false);
                if (downloadError != null)
                    return downloadError;

                // 2. 校验
                NotifyStatus("正在校验更新包…");
                string? verifyError = await VerifyAsync(info).ConfigureAwait(false);
                if (verifyError != null)
                {
                    TryDeleteFile(DownloadTempPath); // 校验不过的包一律丢弃，避免下次误用
                    return verifyError;
                }

                // 3. 解压并定位新 exe
                NotifyStatus("正在解压更新包…");
                string? newExePath = ExtractAndLocateExe(out string? extractError);
                if (newExePath == null)
                    return extractError ?? "解压更新包失败";

                // 4. 替换并启动新进程（此步之后当前进程即将退出）
                NotifyStatus("正在安装并重启…");
                return ApplyAndRestart(exePath, newExePath, info);
            }
            catch (Exception ex)
            {
                Logger.LogError("自动更新失败", ex);
                return $"更新失败：{ex.Message}";
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        /// <summary>
        /// 等待旧进程退出（更新重启后由新进程在初始化任何全局资源之前调用）。
        /// 旧进程已退出（<see cref="ArgumentException"/>）属正常路径；超时也继续启动，不能因为等待失败就起不来。
        /// <para>
        /// 等待前先核对进程名：Windows 的 pid 会复用，旧进程可能在新进程启动前就已退出、其 pid 被系统分配给了
        /// 一个毫不相干的长命进程；不校验就会白等满 15 秒，启动被硬生生拖慢。
        /// </para>
        /// </summary>
        public static void WaitForPreviousInstance(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);

                string expectedName = Process.GetCurrentProcess().ProcessName;
                if (!string.Equals(process.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogWarning($"更新重启：pid {pid} 已被其它进程（{process.ProcessName}）复用，跳过等待");
                    return;
                }

                if (process.WaitForExit(WaitPreviousTimeoutMs))
                    Logger.LogInfo($"更新重启：旧进程（pid {pid}）已退出，继续启动");
                else
                    Logger.LogWarning($"更新重启：等待旧进程（pid {pid}）退出超时（{WaitPreviousTimeoutMs}ms），仍继续启动。若旧实例仍存活，可能出现双托盘图标与热键注册冲突");
            }
            catch (ArgumentException)
            {
                // 进程已不存在，属正常路径（旧进程退出得比新进程启动更快）
                Logger.LogInfo($"更新重启：旧进程（pid {pid}）已不存在，继续启动");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"更新重启：等待旧进程（pid {pid}）退出时异常，仍继续启动：{ex.Message}");
            }
        }

        /// <summary>
        /// 清理上次更新遗留的 .old 备份、未落位的 .new 临时文件与下载/解压临时文件（每次启动调用一次，幂等且静默）。
        /// .old 在刚更新完那次启动通常能删掉；被杀软扫描占用时留到下次启动再清。
        /// <para>
        /// 刻意用 <see cref="EnumerateBackupCandidates"/> 的精确白名单而不是 <c>.old*</c> 通配：
        /// 通配的 <c>*</c> 会匹配任意后缀（含点号），用户自己放在同目录的
        /// <c>WindowsGlobalLauncher.exe.old.bak</c> 之类文件会被当成残留永久删除。
        /// </para>
        /// </summary>
        public static void CleanupLeftovers()
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    foreach (string leftover in EnumerateBackupCandidates(exePath))
                        TryDeleteFileWithRetry(leftover);

                    // 未落位的 .new：说明上次替换在「复制完成」与「rename 落位」之间失败过，本体已回滚，残留可安全删除
                    TryDeleteFileWithRetry(exePath + PendingSuffix);
                }

                TryDeleteFile(DownloadTempPath);
                TryDeleteDirectory(StagingDir);
            }
            catch (Exception ex)
            {
                // 清理是纯善后动作，任何失败都不能影响启动
                Logger.LogWarning($"清理更新残留文件失败：{ex.Message}");
            }
        }

        /// <summary>依次尝试直连与两个镜像前缀下载资产，成功即止。全部失败返回中文错误。</summary>
        private static async Task<string?> DownloadWithFallbackAsync(UpdateInfo info)
        {
            string? lastError = null;

            foreach (string url in BuildMirrorUrls(info.AssetUrl))
            {
                try
                {
                    Logger.LogInfo($"开始下载更新包：{url}");
                    await DownloadAsync(url, DownloadTempPath, info.AssetSize).ConfigureAwait(false);
                    return null;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.LogWarning($"下载源失败（{url}）：{ex.Message}");
                    TryDeleteFile(DownloadTempPath);
                }
            }

            return $"下载更新包失败：{lastError ?? "所有下载源均不可用"}";
        }

        /// <summary>下载源列表：GitHub 直连优先，其后是两个公共加速镜像（与 OcrEngineInstaller 用同一组）。</summary>
        private static string[] BuildMirrorUrls(string assetUrl)
        {
            return
            [
                assetUrl,
                "https://ghfast.top/" + assetUrl,
                "https://gh-proxy.com/" + assetUrl,
            ];
        }

        /// <summary>流式下载到 tmpPath 并按间隔上报进度。失败抛异常由调用方按源回退。</summary>
        private static async Task DownloadAsync(string url, string tmpPath, long expectedSize)
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // 响应头里没有长度时（镜像常见）退回用 API 给出的资产体积，保证进度条仍有分母
            long total = response.Content.Headers.ContentLength ?? (expectedSize > 0 ? expectedSize : -1);

            using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var dest = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            long totalRead = 0;
            long nextReportAt = ProgressReportInterval;
            var buffer = new byte[81920];
            while (true)
            {
                int read = await source.ReadAsync(buffer).ConfigureAwait(false);
                if (read <= 0)
                    break;

                await dest.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                totalRead += read;
                if (totalRead >= nextReportAt)
                {
                    NotifyProgress(totalRead, total);
                    nextReportAt = totalRead + ProgressReportInterval;
                }
            }

            await dest.FlushAsync().ConfigureAwait(false);
            NotifyProgress(totalRead, total);

            if (totalRead <= 0)
                throw new IOException("下载内容为空");
        }

        /// <summary>
        /// 校验下载的包：先比体积，再比 SHA256。
        /// 期望哈希优先取 API 资产的 digest 字段，其次下载 .sha256 校验资产；两者都拿不到时**拒绝安装**
        /// （理由见方法内注释：仅体积校验对一个即将被管理员权限执行的文件而言等同于没有校验）。
        /// </summary>
        private static async Task<string?> VerifyAsync(UpdateInfo info)
        {
            var file = new FileInfo(DownloadTempPath);
            if (!file.Exists || file.Length == 0)
                return "更新包下载不完整";

            if (info.AssetSize > 0 && file.Length != info.AssetSize)
                return $"更新包体积异常（期望 {info.AssetSize} 字节，实际 {file.Length} 字节）";

            // 校验值优先取资产 digest：它来自 api.github.com 的直连响应，是整条链路里唯一未经镜像中转的可信锚点。
            // 取不到才退而下载 .sha256 资产（TryFetchSha256Async 内部同样直连优先）。
            string expected = info.Sha256;
            if (string.IsNullOrEmpty(expected) && !string.IsNullOrEmpty(info.Sha256Url))
                expected = await TryFetchSha256Async(info.Sha256Url).ConfigureAwait(false);

            if (string.IsNullOrEmpty(expected))
            {
                // 刻意**拒绝**而不是降级为仅体积校验：更新包是要拿管理员权限直接替换自身可执行文件的，
                // 体积相同的替换品做起来毫无难度，仅体积校验等同于没有校验。
                // 本项目的 release.yml 保证每个 zip 都带 .sha256 资产，且 GitHub 会自动为资产生成 digest，
                // 两者同时缺失只可能是异常情况，此时引导用户去发布页手动下载才是安全的选择。
                Logger.LogWarning("未获取到更新包的 SHA256 校验值（digest 与 .sha256 资产均不可用），拒绝自动安装");
                return "无法获取更新包的校验值，出于安全考虑已取消自动更新，请到发布页手动下载";
            }

            string actual = await ComputeSha256Async(DownloadTempPath).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogWarning($"更新包校验失败：期望 {expected}，实际 {actual}");
                return "更新包校验失败（文件可能在传输中损坏或被篡改），已丢弃";
            }

            Logger.LogInfo("更新包 SHA256 校验通过");
            return null;
        }

        /// <summary>
        /// 下载 .sha256 校验文件并取出十六进制哈希；失败返回空串（由调用方拒绝安装）。
        /// 直连优先，只有直连失败才退到镜像并记 WARN——镜像同时代理安装包与校验值时，校验的意义会降到只剩「防传输损坏」。
        /// </summary>
        private static async Task<string> TryFetchSha256Async(string sha256Url)
        {
            string[] urls = BuildMirrorUrls(sha256Url);

            for (int i = 0; i < urls.Length; i++)
            {
                string url = urls[i];
                try
                {
                    string text = await Http.GetStringAsync(url).ConfigureAwait(false);
                    // 内容形如 "<hex>  <文件名>"（sha256sum 风格），只取第一个空白分隔字段
                    string? first = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (!string.IsNullOrEmpty(first) && first.Length == 64)
                    {
                        if (i > 0)
                            Logger.LogWarning($"校验值取自镜像（{url}），无法排除镜像侧篡改，仅能保证传输完整性");
                        return first.ToLowerInvariant();
                    }

                    Logger.LogWarning($"校验文件内容格式异常（{url}）");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"获取校验文件失败（{url}）：{ex.Message}");
                }
            }

            return "";
        }

        private static async Task<string> ComputeSha256Async(string path)
        {
            using var sha = SHA256.Create();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            byte[] hash = await sha.ComputeHashAsync(stream).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>解压到 staging 目录并定位新 exe。失败时 error 带中文原因，返回 null。</summary>
        private static string? ExtractAndLocateExe(out string? error)
        {
            error = null;

            try
            {
                TryDeleteDirectory(StagingDir);
                Directory.CreateDirectory(StagingDir);

                // 用内置 ZipFile（SharpCompress 仅为 7z 引入，zip 无需它）；.NET Core 已内置条目路径越界防护
                ZipFile.ExtractToDirectory(DownloadTempPath, StagingDir, overwriteFiles: true);
            }
            catch (Exception ex)
            {
                Logger.LogError("解压更新包失败", ex);
                error = $"解压更新包失败：{ex.Message}";
                return null;
            }

            string? newExe = Directory
                .GetFiles(StagingDir, ExeFileName, SearchOption.AllDirectories)
                .FirstOrDefault();

            if (newExe == null)
            {
                Logger.LogWarning($"更新包中未找到 {ExeFileName}");
                error = "更新包内容异常（未找到程序文件）";
                return null;
            }

            long size = new FileInfo(newExe).Length;
            if (size < MinValidExeSize)
            {
                Logger.LogWarning($"更新包中的 {ExeFileName} 体积异常（{size} 字节）");
                error = "更新包内容异常（程序文件不完整）";
                return null;
            }

            return newExe;
        }

        /// <summary>
        /// 替换 exe 并启动新进程。每一步失败都回滚到替换前的状态，绝不留下「旧的已改名、新的没落位」的半残局面。
        /// <para>
        /// 关键顺序：先把新 exe 复制到目标目录的 <c>.new</c> 临时名（此时旧 exe 仍在原位，失败无损），
        /// 复制成功后才用两次同卷 rename 完成交换。**不能直接 File.Copy 到 exePath**——
        /// CopyFile 中途失败（磁盘满/ I/O 错误 / 杀软拦截）不保证清理目标文件，会在原路径留下一个损坏的 exe，
        /// 那时既回滚不了，损坏的 exe 若还能启动还会把唯一的 .old 备份清理掉，彻底不可恢复。
        /// </para>
        /// 成功返回 null（调用方随即退出当前进程）。
        /// </summary>
        private static string? ApplyAndRestart(string exePath, string newExePath, UpdateInfo info)
        {
            string? backupPath = PrepareBackupPath(exePath);
            if (backupPath == null)
            {
                Logger.LogWarning("无法为当前程序准备备份文件名（旧备份被占用），放弃自动更新");
                return "旧版本备份文件被占用，请重启程序后重试";
            }

            // 步骤 1：把新 exe 复制到目标目录的临时名。此步只是新增一个文件，失败不影响现状。
            string pendingPath = exePath + PendingSuffix;
            try
            {
                TryDeleteFile(pendingPath);
                File.Copy(newExePath, pendingPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.LogError("复制新版本到目标目录失败，放弃自动更新（未改动任何文件）", ex);
                TryDeleteFile(pendingPath);
                return $"写入新版本失败：{ex.Message}";
            }

            // 步骤 2：把正在运行的 exe 改名让位（Windows 允许重命名运行中的映像文件）
            try
            {
                File.Move(exePath, backupPath);
            }
            catch (Exception ex)
            {
                Logger.LogError("重命名当前程序失败，放弃自动更新（未改动任何文件）", ex);
                TryDeleteFile(pendingPath);
                return $"无法替换程序文件：{ex.Message}";
            }

            // 步骤 3：新 exe 就位。同目录同卷的 rename，不存在「部分写入」这种中间态。
            try
            {
                File.Move(pendingPath, exePath);
            }
            catch (Exception ex)
            {
                Logger.LogError("新版本就位失败，正在回滚", ex);
                TryDeleteFile(pendingPath);
                RollbackRename(backupPath, exePath);
                return $"写入新版本失败：{ex.Message}";
            }

            // 步骤 4：启动新进程，由它等待本进程退出后再初始化
            try
            {
                StartNewInstance(exePath);
            }
            catch (Exception ex)
            {
                Logger.LogError("启动新版本进程失败，正在回滚", ex);
                RollbackRename(backupPath, exePath);
                return $"启动新版本失败：{ex.Message}";
            }

            Logger.LogInfo($"自动更新完成：{App.AppVersionString} → {info.Version}，新进程已启动，当前进程即将退出");
            return null;
        }

        /// <summary>启动新版本进程，带上 --wait-for-pid 与原有的配置文件参数。</summary>
        private static void StartNewInstance(string exePath)
        {
            var startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
            };
            startInfo.ArgumentList.Add("--wait-for-pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

            // 保留用户通过命令行指定的配置文件路径，否则更新后会退回默认配置
            if (!string.IsNullOrEmpty(StartupArgs.ConfigPath))
                startInfo.ArgumentList.Add(StartupArgs.ConfigPath);

            try
            {
                // Process 对象只用于启动、不做后续监控（新进程反过来等我们退出），拿到就释放句柄
                Process.Start(startInfo)?.Dispose();
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
            {
                // ERROR_ELEVATION_REQUIRED：正常情况下本进程已是管理员、CreateProcess 足够，
                // 万一权限不如预期（例如未来去掉 requireAdministrator），退回 ShellExecute 提权启动
                Logger.LogWarning("以普通方式启动新版本被要求提权，改用管理员方式启动");
                var elevated = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
                    Arguments = BuildQuotedArguments(),
                };
                Process.Start(elevated)?.Dispose();
            }
        }

        /// <summary>ShellExecute 路径不支持 ArgumentList，只能手工拼引号。</summary>
        private static string BuildQuotedArguments()
        {
            string args = $"--wait-for-pid {Environment.ProcessId}";
            if (!string.IsNullOrEmpty(StartupArgs.ConfigPath))
                args += $" \"{StartupArgs.ConfigPath}\"";
            return args;
        }

        /// <summary>
        /// 回滚：把备份改回原名。
        /// <para>
        /// 刻意**无条件**先清掉 exePath 上可能存在的残留文件再搬回备份——不能加
        /// 「exePath 不存在才回滚」这类前置条件：一旦某个失败路径在原路径留下了半个文件，
        /// 回滚就会被静静跳过，用户得到的是一个跑不起来的 exe 加一个永远搬不回去的备份（即变砖），
        /// 而下次启动的 <see cref="CleanupLeftovers"/> 还会把那个唯一可用的备份删掉。
        /// </para>
        /// 失败只能记 ERROR——此时新旧文件都不在原位，必须让用户看到日志。
        /// </summary>
        private static void RollbackRename(string backupPath, string exePath)
        {
            try
            {
                if (!File.Exists(backupPath))
                {
                    Logger.LogError($"回滚失败：备份文件 {backupPath} 不存在，无法恢复原程序");
                    return;
                }

                if (File.Exists(exePath))
                    File.Delete(exePath);

                File.Move(backupPath, exePath);
                Logger.LogInfo("已回滚到更新前的程序文件");
            }
            catch (Exception ex)
            {
                Logger.LogError($"回滚失败：无法把 {backupPath} 移回 {exePath}，请手动重命名恢复", ex);
            }
        }

        /// <summary>
        /// 选一个可用的备份文件名：优先 exe.old，被占用（上次更新的 .old 还没删掉）时依次尝试 .old1 ~ .old9。
        /// 全被占用返回 null。
        /// </summary>
        private static string? PrepareBackupPath(string exePath)
        {
            foreach (string candidate in EnumerateBackupCandidates(exePath))
            {
                if (!File.Exists(candidate))
                    return candidate;

                TryDeleteFile(candidate);
                if (!File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// 备份文件名的完整白名单（.old、.old1 ~ .old9），是「生成备份名」与「清理残留」共用的唯一定义。
        /// 两处必须严格一致：清理范围比生成范围窄会漏删，宽则会误删用户文件。
        /// </summary>
        private static IEnumerable<string> EnumerateBackupCandidates(string exePath)
        {
            for (int i = 0; i <= MaxBackupSlots; i++)
                yield return i == 0 ? exePath + BackupSuffix : exePath + BackupSuffix + i;
        }

        /// <summary>探测目录是否可写：建一个临时文件再删掉。用于在下载前就挡掉 Program Files 等无权限场景。</summary>
        private static bool CanWriteToDirectory(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return false;

            string probe = Path.Combine(dir, $".update-probe-{Environment.ProcessId}.tmp");
            try
            {
                using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.WriteByte(0);
                }

                File.Delete(probe);
                return true;
            }
            catch
            {
                TryDeleteFile(probe);
                return false;
            }
        }

        private static void NotifyProgress(long received, long total)
        {
            try
            {
                ProgressChanged?.Invoke(received, total);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"更新进度回调异常：{ex.Message}");
            }
        }

        private static void NotifyStatus(string text)
        {
            try
            {
                StatusChanged?.Invoke(text);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"更新状态回调异常：{ex.Message}");
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"删除文件失败（{path}）：{ex.Message}");
            }
        }

        /// <summary>带重试的删除：刚更新完时 .old 可能仍被杀软扫描占用，短暂重试即可。</summary>
        private static void TryDeleteFileWithRetry(string path)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (!File.Exists(path))
                        return;

                    File.Delete(path);
                    Logger.LogInfo($"已清理更新残留文件：{path}");
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt == 2)
                    {
                        Logger.LogWarning($"清理更新残留文件失败（{path}），留待下次启动再试：{ex.Message}");
                        return;
                    }

                    Thread.Sleep(300);
                }
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"删除目录失败（{path}）：{ex.Message}");
            }
        }
    }
}
