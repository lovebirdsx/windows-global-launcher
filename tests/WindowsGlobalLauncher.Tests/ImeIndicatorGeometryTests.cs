using System.Drawing;
using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests
{
    /// <summary>
    /// 输入法角标定位的纯几何测试（<see cref="ImeIndicatorGeometry"/>）。
    /// 全部坐标为物理像素。
    /// </summary>
    public class ImeIndicatorGeometryTests
    {
        private static readonly Rectangle WorkArea = new Rectangle(0, 0, 1920, 1080);

        [Fact]
        public void Place_RightSideFits_PlacesRightOfCaretAndVerticallyCentered()
        {
            Point result = ImeIndicatorGeometry.Place(
                new Rectangle(100, 100, 1, 20),
                new Size(26, 26),
                WorkArea);

            // x = 100 + 1 + 4；y = 100 + (20 - 26) / 2
            Assert.Equal(new Point(105, 97), result);
        }

        [Fact]
        public void Place_NotEnoughRoomOnRight_FlipsToLeft()
        {
            Point result = ImeIndicatorGeometry.Place(
                new Rectangle(1900, 100, 1, 20),
                new Size(26, 26),
                WorkArea);

            // 右侧越界：x = 1900 - 4 - 26
            Assert.Equal(new Point(1870, 97), result);
        }

        [Fact]
        public void Place_AboveWorkArea_ClampsToTopMargin()
        {
            Point result = ImeIndicatorGeometry.Place(
                new Rectangle(100, 0, 1, 20),
                new Size(26, 26),
                WorkArea);

            // 垂直居中后 y = -3，钳到工作区上边 + margin
            Assert.Equal(new Point(105, 2), result);
        }

        [Fact]
        public void Place_BelowWorkArea_ClampsToBottomMargin()
        {
            Point result = ImeIndicatorGeometry.Place(
                new Rectangle(100, 1070, 1, 20),
                new Size(26, 26),
                WorkArea);

            // 垂直居中后 y = 1067，钳到 1080 - 2 - 26
            Assert.Equal(new Point(105, 1052), result);
        }

        [Fact]
        public void Place_ZeroHeightCaret_StillCentersBadge()
        {
            Point result = ImeIndicatorGeometry.Place(
                new Rectangle(100, 100, 1, 0),
                new Size(26, 26),
                WorkArea);

            // y = 100 + (0 - 26) / 2 = 87，仍在工作区内
            Assert.Equal(new Point(105, 87), result);
        }

        [Fact]
        public void Place_NegativeWorkArea_StaysInsideWorkArea()
        {
            var workArea = new Rectangle(-1920, 0, 1920, 1080);

            Point result = ImeIndicatorGeometry.Place(
                new Rectangle(-1000, 100, 1, 20),
                new Size(26, 26),
                workArea);

            Assert.Equal(new Point(-995, 97), result);
            Assert.True(workArea.Contains(result));
            Assert.True(result.X + 26 <= workArea.Right);
            Assert.True(result.Y + 26 <= workArea.Bottom);
        }

        [Fact]
        public void Place_BadgeLargerThanWorkArea_ReturnsTopLeftWithMargin_NoException()
        {
            var workArea = new Rectangle(0, 0, 100, 100);

            // badge 比工作区还大：不能用 Math.Clamp（max < min 会抛异常）
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

            // x = 100 + 1 + 10；y 仍垂直居中
            Assert.Equal(new Point(111, 97), result);
        }
    }
}
