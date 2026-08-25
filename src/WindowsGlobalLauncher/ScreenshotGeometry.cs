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

        /// <summary>
        /// 文字便签贴图的内容区尺寸自适应（DIP，不含描边/内边距，调用方自行加 chrome）：
        /// 宽高均取测量值向上取整后钳在 [min, max]。
        /// **向上取整对宽高都是必须的**：窗口尺寸最终要落成整数设备像素，留着小数会在取整时
        /// 被抹掉，内容区随之短那么零点几像素，ScrollViewer 就会判定内容超出、弹出多余的滚动条。
        /// 高度（取整后）超出上限即意味着内容需要竖向滚动（经 needsScroll 报告给调用方），
        /// 此时再给宽度补上滚动条宽度（否则滚动条会压掉同等宽度的内容、把行尾字裁掉），
        /// 补完仍不越宽度上限。
        /// </summary>
        /// <param name="measured">按 maxContentWidth 约束测量出的内容尺寸</param>
        /// <param name="scrollBarWidth">竖向滚动条宽度，仅在需要滚动时计入</param>
        /// <param name="needsScroll">高度是否被钳到上限（即内容需要竖向滚动）</param>
        public static Size FitTextPinContent(
            Size measured,
            double minContentWidth, double maxContentWidth,
            double minContentHeight, double maxContentHeight,
            double scrollBarWidth,
            out bool needsScroll)
        {
            // 上限本身可能比下限还小（极窄/极矮的工作区），此时以上限为准，保证结果不越界
            double loW = Math.Min(minContentWidth, maxContentWidth);
            double loH = Math.Min(minContentHeight, maxContentHeight);

            double w = Math.Clamp(Math.Ceiling(measured.Width), loW, maxContentWidth);

            // 先取整再判超限：让 needsScroll 与「补滚动条宽度」用同一个判据，不会一个说要滚、
            // 另一个说不用（闭区间——恰好等于上限视为放得下，不算需要滚动）
            double h = Math.Ceiling(measured.Height);
            needsScroll = h > maxContentHeight;
            h = Math.Clamp(h, loH, maxContentHeight);

            if (needsScroll) // 需要滚动：让出滚动条宽度
                w = Math.Min(w + scrollBarWidth, maxContentWidth);

            return new Size(w, h);
        }

        /// <summary>
        /// 文字便签贴图「编辑态放大时左上角固定不动」的内容区尺寸上限（DIP，不含 chrome，也不含阴影边距）。
        /// 调用方 PinWindow.GetMaxContentSize(anchored: true) 先按整块工作区算出基础上限 baseMaxContentW/H，
        /// 本函数再把上限收窄为「内容左上角到工作区右/下边缘的剩余空间」，落到 minContentW/H 地板。
        /// 约束对象是内容矩形右/下边缘 ≤ 工作区右/下边缘（窗口四周的透明阴影边距允许溢出，
        /// 与项目「凡涉及内容在屏幕上的可见位置/大小一律用内容矩形」的既有口径一致）。
        ///
        /// 三条取舍（为什么）：
        /// 1. 地板取 minContentW/H 而非 1：FitTextPinContent 的 loW/loH = min(min, max) 语义在「上限小于下限」
        ///    时以上限为准，若这里只落到 1，贴近右/下边缘的便签会被压成 1 DIP（甚至负值）而不是保持最小可读尺寸。
        ///    地板必须在传入 FitTextPinContent 之前就做好。
        /// 2. 用 min(baseMax, max(avail, min)) 而非 max(min(baseMax, avail), min)：后者在「工作区本身比最小尺寸还窄」
        ///    时会把结果顶到 min、反而越出工作区；前者始终尊重 baseMax（工作区物理上限），与 FitTextPinContent 的
        ///    「上限为准」口径一致。
        /// 3. 地板生效时（剩余空间 < 最小尺寸，含 avail 为负——便签被拖到工作区边缘外、Screen.FromPoint 取到最近屏）
        ///    窗口右/下边缘会越出工作区一点：刻意取舍，宁可溢出也不缩到不可读。
        /// </summary>
        /// <param name="contentLeftPhys">内容左上角 X，虚拟屏物理像素</param>
        /// <param name="contentTopPhys">内容左上角 Y，虚拟屏物理像素</param>
        /// <param name="dpiScaleX">水平 DPI 缩放（物理 → DIP 用除法）</param>
        /// <param name="dpiScaleY">垂直 DPI 缩放（物理 → DIP 用除法）</param>
        /// <param name="workAreaRightPhys">所在屏工作区右边缘，虚拟屏物理像素</param>
        /// <param name="workAreaBottomPhys">所在屏工作区下边缘，虚拟屏物理像素</param>
        /// <param name="baseMaxContentW">按整块工作区算出的内容宽度基础上限（DIP）</param>
        /// <param name="baseMaxContentH">按整块工作区算出的内容高度基础上限（DIP）</param>
        /// <param name="chromeW">内容区之外的水平固定开销（描边 + 内边距等，DIP）</param>
        /// <param name="chromeH">内容区之外的垂直固定开销（描边 + 内边距等，DIP）</param>
        /// <param name="minContentW">内容宽度地板（最小可读宽度，DIP）</param>
        /// <param name="minContentH">内容高度地板（最小可读高度，DIP）</param>
        public static (double MaxContentW, double MaxContentH) AnchorPinMaxContentSize(
            double contentLeftPhys, double contentTopPhys,
            double dpiScaleX, double dpiScaleY,
            double workAreaRightPhys, double workAreaBottomPhys,
            double baseMaxContentW, double baseMaxContentH,
            double chromeW, double chromeH,
            double minContentW, double minContentH)
        {
            // 物理 → DIP 用除法；chrome 是内容区之外的固定开销，剩余空间要先扣掉才装得下完整内容
            double availW = (workAreaRightPhys - contentLeftPhys) / dpiScaleX - chromeW;
            double availH = (workAreaBottomPhys - contentTopPhys) / dpiScaleY - chromeH;
            double w = Math.Min(baseMaxContentW, Math.Max(availW, minContentW));
            double h = Math.Min(baseMaxContentH, Math.Max(availH, minContentH));
            return (w, h);
        }
    }
}
