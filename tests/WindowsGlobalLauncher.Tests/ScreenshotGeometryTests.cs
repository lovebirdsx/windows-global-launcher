using System.Windows;
using System.Windows.Media;
using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests
{
    public class ScreenshotGeometryTests
    {
        // 所有用例的坐标均设计为整数/可精确表示的运算，断言可用精确相等（无需精度容差）。

        [Theory]
        [InlineData(10, 20, 40, 60)]   // 左上 → 右下
        [InlineData(40, 60, 10, 20)]   // 右下 → 左上
        [InlineData(40, 20, 10, 60)]   // 右上 → 左下
        [InlineData(10, 60, 40, 20)]   // 左下 → 右上
        public void Normalize_AnyDragDirection_ReturnsSameRect(double x1, double y1, double x2, double y2)
        {
            Rect result = ScreenshotGeometry.Normalize(new Point(x1, y1), new Point(x2, y2));

            Assert.Equal(new Rect(10, 20, 30, 40), result);
        }

        [Theory]
        [InlineData(1, 1, 2, 2, 1, 1, 2, 2)]     // 完全在内：不变
        [InlineData(5, 5, 10, 10, 5, 5, 5, 5)]   // 右下越界：裁剪到 bounds 边缘
        [InlineData(-5, 2, 10, 4, 0, 2, 5, 4)]   // 左侧越界：X 钳回 0，宽度收缩
        public void ClampToBounds_Intersecting_ClipsToBounds(
            double rx, double ry, double rw, double rh,
            double ex, double ey, double ew, double eh)
        {
            Rect result = ScreenshotGeometry.ClampToBounds(
                new Rect(rx, ry, rw, rh), new Rect(0, 0, 10, 10));

            Assert.Equal(new Rect(ex, ey, ew, eh), result);
        }

        [Fact]
        public void ClampToBounds_NoIntersection_ReturnsEmpty()
        {
            Rect result = ScreenshotGeometry.ClampToBounds(
                new Rect(20, 20, 5, 5), new Rect(0, 0, 10, 10));

            Assert.True(result.IsEmpty);
        }

        [Theory]
        [InlineData(10, 10, 5, -3, 15, 7)]    // 普通移动
        [InlineData(75, 0, 10, 0, 80, 0)]     // 贴右边后大跨度移动：钳到 bounds 右缘
        [InlineData(5, 10, -10, 0, 0, 10)]    // 负方向：钳到 bounds 左缘
        [InlineData(10, 5, 0, -8, 10, 0)]     // 负方向 Y：钳到 bounds 上缘
        public void NudgeOrResize_Move_ClampedInsideBounds(
            double rx, double ry, int dx, int dy, double ex, double ey)
        {
            var r = new Rect(rx, ry, 20, 10);
            var bounds = new Rect(0, 0, 100, 100);

            Rect result = ScreenshotGeometry.NudgeOrResize(r, dx, dy, resize: false, bounds);

            // 位置被钳制、尺寸保持不变（20 × 10）
            Assert.Equal(new Rect(ex, ey, 20, 10), result);
        }

        [Theory]
        [InlineData(20, 10, 5, 3, 25, 13)]    // 正向扩张
        [InlineData(20, 10, 100, 0, 90, 10)]  // 扩张到 bounds 右缘截止（左上角 X=10，最大宽 90）
        [InlineData(5, 5, -10, -10, 1, 1)]    // 收缩下限 1×1，不再缩小
        public void NudgeOrResize_Resize_RespectsBoundsAndMinSize(
            double rw, double rh, int dx, int dy, double ew, double eh)
        {
            var r = new Rect(10, 10, rw, rh);
            var bounds = new Rect(0, 0, 100, 100);

            Rect result = ScreenshotGeometry.NudgeOrResize(r, dx, dy, resize: true, bounds);

            // 左上角不动，仅右/下边缘变化
            Assert.Equal(new Rect(10, 10, ew, eh), result);
        }

        [Theory]
        [InlineData(255, 255, 255, "#FFFFFF")]
        [InlineData(0, 0, 0, "#000000")]
        [InlineData(18, 52, 86, "#123456")]   // 0x12/0x34/0x56
        [InlineData(171, 205, 239, "#ABCDEF")] // 两位以上字母须大写
        public void FormatHex_ReturnsUpperCaseHex(byte r, byte g, byte b, string expected)
        {
            Assert.Equal(expected, ScreenshotGeometry.FormatHex(r, g, b));
        }

        [Theory]
        [InlineData(255, 128, 0, "255, 128, 0")]
        [InlineData(0, 0, 0, "0, 0, 0")]
        public void FormatRgb_ReturnsCommaSpaceSeparated(byte r, byte g, byte b, string expected)
        {
            Assert.Equal(expected, ScreenshotGeometry.FormatRgb(r, g, b));
        }

        [Fact]
        public void BuildArrowPolygon_HorizontalArrow_SevenSymmetricPointsWithTipAtTo()
        {
            // 水平向右箭头：from=(0,10), to=(100,10), width=4
            // 头长 = min(16, 0.4×100=40) = 16，颈截面 x=84；半杆宽 2，半翼宽 6
            var arrow = ScreenshotGeometry.BuildArrowPolygon(new Point(0, 10), new Point(100, 10), 4);

            Assert.Equal(7, arrow.Count);
            Assert.Equal(new Point(0, 12), arrow[0]);    // 尾左
            Assert.Equal(new Point(84, 12), arrow[1]);   // 颈左
            Assert.Equal(new Point(84, 16), arrow[2]);   // 翼左
            Assert.Equal(new Point(100, 10), arrow[3]);  // 尖端 = to
            Assert.Equal(new Point(84, 4), arrow[4]);    // 翼右
            Assert.Equal(new Point(84, 8), arrow[5]);    // 颈右
            Assert.Equal(new Point(0, 8), arrow[6]);     // 尾右

            // 关于箭轴（y = from.Y = 10）镜像对称：翼两点、颈两点、尾两点
            Assert.Equal(20.0, arrow[2].Y + arrow[4].Y);
            Assert.Equal(20.0, arrow[1].Y + arrow[5].Y);
            Assert.Equal(20.0, arrow[0].Y + arrow[6].Y);
        }

        [Fact]
        public void BuildArrowPolygon_SameStartAndEnd_ReturnsEmpty()
        {
            var arrow = ScreenshotGeometry.BuildArrowPolygon(new Point(5, 5), new Point(5, 5), 4);

            Assert.Empty(arrow);
        }

        [Fact]
        public void BuildArrowPolygon_ShortArrow_HeadLengthDegradesNotNegative()
        {
            // 全长 6 < 头长上限 4×4=16，头长应退化为 0.4×6=2.4，翼截面 x=3.6 在箭杆区间内
            var arrow = ScreenshotGeometry.BuildArrowPolygon(new Point(0, 0), new Point(6, 0), 4);

            Assert.Equal(7, arrow.Count);
            Assert.Equal(new Point(6, 0), arrow[3]);             // 尖端 = to
            Assert.True(arrow[2].X > 0 && arrow[2].X < 6,        // 翼截面仍在起点与尖端之间
                $"翼截面 X={arrow[2].X} 应落在 (0, 6) 内");
        }

        // 工具条固定 120×40。canvas 均从 (0,0) 起，间距/内缩 8，X 与选区右缘对齐。
        [Theory]
        [InlineData(100, 100, 200, 150, 1000, 800, 180, 258)]  // 下方足够：Bottom=250，250+8
        [InlineData(100, 110, 200, 150, 1000, 300, 180, 62)]   // 下方不足 → 上方：Top=110，110-8-40
        [InlineData(100, 10, 200, 280, 1000, 300, 180, 242)]   // 上下都不足 → 内部右下角：290-8-40
        [InlineData(-50, 100, 100, 100, 1000, 800, 0, 208)]    // X 越出左边：50-120=-70 钳回 0
        public void PlaceToolbar_PrefersBelowThenAboveThenInside(
            double sx, double sy, double sw, double sh, double cw, double ch, double ex, double ey)
        {
            var selection = new Rect(sx, sy, sw, sh);
            var canvas = new Rect(0, 0, cw, ch);

            Point result = ScreenshotGeometry.PlaceToolbar(selection, new Size(120, 40), canvas);

            Assert.Equal(new Point(ex, ey), result);
        }

        // 放大镜固定 200×150，偏移 24。canvas=(0,0,1000,800)。
        [Theory]
        [InlineData(100, 100, 124, 124)]   // 默认右下偏移
        [InlineData(900, 100, 676, 124)]   // 右侧越界 → 翻到左侧：900-24-200
        [InlineData(100, 700, 124, 526)]   // 下方越界 → 翻到上方：700-24-150
        [InlineData(900, 700, 676, 526)]   // 角落：左右、上下双翻
        public void PlaceMagnifier_FlipsWhenOverflow(double cx, double cy, double ex, double ey)
        {
            var canvas = new Rect(0, 0, 1000, 800);

            Point result = ScreenshotGeometry.PlaceMagnifier(new Point(cx, cy), new Size(200, 150), canvas);

            Assert.Equal(new Point(ex, ey), result);
        }

        // 文字便签内容区自适应。统一参数：宽 [100, 460]、高 [20, 500]、滚动条 8。
        [Theory]
        [InlineData(30, 18, 100, 20)]        // 极短文本：宽高均取下限
        [InlineData(200.2, 40, 201, 40)]     // 常规：宽向上取整
        [InlineData(200.2, 69.33, 201, 70)]  // 高也必须向上取整：留着小数会在窗口取整时被抹掉，凭空长出滚动条
        [InlineData(460, 300, 460, 300)]     // 恰好贴住宽度上限：不加滚动条
        [InlineData(300, 500, 300, 500)]     // 高恰好等于上限：闭区间，不算需要滚动、不加滚动条宽度
        [InlineData(900, 100, 460, 100)]     // 测量宽越上限（理论上不会发生）：钳回上限
        public void FitTextPinContent_WithinLimits_ClampsWithoutScrollBar(
            double mw, double mh, double ew, double eh)
        {
            Size result = ScreenshotGeometry.FitTextPinContent(
                new Size(mw, mh), 100, 460, 20, 500, 8, out bool needsScroll);

            Assert.Equal(new Size(ew, eh), result);
            Assert.False(needsScroll);
        }

        [Fact]
        public void FitTextPinContent_ContentTallerThanMax_ClampsHeightAndWidensForScrollBar()
        {
            Size result = ScreenshotGeometry.FitTextPinContent(
                new Size(300, 900), 100, 460, 20, 500, 8, out bool needsScroll);

            Assert.Equal(new Size(308, 500), result); // 高钳到 500，宽 300+8 让出滚动条
            Assert.True(needsScroll);
        }

        [Fact]
        public void FitTextPinContent_ScrollBarWidening_NeverExceedsMaxWidth()
        {
            Size result = ScreenshotGeometry.FitTextPinContent(
                new Size(458, 900), 100, 460, 20, 500, 8, out bool needsScroll);

            Assert.Equal(new Size(460, 500), result); // 458+8=466 → 钳回上限 460
            Assert.True(needsScroll);
        }

        [Fact]
        public void FitTextPinContent_MaxSmallerThanMin_ResultStaysWithinMax()
        {
            // 极窄/极矮工作区：上限小于下限时以上限为准，不能反而放大到下限
            Size result = ScreenshotGeometry.FitTextPinContent(
                new Size(10, 5), 100, 60, 20, 15, 8, out bool needsScroll);

            Assert.Equal(new Size(60, 15), result);
            Assert.False(needsScroll);
        }

        // 编辑态放大时左上角固定不动的内容区尺寸上限。统一参数：dpiScale=1、工作区右/下边缘 (1000,800)、
        // baseMax=(460,400)、chrome=(22,22)、min=(120,20)。
        [Theory]
        [InlineData(100, 100, 460, 400)]   // 常规：到边剩余充足，取基础上限
        [InlineData(700, 100, 278, 400)]   // 贴右缘：宽收窄到 (1000−700)−22=278，高不受影响
        [InlineData(100, 600, 460, 178)]   // 贴下缘：高收窄到 (800−600)−22=178，宽不受影响
        [InlineData(860, 100, 120, 400)]   // 剩余 (1000−860)−22=118 < minW=120：宽落到地板 120
        [InlineData(1100, 900, 120, 20)]   // 内容左上角已越出工作区、avail 为负：两轴均落到地板
        [InlineData(700, 790, 278, 20)]    // 右下角：宽收窄 278 + 高剩余 (800−790)−22=−12 落地板，两轴各走一条分支
        [InlineData(518, 378, 460, 400)]   // 剩余恰好等于基础上限：闭区间，不该被误收窄
        [InlineData(858, 758, 120, 20)]    // 剩余恰好等于地板：闭区间，不该被误抬到地板之上
        public void AnchorPinMaxContentSize_NarrowsMaxToRemainingSpace(
            double contentLeft, double contentTop, double expectedW, double expectedH)
        {
            var result = ScreenshotGeometry.AnchorPinMaxContentSize(
                contentLeft, contentTop,
                dpiScaleX: 1.0, dpiScaleY: 1.0,
                workAreaRightPhys: 1000, workAreaBottomPhys: 800,
                baseMaxContentW: 460, baseMaxContentH: 400,
                chromeW: 22, chromeH: 22,
                minContentW: 120, minContentH: 20);

            Assert.Equal((expectedW, expectedH), result);
        }

        [Fact]
        public void AnchorPinMaxContentSize_WithDpiScale_ConvertsPhysToDipByDivision()
        {
            // 物理 → DIP 用除法：scale=2 时剩余空间减半再扣 chrome
            var result = ScreenshotGeometry.AnchorPinMaxContentSize(
                contentLeftPhys: 100, contentTopPhys: 100,
                dpiScaleX: 2.0, dpiScaleY: 2.0,
                workAreaRightPhys: 1000, workAreaBottomPhys: 800,
                baseMaxContentW: 460, baseMaxContentH: 400,
                chromeW: 22, chromeH: 22,
                minContentW: 120, minContentH: 20);

            Assert.Equal((428.0, 328.0), result); // (1000−100)/2−22=428、(800−100)/2−22=328
        }

        [Fact]
        public void AnchorPinMaxContentSize_MaxSmallerThanMin_ResultStaysWithinMax()
        {
            // 极窄工作区：baseMax 小于 min 时尊重 baseMax 不反而放大到 min，
            // 与 FitTextPinContent_MaxSmallerThanMin_ResultStaysWithinMax 同口径
            var result = ScreenshotGeometry.AnchorPinMaxContentSize(
                contentLeftPhys: 100, contentTopPhys: 100,
                dpiScaleX: 1.0, dpiScaleY: 1.0,
                workAreaRightPhys: 1000, workAreaBottomPhys: 800,
                baseMaxContentW: 60, baseMaxContentH: 15,
                chromeW: 22, chromeH: 22,
                minContentW: 120, minContentH: 20);

            Assert.Equal((60.0, 15.0), result);
        }

        // ================= 标注命中测试 =================
        // 与上面那组不同，本组含椭圆与几何描边判定（WPF 的 StrokeContains 自带有 0.25 DIP 的隐式扁平化容差），
        // 探针一律离判定边界 ≥0.5 DIP，不能卡在边界上取精确相等。

        [Theory]
        [InlineData(50, 25, true)]     // 内部
        [InlineData(104, 25, true)]    // 右边界上（闭区间）
        [InlineData(-4, -4, true)]     // 左上角外，恰在容差内
        [InlineData(105, 25, false)]   // 右边界外 1 DIP
        [InlineData(-5, 25, false)]    // 左边界外 1 DIP
        public void HitTestBounds_WithinTolerance_ReturnsExpected(double x, double y, bool expected)
        {
            bool result = ScreenshotGeometry.HitTestBounds(new Rect(0, 0, 100, 50), 4.0, new Point(x, y));

            Assert.Equal(expected, result);
        }

        [Fact]
        public void HitTestBounds_EmptyRect_ReturnsFalse()
        {
            // 实现刻意不用 Rect.Inflate（它对 Rect.Empty 会抛异常），这条同时守住「不抛」
            Assert.False(ScreenshotGeometry.HitTestBounds(Rect.Empty, 4.0, new Point(0, 0)));
        }

        [Fact]
        public void PointsBounds_EmptyOrNull_ReturnsEmpty()
        {
            Assert.True(ScreenshotGeometry.PointsBounds(null!).IsEmpty);
            Assert.True(ScreenshotGeometry.PointsBounds(new PointCollection()).IsEmpty);
        }

        [Fact]
        public void PointsBounds_SinglePoint_ReturnsZeroSizedRect()
        {
            Rect bounds = ScreenshotGeometry.PointsBounds(new PointCollection { new Point(3, 4) });

            Assert.Equal(new Rect(3, 4, 0, 0), bounds);
            Assert.False(bounds.IsEmpty); // 尺寸为 0 不等于 Empty（与空集合区分）
        }

        [Fact]
        public void PointsBounds_MultiplePoints_ReturnsBoundingBox()
        {
            var points = new PointCollection { new Point(5, 9), new Point(-3, 20), new Point(11, 2) };

            Assert.Equal(new Rect(-3, 2, 14, 18), ScreenshotGeometry.PointsBounds(points));
        }

        // 布局矩形 (0,0,100,50)、线宽 3 → 描边中线 (1.5,1.5,97,47)，命中带半宽 = 3/2 + 4 = 5.5。
        [Theory]
        [InlineData(1.5, 25, true)]    // 左边框的中线上
        [InlineData(50, 1.5, true)]    // 上边框的中线上
        [InlineData(1.5, 1.5, true)]   // 左上角（角也属于边框带）
        [InlineData(-3.5, 25, true)]   // 边框外侧 5 DIP：容差内
        [InlineData(6.5, 25, true)]    // 边框内侧 5 DIP：容差内
        [InlineData(-5, 25, false)]    // 边框外侧 6.5 DIP：容差外
        [InlineData(8, 25, false)]     // 边框内侧 6.5 DIP：容差外
        [InlineData(50, 25, false)]    // ★ 内部空白必须穿透——否则无法在大矩形内部继续画小矩形
        public void HitTestShapeBorder_Rectangle_OnlyBorderBandHits(double x, double y, bool expected)
        {
            bool result = ScreenshotGeometry.HitTestShapeBorder(
                isEllipse: false, new Rect(0, 0, 100, 50),
                strokeThickness: 3.0, tolerance: 4.0, new Point(x, y));

            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData(1.5, 25, true)]    // 零宽矩形的描边中线
        [InlineData(5, 25, true)]      // 距中线 3.5 DIP：容差内
        [InlineData(9.5, 25, false)]   // 距中线 8 DIP：容差外
        public void HitTestShapeBorder_ZeroWidthRectangle_DegradesToBand(double x, double y, bool expected)
        {
            bool result = ScreenshotGeometry.HitTestShapeBorder(
                isEllipse: false, new Rect(0, 0, 0, 50),
                strokeThickness: 3.0, tolerance: 4.0, new Point(x, y));

            Assert.Equal(expected, result);
        }

        [Fact]
        public void HitTestShapeBorder_StrokeWiderThanSide_DoesNotThrow()
        {
            // 宽 4、线宽 12 是合法元素（MinShapeSize 只要求宽高不同时小于 3）：内缩后边长成负值，
            // 实现须钳到 0，而不是让 new Rect 抛 ArgumentException（那条路径在鼠标事件里）
            const double x = 6.0; // 描边中线

            Assert.True(ScreenshotGeometry.HitTestShapeBorder(
                isEllipse: false, new Rect(0, 0, 4, 100), 12.0, 4.0, new Point(x, 50)));
            Assert.False(ScreenshotGeometry.HitTestShapeBorder(
                isEllipse: false, new Rect(0, 0, 4, 100), 12.0, 4.0, new Point(x + 12, 50)));
            Assert.True(ScreenshotGeometry.HitTestShapeBorder(
                isEllipse: true, new Rect(0, 0, 4, 100), 12.0, 4.0, new Point(x, 50)));
        }

        [Fact]
        public void HitTestShapeBorder_EmptyRect_ReturnsFalseWithoutThrowing()
        {
            // EllipseGeometry(Rect.Empty) 会抛异常，实现必须提前挡掉
            Assert.False(ScreenshotGeometry.HitTestShapeBorder(isEllipse: true, Rect.Empty, 3.0, 4.0, new Point(0, 0)));
            Assert.False(ScreenshotGeometry.HitTestShapeBorder(isEllipse: false, Rect.Empty, 3.0, 4.0, new Point(0, 0)));
        }

        // 同上布局矩形：描边中线为椭圆 (中心 (50,25)，rx=48.5、ry=23.5)。
        [Theory]
        [InlineData(1.5, 25, true)]    // 左顶点（在椭圆环上）
        [InlineData(50, 1.5, true)]    // 上顶点
        [InlineData(98.5, 25, true)]   // 右顶点
        [InlineData(50, 25, false)]    // ★ 椭圆内部空白必须穿透
        [InlineData(50, 40, false)]    // 内部靠下：距下边界 8.5 DIP
        [InlineData(2, 2, false)]      // ★ 布局矩形的角：椭圆之外，旧的包围盒实现会误判命中
        public void HitTestShapeBorder_Ellipse_OnlyRingHits(double x, double y, bool expected)
        {
            bool result = ScreenshotGeometry.HitTestShapeBorder(
                isEllipse: true, new Rect(0, 0, 100, 50),
                strokeThickness: 3.0, tolerance: 4.0, new Point(x, y));

            Assert.Equal(expected, result);
        }

        // 水平向右箭头 from=(0,10) to=(100,10) width=4：箭杆 y∈[8,12]、尾 x∈[0,84]、翼展到 y∈[4,16]。
        [Theory]
        [InlineData(50, 10, true)]     // 箭杆填充内
        [InlineData(99, 10, true)]     // 尖端附近的填充内
        [InlineData(103, 10, true)]    // 尖端外 3 DIP：容差带内
        [InlineData(50, 3, false)]     // ★ 包围盒内、箭杆外的空白：距边界 5 DIP，必须穿透
        [InlineData(105, 10, false)]   // 尖端外 5 DIP：容差带外
        public void HitTestPolygonFill_Arrow_OnlyFillAndToleranceBand(double x, double y, bool expected)
        {
            var arrow = ScreenshotGeometry.BuildArrowPolygon(new Point(0, 10), new Point(100, 10), 4);

            Assert.Equal(expected, ScreenshotGeometry.HitTestPolygonFill(arrow, 4.0, new Point(x, y)));
        }

        [Fact]
        public void HitTestPolygonFill_Triangle_KeepsBoundsBlankTransparent()
        {
            // 直角三角形 (0,0)-(100,0)-(0,100)：斜边外侧的大片空白都在包围盒内，
            // 旧实现按包围盒命中会把这一整片拦截（与箭头包围盒空白角抢走绘制是同一个问题）
            var triangle = new PointCollection { new Point(0, 0), new Point(100, 0), new Point(0, 100) };

            Assert.True(ScreenshotGeometry.HitTestPolygonFill(triangle, 4.0, new Point(10, 10)));      // 填充内
            Assert.True(ScreenshotGeometry.HitTestPolygonFill(triangle, 4.0, new Point(51, 51)));      // 斜边外约 1.4 DIP：容差带内
            Assert.True(ScreenshotGeometry.HitTestPolygonFill(triangle, 4.0, new Point(0, -2)));       // 顶点外 2 DIP：容差带内
            Assert.False(ScreenshotGeometry.HitTestPolygonFill(triangle, 4.0, new Point(53.5, 53.5))); // 斜边外约 5 DIP
            Assert.False(ScreenshotGeometry.HitTestPolygonFill(triangle, 4.0, new Point(80, 80)));     // 斜边外约 42 DIP
        }

        [Fact]
        public void HitTestPolygonFill_TooFewPoints_ReturnsFalse()
        {
            Assert.False(ScreenshotGeometry.HitTestPolygonFill(null!, 4.0, new Point(0, 0)));
            Assert.False(ScreenshotGeometry.HitTestPolygonFill(new PointCollection(), 4.0, new Point(0, 0)));
            Assert.False(ScreenshotGeometry.HitTestPolygonFill(
                new PointCollection { new Point(0, 0) }, 4.0, new Point(0, 0)));
            Assert.False(ScreenshotGeometry.HitTestPolygonFill(
                new PointCollection { new Point(0, 0), new Point(5, 5) }, 4.0, new Point(1, 1)));
        }
    }
}
