using System;
using System.Drawing;

namespace CommandLauncher
{
    /// <summary>
    /// 输入法状态角标（badge）的定位（纯几何、可单测）。
    /// 所有坐标均为物理像素。
    /// <para>
    /// 定位优先级：插入符<b>左上</b> → 右上 → 左下 → 右下，每一步都要求角标完整落在
    /// 工作区扣掉 margin 后的区域内；四象限都放不下时以首选位置（左上）钳制到工作区。
    /// </para>
    /// <para>
    /// 为什么上方优先于下方：IME 候选窗通常从插入符下方展开，角标放上方不会盖住候选窗；
    /// 左侧优先于右侧同理——角标放左侧后，用户正在输入的文字（插入符向右延伸）不受遮挡。
    /// </para>
    /// </summary>
    public static class ImeIndicatorGeometry
    {
        /// <summary>
        /// 计算角标左上角位置。首选插入符左上方（角标右下角对齐
        /// <c>(caret.Left - gap, caret.Top - gap)</c>），左侧放不下翻右上、
        /// 上方放不下翻左下、都放不下翻右下；四象限都放不下时用左上位置钳制兜底。
        /// badge 比工作区还大时返回工作区左上 + margin（不抛异常）。
        /// </summary>
        public static Point Place(
            Rectangle caret, Size badge, Rectangle workArea,
            int gap = 4, int margin = 2)
        {
            // 四个候选位置的左上角坐标
            int leftX = caret.Left - gap - badge.Width;
            int rightX = caret.Right + gap;
            int aboveY = caret.Top - gap - badge.Height;
            int belowY = caret.Bottom + gap;

            // 角标完整落入工作区（扣 margin）的允许区间。
            // 刻意不用 Math.Clamp：max < min（badge 大于工作区）时它会抛异常。
            int minX = workArea.Left + margin;
            int maxX = workArea.Right - margin - badge.Width;
            int minY = workArea.Top + margin;
            int maxY = workArea.Bottom - margin - badge.Height;

            // 回退顺序：左上 → 右上 → 左下 → 右下。
            if (Fits(leftX, aboveY, minX, maxX, minY, maxY))
                return new Point(leftX, aboveY);
            if (Fits(rightX, aboveY, minX, maxX, minY, maxY))
                return new Point(rightX, aboveY);
            if (Fits(leftX, belowY, minX, maxX, minY, maxY))
                return new Point(leftX, belowY);
            if (Fits(rightX, belowY, minX, maxX, minY, maxY))
                return new Point(rightX, belowY);

            // 四象限都放不下：以首选位置（左上）做钳制兜底。
            // Math.Max(minX, maxX) 保证 badge 超宽/超高时返回工作区左上 + margin 而非抛异常。
            int x = Math.Min(Math.Max(leftX, minX), Math.Max(minX, maxX));
            int y = Math.Min(Math.Max(aboveY, minY), Math.Max(minY, maxY));

            return new Point(x, y);
        }

        /// <summary>候选位置的左上角是否能让角标完整落在允许区间内。</summary>
        private static bool Fits(int x, int y, int minX, int maxX, int minY, int maxY)
        {
            // max < min（badge 比工作区大）时任何候选都不成立，交给钳制兜底。
            return maxX >= minX && maxY >= minY
                && x >= minX && x <= maxX
                && y >= minY && y <= maxY;
        }
    }
}
