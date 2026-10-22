using System;
using System.Drawing;
using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests
{
    /// <summary>
    /// 输入法提示严格插入符定位的纯几何测试（<see cref="CaretLocator"/>）。
    /// 全部坐标为物理像素；不访问真实 UIA/Win32 焦点，避免依赖运行环境。
    /// </summary>
    public class CaretLocatorTests
    {
        [Fact]
        public void ReadSingleCaretRect_NormalCaret_ReturnsRoundedRect()
        {
            Rectangle? result = CaretLocator.ReadSingleCaretRect(new[] { 100.4, 200.6, 1.2, 18.8 });

            Assert.Equal<Rectangle?>(new Rectangle(100, 201, 1, 19), result);
        }

        [Fact]
        public void ReadSingleCaretRect_ZeroWidthCaret_NormalizesToSinglePixel()
        {
            // UIA 的退化插入点可能给出零宽矩形：这是合法 caret，只补 1px
            Rectangle? result = CaretLocator.ReadSingleCaretRect(new[] { 100.0, 200.0, 0.0, 18.0 });

            Assert.Equal<Rectangle?>(new Rectangle(100, 200, 1, 18), result);
        }

        [Fact]
        public void ReadSingleCaretRect_EmptyBounds_ReturnsNull()
        {
            // 零长范围可能没有边界矩形：合法结果；当前 UIA 路径不启用邻字推导
            Assert.Null(CaretLocator.ReadSingleCaretRect(Array.Empty<double>()));
            Assert.Null(CaretLocator.ReadSingleCaretRect(null));
        }

        [Fact]
        public void ReadSingleCaretRect_MultipleRectangles_ReturnsNull()
        {
            // 两个矩形（多行/折行）不是单个 caret，绝不能取最后一个
            Assert.Null(CaretLocator.ReadSingleCaretRect(new[]
            {
                0.0, 0.0, 1.0, 20.0,
                0.0, 20.0, 1.0, 20.0,
            }));
        }

        [Fact]
        public void ReadSingleCaretRect_FullLineWideRect_ReturnsNull()
        {
            // 整行/整个控件宽矩形不是 caret，不能压成 1px 冒充
            Assert.Null(CaretLocator.ReadSingleCaretRect(new[] { 0.0, 0.0, 500.0, 20.0 }));
        }

        [Theory]
        [InlineData(0.0)]      // 零高
        [InlineData(-1.0)]     // 负高
        public void ReadSingleCaretRect_InvalidHeight_ReturnsNull(double height)
        {
            Assert.Null(CaretLocator.ReadSingleCaretRect(new[] { 0.0, 0.0, 1.0, height }));
        }

        [Fact]
        public void ReadSingleCaretRect_NegativeWidth_ReturnsNull()
        {
            Assert.Null(CaretLocator.ReadSingleCaretRect(new[] { 0.0, 0.0, -1.0, 20.0 }));
        }

        [Fact]
        public void ReadSingleCaretRect_NonFiniteValues_ReturnsNull()
        {
            Assert.Null(CaretLocator.ReadSingleCaretRect(new[] { double.NaN, 0.0, 1.0, 20.0 }));
            Assert.Null(CaretLocator.ReadSingleCaretRect(new[] { 0.0, double.PositiveInfinity, 1.0, 20.0 }));
        }

        [Fact]
        public void IsTrustedCaretRect_NarrowAndTall_IsTrusted()
        {
            Assert.True(CaretLocator.IsTrustedCaretRect(new Rectangle(100, 200, 1, 20)));
            Assert.True(CaretLocator.IsTrustedCaretRect(new Rectangle(100, 200, 2, 40)));
            Assert.True(CaretLocator.IsTrustedCaretRect(new Rectangle(100, 200, 0, 20)));
        }

        [Fact]
        public void IsTrustedCaretRect_FullLineWide_IsRejected()
        {
            Assert.False(CaretLocator.IsTrustedCaretRect(new Rectangle(0, 0, 500, 20)));
            Assert.False(CaretLocator.IsTrustedCaretRect(new Rectangle(0, 0, 20, 12)));
        }

        [Fact]
        public void IsTrustedCaretRect_ZeroOrNegativeHeight_IsRejected()
        {
            Assert.False(CaretLocator.IsTrustedCaretRect(new Rectangle(0, 0, 1, 0)));
            Assert.False(CaretLocator.IsTrustedCaretRect(new Rectangle(0, 0, 1, -1)));
        }

        [Theory]
        [InlineData(100, 200, 12, 20, false, 100)]
        [InlineData(100, 200, 12, 20, true, 112)]
        public void DeriveCaretFromNeighbor_LtrSingleChar_UsesProvenEndpoint(
            int left, int top, int width, int height, bool caretAtEnd, int expectedX)
        {
            Rectangle? result = CaretLocator.DeriveCaretFromNeighbor(
                new Rectangle(left, top, width, height), caretAtEnd, isRightToLeft: false);

            Assert.Equal<Rectangle?>(new Rectangle(expectedX, top, 1, height), result);
        }

        [Fact]
        public void DeriveCaretFromNeighbor_CjkChar_IsAccepted()
        {
            Rectangle? result = CaretLocator.DeriveCaretFromNeighbor(
                new Rectangle(100, 200, 20, 20), caretAtEnd: true, isRightToLeft: false);

            Assert.Equal<Rectangle?>(new Rectangle(120, 200, 1, 20), result);
        }

        [Theory]
        [InlineData(true)]   // RTL
        [InlineData(null)]   // 方向未知
        public void DeriveCaretFromNeighbor_RtlOrUnknownDirection_IsRejected(bool? isRightToLeft)
        {
            Assert.Null(CaretLocator.DeriveCaretFromNeighbor(
                new Rectangle(100, 200, 12, 20), caretAtEnd: true, isRightToLeft));
        }

        [Fact]
        public void DeriveCaretFromNeighbor_FullLineRect_IsRejected()
        {
            // 换行/折行导致矩形宽得像整行时，不推导
            Assert.Null(CaretLocator.DeriveCaretFromNeighbor(
                new Rectangle(0, 0, 500, 20), caretAtEnd: true, isRightToLeft: false));
        }

        [Theory]
        [InlineData(0, 20)]    // 零宽
        [InlineData(12, 0)]    // 零高
        [InlineData(-1, 20)]   // 负宽
        public void DeriveCaretFromNeighbor_DegenerateRect_IsRejected(int width, int height)
        {
            Assert.Null(CaretLocator.DeriveCaretFromNeighbor(
                new Rectangle(0, 0, width, height), caretAtEnd: true, isRightToLeft: false));
        }

        [Theory]
        [InlineData(1, 0, 0, true)]    // 退化范围、无矩形：合法（当前不启用邻字推导）
        [InlineData(1, 0, 1, true)]    // 退化范围、单矩形
        [InlineData(1, -1, 1, false)]  // 非空选区（起点在前）
        [InlineData(1, 1, 1, false)]   // 非空选区（起点在后）：绝不折叠到末尾
        [InlineData(0, 0, 0, false)]   // 没有选区
        [InlineData(2, 0, 1, false)]   // 多选区：不猜 active end
        [InlineData(1, 0, 2, false)]   // 多矩形：不是单个 caret
        [InlineData(1, 0, -1, false)]  // 矩形无法解析
        public void CanUseSelectionAsCaret_OnlyAcceptsProvablyDegenerateSingleRange(
            int rangeCount, int compareStartToEnd, int rectCount, bool expected)
        {
            Assert.Equal(expected, CaretLocator.CanUseSelectionAsCaret(
                rangeCount, compareStartToEnd, rectCount));
        }

        [Fact]
        public void ImeFocusIdentity_SameHwndDifferentRuntimeId_IsDistinct()
        {
            // 同一 HWND 内的虚拟控件（浏览器/Electron）必须靠 RuntimeId 区分
            var a = new ImeFocusIdentity(new IntPtr(1), new IntPtr(2), 3, 4, "42,100");
            var b = new ImeFocusIdentity(new IntPtr(1), new IntPtr(2), 3, 4, "42,200");

            Assert.NotEqual(a, b);
            Assert.Equal(a, new ImeFocusIdentity(new IntPtr(1), new IntPtr(2), 3, 4, "42,100"));
        }

        [Theory]
        [InlineData("Edit", true)]
        [InlineData("edit", true)]
        [InlineData("RichEdit20W", true)]
        [InlineData("RICHEDIT50W", true)]
        [InlineData("HwndWrapper[DefaultDomain;;...]", false)]
        [InlineData("Chrome_RenderWidgetHostHWND", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsNativeTextClassName_OnlyAcceptsNativeEditRichEdit(string? className, bool expected)
        {
            // 只有原生独立 HWND 的 Edit/RichEdit 允许纯 Win32 快路径
            Assert.Equal(expected, CaretLocator.IsNativeTextClassName(className));
        }

        [Fact]
        public void IsNativeTextReadOnly_UsesEsReadOnly()
        {
            Assert.False(CaretLocator.IsNativeTextReadOnly(0));
            Assert.True(CaretLocator.IsNativeTextReadOnly(0x0800)); // ES_READONLY
            Assert.True(CaretLocator.IsNativeTextReadOnly(0x0800 | 0x0004));
        }

        [Theory]
        [InlineData(true, null, true)]   // 原生独立 HWND：HWND 本身足够
        [InlineData(true, "", true)]
        [InlineData(false, "42,100", true)]
        [InlineData(false, null, false)] // 非原生控件无 RuntimeId：身份不足，不猜
        [InlineData(false, "", false)]
        [InlineData(false, "   ", false)]
        public void HasSufficientIdentity_RequiresRuntimeIdForNonNativeControls(
            bool isNativeTextControl, string? runtimeId, bool expected)
        {
            // 同一 HWND 内可能有多个 WPF/浏览器输入框，非原生控件必须有 RuntimeId
            Assert.Equal(expected, CaretLocator.HasSufficientIdentity(isNativeTextControl, runtimeId));
        }
    }
}
