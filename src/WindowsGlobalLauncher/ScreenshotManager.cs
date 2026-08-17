using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

namespace CommandLauncher
{
    /// <summary>截图会话结束时用户选择的动作。</summary>
    public enum SnipAction
    {
        Cancel,
        CopyToClipboard,
        Pin,
        SaveToFile,
        /// <summary>识别选区文字（OCR）。</summary>
        Ocr,
    }

    /// <summary>截图会话结果。Cancel 时 Image 为 null；PhysicalRect 为选区的虚拟屏物理像素矩形。</summary>
    public sealed record SnipResult(SnipAction Action, BitmapSource? Image, System.Drawing.Rectangle PhysicalRect);

    /// <summary>
    /// 截图/贴图功能的中枢：发起区域截图（冻结全屏 + 全屏遮罩交互）、分发截图结果
    /// （复制到剪贴板 / 钉为屏幕贴图 / 保存为 PNG）、把剪贴板图片直接贴出为屏幕贴图。
    /// 仅可从 UI 线程调用；IsCapturing 防止截图会话重入。
    /// </summary>
    public static class ScreenshotManager
    {
        private const int ClipboardRetryCount = 3;
        private const int ClipboardRetryDelayMs = 50;

        /// <summary>贴图热键接受的剪贴板文字长度上限（与剪贴板历史上限语义一致，超长跳过）。</summary>
        private const int MaxPinTextLength = 50_000;

        /// <summary>截图会话是否进行中（重入保护，仅 UI 线程读写）。</summary>
        public static bool IsCapturing { get; private set; }

        /// <summary>进行中的遮罩窗口（贴图热键转发用）；会话结束（HandleResult）时置空。</summary>
        private static ScreenshotOverlayWindow? _activeOverlay;

        /// <summary>发起一次区域截图：冻结整个虚拟屏 → 弹出全屏遮罩交互选区。仅 UI 线程调用。</summary>
        public static void StartCapture()
        {
            if (IsCapturing)
            {
                Logger.LogWarning("截图已在进行中，忽略重入");
                return;
            }

            IsCapturing = true;
            try
            {
                var bounds = ScreenCapture.GetVirtualScreenBounds();

                // 护眼模式的 Magnification 颜色矩阵会包含在 CopyFromScreen 结果里（实测 Win10 19045），
                // 直接抓屏成品图会偏暗偏黄。抓屏前临时挂起矩阵（恒等）→ 等 DWM 合成生效 → 抓真实
                // 色彩 → 立即恢复。恢复后遮罩显示冻结帧时矩阵恰好生效一次，观感与平时桌面一致；
                // 成品图与取色器均为真实颜色。
                bool eyeCareSuspended = EyeCareManager.SuspendEffect();
                BitmapSource frozen;
                try
                {
                    if (eyeCareSuspended)
                    {
                        WaitForDwmCompose();
                    }
                    frozen = ScreenCapture.CaptureVirtualScreen(bounds);
                }
                finally
                {
                    if (eyeCareSuspended)
                    {
                        EyeCareManager.ResumeEffect();
                    }
                }
                Logger.LogInfo($"开始截图：虚拟屏 ({bounds.X},{bounds.Y}) {bounds.Width}x{bounds.Height}，护眼模式={EyeCareManager.CurrentModeName ?? "无"}"
                    + (eyeCareSuspended ? "（抓屏期间已临时挂起）" : ""));

                var snapshot = WindowRectSnapshot.Capture(IntPtr.Zero);
                var overlay = new ScreenshotOverlayWindow(frozen, bounds, snapshot);
                overlay.Completed += HandleResult;
                _activeOverlay = overlay;
                overlay.Show();
            }
            catch (Exception ex)
            {
                Logger.LogError("启动截图失败", ex);
                IsCapturing = false; // 不弹窗：截图由热键高频触发，失败静默记日志即可
            }
        }

        /// <summary>
        /// 把当前剪贴板内容钉为屏幕贴图（初始位置取鼠标光标处）：图片优先钉为图片贴图，
        /// 无图片但有文字时钉为便签式文字贴图。仅 UI 线程调用。
        /// 截图会话进行中时改为「钉图当前选区」：F7 被全局钩子吞掉、不会到达遮罩窗口，
        /// 从这里转发给遮罩执行（等同点工具条 📌）。
        /// </summary>
        public static void PinFromClipboard()
        {
            if (IsCapturing && _activeOverlay != null)
            {
                _activeOverlay.PinCurrentSelection();
                return;
            }

            var pos = System.Windows.Forms.Cursor.Position; // 物理像素（PerMonitorV2）

            // 图片优先：保留原有行为
            if (Clipboard.ContainsImage())
            {
                BitmapSource? image = null;
                if (!RunWithClipboardRetry(() => image = Clipboard.GetImage(), "读取剪贴板图片"))
                    return;
                if (image == null)
                {
                    Logger.LogWarning("剪贴板图片读取结果为 null，忽略贴图");
                    return;
                }
                if (!image.IsFrozen)
                    image.Freeze();

                PinWindow.FromImage(image, pos);
                Logger.LogInfo($"已从剪贴板贴图：{image.PixelWidth}x{image.PixelHeight} @ ({pos.X},{pos.Y})");
                return;
            }

            // 无图片：有文字则钉为便签
            if (!Clipboard.ContainsText())
            {
                Logger.LogInfo("剪贴板无图片也无文字，忽略贴图热键");
                return;
            }

            string? text = null;
            if (!RunWithClipboardRetry(() => text = Clipboard.GetText(), "读取剪贴板文字"))
                return;
            if (string.IsNullOrWhiteSpace(text))
            {
                Logger.LogInfo("剪贴板文字为空或全空白，忽略贴图热键");
                return;
            }
            if (text!.Length > MaxPinTextLength)
            {
                Logger.LogInfo($"剪贴板文字过长（{text.Length} 字符，上限 {MaxPinTextLength}），忽略贴图热键");
                return;
            }

            PinWindow.FromText(text, pos);
            Logger.LogInfo($"已从剪贴板贴出文本：{text.Length} 字符 @ ({pos.X},{pos.Y})");
        }

