using System;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace CommandLauncher
{
    /// <summary>
    /// 截图 OCR 识别服务：单一后端——RapidOCR 增强引擎（<see cref="RapidOcrBackend"/>，独立进程，
    /// 由 <see cref="OcrEngineInstaller"/> 首次启动时后台下载）。无 UI 依赖，可从任意线程调用；
    /// 识别结果按行以 \n 合并返回。
    /// </summary>
    public static class OcrService
    {
        /// <summary>识图引擎（RapidOCR）是否已就绪。</summary>
        public static bool IsReady => RapidOcrBackend.IsInstalled;

        /// <summary>
        /// 识别图像文字，按行 \n 合并；未识别到文字或引擎故障返回 null（内部记日志）。image 必须已 Freeze。
        /// </summary>
        public static async Task<string?> RecognizeAsync(BitmapSource image)
        {
            try
            {
                if (image == null)
                {
                    Logger.LogWarning("OCR 输入图片为 null，忽略");
                    return null;
                }

                // RapidOcrBackend 返回：识别文本 / ""（引擎正常但未识别到文字，code=101）/ null（引擎故障）。
                string? text = await RapidOcrBackend.RecognizeAsync(image).ConfigureAwait(false);

                // "" 视为未识别到文字：与调用方语义一致返回 null（弹窗显示占位）。
                if (text == null)
                    return null;
                if (text.Length == 0)
                {
                    Logger.LogInfo("OCR 未识别到文字");
                    return null;
                }
                return text;
            }
            catch (Exception ex)
            {
                Logger.LogError("OCR 识别失败", ex);
                return null;
            }
        }
    }
}
