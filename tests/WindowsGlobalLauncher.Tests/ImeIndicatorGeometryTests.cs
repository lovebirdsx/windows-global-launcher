using System.Drawing;
using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests
{
    /// <summary>
    /// 输入法角标定位的纯几何测试（<see cref="ImeIndicatorGeometry"/>）。
    /// 全部坐标为物理像素；定位优先级为 左上 → 右上 → 左下 → 右下，最后钳制兜底。
    /// </summary>
    public class ImeIndicatorGeometryTests
    {
        private static readonly Rectangle WorkArea = new Rectangle(0, 0, 1920, 1080);

        [Fact]
        public void Place_PrefersAboveLeft_EvenWhenRightAndBelowHaveRoom()
        {
            Point result = ImeIndicatorGeometry.Place(
                new Rectangle(500, 500, 1, 20),
                new Size(26, 26),
                WorkArea);

            // 右侧、下方都放得下，仍选左上：x = 500 - 4 - 26；y = 500 - 4 - 26
            Assert.Equal(new Point(470, 470), result);
        }

        [Fact]
        public void Place_NoRoomOnLeft_FlipsToAboveRight()
        {
            Point result = ImeIndicatorGeometry.Place(
                new Rectangle(0, 100, 1, 20),
                new Size(26, 26),
                WorkArea);

            // 左侧 x = 0 - 4 - 26 越界，翻到右上；y 仍在上方：100 - 4 - 26
            Assert.Equal(new Point(5, 70), result);
        }

        [Fact]
        public void Place_NoRoomAbove_FlipsToBelowLeft()
        {
            Point result = ImeIndicatorGeometry.Place(
                new Rectangle(100, 0, 1, 20),
                new Size(26, 26),
                WorkArea);

            // 上方的 y 都越界，翻到左下；x 仍在左侧：x = 100 - 4 - 26；y = 0 + 20 + 4
            Assert.Equal(new Point(70, 24), result);
        }

        [Fact]
        public void Place_NoRoomOnLeftOrAbove_FlipsToBelowRight()
        {
            Point result = ImeIndicatorGeometry.Place(
                new Rectangle(0, 0, 1, 20),
                new Size(26, 26),
                WorkArea);

            // 左上/右上/左下都放不下，落到右下：x = 0 + 1 + 4；y = 0 + 20 + 4
            Assert.Equal(new Point(5, 24), result);
        }

        [Fact]
        public void Place_CaretBeyondRightEdge_ClampsToRightMargin()
        {
            Point result = ImeIndicatorGeometry.Place(
                new Rectangle(2000, 100, 1, 20),
                new Size(26, 26),
                WorkArea);

            // 四个象限在 x 方向都放不下：左上位置钳到工作区右边 - margin - badge 宽
            Assert.Equal(new Point(1892, 70), result);
        }

        [Fact]
        public void Place_CaretBelowWorkArea_ClampsToBottomMargin()
        {
            Point result = ImeIndicatorGeometry.Place(
                new Rectangle(100, 2000, 1, 20),
                new Size(26, 26),
                WorkArea);

            // 左上位置越出工作区下边，钳到 1080 - 2 - 26
            Assert.Equal(new Point(70, 1052), result);
        }

        [Fact]
        public void Place_CaretBeyondLeftEdge_ClampsToLeftMargin()
        {
            Point result = ImeIndicatorGeometry.Place(
                new Rectangle(-100, 100, 1, 20),
                new Size(26, 26),
                WorkArea);

            // 四个象限在 x 方向都放不下：钳到工作区左边 + margin
            Assert.Equal(new Point(2, 70), result);
        }

        [Fact]
        public void Place_CaretAboveWorkArea_ClampsToTopMargin()
        {
            Point result = ImeIndicatorGeometry.Place(
                new Rectangle(100, -100, 1, 20),
                new Size(26, 26),
                WorkArea);

            // 左上位置越出工作区上边，钳到工作区上边 + margin
            Assert.Equal(new Point(70, 2), result);
        }

        [Fact]
        public void Place_NegativeWorkArea_PlacesAboveLeftWithinBounds()
        {
            var workArea = new Rectangle(-1920, 0, 1920, 1080);

            Point result = ImeIndicatorGeometry.Place(
                new Rectangle(-1000, 100, 1, 20),
                new Size(26, 26),
                workArea);

            // 左上：x = -1000 - 4 - 26；y = 100 - 4 - 26
            Assert.Equal(new Point(-1030, 70), result);
            Assert.True(workArea.Contains(result));
            Assert.True(result.X + 26 <= workArea.Right);
            Assert.True(result.Y + 26 <= workArea.Bottom);
        }

        [Fact]
        public void Place_BadgeLargerThanWorkArea_ReturnsTopLeftWithMargin_NoException()
        {
            var workArea = new Rectangle(0, 0, 100, 100);

            // badge 比工作区还大：四个象限都不可能放下，钳制兜底返回工作区左上 + margin。
            // 不能用 Math.Clamp（max < min 会抛异常）。
            Point result = ImeIndicatorGeometry.Place(
                new Rectangle(10, 10, 1, 20),
                new Size(200, 200),
                workArea);

            Assert.Equal(new Point(workArea.Left + 2, workArea.Top + 2), result);
        }

        [Fact]
        public void Place_CustomGapAndMargin_UsesProvidedValues()
        {
            Point result = ImeIndicatorGeometry.Place(
                new Rectangle(100, 100, 1, 20),
                new Size(26, 26),
                WorkArea,
                gap: 10,
                margin: 5);

            // 左上：x = 100 - 10 - 26；y = 100 - 10 - 26
            Assert.Equal(new Point(64, 64), result);
        }

        [Fact]
        public void Place_ZeroHeightCaret_DoesNotThrowAndPlacesAboveLeft()
        {
            Point result = ImeIndicatorGeometry.Place(
                new Rectangle(100, 100, 1, 0),
                new Size(26, 26),
                WorkArea);

            // 零高度插入符同样按上边计算：y = 100 - 4 - 26，不抛异常
            Assert.Equal(new Point(70, 70), result);
        }
    }
}
