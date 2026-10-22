using System;
using System.Drawing;

namespace CommandLauncher
{
    /// <summary>
    /// 输入法状态角标（badge）的定位（纯几何、可单测）。
    /// 所有坐标均为物理像素；默认放在插入符右侧、垂直居中，
    /// 右侧放不下时翻到左侧，最后钳制到工作区。
    /// </summary>
    public static class ImeIndicatorGeometry
    {
        /// <summary>
        /// 计算角标左上角位置。
        /// badge 比工作区还大时返回工作区左上 + margin（不抛异常）。
        /// </summary>
        public static Point Place(
            Rectangle caret, Size badge, Rectangle workArea,
            int gap = 4, int margin = 2)
        {
            int x = caret.Right + gap;
            if (x + badge.Width > workArea.Right - margin)
            {
                // 右侧放不下：翻到插入符左侧
                x = caret.Left - gap - badge.Width;
            }

            int y = caret.Top + (caret.Height - badge.Height) / 2;

            // 钳制到工作区。刻意不用 Math.Clamp：max < min（badge 大于工作区）时它会抛异常。
            int minX = workArea.Left + margin;
            int maxX = workArea.Right - margin - badge.Width;
            int minY = workArea.Top + margin;
            int maxY = workArea.Bottom - margin - badge.Height;

            x = Math.Min(Math.Max(x, minX), Math.Max(minX, maxX));
            y = Math.Min(Math.Max(y, minY), Math.Max(minY, maxY));

            return new Point(x, y);
        }
    }
}