        /// <summary>截图会话结束分发。无论何种结果，finally 中都复位 IsCapturing。</summary>
        public static void HandleResult(SnipResult result)
        {
            try
            {
                switch (result.Action)
                {
                    case SnipAction.Cancel:
                        Logger.LogInfo("截图已取消");
                        break;

                    case SnipAction.CopyToClipboard:
                        var image = result.Image;
                        if (image != null
                            && RunWithClipboardRetry(() => Clipboard.SetImage(image), "截图写入剪贴板"))
                        {
                            // 写剪贴板会被剪贴板历史（ClipboardHistoryManager）自动捕获，无需额外登记
                            Logger.LogInfo($"截图已复制到剪贴板：{image.PixelWidth}x{image.PixelHeight}");
                        }
                        break;

                    case SnipAction.Pin:
                        PinWindow.FromImage(result.Image!, result.PhysicalRect.Location);
                        Logger.LogInfo($"已贴图：{result.PhysicalRect.Width}x{result.PhysicalRect.Height} @ ({result.PhysicalRect.X},{result.PhysicalRect.Y})");
                        break;

                    case SnipAction.SaveToFile:
                        SaveImageToFile(result.Image);
                        break;

                    case SnipAction.Ocr:
                        RunOcr(result.Image);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"处理截图结果失败（{result.Action}）", ex);
            }
            finally
            {
                _activeOverlay = null;
                IsCapturing = false;
            }
        }

        /// <summary>
        /// 异步识别选区文字并填充 OCR 结果窗。fire-and-forget（async void）：弹出「正在识别…」
        /// 窗口后立即返回，不影响 HandleResult 的 finally 复位 IsCapturing/_activeOverlay；
        /// await 后经 WPF SynchronizationContext 回到 UI 线程再 SetResult。
        /// 引擎未就绪时先进入「下载中」态，等待后台下载完成后自动识别。
        /// </summary>
        private static async void RunOcr(BitmapSource? image)
        {
            var window = new OcrResultWindow();
            window.Show();

            if (image == null)
            {
                window.SetResult(null);
                return;
            }

            try
            {
                if (!OcrService.IsReady)
                {
                    window.SetDownloading();
                    bool ok = await OcrEngineInstaller.EnsureInstalledAsync();
                    if (!ok)
                    {
                        window.SetEngineUnavailable("下载失败");
                        return;
                    }
                }

                var text = await OcrService.RecognizeAsync(image);
                window.SetResult(text);
                if (text != null)
                    Logger.LogInfo($"OCR 识别成功：{text.Length} 个字符");
            }
            catch (Exception ex)
            {
                // OcrService/安装器内部已兜底，此处再兜一层以防弹窗永久停在加载态
                Logger.LogError("OCR 识别流程异常", ex);
                window.SetResult(null);
            }
        }

        /// <summary>
        /// 等待 DWM 完成两次合成周期（约 1~2 帧），确保刚写入的全屏颜色矩阵已呈现到屏幕，
        /// 之后 CopyFromScreen 抓到的才是新矩阵下的像素。DwmFlush 失败时静默忽略
        /// （Win10 桌面 DWM 恒开，失败仅见于极端环境，此时抓屏最多带上护眼色彩，不影响截图本身）。
        /// </summary>
        private static void WaitForDwmCompose()
        {
            try
            {
                DwmFlush();
                DwmFlush();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"等待 DWM 合成失败，抓屏可能仍包含护眼色彩: {ex.Message}");
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        private static void SaveImageToFile(BitmapSource? image)
        {
            if (image == null)
                return;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存截图",
                Filter = "PNG 图片 (*.png)|*.png",
                FileName = $"Snip_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            if (dialog.ShowDialog() != true)
            {
                Logger.LogInfo("保存截图已取消");
                return;
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(dialog.FileName))
            {
                encoder.Save(stream);
            }
            Logger.LogInfo($"截图已保存: {dialog.FileName}");
        }

        /// <summary>
        /// 带重试地执行剪贴板读写：剪贴板被其它进程占用时抛 ExternalException，
        /// 短间隔重试 ClipboardRetryCount 次后放弃。返回是否成功。
        /// </summary>
        private static bool RunWithClipboardRetry(Action action, string what)
        {
            for (int attempt = 1; attempt <= ClipboardRetryCount; attempt++)
            {
                try
                {
                    action();
                    return true;
                }
                catch (ExternalException ex)
                {
                    if (attempt >= ClipboardRetryCount)
                    {
                        Logger.LogError($"{what}失败（重试 {ClipboardRetryCount} 次后放弃）", ex);
                        return false;
                    }
                    Logger.LogWarning($"{what}被占用，{ClipboardRetryDelayMs}ms 后重试（第 {attempt + 1}/{ClipboardRetryCount} 次）: {ex.Message}");
                    Thread.Sleep(ClipboardRetryDelayMs);
                }
            }
            return false; // 不可达，仅为编译器确定性返回
        }
    }
}
