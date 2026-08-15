using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CommandLauncher
{
    /// <summary>
    /// 屏幕抓图工具：以物理像素抓取整个虚拟屏（多屏联合区域），产出冻结的 WPF 位图供截图遮罩使用。
    /// 坐标约定：虚拟屏原点 = 各屏幕 Bounds 的最小 Left/Top（多屏时可能为负），
    /// 本类所有矩形与取色坐标均相对该原点（进程已声明 PerMonitorV2，见 app.manifest）。
    /// </summary>
    public static class ScreenCapture
    {
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        /// <summary>整个虚拟屏的物理像素矩形（所有屏幕的并集外接框，原点可能为负）。</summary>
        public static System.Drawing.Rectangle GetVirtualScreenBounds()
        {
            int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var b = screen.Bounds; // PerMonitorV2 下为物理像素
                left = Math.Min(left, b.Left);
                top = Math.Min(top, b.Top);
                right = Math.Max(right, b.Right);
                bottom = Math.Max(bottom, b.Bottom);
            }
            return left <= right && top <= bottom
                ? System.Drawing.Rectangle.FromLTRB(left, top, right, bottom)
                : new System.Drawing.Rectangle(0, 0, 1, 1); // 无屏幕的极端兜底，实际不会触发
        }

        /// <summary>
        /// 抓取指定虚拟屏区域（物理像素）为冻结的 BitmapSource。
        /// 经 GDI Bitmap.CopyFromScreen 抓帧，再转 WPF；GDI 对象随用随放。
        /// 失败记录 ERROR 日志后原样抛出（由调用方决定兜底）。
        /// </summary>
        public static BitmapSource CaptureVirtualScreen(System.Drawing.Rectangle virtualBounds)
        {
            try
            {
                using var gdiBitmap = new System.Drawing.Bitmap(
                    virtualBounds.Width, virtualBounds.Height,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var graphics = System.Drawing.Graphics.FromImage(gdiBitmap))
                {
                    graphics.CopyFromScreen(virtualBounds.Left, virtualBounds.Top, 0, 0, gdiBitmap.Size);
                }

                IntPtr hBitmap = gdiBitmap.GetHbitmap();
                try
                {
                    var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze(); // 冻结：防止句柄释放后延迟解码失效，且可跨线程安全传阅
                    return source;
                }
                finally
                {
                    DeleteObject(hBitmap); // GDI 位图句柄必须显式释放，否则每次截图泄漏一个 GDI 位图对象
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"抓取虚拟屏失败（{virtualBounds}）", ex);
                throw;
            }
        }

        /// <summary>
        /// 读取冻结帧上相对虚拟屏原点的 (x, y) 处颜色（物理像素，即位图像素坐标）。越界返回黑色。
        /// 帧来自 32bpp HBITMAP，格式为 BGRA 系（4 字节/像素，B,G,R,A 字节序）。
        /// </summary>
        public static System.Windows.Media.Color GetPixel(BitmapSource frozen, int x, int y)
        {
            if (x < 0 || y < 0 || x >= frozen.PixelWidth || y >= frozen.PixelHeight)
                return Colors.Black;

            var pixel = new byte[4];
            frozen.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]); // BGRA → ARGB
        }
    }
}
