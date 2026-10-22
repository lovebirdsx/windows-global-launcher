using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests
{
    /// <summary>
    /// 输入法光标后端的决策组合测试：Pattern2 配对、Terminal 专用适配、MSAA 接受条件、
    /// 前后身份校验与坐标来源优先级。
    /// <para>
    /// 这些测试直接调用生产决策函数（不是只断言 ComImport 声明形状），但不会创建真实
    /// UIA/MSAA/终端 provider，因此不代表真机验收；真机验收需在用户允许部署后另行进行。
    /// </para>
    /// </summary>
    public class ImeCaretBackendTests
    {
        // ---------- TextPattern2 patternId / IID 配对 ----------

        [Fact]
        public void TextPattern2_UsesDocumentedPatternIdAndMatchingIidPair()
        {
            // 微软文档：TextPatternId=10014，TextPattern2Id=10024。
            Assert.Equal(10014, UiaCaretReader.UIA_TextPatternId);
            Assert.Equal(10024, UiaCaretReader.UIA_TextPattern2Id);

            // 请求 Pattern2 时使用的 IID 必须与 ComImport 接口 Guid 完全一致，
            // 否则 GetCurrentPatternAs 永远返回 E_NOINTERFACE（真实兼容缺口）。
            Assert.Equal(
                new Guid("506A921A-FCC9-409F-B23B-37EB74106872"),
                UiaCaretReader.IidIUIAutomationTextPattern2);
            Assert.Equal(
                typeof(UiaCaretReader.IUIAutomationTextPattern2).GUID,
                UiaCaretReader.IidIUIAutomationTextPattern2);

            Assert.Equal(
                new Guid("32EBA289-3583-42C9-9C59-3B6D9A1E9B6A"),
                UiaCaretReader.IidIUIAutomationTextPattern);
        }

        // ---------- 前后元素身份 ----------

        [Theory]
        [InlineData(0, 1, "42,100", "42,100", true)]   // CompareElements 成功且相同
        [InlineData(0, 0, "42,100", "42,200", false)]  // 同一 HWND 内 A→B：必须作废
        [InlineData(1, 0, "42,100", "42,100", true)]   // CompareElements 不可用，RuntimeId 可证明
        [InlineData(1, 0, "42,100", "42,200", false)]  // RuntimeId 不同：作废
        [InlineData(1, 0, null, "42,100", false)]      // 只有一边有 RuntimeId：无法证明
        [InlineData(1, 0, "42,100", null, false)]
        [InlineData(1, 0, null, null, false)]
        public void IsSameFocusIdentity_RequiresElementOrRuntimeIdProof(
            int compareHr, int compareSame, string? runtimeId, string? runtimeId2, bool expected)
        {
            Assert.Equal(expected, UiaCaretReader.IsSameFocusIdentity(
                compareHr, compareSame, runtimeId, runtimeId2));
        }

        // ---------- Terminal provider 限定 ----------

        [Theory]
        [InlineData(50020, "TermControl", "WindowsTerminal", true)]
        [InlineData(50020, "TermControl", "WindowsTerminal.exe", false)] // ProcessName 不带扩展名
        [InlineData(50020, "TermControl", "conhost", false)]
        [InlineData(50020, "Pane", "WindowsTerminal", false)]
        [InlineData(50004, "TermControl", "WindowsTerminal", false)]     // 必须 ControlType=Text
        [InlineData(50020, null, "WindowsTerminal", false)]
        [InlineData(50020, "termcontrol", "WindowsTerminal", false)]     // 类名大小写严格
        public void IsTerminalProvider_RequiresTextTermControlAndWindowsTerminal(
            int controlType, string? className, string? processName, bool expected)
        {
            Assert.Equal(expected, UiaCaretReader.IsTerminalProvider(
                controlType, className, processName));
        }

        [Fact]
        public void DecideProviderEditable_PlainTextControlIsNeverEditable()
        {
            // 普通 Text（非限定 TermControl）不得被当作输入控件放行。
            Assert.False(UiaCaretReader.DecideProviderEditable(
                controlType: 50020,
                terminalProvider: false,
                hasKeyboardFocus: true,
                focusable: true,
                enabled: true,
                isPassword: false,
                valueReadOnly: null,
                textPatternAvailable: true,
                textReadOnly: false,
                caretActive: true));
        }

        [Fact]
        public void DecideProviderEditable_EditWithoutTextPatternKeepsLegacyPath()
        {
            // Edit 本身是可编辑的强证据（密码框/旧 provider 常不暴露 TextPattern），
            // 本次改造不得把既有 UIA Edit 路径改坏。
            Assert.True(UiaCaretReader.DecideProviderEditable(
                controlType: 50004, // Edit
                terminalProvider: false,
                hasKeyboardFocus: true,
                focusable: false,
                enabled: true,
                isPassword: false,
                valueReadOnly: null,
                textPatternAvailable: false,
                textReadOnly: null,
                caretActive: false));
        }

        [Fact]
        public void DocumentWithActiveCaretButEmptyRect_StillEligibleForMsaa()
        {
            // Pattern2 报告 active 但矩形为空：CaretActive 仍证明可编辑，
            // 因此后续可以继续尝试 MSAA，而不是直接判成“不可编辑”。
            bool editable = UiaCaretReader.DecideProviderEditable(
                controlType: 50030, // Document
                terminalProvider: false,
                hasKeyboardFocus: true,
                focusable: false,
                enabled: true,
                isPassword: false,
                valueReadOnly: null,
                textPatternAvailable: true,
                textReadOnly: null,
                caretActive: true);

            Assert.True(editable);
            Assert.True(UiaCaretReader.ShouldTryMsaa(
                editable, terminalProvider: false, hasRuntimeId: true,
                hasUiaCaret: false, allowMsaa: true));
        }

        [Theory]
        [InlineData(true, true, false, null, true, false, true)]   // 合格 TermControl
        [InlineData(false, true, false, null, true, false, false)] // 仅 focusable，无真实 HasFocus
        [InlineData(true, false, false, null, true, false, false)] // 禁用
        [InlineData(true, true, true, null, true, false, false)]   // 密码终端
        [InlineData(true, true, false, true, true, false, false)]  // ValuePattern 明确只读
        [InlineData(true, true, false, null, false, false, false)] // 无 TextPattern
        [InlineData(true, true, false, null, true, null, false)]   // IsReadOnly 未知：不放行
        [InlineData(true, true, false, null, true, true, false)]   // IsReadOnly=true：不放行
        public void DecideTerminalEditable_RequiresStrictEvidence(
            bool hasKeyboardFocus, bool enabled, bool isPassword, bool? valueReadOnly,
            bool textPatternAvailable, bool? textReadOnly, bool expected)
        {
            Assert.Equal(expected, UiaCaretReader.DecideTerminalEditable(
                hasKeyboardFocus, enabled, isPassword, valueReadOnly,
                textPatternAvailable, textReadOnly));
        }

        // ---------- MSAA 启用条件 ----------

        [Theory]
        [InlineData(true, false, true, false, true, true)]   // Chrome 类：可编辑+RuntimeId+无矩形
        [InlineData(true, false, true, true, true, false)]   // 已有 UIA 矩形：不采样 MSAA
        [InlineData(true, false, false, false, true, false)] // 无 RuntimeId：身份不足
        [InlineData(true, true, true, false, true, false)]   // Terminal：绝不走 MSAA
        [InlineData(false, false, true, false, true, false)] // UIA 未证明可编辑
        [InlineData(true, false, true, false, false, false)] // 已有可信 Win32 光标
        public void ShouldTryMsaa_OnlyForProvenEditableNonTerminalWithStableIdentity(
            bool editable, bool terminal, bool hasRuntimeId, bool hasUiaCaret,
            bool allowMsaa, bool expected)
        {
            Assert.Equal(expected, UiaCaretReader.ShouldTryMsaa(
                editable, terminal, hasRuntimeId, hasUiaCaret, allowMsaa));
        }

        [Fact]
        public void ReadOnlyOrUnknownEditableEvidence_NeverEnablesMsaa()
        {
            // 明确只读（ValuePattern=true）→ 不可编辑 → 也不允许由 MSAA 几何反向放行。
            bool editable = UiaCaretReader.DecideProviderEditable(
                controlType: 50004, // Edit
                terminalProvider: false,
                hasKeyboardFocus: true,
                focusable: false,
                enabled: true,
                isPassword: false,
                valueReadOnly: true,
                textPatternAvailable: true,
                textReadOnly: null,
                caretActive: false);

            Assert.False(editable);
            Assert.False(UiaCaretReader.ShouldTryMsaa(
                editable, terminalProvider: false, hasRuntimeId: true,
                hasUiaCaret: false, allowMsaa: true));
        }

        // ---------- Terminal 格子接受条件 ----------

        [Fact]
        public void AcceptTerminalCellRect_WideCharCellKeepsFullWidth()
        {
            var element = new Rectangle(0, 0, 800, 600);

            Rectangle? cell = UiaCaretReader.AcceptTerminalCellRect(
                caretRangeDegenerate: true,
                expandedStartMatchesCaretStart: true,
                expandedNonDegenerate: true,
                bounds: new[] { 100.0, 200.0, 20.0, 20.0 },
                elementBounds: element);

            // 20px 宽的 CJK 格子必须原样保留，不能被普通插入符阈值压成 1px。
            Assert.Equal<Rectangle?>(new Rectangle(100, 200, 20, 20), cell);
        }

        [Theory]
        [InlineData(false, true, true)]   // 非退化选择：不折叠、不取末端
        [InlineData(true, false, true)]   // 展开起点不等于原 caret 起点（含 end 匹配猜测）
        [InlineData(true, true, false)]   // 展开后退化：无法证明对应真实字符
        public void AcceptTerminalCellRect_RejectsUnprovenRanges(
            bool caretRangeDegenerate, bool expandedStartMatchesCaretStart, bool expandedNonDegenerate)
        {
            Rectangle? cell = UiaCaretReader.AcceptTerminalCellRect(
                caretRangeDegenerate,
                expandedStartMatchesCaretStart,
                expandedNonDegenerate,
                bounds: new[] { 100.0, 200.0, 20.0, 20.0 },
                elementBounds: new Rectangle(0, 0, 800, 600));

            Assert.Null(cell);
        }

        [Fact]
        public void AcceptTerminalCellRect_RejectsMultipleRectangles()
        {
            Rectangle? cell = UiaCaretReader.AcceptTerminalCellRect(
                caretRangeDegenerate: true,
                expandedStartMatchesCaretStart: true,
                expandedNonDegenerate: true,
                bounds: new double[]
                {
                    100.0, 200.0, 20.0, 20.0,
                    120.0, 200.0, 20.0, 20.0,
                },
                elementBounds: new Rectangle(0, 0, 800, 600));

            Assert.Null(cell);
        }

        [Fact]
        public void AcceptTerminalCellRect_RejectsOffscreenOrOutsideElement()
        {
            var element = new Rectangle(0, 0, 800, 600);

            Assert.Null(UiaCaretReader.AcceptTerminalCellRect(
                true, true, true,
                bounds: new[] { 900.0, 200.0, 20.0, 20.0 },
                elementBounds: element));

            // 没有元素边界时无法证明属于当前可视输入区域：拒绝，不猜。
            Assert.Null(UiaCaretReader.AcceptTerminalCellRect(
                true, true, true,
                bounds: new[] { 100.0, 200.0, 20.0, 20.0 },
                elementBounds: null));
        }

        [Theory]
        [InlineData(0.0, 20.0)]    // 零宽
        [InlineData(20.0, 0.0)]    // 零高
        [InlineData(-1.0, 20.0)]   // 负宽
        [InlineData(double.NaN, 20.0)]
        [InlineData(double.PositiveInfinity, 20.0)]
        public void AcceptTerminalCellRect_RejectsInvalidGeometry(double width, double height)
        {
            Assert.Null(UiaCaretReader.AcceptTerminalCellRect(
                true, true, true,
                bounds: new[] { 100.0, 200.0, width, height },
                elementBounds: new Rectangle(0, 0, 800, 600)));
        }

        // ---------- MSAA 矩形接受条件 ----------

        [Fact]
        public void AcceptCaretRect_AcceptsCaretRole()
        {
            Rectangle? caret = MsaaCaretReader.AcceptCaretRect(
                MsaaCaretReader.RoleSystemCaret, state: 0, left: 100, top: 200, width: 1, height: 18,
                focusedElementBounds: new Rectangle(0, 0, 800, 600));

            Assert.Equal<Rectangle?>(new Rectangle(100, 200, 1, 18), caret);
        }

        [Fact]
        public void AcceptCaretRect_RejectsTextRole()
        {
            // 按批准计划只接受 ROLE_SYSTEM_CARET；ROLE_SYSTEM_TEXT 还覆盖普通静态文本，
            // 无法证明它是插入符，不得宽松放行。
            Assert.Null(MsaaCaretReader.AcceptCaretRect(
                0x2A /* ROLE_SYSTEM_TEXT */, 0, 100, 200, 1, 18,
                new Rectangle(0, 0, 800, 600)));
        }

        [Fact]
        public void AcceptCaretRect_RejectsWrongRole()
        {
            // 可点击控件/列表项等不是 caret 对象
            Assert.Null(MsaaCaretReader.AcceptCaretRect(
                0x2B /* ROLE_SYSTEM_PUSHBUTTON */, 0, 100, 200, 1, 18,
                new Rectangle(0, 0, 800, 600)));
        }

        [Theory]
        [InlineData(MsaaCaretReader.StateSystemUnavailable)]
        [InlineData(MsaaCaretReader.StateSystemInvisible)]
        [InlineData(MsaaCaretReader.StateSystemOffscreen)]
        [InlineData(MsaaCaretReader.StateSystemInvisible | MsaaCaretReader.StateSystemOffscreen)]
        public void AcceptCaretRect_RejectsUnavailableInvisibleOrOffscreenState(int state)
        {
            Assert.Null(MsaaCaretReader.AcceptCaretRect(
                MsaaCaretReader.RoleSystemCaret, state, 100, 200, 1, 18,
                new Rectangle(0, 0, 800, 600)));
        }

        [Fact]
        public void AcceptCaretRect_RejectsFullLineWideRectangle()
        {
            // 宽矩形不是 caret，绝不压成 1px。
            Assert.Null(MsaaCaretReader.AcceptCaretRect(
                MsaaCaretReader.RoleSystemCaret, 0, 0, 200, 500, 18,
                new Rectangle(0, 0, 800, 600)));
        }

        [Fact]
        public void AcceptCaretRect_RejectsOutsideFocusedElementBounds()
        {
            Assert.Null(MsaaCaretReader.AcceptCaretRect(
                MsaaCaretReader.RoleSystemCaret, 0, 900, 200, 1, 18,
                new Rectangle(0, 0, 800, 600)));

            // 没有 UIA 元素边界时无法证明归属：拒绝。
            Assert.Null(MsaaCaretReader.AcceptCaretRect(
                MsaaCaretReader.RoleSystemCaret, 0, 100, 200, 1, 18,
                focusedElementBounds: null));
        }

        [Theory]
        [InlineData(0, 18)]
        [InlineData(1, 0)]
        [InlineData(-1, 18)]
        [InlineData(1, -1)]
        public void AcceptCaretRect_RejectsDegenerateSize(int width, int height)
        {
            Assert.Null(MsaaCaretReader.AcceptCaretRect(
                MsaaCaretReader.RoleSystemCaret, 0, 100, 200, width, height,
                new Rectangle(0, 0, 800, 600)));
        }

        // ---------- 坐标来源合成（实际后端组合） ----------

        [Fact]
        public void SelectCaret_Win32TakesPriorityOverUiaAndTerminal()
        {
            var selection = CaretLocator.SelectCaret(
                win32Caret: new Rectangle(10, 20, 1, 18),
                uiaSource: ImeCaretSource.TerminalCell,
                uiaCaret: new Rectangle(100, 200, 20, 20));

            Assert.Equal(ImeCaretSource.Win32, selection.Source);
            Assert.Equal<Rectangle?>(new Rectangle(10, 20, 1, 18), selection.Caret);
        }

        [Fact]
        public void SelectCaret_TerminalCellPreservesWideCell()
        {
            var selection = CaretLocator.SelectCaret(
                win32Caret: null,
                uiaSource: ImeCaretSource.TerminalCell,
                uiaCaret: new Rectangle(100, 200, 20, 20));

            Assert.Equal(ImeCaretSource.TerminalCell, selection.Source);
            Assert.Equal<Rectangle?>(new Rectangle(100, 200, 20, 20), selection.Caret);
        }

        [Fact]
        public void SelectCaret_GenericUiaWideRectIsRejectedWithoutFakingOnePixel()
        {
            var selection = CaretLocator.SelectCaret(
                win32Caret: null,
                uiaSource: ImeCaretSource.Uia,
                uiaCaret: new Rectangle(0, 0, 500, 20));

            Assert.Equal(ImeCaretSource.None, selection.Source);
            Assert.Null(selection.Caret);
        }

        [Fact]
        public void SelectCaret_MsaaNarrowRectKeepsSource()
        {
            var selection = CaretLocator.SelectCaret(
                win32Caret: null,
                uiaSource: ImeCaretSource.Msaa,
                uiaCaret: new Rectangle(100, 200, 1, 18));

            Assert.Equal(ImeCaretSource.Msaa, selection.Source);
            Assert.Equal<Rectangle?>(new Rectangle(100, 200, 1, 18), selection.Caret);
        }

        [Fact]
        public void SelectCaret_UiaNarrowRectKeepsExistingUiaPath()
        {
            // 既有 UIA 直接矩形路径（WPF/VS Code 等）不得因本次改造退化。
            var selection = CaretLocator.SelectCaret(
                win32Caret: null,
                uiaSource: ImeCaretSource.Uia,
                uiaCaret: new Rectangle(100, 200, 2, 20));

            Assert.Equal(ImeCaretSource.Uia, selection.Source);
            Assert.Equal<Rectangle?>(new Rectangle(100, 200, 2, 20), selection.Caret);
        }

        [Fact]
        public void SelectCaret_NoProofMeansNoCoordinate()
        {
            var selection = CaretLocator.SelectCaret(
                win32Caret: null, uiaSource: ImeCaretSource.None, uiaCaret: null);

            Assert.Equal(ImeCaretSource.None, selection.Source);
            Assert.Null(selection.Caret);
        }

        [Fact]
        public void ChromeLikeComposition_EditableWithRuntimeIdUsesMsaaCaret()
        {
            // Chrome 类 Edit：UIA 证明可编辑、有 RuntimeId、无直接矩形。
            bool editable = UiaCaretReader.DecideProviderEditable(
                controlType: 50004, // Edit
                terminalProvider: false,
                hasKeyboardFocus: true,
                focusable: false,
                enabled: true,
                isPassword: false,
                valueReadOnly: null,
                textPatternAvailable: false,
                textReadOnly: null,
                caretActive: false);

            Assert.True(editable);
            Assert.True(UiaCaretReader.ShouldTryMsaa(
                editable, terminalProvider: false, hasRuntimeId: true,
                hasUiaCaret: false, allowMsaa: true));

            var selection = CaretLocator.SelectCaret(
                win32Caret: null,
                uiaSource: ImeCaretSource.Msaa,
                uiaCaret: new Rectangle(50, 60, 1, 18));

            Assert.Equal(ImeCaretSource.Msaa, selection.Source);
        }

        [Fact]
        public void TerminalLikeComposition_UsesCellAndNeverMsaa()
        {
            bool editable = UiaCaretReader.DecideProviderEditable(
                controlType: 50020, // Text
                terminalProvider: true,
                hasKeyboardFocus: true,
                focusable: true,
                enabled: true,
                isPassword: false,
                valueReadOnly: null,
                textPatternAvailable: true,
                textReadOnly: false,
                caretActive: true);

            Assert.True(editable);

            // Terminal 即使没有矩形也不允许由 MSAA 补坐标。
            Assert.False(UiaCaretReader.ShouldTryMsaa(
                editable, terminalProvider: true, hasRuntimeId: true,
                hasUiaCaret: false, allowMsaa: true));

            var selection = CaretLocator.SelectCaret(
                win32Caret: null,
                uiaSource: ImeCaretSource.TerminalCell,
                uiaCaret: new Rectangle(100, 200, 20, 20));

            Assert.Equal(ImeCaretSource.TerminalCell, selection.Source);
            Assert.Equal<Rectangle?>(new Rectangle(100, 200, 20, 20), selection.Caret);
        }

        // ---------- 诊断契约（失败也有 Reason/PID） ----------

        [Fact]
        public void TryCaptureFocusSnapshot_NoForegroundStillFillsDiagnostic()
        {
            var snapshot = CaretLocator.TryCaptureFocusSnapshot(IntPtr.Zero, out var diagnostic);

            Assert.Null(snapshot);
            Assert.Equal(ImeProbeReason.NoForeground, diagnostic.Reason);
            Assert.Equal(ImeCaretSource.None, diagnostic.Source);
            Assert.Equal(0u, diagnostic.ProcessId);
            Assert.False(diagnostic.Succeeded);
        }

        [Fact]
        public void ImeFocusSnapshot_LegacyFourArgumentConstructionKeepsDefaults()
        {
            var identity = new ImeFocusIdentity(
                new IntPtr(1), new IntPtr(2), 3, 4, "42,100");

            var snapshot = new ImeFocusSnapshot(
                identity, true, new Rectangle(10, 20, 1, 18), new IntPtr(2));

            Assert.Equal(ImeCaretSource.None, snapshot.Source);
            Assert.Null(snapshot.Diagnostic);
        }

        [Fact]
        public void Diagnostic_WithExpressionKeepsIdentityFieldsAndUpdatesReason()
        {
            var diagnostic = ImeProbeDiagnostic.For(
                ImeProbeReason.NoForeground, ImeCaretSource.None, processId: 1234);

            var updated = diagnostic with { Reason = ImeProbeReason.UiaIdentityChanged };

            Assert.Equal(1234u, updated.ProcessId);
            Assert.Equal(ImeProbeReason.UiaIdentityChanged, updated.Reason);
            Assert.False(updated.Succeeded);
        }

        // ---------- 标准 Accessibility.IAccessible 契约 ----------

        [Fact]
        public void Msaa_UsesStandardAccessibilityIAccessiblePropertyContract()
        {
            // 生产实现直接使用 Windows Desktop 自带的 Accessibility.IAccessible。
            // 契约必须来自框架元数据：accRole/accState 是带一个索引参数的属性，
            // 其 getter 的 DISPID 为 -5006/-5007；accLocation 是普通方法（-5015）。
            // 不能用自定义 fake 自己证明自己。
            Type? type = Type.GetType("Accessibility.IAccessible, Accessibility");
            Assert.NotNull(type);
            Assert.Equal(new Guid("618736E0-3C3D-11CF-810C-00AA00389B71"), type!.GUID);

            AssertIndexedPropertyDispId(type, "accRole", -5006);
            AssertIndexedPropertyDispId(type, "accState", -5007);
            AssertDispId(type, "accLocation", -5015);
        }

        private static void AssertIndexedPropertyDispId(Type type, string propertyName, int expected)
        {
            PropertyInfo? property = type.GetProperty(
                propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(property);
            Assert.Single(property!.GetIndexParameters());

            MethodInfo? getter = property.GetMethod;
            Assert.NotNull(getter);

            DispIdAttribute? attribute = getter!
                .GetCustomAttributes(typeof(DispIdAttribute), inherit: false)
                .OfType<DispIdAttribute>()
                .SingleOrDefault();

            Assert.NotNull(attribute);
            Assert.Equal(expected, attribute!.Value);
        }

        private static void AssertDispId(Type type, string methodName, int expected)
        {
            MethodInfo? method = type.GetMethod(methodName);
            Assert.NotNull(method);

            DispIdAttribute? attribute = method!
                .GetCustomAttributes(typeof(DispIdAttribute), inherit: false)
                .OfType<DispIdAttribute>()
                .SingleOrDefault();

            Assert.NotNull(attribute);
            Assert.Equal(expected, attribute!.Value);
        }

        // ---------- 文本模式 Core：Pattern2 正式 ID/IID 与回退 ----------

        [Fact]
        public void TextPattern2_UsesOfficialPatternIdWithMatchingIid()
        {
            // Pattern2 必须以官方 patternId=10024 请求，且请求 IID 与接口声明一致；
            // 用 10014 请求会拿到 TextPattern（无 GetCaretRange）。
            Assert.Equal(10024, UiaCaretReader.UIA_TextPattern2Id);
            Assert.Equal(
                new Guid("506A921A-FCC9-409F-B23B-37EB74106872"),
                UiaCaretReader.IidIUIAutomationTextPattern2);
            Assert.Equal(
                typeof(UiaCaretReader.IUIAutomationTextPattern2).GUID,
                UiaCaretReader.IidIUIAutomationTextPattern2);
        }

        [Theory]
        [InlineData(true, 10024, "506A921A-FCC9-409F-B23B-37EB74106872")]
        [InlineData(false, 10014, "32EBA289-3583-42C9-9C59-3B6D9A1E9B6A")]
        public void RequestTextPattern_PassesOfficialPairToRequest(
            bool extended, int expectedId, string expectedIid)
        {
            var expected = new object();
            int calls = 0;
            object? actual = UiaCaretReader.RequestTextPattern(extended, (id, iid) =>
            {
                calls++;
                Assert.Equal(expectedId, id);
                Assert.Equal(new Guid(expectedIid), iid);
                return expected;
            });
            Assert.Equal(1, calls);
            Assert.Same(expected, actual);
        }

        [Fact]
        public void QueryCore_Pattern2ReadOnlyVetoesContradictorySelection()
        {
            var backend = CreateEditBackend();
            var pattern = new FakeTextPattern
            {
                HasPattern2 = true,
                CaretRangeCallSucceeds = true,
                CaretActive = true,
                CaretRange = new FakeTextRange { ReadOnlyValue = true },
                Selection = new FakeTextRange
                {
                    ReadOnlyValue = false,
                    Bounds = new[] { 100.0, 200.0, 2.0, 20.0 },
                },
            };
            backend.Pattern = pattern;
            backend.MsaaFactory = () => throw new InvalidOperationException("只读时不得查询 MSAA");

            var text = CaretBackendCore.ReadTextGeometryCore(pattern, false);
            Assert.True(text.ReadOnly);
            Assert.Null(text.Caret);

            var reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, true, backend, out _);
            Assert.NotNull(reading);
            Assert.False(reading.Value.IsEditable);
            Assert.Null(reading.Value.Caret);
            Assert.Equal(0, backend.MsaaCalls);
        }

        [Theory]
        [InlineData("failed")]
        [InlineData("malformed")]
        [InlineData("multiple")]
        [InlineData("endpoints")]
        [InlineData("empty")]
        public void QueryCore_DocumentRetainsEditableEvidenceWithoutUiaGeometry(string failure)
        {
            var backend = CreateEditBackend(UiaCaretReader.UIA_DocumentControlTypeId);
            var range = new FakeTextRange
            {
                ReadOnlyValue = false,
                BoundsCallFails = failure == "failed",
                CompareStartToEndFails = failure == "endpoints",
                Bounds = failure switch
                {
                    "malformed" => new[] { 100.0 },
                    "multiple" => new[] { 100.0, 200.0, 2.0, 20.0, 110.0, 200.0, 2.0, 20.0 },
                    _ => Array.Empty<double>(),
                },
            };
            var pattern = new FakeTextPattern { Selection = range };
            backend.Pattern = pattern;
            backend.Msaa = new MsaaCaretReading(true, new Rectangle(50, 60, 1, 18),
                ImeProbeReason.None, 0);

            var text = CaretBackendCore.ReadTextGeometryCore(pattern, false);
            Assert.False(text.ReadOnly);
            Assert.Null(text.Caret);

            var reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, true, backend, out _);
            Assert.NotNull(reading);
            Assert.True(reading.Value.IsEditable);
            Assert.Equal(ImeCaretSource.Msaa, reading.Value.CaretSource);
            Assert.Equal(1, backend.MsaaCalls);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(1)]
        public void ReadTextGeometryCore_RejectsNonDegenerateSelectionEvenWithNarrowRect(int span)
        {
            var pattern = new FakeTextPattern
            {
                Selection = new FakeTextRange
                {
                    ReadOnlyValue = false,
                    StartToEnd = span,
                    Bounds = new[] { 100.0, 200.0, 1.0, 18.0 },
                },
            };

            var reading = CaretBackendCore.ReadTextGeometryCore(pattern, false);

            Assert.False(reading.ReadOnly);
            Assert.Null(reading.Caret);
            Assert.Equal(ImeProbeReason.CaretRejected, reading.Reason);
        }

        [Fact]
        public void ReadTextGeometryCore_Pattern2FailureFallsBackToTextPatternSelection()
        {
            var selection = new FakeTextRange
            {
                StartToEnd = 0,
                Bounds = new[] { 10.0, 20.0, 1.0, 18.0 },
            };
            var source = new FakeTextPattern
            {
                HasPattern2 = true,
                CaretRangeCallSucceeds = false, // 模拟 GetCaretRange 失败
                HasPattern = true,
                Selection = selection,
            };

            TextCaretReading text = CaretBackendCore.ReadTextGeometryCore(source, isPassword: false);

            Assert.True(text.PatternAvailable);
            Assert.True(text.Pattern2Available);
            Assert.Equal(ImeCaretSource.Uia, text.Source);
            Assert.Equal<Rectangle?>(new Rectangle(10, 20, 1, 18), text.Caret);
            Assert.Equal(ImeProbeReason.None, text.Reason);
        }

        [Fact]
        public void ReadTextGeometryCore_ActivePattern2WithEmptyRectKeepsCaretActive()
        {
            // Pattern2 报告 active 但矩形为空：CaretActive 仍证明可编辑，
            // 回退 TextPattern 也拿不到矩形时交给上层尝试 MSAA。
            var pattern2Range = new FakeTextRange
            {
                StartToEnd = 0,
                Bounds = Array.Empty<double>(),
            };
            var selection = new FakeTextRange
            {
                StartToEnd = 0,
                Bounds = Array.Empty<double>(),
            };
            var source = new FakeTextPattern
            {
                HasPattern2 = true,
                CaretRangeCallSucceeds = true,
                CaretActive = true,
                CaretRange = pattern2Range,
                HasPattern = true,
                Selection = selection,
            };

            TextCaretReading text = CaretBackendCore.ReadTextGeometryCore(source, isPassword: false);

            Assert.Null(text.Caret);
            Assert.True(text.CaretActive);
            Assert.True(text.Pattern2Available);
            Assert.Equal(ImeProbeReason.CaretNotReady, text.Reason);
        }

        // ---------- Terminal Core：严格 selection/expand 流程 ----------

        [Fact]
        public void ReadTerminalGeometryCore_AcceptsExpandedCharacterCell()
        {
            (FakeTextPattern pattern, _, _) = CreateTerminalPattern();

            TextCaretReading text = CaretBackendCore.ReadTerminalGeometryCore(
                pattern, new Rectangle(0, 0, 800, 600));

            Assert.Equal(ImeCaretSource.TerminalCell, text.Source);
            Assert.Equal<Rectangle?>(new Rectangle(100, 200, 20, 20), text.Caret);
            Assert.Equal<bool?>(false, text.ReadOnly);
            Assert.Equal(ImeProbeReason.None, text.Reason);
        }

        [Fact]
        public void ReadTerminalGeometryCore_RejectsNonDegenerateSelection()
        {
            (FakeTextPattern pattern, FakeTextRange original, _) = CreateTerminalPattern();
            original.StartToEnd = 1;

            TextCaretReading text = CaretBackendCore.ReadTerminalGeometryCore(
                pattern, new Rectangle(0, 0, 800, 600));

            Assert.Null(text.Caret);
            Assert.Equal(ImeProbeReason.TerminalRejected, text.Reason);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(true)]
        public void ReadTerminalGeometryCore_RejectsUnknownOrReadOnly(bool? readOnly)
        {
            (FakeTextPattern pattern, FakeTextRange original, _) = CreateTerminalPattern();
            original.ReadOnlyValue = readOnly;

            TextCaretReading text = CaretBackendCore.ReadTerminalGeometryCore(
                pattern, new Rectangle(0, 0, 800, 600));

            Assert.Null(text.Caret);
            Assert.Equal(ImeProbeReason.TerminalRejected, text.Reason);
        }

        [Fact]
        public void ReadTerminalGeometryCore_RejectsMultipleRanges()
        {
            (FakeTextPattern pattern, _, _) = CreateTerminalPattern();
            pattern.SelectionCount = 2;

            TextCaretReading text = CaretBackendCore.ReadTerminalGeometryCore(
                pattern, new Rectangle(0, 0, 800, 600));

            Assert.Null(text.Caret);
            Assert.Equal(ImeProbeReason.TerminalRejected, text.Reason);
        }

        [Fact]
        public void ReadTerminalGeometryCore_RejectsCloneOrExpandFailure()
        {
            (FakeTextPattern cloneFailPattern, FakeTextRange cloneFailOriginal, _) =
                CreateTerminalPattern();
            cloneFailOriginal.CloneFails = true;

            TextCaretReading cloneFail = CaretBackendCore.ReadTerminalGeometryCore(
                cloneFailPattern, new Rectangle(0, 0, 800, 600));
            Assert.Null(cloneFail.Caret);
            Assert.Equal(ImeProbeReason.TerminalRejected, cloneFail.Reason);

            (FakeTextPattern expandFailPattern, _, FakeTextRange expandFailExpanded) =
                CreateTerminalPattern();
            expandFailExpanded.ExpandFails = true;

            TextCaretReading expandFail = CaretBackendCore.ReadTerminalGeometryCore(
                expandFailPattern, new Rectangle(0, 0, 800, 600));
            Assert.Null(expandFail.Caret);
            Assert.Equal(ImeProbeReason.TerminalRejected, expandFail.Reason);
        }

        [Fact]
        public void ReadTerminalGeometryCore_RejectsEndMatchGuess()
        {
            // 展开范围的起点与原 caret 起点不一致（若实现按 end 比较则可能被误接受），
            // 必须拒绝：文档末端向前夹取会变成“前一个字符”。
            (FakeTextPattern pattern, _, FakeTextRange expanded) = CreateTerminalPattern();
            expanded.StartToStart = -1;

            TextCaretReading text = CaretBackendCore.ReadTerminalGeometryCore(
                pattern, new Rectangle(0, 0, 800, 600));

            Assert.Null(text.Caret);
            Assert.Equal(ImeProbeReason.TerminalRejected, text.Reason);
        }

        [Fact]
        public void ReadTerminalGeometryCore_RejectsMultipleRectangles()
        {
            (FakeTextPattern pattern, _, FakeTextRange expanded) = CreateTerminalPattern();
            expanded.Bounds = new double[]
            {
                100.0, 200.0, 20.0, 20.0,
                120.0, 200.0, 20.0, 20.0,
            };

            TextCaretReading text = CaretBackendCore.ReadTerminalGeometryCore(
                pattern, new Rectangle(0, 0, 800, 600));

            Assert.Null(text.Caret);
            Assert.Equal(ImeProbeReason.CaretRejected, text.Reason);
        }

        // ---------- QueryCore：生产/测试共享的编排 ----------

        [Fact]
        public void QueryCore_EditWithRuntimeIdAndNoCaretUsesMsaa()
        {
            var caret = new Rectangle(50, 60, 1, 18);
            var backend = CreateEditBackend();
            backend.Pattern = new FakeTextPattern { HasPattern = false, HasPattern2 = false };
            backend.Msaa = new MsaaCaretReading(true, caret, ImeProbeReason.None, 0);

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.NotNull(reading);
            Assert.Equal(ImeCaretSource.Msaa, reading!.Value.CaretSource);
            Assert.Equal<Rectangle?>(caret, reading.Value.Caret);
            Assert.Equal(1, backend.MsaaCalls);
            Assert.Equal(ImeProbeReason.None, diagnostic.Reason);
            Assert.Equal(ImeCaretSource.Msaa, diagnostic.Source);
        }

        [Fact]
        public void QueryCore_ActivePattern2EmptyRectFallsThroughToMsaa()
        {
            var caret = new Rectangle(70, 80, 1, 18);
            var backend = CreateEditBackend(UiaCaretReader.UIA_DocumentControlTypeId);
            backend.Pattern = CreateEmptyPattern2();
            backend.Msaa = new MsaaCaretReading(true, caret, ImeProbeReason.None, 0);

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out _);

            Assert.NotNull(reading);
            Assert.True(reading!.Value.IsEditable); // active caret 仍证明可编辑
            Assert.Equal(ImeCaretSource.Msaa, reading.Value.CaretSource);
            Assert.Equal<Rectangle?>(caret, reading.Value.Caret);
        }

        [Fact]
        public void QueryCore_ValidUiaCaretIsNeverOverwrittenByMsaa()
        {
            var backend = CreateEditBackend();
            backend.Pattern = CreatePattern2WithCaret(new Rectangle(100, 200, 2, 20));
            backend.MsaaFactory = () => throw new InvalidOperationException(
                "已有有效 UIA 坐标时不得采样 MSAA");

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out _);

            Assert.NotNull(reading);
            Assert.Equal(ImeCaretSource.Uia, reading!.Value.CaretSource);
            Assert.Equal<Rectangle?>(new Rectangle(100, 200, 2, 20), reading.Value.Caret);
            Assert.Equal(0, backend.MsaaCalls);
        }

        [Fact]
        public void QueryCore_MsaaFailureYieldsNoCoordinateAndKeepsReason()
        {
            var backend = CreateEditBackend();
            backend.Pattern = new FakeTextPattern { HasPattern = false, HasPattern2 = false };
            backend.Msaa = new MsaaCaretReading(
                false, default, ImeProbeReason.MsaaRejected, unchecked((int)0x80004005));

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.NotNull(reading);
            Assert.True(reading!.Value.IsEditable);
            Assert.Null(reading.Value.Caret);
            Assert.Equal(ImeCaretSource.None, reading.Value.CaretSource);
            Assert.Equal(ImeProbeReason.MsaaRejected, reading.Value.Reason);
            Assert.Equal(ImeProbeReason.MsaaRejected, diagnostic.Reason);
            Assert.Equal(ImeCaretSource.None, diagnostic.Source);
            Assert.False(diagnostic.CaretRectUsable);
        }

        [Fact]
        public void QueryCore_MsaaExceptionNeverFabricatesCoordinate()
        {
            var backend = CreateEditBackend();
            backend.Pattern = new FakeTextPattern { HasPattern = false, HasPattern2 = false };
            var failure = new InvalidOperationException("MSAA boom");
            backend.MsaaFactory = () => throw failure;

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.Null(reading);
            Assert.Equal(ImeProbeReason.ProbeException, diagnostic.Reason);
            Assert.Equal(failure.HResult, diagnostic.HResult);
            Assert.Equal(ImeCaretSource.None, diagnostic.Source);
            Assert.False(diagnostic.CaretRectUsable);
        }

        [Theory]
        [InlineData(true, true, true)]
        [InlineData(false, false, true)]
        [InlineData(false, true, false)]
        public void QueryCore_RejectsMsaaWithoutEditableIdentityEvidence(
            bool readOnly, bool enabled, bool hasRuntimeId)
        {
            var backend = CreateEditBackend();
            backend.ValueReadOnly = readOnly;
            backend.Element = backend.Element with
            {
                IsEnabled = enabled,
                RuntimeId = hasRuntimeId ? "42,100" : null,
            };
            backend.IdentityRuntimeId = backend.Element.RuntimeId;
            backend.Pattern = new FakeTextPattern { HasPattern = false };
            backend.MsaaFactory = () => throw new InvalidOperationException("不应查询 MSAA");

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out _);

            Assert.Equal(0, backend.MsaaCalls);
            Assert.False(reading?.Caret.HasValue ?? false);
        }

        [Fact]
        public void QueryCore_RechecksRuntimeIdAfterMsaaSampling()
        {
            var backend = CreateEditBackend();
            backend.Pattern = new FakeTextPattern { HasPattern = false };
            backend.MsaaFactory = () =>
            {
                // 查询途中同 HWND 换框：即使 CompareElements 仍声称相同，也须核对 RuntimeId。
                backend.IdentityRuntimeId = "42,200";
                return new MsaaCaretReading(true, new Rectangle(50, 60, 1, 18),
                    ImeProbeReason.None, 0);
            };

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.Equal(1, backend.MsaaCalls);
            Assert.Null(reading);
            Assert.Equal(ImeProbeReason.UiaIdentityChanged, diagnostic.Reason);
        }

        [Fact]
        public void QueryCore_AllowMsaaFalseDoesNotSample()
        {
            var backend = CreateEditBackend();
            backend.Pattern = CreateEmptyPattern2();

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: false, backend, out _);

            Assert.NotNull(reading);
            Assert.Null(reading!.Value.Caret);
            Assert.Equal(0, backend.MsaaCalls);
            Assert.Equal(ImeProbeReason.CaretNotReady, reading.Value.Reason);
        }

        [Fact]
        public void QueryCore_SameHwndFocusChangedToAnotherElementIsDiscarded()
        {
            var backend = CreateEditBackend();
            backend.Pattern = new FakeTextPattern { HasPattern = false, HasPattern2 = false };
            backend.Msaa = new MsaaCaretReading(true, new Rectangle(50, 60, 1, 18),
                ImeProbeReason.None, 0);
            backend.CompareHr = 0;
            backend.CompareSame = 0; // 同一 HWND 内已换成另一个焦点元素

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.Null(reading);
            Assert.Equal(ImeProbeReason.UiaIdentityChanged, diagnostic.Reason);
            Assert.Equal(ImeCaretSource.None, diagnostic.Source);
        }

        [Fact]
        public void QueryCore_IdentityReadFailureIsUiaUnavailableAndKeepsSampledDiagnostic()
        {
            var backend = CreateEditBackend();
            backend.ValueReadOnly = false;
            backend.Pattern = new FakeTextPattern { HasPattern = false, HasPattern2 = false };
            backend.Msaa = new MsaaCaretReading(true, new Rectangle(50, 60, 1, 18),
                ImeProbeReason.None, 0);
            backend.IdentityReadFails = true;
            backend.IdentityReadHResult = unchecked((int)0x80004005);

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.Null(reading);
            // 读取失败是探测不可用，不能伪装成「读到了另一个元素」。
            Assert.Equal(ImeProbeReason.UiaUnavailable, diagnostic.Reason);
            Assert.Equal(unchecked((int)0x80004005), diagnostic.HResult);
            // 失败前已采样的模式/焦点信息必须保留。
            Assert.True(diagnostic.ValuePatternProbed);
            Assert.Equal<bool?>(false, diagnostic.ValueReadOnly);
            Assert.Equal<bool?>(true, diagnostic.IsEditable);
            Assert.Equal<bool?>(true, diagnostic.HasKeyboardFocus);
            Assert.Equal<bool?>(true, diagnostic.IsEnabled);
        }

        [Fact]
        public void QueryCore_TerminalCellFlowsThroughStrictProviderAndRangeChecks()
        {
            var backend = CreateTerminalBackend(processName: UiaCaretReader.TerminalProcessName);
            (FakeTextPattern pattern, _, _) = CreateTerminalPattern();
            backend.Pattern = pattern;

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.NotNull(reading);
            Assert.True(reading!.Value.IsEditable);
            Assert.True(reading.Value.TerminalProvider);
            Assert.Equal(ImeCaretSource.TerminalCell, reading.Value.CaretSource);
            Assert.Equal<Rectangle?>(new Rectangle(100, 200, 20, 20), reading.Value.Caret);
            Assert.Equal(0, backend.MsaaCalls); // Terminal 绝不走 MSAA
            Assert.Equal(ImeCaretSource.TerminalCell, diagnostic.Source);
        }

        [Fact]
        public void QueryCore_TerminalRequiresWindowsTerminalProcess()
        {
            // 同样 Text+TermControl，但进程不是 WindowsTerminal：按普通 Text 拒绝，
            // 不读文本范围，也绝不走 MSAA。
            var backend = CreateTerminalBackend(processName: null);
            (FakeTextPattern pattern, _, _) = CreateTerminalPattern();
            backend.Pattern = pattern;
            backend.Msaa = new MsaaCaretReading(true, new Rectangle(50, 60, 1, 18),
                ImeProbeReason.None, 0);

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.NotNull(reading);
            Assert.False(reading!.Value.IsEditable);
            Assert.Null(reading.Value.Caret);
            Assert.Equal(0, backend.MsaaCalls);
            Assert.Equal(ImeProbeReason.NotEditable, diagnostic.Reason);
        }

        // ---------- ComboBox / Custom 候选类型 ----------

        [Fact]
        public void QueryCore_ComboBoxValueWritableUsesStrictIdentityAndMsaa()
        {
            var caret = new Rectangle(50, 60, 1, 18);
            var backend = CreateEditBackend(UiaCaretReader.UIA_ComboBoxControlTypeId);
            backend.ValueReadOnly = false;
            backend.Pattern = new FakeTextPattern { HasPattern = false, HasPattern2 = false };
            backend.Msaa = new MsaaCaretReading(true, caret, ImeProbeReason.None, 0);

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.NotNull(reading);
            Assert.True(reading!.Value.IsEditable);
            Assert.Equal(ImeCaretSource.Msaa, reading.Value.CaretSource);
            Assert.Equal<Rectangle?>(caret, reading.Value.Caret);
            Assert.Equal(1, backend.ValuePatternReadCalls);
            Assert.Equal(1, backend.TextPatternRequestCalls);
            Assert.Equal(1, backend.MsaaCalls);

            // 诊断必须区分「查询后未知」与「未查询」。
            Assert.True(diagnostic.ValuePatternProbed);
            Assert.True(diagnostic.TextPatternProbed);
            Assert.Equal<bool?>(false, diagnostic.ValueReadOnly);
            Assert.Null(diagnostic.TextReadOnly);
            Assert.Equal<bool?>(true, diagnostic.IsEditable);
            Assert.Equal<bool?>(true, diagnostic.HasKeyboardFocus);
            Assert.Equal<bool?>(true, diagnostic.IsEnabled);
        }

        [Fact]
        public void QueryCore_ComboBoxValueWritableAcceptsTrustedUiaCaret()
        {
            var backend = CreateEditBackend(UiaCaretReader.UIA_ComboBoxControlTypeId);
            backend.ValueReadOnly = false;
            backend.Pattern = CreatePattern2WithCaret(new Rectangle(100, 200, 2, 20));
            backend.MsaaFactory = () => throw new InvalidOperationException(
                "已有可信 UIA 矩形时不得采样 MSAA");

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.NotNull(reading);
            Assert.True(reading!.Value.IsEditable);
            Assert.Equal(ImeCaretSource.Uia, reading.Value.CaretSource);
            Assert.Equal<Rectangle?>(new Rectangle(100, 200, 2, 20), reading.Value.Caret);
            Assert.Equal(0, backend.MsaaCalls);
            Assert.Equal<bool?>(false, diagnostic.ValueReadOnly);
        }

        [Fact]
        public void QueryCore_ComboBoxValueReadOnlyShortCircuitsTextPattern()
        {
            var backend = CreateEditBackend(UiaCaretReader.UIA_ComboBoxControlTypeId);
            backend.ValueReadOnly = true;
            // provider 支持文本模式，但 Value=true 时不得继续读取。
            backend.Pattern = new FakeTextPattern
            {
                HasPattern = true,
                Selection = new FakeTextRange { ReadOnlyValue = false },
            };
            backend.MsaaFactory = () => throw new InvalidOperationException("只读 ComboBox 不得采样 MSAA");

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.NotNull(reading);
            Assert.False(reading!.Value.IsEditable);
            Assert.Null(reading.Value.Caret);
            Assert.Equal(0, backend.TextPatternRequestCalls);
            Assert.Equal(0, backend.MsaaCalls);
            Assert.Equal(ImeProbeReason.NotEditable, diagnostic.Reason);
            Assert.True(diagnostic.ValuePatternProbed);
            Assert.False(diagnostic.TextPatternProbed);
            Assert.Equal<bool?>(true, diagnostic.ValueReadOnly);
            Assert.Null(diagnostic.TextReadOnly);
            Assert.Equal<bool?>(false, diagnostic.IsEditable);
        }

        [Fact]
        public void QueryCore_ComboBoxTextWritableAloneIsNotEditable()
        {
            var backend = CreateEditBackend(UiaCaretReader.UIA_ComboBoxControlTypeId);
            backend.ValueReadOnly = null; // 明确未知，不依赖 fake 默认值
            backend.Pattern = new FakeTextPattern
            {
                HasPattern2 = false,
                HasPattern = true,
                Selection = new FakeTextRange
                {
                    ReadOnlyValue = false,
                    StartToEnd = 0,
                    Bounds = new[] { 100.0, 200.0, 2.0, 18.0 },
                },
            };
            backend.MsaaFactory = () => throw new InvalidOperationException("未证明可编辑不得采样 MSAA");

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.NotNull(reading);
            Assert.False(reading!.Value.IsEditable);
            Assert.Null(reading.Value.Caret);
            Assert.Equal(0, backend.MsaaCalls);
            Assert.Equal(ImeProbeReason.EditableUnknown, diagnostic.Reason);
            Assert.Equal<bool?>(false, diagnostic.TextReadOnly);
            Assert.Equal<bool?>(false, diagnostic.IsEditable);
        }

        [Fact]
        public void QueryCore_ComboBoxWritableValueIsVetoedByReadOnlyText()
        {
            var backend = CreateEditBackend(UiaCaretReader.UIA_ComboBoxControlTypeId);
            backend.ValueReadOnly = false;
            backend.Pattern = new FakeTextPattern
            {
                HasPattern2 = false,
                HasPattern = true,
                Selection = new FakeTextRange
                {
                    ReadOnlyValue = true,
                    StartToEnd = 0,
                    Bounds = new[] { 100.0, 200.0, 2.0, 18.0 },
                },
            };
            backend.MsaaFactory = () => throw new InvalidOperationException("只读时不得采样 MSAA");

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            // 统一否决优先级：Value=false 不能覆盖已采样的 Text=true。
            Assert.NotNull(reading);
            Assert.False(reading!.Value.IsEditable);
            Assert.Null(reading.Value.Caret);
            Assert.Equal(0, backend.MsaaCalls);
            Assert.Equal(ImeProbeReason.NotEditable, diagnostic.Reason);
            Assert.Equal<bool?>(false, diagnostic.ValueReadOnly);
            Assert.Equal<bool?>(true, diagnostic.TextReadOnly);
        }

        [Theory]
        [InlineData(50003, false, true, true, false, "42,100", 5)]  // ComboBox 仅 focusable → EditableUnknown
        [InlineData(50003, true, false, false, false, "42,100", 4)] // 禁用 → NotEditable
        [InlineData(50003, true, false, true, true, "42,100", 4)]   // 密码 → NotEditable
        [InlineData(50003, true, false, true, false, null, 6)]      // 无 RuntimeId → IdentityMissing
        [InlineData(50003, true, false, true, false, "   ", 6)]     // 空白 RuntimeId 不算身份 → IdentityMissing
        [InlineData(50026, false, true, true, false, "42,100", 5)]  // Custom 仅 focusable → EditableUnknown
        [InlineData(50026, true, false, false, false, "42,100", 4)] // 禁用 → NotEditable
        [InlineData(50026, true, false, true, true, "42,100", 4)]   // 密码 → NotEditable
        [InlineData(50026, true, false, true, false, null, 6)]      // 无 RuntimeId → IdentityMissing
        [InlineData(50026, true, false, true, false, "   ", 6)]     // 空白 RuntimeId → IdentityMissing
        [InlineData(50026, true, false, false, false, null, 4)]     // 禁用优先于身份缺失 → NotEditable
        public void QueryCore_NewCandidatePreconditionFailureSkipsModeReads(
            int controlType, bool hasKeyboardFocus, bool focusable, bool enabled, bool isPassword,
            string? runtimeId, int expectedReason)
        {
            var backend = CreateEditBackend(controlType);
            backend.Element = backend.Element with
            {
                HasKeyboardFocus = hasKeyboardFocus,
                IsKeyboardFocusable = focusable,
                IsEnabled = enabled,
                IsPassword = isPassword,
                RuntimeId = runtimeId,
            };
            backend.IdentityRuntimeId = runtimeId;

            // 即使 ValuePattern 报告可写，前置失败也不得读取模式。
            backend.ValueReadOnly = false;
            backend.Pattern = new FakeTextPattern
            {
                HasPattern = true,
                Selection = new FakeTextRange { ReadOnlyValue = false },
            };
            backend.MsaaFactory = () => throw new InvalidOperationException("前置失败不得采样 MSAA");

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.NotNull(reading);
            Assert.False(reading!.Value.IsEditable);
            Assert.Null(reading.Value.Caret);
            Assert.Equal(0, backend.ValuePatternReadCalls);
            Assert.Equal(0, backend.TextPatternRequestCalls);
            Assert.Equal(0, backend.MsaaCalls);

            // 用 int 比较，避免公开测试方法签名暴露 internal 枚举。
            Assert.Equal(expectedReason, (int)diagnostic.Reason);
            Assert.False(diagnostic.ValuePatternProbed);
            Assert.False(diagnostic.TextPatternProbed);
            Assert.Null(diagnostic.ValueReadOnly);
            Assert.Null(diagnostic.TextReadOnly);
            Assert.Equal<bool?>(false, diagnostic.IsEditable);
            Assert.Equal<bool?>(hasKeyboardFocus, diagnostic.HasKeyboardFocus);
            Assert.Equal<bool?>(enabled, diagnostic.IsEnabled);
        }

        [Fact]
        public void QueryCore_CustomValueWritableIsEditable()
        {
            var caret = new Rectangle(60, 70, 1, 18);
            var backend = CreateEditBackend(UiaCaretReader.UIA_CustomControlTypeId);
            backend.ValueReadOnly = false;
            backend.Pattern = new FakeTextPattern { HasPattern = false, HasPattern2 = false };
            backend.Msaa = new MsaaCaretReading(true, caret, ImeProbeReason.None, 0);

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.NotNull(reading);
            Assert.True(reading!.Value.IsEditable);
            Assert.Equal(ImeCaretSource.Msaa, reading.Value.CaretSource);
            Assert.Equal<bool?>(false, diagnostic.ValueReadOnly);
            Assert.Null(diagnostic.TextReadOnly);
        }

        [Fact]
        public void QueryCore_CustomTextWritableWithPatternIsEditable()
        {
            var backend = CreateEditBackend(UiaCaretReader.UIA_CustomControlTypeId);
            backend.ValueReadOnly = null;
            backend.Pattern = new FakeTextPattern
            {
                HasPattern2 = false,
                HasPattern = true,
                Selection = new FakeTextRange
                {
                    ReadOnlyValue = false,
                    StartToEnd = 0,
                    Bounds = Array.Empty<double>(),
                },
            };
            backend.Msaa = new MsaaCaretReading(
                true, new Rectangle(10, 20, 1, 18), ImeProbeReason.None, 0);

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.NotNull(reading);
            Assert.True(reading!.Value.IsEditable);
            Assert.True(diagnostic.TextPatternProbed);
            Assert.Equal<bool?>(false, diagnostic.TextReadOnly);
            Assert.Null(diagnostic.ValueReadOnly);
        }

        [Fact]
        public void QueryCore_CustomTextWritableWithoutPatternIsEditableUnknown()
        {
            var backend = CreateEditBackend(UiaCaretReader.UIA_CustomControlTypeId);
            backend.ValueReadOnly = null;
            // 文本范围伪报 false 但模式不可用：不能当作正向证据。
            backend.Pattern = new FakeTextPattern { HasPattern = false, HasPattern2 = false };
            backend.MsaaFactory = () => throw new InvalidOperationException("未证明可编辑不得采样 MSAA");

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.NotNull(reading);
            Assert.False(reading!.Value.IsEditable);
            Assert.Equal(ImeProbeReason.EditableUnknown, diagnostic.Reason);
            Assert.Equal(0, backend.MsaaCalls);
        }

        [Fact]
        public void QueryCore_CustomActiveCaretRectangleAloneIsNotEditable()
        {
            var backend = CreateEditBackend(UiaCaretReader.UIA_CustomControlTypeId);
            backend.ValueReadOnly = null;
            // active caret + 合法矩形 + 只读属性未知：Custom 仍然缺正向证据。
            backend.Pattern = new FakeTextPattern
            {
                HasPattern2 = true,
                HasPattern = true,
                CaretRangeCallSucceeds = true,
                CaretActive = true,
                CaretRange = new FakeTextRange
                {
                    StartToEnd = 0,
                    ReadOnlyValue = null,
                    Bounds = new[] { 100.0, 200.0, 2.0, 18.0 },
                },
                Selection = new FakeTextRange
                {
                    StartToEnd = 0,
                    ReadOnlyValue = null,
                    Bounds = Array.Empty<double>(),
                },
            };
            backend.MsaaFactory = () => throw new InvalidOperationException("未证明可编辑不得采样 MSAA");

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.NotNull(reading);
            Assert.False(reading!.Value.IsEditable);
            Assert.Null(reading.Value.Caret);
            Assert.Equal(ImeProbeReason.EditableUnknown, diagnostic.Reason);
            Assert.Equal(0, backend.MsaaCalls);
        }

        [Theory]
        [InlineData(true, null)]   // Value=true：短路文本查询后仍否决
        [InlineData(false, true)]  // Value=false 但已采样的 Text=true 覆盖
        public void QueryCore_CustomAnySampledReadOnlyTrueVetoes(bool? valueReadOnly, bool? textReadOnly)
        {
            var backend = CreateEditBackend(UiaCaretReader.UIA_CustomControlTypeId);
            backend.ValueReadOnly = valueReadOnly;
            backend.Pattern = new FakeTextPattern
            {
                HasPattern2 = false,
                HasPattern = true,
                Selection = new FakeTextRange
                {
                    ReadOnlyValue = textReadOnly,
                    StartToEnd = 0,
                    Bounds = new[] { 100.0, 200.0, 2.0, 18.0 },
                },
            };
            backend.MsaaFactory = () => throw new InvalidOperationException("只读时不得采样 MSAA");

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.NotNull(reading);
            Assert.False(reading!.Value.IsEditable);
            Assert.Null(reading.Value.Caret);
            Assert.Equal(0, backend.MsaaCalls);
            Assert.Equal(ImeProbeReason.NotEditable, diagnostic.Reason);
        }

        // ---------- Document：ValuePattern=false 独立正向证据 ----------

        [Fact]
        public void QueryCore_DocumentValueWritableWithoutTextPatternUsesMsaa()
        {
            var caret = new Rectangle(80, 90, 1, 18);
            var backend = CreateEditBackend(UiaCaretReader.UIA_DocumentControlTypeId);
            backend.ValueReadOnly = false;
            backend.Pattern = new FakeTextPattern { HasPattern = false, HasPattern2 = false };
            backend.Msaa = new MsaaCaretReading(true, caret, ImeProbeReason.None, 0);

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.NotNull(reading);
            Assert.True(reading!.Value.IsEditable);
            Assert.Equal(ImeCaretSource.Msaa, reading.Value.CaretSource);
            Assert.Equal<Rectangle?>(caret, reading.Value.Caret);
            Assert.Equal(1, backend.MsaaCalls);
            Assert.True(diagnostic.ValuePatternProbed);
            Assert.False(diagnostic.HasTextPattern);
            Assert.Equal<bool?>(false, diagnostic.ValueReadOnly);
            Assert.Null(diagnostic.TextReadOnly);
        }

        [Fact]
        public void QueryCore_DocumentUnknownEvidenceIsEditableUnknown()
        {
            var backend = CreateEditBackend(UiaCaretReader.UIA_DocumentControlTypeId);
            backend.ValueReadOnly = null;
            backend.Pattern = new FakeTextPattern { HasPattern = false, HasPattern2 = false };
            backend.MsaaFactory = () => throw new InvalidOperationException("未证明可编辑不得采样 MSAA");

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.NotNull(reading);
            Assert.False(reading!.Value.IsEditable);
            Assert.Equal(ImeProbeReason.EditableUnknown, diagnostic.Reason);
            Assert.Equal(0, backend.MsaaCalls);
            Assert.True(diagnostic.ValuePatternProbed);
            Assert.True(diagnostic.TextPatternProbed);
            Assert.Null(diagnostic.ValueReadOnly);
            Assert.Null(diagnostic.TextReadOnly);
            Assert.Equal<bool?>(false, diagnostic.IsEditable);
        }

        [Theory]
        [InlineData(true, null)]    // Value=true（文本未查询）
        [InlineData(false, true)]   // Value=false + Text=true
        [InlineData(null, true)]    // 两者采集中 Text=true
        public void QueryCore_DocumentAnySampledReadOnlyTrueVetoes(bool? valueReadOnly, bool? textReadOnly)
        {
            var backend = CreateEditBackend(UiaCaretReader.UIA_DocumentControlTypeId);
            backend.ValueReadOnly = valueReadOnly;
            backend.Pattern = new FakeTextPattern
            {
                HasPattern2 = false,
                HasPattern = true,
                Selection = new FakeTextRange
                {
                    ReadOnlyValue = textReadOnly,
                    StartToEnd = 0,
                    Bounds = new[] { 100.0, 200.0, 2.0, 18.0 },
                },
            };
            backend.MsaaFactory = () => throw new InvalidOperationException("只读时不得采样 MSAA");

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.NotNull(reading);
            Assert.False(reading!.Value.IsEditable);
            Assert.Null(reading.Value.Caret);
            Assert.Equal(ImeProbeReason.NotEditable, diagnostic.Reason);
            Assert.Equal(0, backend.MsaaCalls);
        }

        // ---------- 其它类型与诊断保留 ----------

        [Fact]
        public void QueryCore_UnsupportedButtonSkipsAllModeReads()
        {
            var backend = CreateEditBackend(controlType: 50000); // Button
            backend.ValueReadOnly = false;

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.NotNull(reading);
            Assert.False(reading!.Value.IsEditable);
            Assert.Equal(ImeProbeReason.NotEditable, diagnostic.Reason);
            Assert.Equal(0, backend.ValuePatternReadCalls);
            Assert.Equal(0, backend.TextPatternRequestCalls);
            Assert.Equal(0, backend.MsaaCalls);
            Assert.False(diagnostic.ValuePatternProbed);
            Assert.False(diagnostic.TextPatternProbed);
        }

        [Fact]
        public void QueryCore_EditableCandidateWithoutTrustedCaretKeepsPositiveEvidence()
        {
            var backend = CreateEditBackend(UiaCaretReader.UIA_CustomControlTypeId);
            backend.ValueReadOnly = false;
            backend.Pattern = new FakeTextPattern { HasPattern = false, HasPattern2 = false };
            backend.Msaa = new MsaaCaretReading(
                false, default, ImeProbeReason.MsaaRejected, unchecked((int)0x80004005));

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            // 已证明可编辑但没有可信 caret：仍不得伪造坐标。
            Assert.NotNull(reading);
            Assert.True(reading!.Value.IsEditable);
            Assert.Null(reading.Value.Caret);
            Assert.Equal(ImeCaretSource.None, reading.Value.CaretSource);
            Assert.Equal(ImeProbeReason.MsaaRejected, reading.Value.Reason);
            Assert.Equal(1, backend.MsaaCalls);
            Assert.Equal<bool?>(true, diagnostic.IsEditable);
        }

        [Fact]
        public void QueryCore_EarlyIdentityFailureKeepsSampledDiagnostic()
        {
            var backend = CreateEditBackend(UiaCaretReader.UIA_ComboBoxControlTypeId);
            backend.ValueReadOnly = false;
            backend.Pattern = new FakeTextPattern { HasPattern = false, HasPattern2 = false };
            backend.Msaa = new MsaaCaretReading(
                true, new Rectangle(50, 60, 1, 18), ImeProbeReason.None, 0);
            backend.IdentityReadFails = true; // 采样后身份核对失败
            backend.IdentityReadHResult = unchecked((int)0x80070005);

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.Null(reading);
            // 身份读取失败 = UiaUnavailable（不是「读到了另一个元素」）。
            Assert.Equal(ImeProbeReason.UiaUnavailable, diagnostic.Reason);
            Assert.Equal(unchecked((int)0x80070005), diagnostic.HResult);
            // 已采样的能力/焦点信息不能被提前失败抹掉。
            Assert.Equal<bool?>(true, diagnostic.IsEditable);
            Assert.True(diagnostic.ValuePatternProbed);
            Assert.Equal<bool?>(false, diagnostic.ValueReadOnly);
            Assert.Equal<bool?>(true, diagnostic.HasKeyboardFocus);
            Assert.Equal<bool?>(true, diagnostic.IsEnabled);
            Assert.Equal(UiaCaretReader.UIA_ComboBoxControlTypeId, diagnostic.ControlType);
        }

        [Fact]
        public void QueryCore_ElementReadFailureLeavesEditableUnknown()
        {
            var backend = CreateEditBackend();
            backend.ElementReadFails = true;

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.Null(reading);
            Assert.Equal(ImeProbeReason.UiaUnavailable, diagnostic.Reason);
            // 没有判定过就不能默认 false。
            Assert.Null(diagnostic.IsEditable);
            Assert.False(diagnostic.ValuePatternProbed);
            Assert.False(diagnostic.TextPatternProbed);
            Assert.Null(diagnostic.HasKeyboardFocus);
            Assert.Null(diagnostic.IsEnabled);
        }

        [Fact]
        public void QueryCore_IdentityReadFailureOverridesEditableUnknown()
        {
            var backend = CreateEditBackend(UiaCaretReader.UIA_CustomControlTypeId);
            backend.ValueReadOnly = null;
            backend.Pattern = new FakeTextPattern { HasPattern = false, HasPattern2 = false };
            backend.IdentityReadFails = true;
            backend.IdentityReadHResult = unchecked((int)0x80010108);

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.Null(reading);
            // 身份读取失败优先于「证据不足」，不能被 EditableUnknown 覆盖。
            Assert.Equal(ImeProbeReason.UiaUnavailable, diagnostic.Reason);
            Assert.Equal(unchecked((int)0x80010108), diagnostic.HResult);
            Assert.Equal<bool?>(false, diagnostic.IsEditable);
            Assert.True(diagnostic.ValuePatternProbed);
            Assert.Null(diagnostic.ValueReadOnly);
            Assert.True(diagnostic.TextPatternProbed);
            Assert.Null(diagnostic.TextReadOnly);
        }

        [Fact]
        public void QueryCore_TargetForegroundChangedBeforeProbeLeavesDiagnosticsUnset()
        {
            var backend = CreateEditBackend();
            backend.Foreground = new IntPtr(0x9999); // 与期望前台不一致

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.Null(reading);
            Assert.Equal(ImeProbeReason.ForegroundChanged, diagnostic.Reason);
            // 尚未读取元素/模式：保持未查询，不默认 false。
            Assert.Null(diagnostic.IsEditable);
            Assert.False(diagnostic.ValuePatternProbed);
            Assert.False(diagnostic.TextPatternProbed);
            Assert.Null(diagnostic.HasKeyboardFocus);
            Assert.Null(diagnostic.IsEnabled);
        }

        [Fact]
        public void QueryCore_ForegroundChangeAfterSamplingKeepsSampledDiagnostic()
        {
            var backend = CreateEditBackend(UiaCaretReader.UIA_ComboBoxControlTypeId);
            backend.ValueReadOnly = false;
            backend.Pattern = new FakeTextPattern { HasPattern = false, HasPattern2 = false };
            backend.MsaaFactory = () =>
            {
                // 采样后前台被切换：结果整体作废，但诊断要保留已采样内容。
                backend.Foreground = new IntPtr(0x9999);
                return new MsaaCaretReading(
                    true, new Rectangle(50, 60, 1, 18), ImeProbeReason.None, 0);
            };

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.Null(reading);
            Assert.Equal(ImeProbeReason.ForegroundChanged, diagnostic.Reason);
            Assert.Equal<bool?>(true, diagnostic.IsEditable);
            Assert.True(diagnostic.ValuePatternProbed);
            Assert.Equal<bool?>(false, diagnostic.ValueReadOnly);
            Assert.True(diagnostic.TextPatternProbed);
        }

        [Fact]
        public void QueryCore_ValuePatternExceptionKeepsProbedFlagAndHResult()
        {
            var backend = CreateEditBackend(UiaCaretReader.UIA_ComboBoxControlTypeId);
            var failure = new InvalidOperationException("value boom");
            backend.ValuePatternReadFailure = failure;

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.Null(reading);
            Assert.Equal(ImeProbeReason.ProbeException, diagnostic.Reason);
            Assert.Equal(failure.HResult, diagnostic.HResult);
            // 调用前已置 probed：异常不能把它变回「未查询」。
            Assert.True(diagnostic.ValuePatternProbed);
            Assert.Null(diagnostic.ValueReadOnly);
            Assert.False(diagnostic.TextPatternProbed);
            // 尚未走到可编辑判定时保持 null，不默认 false。
            Assert.Null(diagnostic.IsEditable);
            Assert.Equal(1, backend.ValuePatternReadCalls);
            Assert.Equal(0, backend.TextPatternRequestCalls);
        }

        [Fact]
        public void QueryCore_TextPatternExceptionKeepsEarlierValueDiagnostic()
        {
            var backend = CreateEditBackend(UiaCaretReader.UIA_ComboBoxControlTypeId);
            backend.ValueReadOnly = false;
            var failure = new InvalidOperationException("text boom");
            backend.TextPatternRequestFailure = failure;

            UiaFocusReading? reading = CaretBackendCore.QueryCore(
                TestForeground, TestFocusHwnd, allowMsaaCaret: true, backend, out var diagnostic);

            Assert.Null(reading);
            Assert.Equal(ImeProbeReason.ProbeException, diagnostic.Reason);
            Assert.Equal(failure.HResult, diagnostic.HResult);
            // 文本查询抛异常时，之前已采样的 Value 结果仍要保留。
            Assert.True(diagnostic.ValuePatternProbed);
            Assert.Equal<bool?>(false, diagnostic.ValueReadOnly);
            Assert.True(diagnostic.TextPatternProbed);
            Assert.Null(diagnostic.TextReadOnly);
            Assert.Null(diagnostic.IsEditable);
            Assert.Equal(1, backend.ValuePatternReadCalls);
            Assert.Equal(1, backend.TextPatternRequestCalls);
        }

        [Theory]
        [InlineData(0x1234, 0x1234, 17)] // 前台未变、焦点变化 → FocusChanged
        [InlineData(0x1234, 0x5678, 2)]  // 前台已变 → ForegroundChanged
        [InlineData(0x1234, 0x0000, 2)]  // 前台不可读（0）→ ForegroundChanged
        public void ClassifyFocusStateChange_SplitsForegroundAndFocusChange(
            int expectedForeground, int actualForeground, int expectedReason)
        {
            Assert.Equal(
                expectedReason,
                (int)CaretLocator.ClassifyFocusStateChange(
                    new IntPtr(expectedForeground), new IntPtr(actualForeground)));
        }

        // ---------- 测试假后端 ----------

        private static readonly IntPtr TestForeground = new(0x1234);
        private static readonly IntPtr TestFocusHwnd = new(0x5678);

        private static FakeFocusBackend CreateEditBackend(int controlType = 50004)
        {
            return new FakeFocusBackend
            {
                Foreground = TestForeground,
                Element = new UiaElementSnapshot(
                    RuntimeId: "42,100",
                    ProcessId: 4321,
                    Hwnd: new IntPtr(0x9000),
                    ControlType: controlType,
                    ClassName: "Edit",
                    Bounds: new Rectangle(0, 0, 800, 600),
                    HasKeyboardFocus: true,
                    IsKeyboardFocusable: false,
                    IsEnabled: true,
                    IsPassword: false,
                    ProcessName: null),
                CompareHr = 0,
                CompareSame = 1,
                IdentityRuntimeId = "42,100",
            };
        }

        private static FakeFocusBackend CreateTerminalBackend(string? processName)
        {
            return new FakeFocusBackend
            {
                Foreground = TestForeground,
                Element = new UiaElementSnapshot(
                    RuntimeId: "77,1",
                    ProcessId: 4321,
                    Hwnd: IntPtr.Zero,
                    ControlType: UiaCaretReader.UIA_TextControlTypeId,
                    ClassName: UiaCaretReader.TerminalControlClassName,
                    Bounds: new Rectangle(0, 0, 800, 600),
                    HasKeyboardFocus: true,
                    IsKeyboardFocusable: true,
                    IsEnabled: true,
                    IsPassword: false,
                    ProcessName: processName),
                CompareHr = 0,
                CompareSame = 1,
                IdentityRuntimeId = "77,1",
            };
        }

        private static FakeTextPattern CreateEmptyPattern2()
        {
            return new FakeTextPattern
            {
                HasPattern2 = true,
                CaretRangeCallSucceeds = true,
                CaretActive = true,
                CaretRange = new FakeTextRange
                {
                    StartToEnd = 0,
                    Bounds = Array.Empty<double>(),
                },
                HasPattern = true,
                Selection = new FakeTextRange
                {
                    StartToEnd = 0,
                    Bounds = Array.Empty<double>(),
                },
            };
        }

        private static FakeTextPattern CreatePattern2WithCaret(Rectangle caret)
        {
            return new FakeTextPattern
            {
                HasPattern2 = true,
                CaretRangeCallSucceeds = true,
                CaretActive = true,
                CaretRange = new FakeTextRange
                {
                    StartToEnd = 0,
                    Bounds = new[]
                    {
                        (double)caret.Left, caret.Top, caret.Width, caret.Height,
                    },
                },
                HasPattern = true,
                Selection = new FakeTextRange
                {
                    StartToEnd = 0,
                    Bounds = Array.Empty<double>(),
                },
            };
        }

        private static (FakeTextPattern Pattern, FakeTextRange Original, FakeTextRange Expanded)
            CreateTerminalPattern()
        {
            var original = new FakeTextRange
            {
                StartToEnd = 0,
                ReadOnlyValue = false,
            };
            var expanded = new FakeTextRange
            {
                StartToEnd = 1, // 展开后必须非退化
                StartToStart = 0, // 展开起点 == 原 caret 起点
                Bounds = new[] { 100.0, 200.0, 20.0, 20.0 },
            };
            original.CloneValue = expanded;

            var pattern = new FakeTextPattern
            {
                HasPattern = true,
                HasPattern2 = false,
                SelectionCount = 1,
                Selection = original,
            };

            return (pattern, original, expanded);
        }

        private sealed class FakeFocusBackend : IUiaFocusBackend
        {
            public UiaElementSnapshot Element { get; set; }
            public IntPtr Foreground { get; set; }
            public bool ElementReadFails { get; set; }
            public bool IdentityReadFails { get; set; }
            public int IdentityReadHResult { get; set; }
            public int CompareHr { get; set; }
            public int CompareSame { get; set; } = 1;
            public string? IdentityRuntimeId { get; set; }
            public bool? ValueReadOnly { get; set; }
            public Exception? ValuePatternReadFailure { get; set; }
            public Exception? TextPatternRequestFailure { get; set; }
            public ITextPatternBackend? Pattern { get; set; }
            public MsaaCaretReading Msaa { get; set; }
            public Func<MsaaCaretReading>? MsaaFactory { get; set; }
            public int MsaaCalls { get; private set; }
            public int ValuePatternReadCalls { get; private set; }
            public int TextPatternRequestCalls { get; private set; }

            public uint GetProcessId(IntPtr hwnd) => Element.ProcessId;

            public IntPtr GetForeground() => Foreground;

            public bool TryReadFocusElement(out UiaElementSnapshot element, out int hresult)
            {
                element = Element;
                hresult = 0;
                return !ElementReadFails;
            }

            public bool TryReadFocusIdentity(
                out int compareHr, out int compareSame, out string? runtimeId, out int hresult)
            {
                compareHr = CompareHr;
                compareSame = CompareSame;
                runtimeId = IdentityRuntimeId;
                hresult = IdentityReadHResult;
                return !IdentityReadFails;
            }

            public bool? ReadValuePatternReadOnly()
            {
                ValuePatternReadCalls++;
                if (ValuePatternReadFailure != null)
                    throw ValuePatternReadFailure;
                return ValueReadOnly;
            }

            public ITextPatternBackend? GetTextPatternBackend()
            {
                TextPatternRequestCalls++;
                if (TextPatternRequestFailure != null)
                    throw TextPatternRequestFailure;
                return Pattern;
            }

            public MsaaCaretReading TryReadMsaaCaret(IntPtr focusHwnd, Rectangle? elementBounds)
            {
                MsaaCalls++;
                return MsaaFactory != null ? MsaaFactory() : Msaa;
            }

            public void Dispose()
            {
            }
        }

        private sealed class FakeTextPattern : ITextPatternBackend
        {
            public bool HasPattern2 { get; set; }
            public bool HasPattern { get; set; } = true;
            public bool CaretRangeCallSucceeds { get; set; }
            public bool CaretActive { get; set; }
            public FakeTextRange? CaretRange { get; set; }
            public bool SelectionCallSucceeds { get; set; } = true;
            public int SelectionCount { get; set; } = 1;
            public FakeTextRange? Selection { get; set; }

            public bool TryGetCaretRange(out bool isActive, out ITextRangeBackend? range)
            {
                isActive = CaretActive;
                range = CaretRange;
                return CaretRangeCallSucceeds;
            }

            public bool TryGetSelection(out int rangeCount, out ITextRangeBackend? range)
            {
                rangeCount = SelectionCount;
                range = Selection;
                return SelectionCallSucceeds;
            }

            public void Dispose()
            {
            }
        }

        private sealed class FakeTextRange : ITextRangeBackend
        {
            public int StartToEnd { get; set; }
            public bool CompareStartToEndFails { get; set; }
            public bool? ReadOnlyValue { get; set; } = false;
            public bool BoundsCallFails { get; set; }
            public double[]? Bounds { get; set; } = Array.Empty<double>();
            public bool CloneFails { get; set; }
            public FakeTextRange? CloneValue { get; set; }
            public bool ExpandFails { get; set; }
            public bool CompareStartToStartFails { get; set; }
            public int StartToStart { get; set; }

            public bool TryCompareStartToEnd(out int compare)
            {
                compare = StartToEnd;
                return !CompareStartToEndFails;
            }

            public bool? GetIsReadOnly() => ReadOnlyValue;

            public bool TryGetBoundingRectangles(out double[]? bounds)
            {
                bounds = Bounds;
                return !BoundsCallFails;
            }

            public ITextRangeBackend? Clone() => CloneFails ? null : CloneValue;

            public bool TryExpandToEnclosingCharacter() => !ExpandFails;

            public bool TryCompareStartToStart(ITextRangeBackend other, out int compare)
            {
                compare = StartToStart;
                return !CompareStartToStartFails;
            }

            public void Dispose()
            {
            }
        }
    }
}
