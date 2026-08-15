using System;
using System.Windows;
using System.Windows.Media;

namespace CommandLauncher
{
    /// <summary>
    /// 截图功能的纯几何与格式化工具（无状态、可单测）。
    /// 纯静态函数，不依赖任何窗口/控件实例；坐标语义由调用方决定（DIP 或物理像素均可）。
    /// </summary>
    public static class ScreenshotGeometry
    {
        /// <summary>
        /// 任意两个对角点规整为矩形（等价 new Rect(a, b)，显式命名以表达「拖拽选区规整」意图）。
        /// </summary>
        public static Rect Normalize(Point a, Point b) => new Rect(a, b);

        /// <summary>
        /// r 与 bounds 求交；不相交时返回 Rect.Empty（调用方用 IsEmpty 判定）。
        /// </summary>
        public static Rect ClampToBounds(Rect r, Rect bounds) => Rect.Intersect(r, bounds);

        /// <summary>
        /// 方向键微调选区。
        /// resize = false：整体平移 (dx, dy)，平移后钳制回 bounds 内（保持尺寸不变）；
        /// resize = true：保持左上角不动，调整右/下边缘（宽 += dx，高 += dy），
        /// 尺寸下限 1×1，右/下边缘不得越出 bounds。
        /// </summary>
        public static Rect NudgeOrResize(Rect r, int dx, int dy, bool resize, Rect bounds)
        {
            if (!resize)
            {
                // 整体平移：钳制公式保证矩形完全落在 bounds 内（左上角域为闭区间）
                double x = Math.Max(bounds.Left, Math.Min(r.X + dx, bounds.Right - r.Width));
                double y = Math.Max(bounds.Top, Math.Min(r.Y + dy, bounds.Bottom - r.Height));
                return new Rect(x, y, r.Width, r.Height);
            }

            // 缩放：下限 1，上限顶到 bounds 右/下边缘
            double newWidth = Math.Max(1, Math.Min(r.Width + dx, bounds.Right - r.X));
            double newHeight = Math.Max(1, Math.Min(r.Height + dy, bounds.Bottom - r.Y));
            return new Rect(r.X, r.Y, newWidth, newHeight);
        }

        /// <summary>颜色格式化为 "#RRGGBB"（大写十六进制）。</summary>
        public static string FormatHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

        /// <summary>颜色格式化为 "255, 128, 0"（逗号 + 空格分隔）。</summary>
        public static string FormatRgb(byte r, byte g, byte b) => $"{r}, {g}, {b}";

        /// <summary>解析 "#RRGGBB"（可省略 #）为 Color；格式非法时回退黑色。</summary>
        public static Color ParseHex(string text)
        {
            string hex = text.TrimStart('#');
            if (hex.Length == 6)
            {
                try
                {
                    byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                    byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                    byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                    return Color.FromRgb(r, g, b);
                }
                catch (FormatException)
                {
                    // 非法十六进制，落到下方返回黑色
                }
            }
            return Colors.Black;
        }

        /// <summary>
        /// 构造实心箭头七点多边形（尾部两点、颈部两点、头部翼两点、尖端一点）。
        /// width 为箭杆宽度；箭头翼宽 = 3 × width，头长 = min(4 × width, 0.4 × 全长)（短箭头时头长按比例退化）。
        /// from 到 to 距离 &lt; 0.5 时返回空 PointCollection。
        /// 点序：尾左 → 颈左 → 翼左 → 尖端 → 翼右 → 颈右 → 尾右，保证多边形不自交且关于箭轴左右对称。
        /// </summary>
        public static PointCollection BuildArrowPolygon(Point from, Point to, double width)
        {
            var points = new PointCollection();
            Vector dirVec = to - from;
            double len = dirVec.Length;
            if (len < 0.5)
                return points;

            Vector dir = dirVec / len;              // 箭轴单位向量
            Vector n = new Vector(-dir.Y, dir.X);   // 左法向量
            double halfShaft = width / 2.0;
            double halfHead = width * 1.5;          // 翼宽 3 × width 的一半
            double headLen = Math.Min(4.0 * width, 0.4 * len);
            Point neck = to - dir * headLen;        // 箭头颈部（翼所在截面）在箭轴上的位置

            points.Add(from + n * halfShaft);  // 尾左
            points.Add(neck + n * halfShaft);  // 颈左
            points.Add(neck + n * halfHead);   // 翼左
            points.Add(to);                    // 尖端
            points.Add(neck - n * halfHead);   // 翼右
            points.Add(neck - n * halfShaft);  // 颈右
            points.Add(from - n * halfShaft);  // 尾右
            return points;
        }

        /// <summary>
        /// 工具条摆放：优先选区正下方（间距 8）；下方放不下 → 选区上方（间距 8）；
        /// 上方也放不下 → 选区内部右下角（内缩 8）。X 与选区右边缘对齐（工具条右边 = 选区右边），
        /// 最终整体钳制在 canvas 内。返回工具条左上角坐标。
        /// 「放得下」为闭区间判定：恰好贴住 canvas 边缘视为放得下。
        /// </summary>
        public static Point PlaceToolbar(Rect selection, Size toolbarSize, Rect canvas)
        {
            const double gap = 8;
            double x = selection.Right - toolbarSize.Width;
            double y;
            if (selection.Bottom + gap + toolbarSize.Height <= canvas.Bottom)
                y = selection.Bottom + gap;                              // 下方
            else if (selection.Top - gap - toolbarSize.Height >= canvas.Top)
                y = selection.Top - gap - toolbarSize.Height;            // 上方
            else
                y = selection.Bottom - gap - toolbarSize.Height;         // 选区内部右下角

            // 整体钳制在 canvas 内
            x = Math.Max(canvas.Left, Math.Min(x, canvas.Right - toolbarSize.Width));
            y = Math.Max(canvas.Top, Math.Min(y, canvas.Bottom - toolbarSize.Height));
            return new Point(x, y);
        }

        /// <summary>
        /// 放大镜摆放：默认光标右下偏移 (24, 24)；右侧越出 canvas → 翻到光标左侧；
        /// 下方越出 → 翻到上方；最终钳制在 canvas 内。返回放大镜左上角坐标。
        /// 「越出」为严格大于判定：恰好贴住 canvas 边缘不算越出、不翻转。
        /// </summary>
        public static Point PlaceMagnifier(Point cursor, Size magSize, Rect canvas)
        {
            const double offset = 24;
            double x = cursor.X + offset;
            double y = cursor.Y + offset;
            if (x + magSize.Width > canvas.Right)
                x = cursor.X - offset - magSize.Width;   // 翻到光标左侧
            if (y + magSize.Height > canvas.Bottom)
                y = cursor.Y - offset - magSize.Height;  // 翻到光标上方

            x = Math.Max(canvas.Left, Math.Min(x, canvas.Right - magSize.Width));
            y = Math.Max(canvas.Top, Math.Min(y, canvas.Bottom - magSize.Height));
            return new Point(x, y);
        }
    }
}
