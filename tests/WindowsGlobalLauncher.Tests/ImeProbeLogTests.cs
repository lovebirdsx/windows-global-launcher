using System;
using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests
{
    /// <summary>
    /// 输入法探测日志的纯格式化测试。
    /// <para>
    /// 说明：Manager 对 WPF 角标窗的实际绑定仍需 UI 线程；处置逻辑已抽到
    /// <see cref="ImeProbeHandler"/>，由 <c>ImeProbeHandlerTests</c> 覆盖。
    /// 本文件只覆盖生产复用的格式/限频纯函数，不另写决策逻辑。
    /// </para>
    /// </summary>
    public class ImeProbeLogTests
    {
        [Theory]
        [InlineData(null, false, "未查询")]
        [InlineData(true, false, "未查询")]
        [InlineData(false, false, "未查询")]
        [InlineData(null, true, "未知")]
        [InlineData(false, true, "可写")]
        [InlineData(true, true, "只读")]
        public void ReadOnlyState_DistinguishesUnqueriedUnknownAndValues(
            bool? readOnly, bool probed, string expected)
        {
            Assert.Equal(expected, ImeProbeLog.ReadOnlyState(readOnly, probed));
        }

        [Theory]
        [InlineData(null, "未知")]
        [InlineData(true, "true")]
        [InlineData(false, "false")]
        public void BoolState_RendersThreeStates(bool? value, string expected)
        {
            Assert.Equal(expected, ImeProbeLog.BoolState(value));
        }

        [Fact]
        public void HandleText_NullMeansNotHandled()
        {
            Assert.Equal(ImeProbeLog.UnhandledResult, ImeProbeLog.HandleText(null));
            Assert.Equal("Shown", ImeProbeLog.HandleText(ImeHandleResult.Shown));
        }

        [Fact]
        public void ThrottleKey_SeparatesControlTypeAndHandleResult()
        {
            string combo = ImeProbeLog.BuildThrottleKey(
                success: false, ImeCaretSource.None, "NotEditable", 50003, ImeHandleResult.Hidden);
            string custom = ImeProbeLog.BuildThrottleKey(
                success: false, ImeCaretSource.None, "NotEditable", 50026, ImeHandleResult.Hidden);
            string customShown = ImeProbeLog.BuildThrottleKey(
                success: false, ImeCaretSource.None, "NotEditable", 50026, ImeHandleResult.Shown);
            string unhandled = ImeProbeLog.BuildThrottleKey(
                success: false, ImeCaretSource.None, "Expired", 0, null);

            Assert.NotEqual(combo, custom);
            Assert.NotEqual(custom, customShown);
            Assert.NotEqual(custom, unhandled);
            Assert.EndsWith(":Unhandled", unhandled);

            // 键只含有限分类，不含请求号/时间戳等持续变化的值。
            Assert.DoesNotContain("request", combo, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("request", customShown, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ThrottleKey_UsesFiniteSuccessPrefix()
        {
            string key = ImeProbeLog.BuildThrottleKey(
                success: true, ImeCaretSource.Uia, "IgnoredReason", 50004, ImeHandleResult.Shown);

            Assert.Equal("Ready:Uia:ct50004:Shown", key);
        }

        [Fact]
        public void Throttle_SeparatesKeysWhileStillLimitingSameKey()
        {
            var throttle = new ImeDiagnosticThrottle();
            string combo = ImeProbeLog.BuildThrottleKey(
                success: false, ImeCaretSource.None, "NotEditable", 50003, ImeHandleResult.Hidden);
            string custom = ImeProbeLog.BuildThrottleKey(
                success: false, ImeCaretSource.None, "NotEditable", 50026, ImeHandleResult.Hidden);
            string customShown = ImeProbeLog.BuildThrottleKey(
                success: false, ImeCaretSource.None, "NotEditable", 50026, ImeHandleResult.Shown);

            Assert.True(throttle.ShouldWrite(1, combo, 0));
            Assert.False(throttle.ShouldWrite(1, combo, 1));

            // 同一 PID 下不同控件类型/处置结果各占独立限频项，互不遮蔽。
            Assert.True(throttle.ShouldWrite(1, custom, 2));
            Assert.True(throttle.ShouldWrite(1, customShown, 3));
            Assert.False(throttle.ShouldWrite(1, custom, 4));
            Assert.Equal(3, throttle.Count);
        }

        [Fact]
        public void BuildMessage_UsesPureReasonWhileThrottleKeyStaysComposite()
        {
            string throttleKey = ImeProbeLog.BuildThrottleKey(
                success: false, ImeCaretSource.None, "NotEditable", 50026, ImeHandleResult.Hidden);
            string message = ImeProbeLog.BuildMessage(123, "NotEditable", "details");

            // 日志 reason 是纯原因；复合键只用于限频，不混进消息。
            Assert.Contains("reason=NotEditable", message);
            Assert.DoesNotContain(throttleKey, message);
            Assert.DoesNotContain("ct50026", message);
        }

        [Fact]
        public void BuildDetails_ShowsTriStatesAndNeverProviderText()
        {
            var diagnostic = new ImeProbeDiagnostic(
                ImeProbeReason.EditableUnknown,
                ImeCaretSource.None,
                ProcessId: 123,
                ControlType: 50026,
                ClassName: "SENSITIVE_CLASS_NAME",
                HasRuntimeId: true,
                HasTextPattern: false,
                HasTextPattern2: false,
                CaretRectUsable: false,
                RectCount: 0,
                IsPassword: false,
                HResult: unchecked((int)0x80004005),
                IsEditable: null,
                ValuePatternProbed: false,
                TextPatternProbed: true,
                // 未查询 Value 模式时，即使携带了值也不能当成「只读」。
                ValueReadOnly: true,
                TextReadOnly: null,
                HasKeyboardFocus: false,
                IsEnabled: true);

            string details = ImeProbeLog.BuildDetails(
                requestId: 42,
                queueWait: TimeSpan.FromMilliseconds(3),
                queryTime: TimeSpan.FromMilliseconds(7),
                diagnostic,
                handled: ImeHandleResult.Ignored);

            Assert.Contains("isEditable=未知", details);
            Assert.Contains("valueProbed=False", details);
            Assert.Contains("valueReadOnly=未查询", details);
            Assert.Contains("textProbed=True", details);
            Assert.Contains("textReadOnly=未知", details);
            Assert.Contains("hasKeyboardFocus=false", details);
            Assert.Contains("isEnabled=true", details);
            Assert.Contains("result=Ignored", details);
            Assert.Contains("HRESULT=0x80004005", details);

            // 隐私：不输出 provider 文本/身份字段，只保留布尔与 HRESULT。
            Assert.Contains("runtimeId=True", details);
            Assert.DoesNotContain("SENSITIVE_CLASS_NAME", details);
            Assert.DoesNotContain("ClassName", details, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Name=", details);
            Assert.DoesNotContain("Value=", details);
        }

        [Fact]
        public void BuildDetails_ProbedButUnknownDiffersFromUnqueried()
        {
            var unqueried = new ImeProbeDiagnostic(
                ImeProbeReason.EditableUnknown, ImeCaretSource.None, ProcessId: 1,
                ValuePatternProbed: false, TextPatternProbed: false);
            var unknown = new ImeProbeDiagnostic(
                ImeProbeReason.EditableUnknown, ImeCaretSource.None, ProcessId: 1,
                ValuePatternProbed: true, TextPatternProbed: true);

            string unqueriedText = ImeProbeLog.BuildDetails(1, TimeSpan.Zero, TimeSpan.Zero, unqueried, null);
            string unknownText = ImeProbeLog.BuildDetails(1, TimeSpan.Zero, TimeSpan.Zero, unknown, null);

            Assert.Contains("valueReadOnly=未查询", unqueriedText);
            Assert.Contains("textReadOnly=未查询", unqueriedText);
            Assert.Contains("valueReadOnly=未知", unknownText);
            Assert.Contains("textReadOnly=未知", unknownText);
            Assert.Contains("result=Unhandled", unknownText);
        }
    }
}
