using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace CommandLauncher
{
    /// <summary>
    /// 截图 OCR 增强引擎后端：调用 RapidOCR-json（PP-OCR ONNX 深度学习模型）识别，效果优于 Windows 内置引擎。
    ///
    /// 引擎以「常驻子进程 + stdin/stdout JSON」方式运行：首次识别时启动，之后 stdin 每行一个 JSON 请求、
    /// stdout 每行一个 JSON 响应。增强包不随主程序分发，由 <see cref="OcrEngineInstaller"/> 按需下载解压到
    /// 数据目录 ocr-engine\（递归查找 RapidOCR*json*.exe，兼容压缩包自带顶层子目录）。
    ///
    /// 无 UI 依赖，可从任意线程调用；识别经 SemaphoreSlim 串行化，进程意外退出/超时后统一 Kill 并置空，
    /// 下次识别自动重启。主程序以管理员运行，子进程直接继承即可（本机推理无降权需求）。
    /// </summary>
    public static class RapidOcrBackend
    {
        /// <summary>启动初始化总超时（等待 stdout 输出 OCR init completed.）。</summary>
        private static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(30);

        /// <summary>单次识别的响应读取超时。</summary>
        private static readonly TimeSpan RecognizeTimeout = TimeSpan.FromSeconds(20);

        /// <summary>引擎根目录：数据目录下的 ocr-engine（models 相对 exe 同级，WorkingDirectory 必须指向 exe 所在目录）。</summary>
        private static string EngineRoot => Path.Combine(App.BaseDir, "ocr-engine");

        // ---- 安装检测（结果缓存，InvalidateInstallCache 后重查） ----
        private static readonly object _installCacheLock = new();
        private static bool _installCacheValid;
        private static string? _installedExePath;

        // ---- 常驻子进程 ----
        private static readonly SemaphoreSlim _recognitionLock = new(1, 1);
        private static readonly object _processLock = new();
        private static Process? _process;

        /// <summary>增强引擎是否已安装（数据目录 ocr-engine\ 下能找到 RapidOCR exe；结果缓存，InvalidateInstallCache 后重查）。</summary>
        public static bool IsInstalled => ResolveInstalledExe() != null;

        /// <summary>安装状态缓存失效（安装器完成后调用，让 IsInstalled 重新探测）。</summary>
        public static void InvalidateInstallCache()
        {
            lock (_installCacheLock)
            {
                _installCacheValid = false;
                _installedExePath = null;
            }
        }

        /// <summary>
        /// 用增强引擎识别。返回：识别文本（按行 \n 合并）/ ""（引擎正常但未识别到文字，code=101）/
        /// null（引擎故障：未安装、启动失败、超时、协议错误——调用方据此回退基础引擎）。
        /// image 必须已 Freeze，可从任意线程调用。
        /// </summary>
        public static async Task<string?> RecognizeAsync(BitmapSource image)
        {
            if (image == null)
            {
                Logger.LogWarning("增强 OCR 输入图片为 null，忽略");
                return null;
            }

            await _recognitionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                return await RecognizeCoreAsync(image).ConfigureAwait(false);
            }
            finally
            {
                _recognitionLock.Release();
            }
        }

        /// <summary>杀掉常驻子进程（程序退出时调用，幂等）。</summary>
        public static void Shutdown()
        {
            KillProcess();
        }

        // ---- 安装检测 ----

        /// <summary>返回缓存的引擎 exe 完整路径；缓存失效时重新探测。</summary>
        private static string? ResolveInstalledExe()
        {
            lock (_installCacheLock)
            {
                if (_installCacheValid)
                    return _installedExePath;
            }

            string? found = FindExe();

            lock (_installCacheLock)
            {
                _installedExePath = found;
                _installCacheValid = true;
                return found;
            }
        }

        /// <summary>在 ocr-engine 下递归查找 RapidOCR exe：文件名匹配 RapidOCR*json*.exe（不区分大小写、容忍 - / _）。</summary>
        private static string? FindExe()
        {
            try
            {
                if (!Directory.Exists(EngineRoot))
                    return null;

                foreach (string file in Directory.GetFiles(EngineRoot, "*.exe", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (name.StartsWith("RapidOCR", StringComparison.OrdinalIgnoreCase)
                        && name.Contains("json", StringComparison.OrdinalIgnoreCase))
                    {
                        return file;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("探测增强 OCR 引擎安装失败", ex);
            }
            return null;
        }

        // ---- 识别 ----

        private static async Task<string?> RecognizeCoreAsync(BitmapSource image)
        {
            if (!IsInstalled)
            {
                Logger.LogWarning("增强 OCR 引擎未安装（ocr-engine 下未找到 RapidOCR exe），回退基础引擎");
                return null;
            }

            // 取当前常驻进程；不存在或已意外退出则启动新的
            Process? process;
            lock (_processLock)
            {
                process = _process;
            }
            if (process != null && process.HasExited)
            {
                KillProcess();
                process = null;
            }
            if (process == null)
            {
                process = await StartProcessAsync().ConfigureAwait(false);
                if (process == null)
                    return null;
                lock (_processLock)
                {
                    _process = process;
                }
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                // PNG 编码为 CPU 密集操作：放到后台线程，避免大选区在调用线程（可能是 UI 线程）阻塞
                string base64 = await Task.Run(() => EncodeToPngBase64(image)).ConfigureAwait(false);

                string request = JsonSerializer.Serialize(new { image_base64 = base64 });
                await process.StandardInput.WriteLineAsync(request).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);

                var (response, timedOut) = await ReadLineWithTimeoutAsync(process, RecognizeTimeout).ConfigureAwait(false);
                if (timedOut)
                {
                    KillProcess();
                    Logger.LogError($"增强 OCR 识别超时（{RecognizeTimeout.TotalSeconds:0}s），已终止子进程",
                        new TimeoutException("识别响应超时"));
                    return null;
                }
                if (response == null)
                {
                    KillProcess();
                    Logger.LogWarning("增强 OCR 子进程在识别期间意外退出");
                    return null;
                }

                stopwatch.Stop();
                string? text = ParseResponse(response);
                if (text != null)
                    Logger.LogInfo($"增强 OCR 识别完成，耗时 {stopwatch.ElapsedMilliseconds} ms");
                return text;
            }
            catch (Exception ex)
            {
                KillProcess();
                Logger.LogError("增强 OCR 识别失败（子进程通信异常）", ex);
                return null;
            }
        }

        /// <summary>启动常驻子进程并等待初始化完成（stdout 输出含 OCR init completed. 的行，总超时 30s）。失败返回 null。</summary>
        private static async Task<Process?> StartProcessAsync()
        {
            string? exePath = ResolveInstalledExe();
            if (exePath == null)
                return null;

            string? workingDir = Path.GetDirectoryName(exePath);
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                // WorkingDirectory 必须为 exe 所在目录，models 相对路径才能命中
                WorkingDirectory = string.IsNullOrEmpty(workingDir) ? EngineRoot : workingDir,
                Arguments = "--ensureAscii=1 --maxSideLen=2048",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // ensureAscii=1 后 stdout 为纯 ASCII（非 ASCII 转成 \uXXXX），UTF-8（ASCII 兼容）即可；
                // 但输入侧必须无 BOM：Encoding.UTF8 带 BOM，StandardInput 的 StreamWriter 首次 Flush 会把
                // EF BB BF 写到 stdin 首行 JSON 之前，引擎按 jsonIn[0]=='{' 判定失败 → 首次识别必失败（第二次 BOM 已写尽才成功）
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            Process process;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start 返回 null");
            }
            catch (Exception ex)
            {
                Logger.LogError($"启动增强 OCR 引擎失败：{exePath}", ex);
                return null;
            }

            // 后台清空 stderr，避免子进程 stderr 缓冲区写满被阻塞（内容仅记日志）
            _ = DrainStderrAsync(process);

            try
            {
                while (true)
                {
                    var remaining = InitTimeout - stopwatch.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        TerminateProcess(process);
                        Logger.LogError("增强 OCR 引擎启动超时（30s 内未输出 OCR init completed.）",
                            new TimeoutException("30 秒未输出初始化完成标记"));
                        return null;
                    }

                    var (line, timedOut) = await ReadLineWithTimeoutAsync(process, remaining).ConfigureAwait(false);
                    if (timedOut || line == null)
                    {
                        TerminateProcess(process);
                        Logger.LogError(
                            timedOut
                                ? "增强 OCR 引擎启动超时（30s 内未输出 OCR init completed.）"
                                : "增强 OCR 引擎在初始化完成前意外退出",
                            timedOut
                                ? new TimeoutException("30 秒未输出初始化完成标记")
                                : new InvalidOperationException("子进程在输出初始化完成标记前退出"));
                        return null;
                    }

                    if (line.Contains("OCR init completed.", StringComparison.Ordinal))
                    {
                        stopwatch.Stop();
                        Logger.LogInfo($"增强 OCR 引擎已启动，初始化耗时 {stopwatch.ElapsedMilliseconds} ms（{exePath}）");
                        return process;
                    }
                }
            }
            catch (Exception ex)
            {
                TerminateProcess(process);
                Logger.LogError("读取增强 OCR 引擎初始化输出失败", ex);
                return null;
            }
        }

        /// <summary>把 frozen BitmapSource 编码为 PNG 并转 base64（后台线程安全，image 已 Freeze 可跨线程读）。</summary>
        private static string EncodeToPngBase64(BitmapSource image)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return Convert.ToBase64String(ms.ToArray());
        }

        /// <summary>
        /// 解析响应 JSON：code==100 把 data[] 每项 text 按顺序 \n 合并；code==101 返回 ""（未识别到文字）；
        /// 其它 code / 协议错误返回 null。
        /// </summary>
        private static string? ParseResponse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("code", out var codeEl) || !codeEl.TryGetInt32(out int code))
                {
                    Logger.LogWarning($"增强 OCR 响应缺少 code 字段：{Truncate(json)}");
                    return null;
                }

                if (code == 100)
                {
                    if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
                    {
                        Logger.LogWarning("增强 OCR 响应 code=100 但 data 不是数组");
                        return null;
                    }

                    var lines = new List<string>();
                    foreach (var item in dataEl.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object
                            && item.TryGetProperty("text", out var textEl)
                            && textEl.ValueKind == JsonValueKind.String)
                        {
                            lines.Add(textEl.GetString() ?? "");
                        }
                    }
                    return string.Join("\n", lines);
                }

                if (code == 101)
                    return ""; // 引擎正常但未识别到文字

                Logger.LogWarning($"增强 OCR 返回错误 code={code}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"增强 OCR 响应解析失败：{ex.Message}（原始：{Truncate(json)}）");
                return null;
            }
        }

        private static string Truncate(string s) => s.Length <= 500 ? s : s.Substring(0, 500) + "…";

        /// <summary>
        /// 读一行 stdout，带超时（Task.WhenAny + Task.Delay）。超时返回 timedOut=true；此时不等待挂起的 readTask，
        /// 调用方终止进程后 readTask 自然结算，这里用 ContinueWith 观察其异常防未观察泄漏。
        /// </summary>
        private static async Task<(string? Line, bool TimedOut)> ReadLineWithTimeoutAsync(Process process, TimeSpan timeout)
        {
            var readTask = process.StandardOutput.ReadLineAsync();
            var completed = await Task.WhenAny(readTask, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed == readTask)
                return (await readTask.ConfigureAwait(false), false);

            _ = readTask.ContinueWith(
                static t => { _ = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return (null, true);
        }

        /// <summary>后台清空 stderr：逐行记 WARN，进程退出/流关闭时结束（异常被吞掉，避免未观察）。</summary>
        private static async Task DrainStderrAsync(Process process)
        {
            try
            {
                while (true)
                {
                    string? line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                        break;
                    if (!string.IsNullOrWhiteSpace(line))
                        Logger.LogWarning($"增强 OCR stderr：{line}");
                }
            }
            catch
            {
                // 进程退出 / 流关闭时忽略
            }
        }

        /// <summary>终止并释放指定进程（已退出/已释放都安全，Kill 用 try/catch 包住）。</summary>
        private static void TerminateProcess(Process? process)
        {
            if (process == null)
                return;
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 忽略：可能已退出
            }
            try
            {
                process.Dispose();
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>终止并清空常驻进程引用（幂等）。</summary>
        private static void KillProcess()
        {
            Process? process;
            lock (_processLock)
            {
                process = _process;
                _process = null;
            }
            TerminateProcess(process);
        }
    }
}
