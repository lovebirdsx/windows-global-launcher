using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives.SevenZip;

namespace CommandLauncher
{
    /// <summary>
    /// 增强 OCR 引擎（RapidOCR-json）安装器：下载 7z 增强包并解压到数据目录 ocr-engine\。
    /// 增强包约 70MB，不随主程序分发，按需下载；直连失败时依次回退镜像源。
    /// </summary>
    public static class OcrEngineInstaller
    {
        /// <summary>官方下载页 URL（手动安装指引用）。</summary>
        private const string ReleasePageUrl = "https://github.com/hiroi-sora/RapidOCR-json/releases";

        /// <summary>直连下载 URL（v0.2.0 的 7z 资产）。</summary>
        private const string DirectUrl =
            "https://github.com/hiroi-sora/RapidOCR-json/releases/download/v0.2.0/RapidOCR-json_v0.2.0.7z";

        /// <summary>依次尝试的下载源（直连 → 两个镜像前缀）。</summary>
        private static readonly string[] DownloadUrls =
        {
            DirectUrl,
            "https://ghfast.top/" + DirectUrl,
            "https://gh-proxy.com/" + DirectUrl,
        };

        /// <summary>有效包的最小体积（约 70MB，粗校验防错误页面/错误响应）。</summary>
        private const long MinValidFileSize = 50L * 1024 * 1024;

        /// <summary>进度上报间隔：每 512KB 触发一次 StatusChanged。</summary>
        private const long ProgressReportInterval = 512L * 1024;

        /// <summary>0 = 空闲，1 = 进行中（Interlocked 防重入）。</summary>
        private static int _busy;

        /// <summary>合流锁：保护 <see cref="_currentInstall"/> 的读写，保证并发调用共享同一安装任务。</summary>
        private static readonly object _installLock = new();

        /// <summary>进行中的安装任务（合流）；完成后由 continuation 清空。null 表示当前无安装任务。</summary>
        private static Task<bool>? _currentInstall;

        private static string EngineRoot => Path.Combine(App.BaseDir, "ocr-engine");

        /// <summary>共享 HttpClient：单源连接（TCP + TLS）超时 30s，整体 10 分钟兜底（70MB 下载需要时间）。</summary>
        private static readonly HttpClient Http = new(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
        })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };

        /// <summary>增强包下载解压是否正在进行（防重入）。</summary>
        public static bool IsBusy => Interlocked.CompareExchange(ref _busy, 0, 0) == 1;

        /// <summary>状态/进度文本变化（如「下载中 12.3 / 70.1 MB」「解压中…」），可能从后台线程触发，订阅方自行切 UI 线程。</summary>
        public static event Action<string>? StatusChanged;

        /// <summary>手动安装指引文本（下载失败时 UI 展示）：官方下载页 URL + 解压目标目录完整路径。</summary>
        public static string ManualInstallHint =>
            $"请手动下载增强 OCR 引擎（RapidOCR-json v0.2.0）：\n{ReleasePageUrl}\n" +
            $"下载后将其中的全部内容解压到：\n{EngineRoot}";

        /// <summary>确保增强引擎已安装：已安装立即返回 true；正在下载则等待同一个进行中的任务；
        /// 否则发起新安装。多处并发调用共享同一个安装任务（合流），不会重复下载。</summary>
        public static Task<bool> EnsureInstalledAsync()
        {
            if (RapidOcrBackend.IsInstalled)
                return Task.FromResult(true);

            lock (_installLock)
            {
                if (_currentInstall != null)
                    return _currentInstall;

                var task = InstallAsync();
                _currentInstall = task;
                _ = ClearWhenDoneAsync(task);
                return task;
            }
        }

        /// <summary>等待安装任务结束后清空 <see cref="_currentInstall"/>（仅当字段仍指向该任务时）。
        /// 观察任务异常防止未观察泄漏；InstallAsync 内部已兜底不会抛，此处仍吞掉保险。</summary>
        private static async Task ClearWhenDoneAsync(Task<bool> task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // 忽略：InstallAsync 自身已 try/catch，不应抛出
            }
            finally
            {
                lock (_installLock)
                {
                    if (ReferenceEquals(_currentInstall, task))
                        _currentInstall = null;
                }
            }
        }

        /// <summary>
        /// 下载并解压增强引擎到数据目录 ocr-engine\。成功 true（内部已调 RapidOcrBackend.InvalidateInstallCache）；
        /// 失败 false 并把原因经 StatusChanged 通知 + 记日志。重入时直接返回 false。
        /// </summary>
        public static async Task<bool> InstallAsync()
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                NotifyStatus("增强引擎安装已在进行中");
                return false;
            }

            try
            {
                Directory.CreateDirectory(EngineRoot);
                string tmpPath = Path.Combine(EngineRoot, "download.tmp");

                // 依次尝试各下载源，成功即停止
                bool downloaded = false;
                string? lastError = null;
                foreach (string url in DownloadUrls)
                {
                    try
                    {
                        if (await DownloadAsync(url, tmpPath).ConfigureAwait(false))
                        {
                            downloaded = true;
                            break;
                        }
                        lastError = "下载文件校验失败（体积异常）";
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                        Logger.LogWarning($"从下载源下载增强引擎失败：{url}，原因：{ex.Message}");
                    }
                }

                if (!downloaded)
                {
                    Logger.LogWarning("增强引擎下载失败：所有下载源均不可用");
                    NotifyStatus($"下载失败：{lastError ?? "所有下载源均不可用"}");
                    TryDeleteFile(tmpPath);
                    return false;
                }

                try
                {
                    NotifyStatus("解压中…");
                    ExtractArchive(tmpPath, EngineRoot);
                }
                catch (Exception ex)
                {
                    Logger.LogError("解压增强引擎失败", ex);
                    NotifyStatus($"解压失败：{ex.Message}");
                    TryDeleteFile(tmpPath);
                    return false;
                }

                TryDeleteFile(tmpPath);

                RapidOcrBackend.InvalidateInstallCache();
                if (RapidOcrBackend.IsInstalled)
                {
                    NotifyStatus("增强引擎已就绪");
                    Logger.LogInfo("增强 OCR 引擎安装完成");
                    return true;
                }

                Logger.LogWarning("增强 OCR 引擎解压完成但未找到 RapidOCR exe");
                NotifyStatus("安装失败：解压后未找到 RapidOCR exe，请手动安装");
                return false;
            }
            catch (Exception ex)
            {
                Logger.LogError("安装增强引擎失败", ex);
                NotifyStatus($"安装失败：{ex.Message}");
                return false;
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        /// <summary>流式下载到 tmpPath，按进度触发 StatusChanged；完成校验体积 &gt; 50MB。成功 true，失败抛异常或返回 false。</summary>
        private static async Task<bool> DownloadAsync(string url, string tmpPath)
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? -1;
            using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var dest = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            long totalRead = 0;
            long nextReportAt = ProgressReportInterval;
            var buffer = new byte[81920];
            while (true)
            {
                int read = await source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read <= 0)
                    break;
                await dest.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                totalRead += read;
                if (totalRead >= nextReportAt)
                {
                    ReportProgress(totalRead, total);
                    nextReportAt = totalRead + ProgressReportInterval;
                }
            }
            await dest.FlushAsync().ConfigureAwait(false);

            if (totalRead < MinValidFileSize)
            {
                Logger.LogWarning($"下载文件体积异常（{totalRead} 字节 < {MinValidFileSize} 字节），可能为错误页面，丢弃");
                return false;
            }

            ReportProgress(totalRead, total);
            return true;
        }

        /// <summary>用 SharpCompress 解压 7z 到 destDir，保留相对路径、覆盖已存在文件。</summary>
        private static void ExtractArchive(string archivePath, string destDir)
        {
            string destRoot = Path.GetFullPath(destDir);

            using var stream = File.OpenRead(archivePath);
            using var archive = SevenZipArchive.OpenArchive(stream);
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key))
                    continue;

                string fullPath = Path.GetFullPath(Path.Combine(destDir, entry.Key));

                // 防御：条目路径不得跳出目标目录（恶意/异常归档的 .. 或绝对路径）
                if (!fullPath.StartsWith(destRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogWarning($"增强引擎解压跳过异常路径条目：{entry.Key}");
                    continue;
                }

                string? dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                using var entryStream = entry.OpenEntryStream();
                using var fileStream = File.Create(fullPath);
                entryStream.CopyTo(fileStream);
            }
        }

        private static void ReportProgress(long totalRead, long total)
        {
            if (total > 0)
                NotifyStatus($"下载中 {totalRead / 1048576.0:F1} / {total / 1048576.0:F1} MB");
            else
                NotifyStatus($"下载中 {totalRead / 1048576.0:F1} MB");
        }

        private static void NotifyStatus(string message)
        {
            try
            {
                StatusChanged?.Invoke(message);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"增强引擎状态通知订阅方异常：{ex.Message}");
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
                Logger.LogWarning($"删除临时文件失败：{path}，原因：{ex.Message}");
            }
        }
    }
}
