using System;
using System.Drawing;
using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests
{
    /// <summary>
    /// 焦点/切换处置结果的行为测试：使用真实的 <see cref="ImeFocusSessionPolicy"/> 和
    /// 假隐藏/显示/重试操作调用生产 <see cref="ImeProbeHandler"/>，不复制映射规则。
    /// </summary>
    public class ImeProbeHandlerTests
    {
        private static readonly ImeFocusIdentity SampleIdentity =
            new(new IntPtr(0x100), new IntPtr(0x200), 7, 1234, "42,1");

        private sealed class FakeOperations
        {
            public int HideCount { get; private set; }
            public int ShowCount { get; private set; }
            public int ScheduleCount { get; private set; }
            public bool ShowResult { get; set; } = true;
            public bool ScheduleResult { get; set; } = true;
            public string? ShownText { get; private set; }
            public Rectangle? ShownCaret { get; private set; }
            public IntPtr ShownDpiHwnd { get; private set; }
            public ImeFocusIdentity? ShownIdentity { get; private set; }
            public int ScheduledDelay { get; private set; }

            public void Hide() => HideCount++;

            public bool Show(string text, Rectangle caret, IntPtr dpiHwnd, ImeFocusIdentity identity)
            {
                ShowCount++;
                ShownText = text;
                ShownCaret = caret;
                ShownDpiHwnd = dpiHwnd;
                ShownIdentity = identity;
                return ShowResult;
            }

            public bool Schedule(int delayMs)
            {
                ScheduleCount++;
                ScheduledDelay = delayMs;
                return ScheduleResult;
            }
        }

        private static ImeFocusSnapshot Snapshot(
            ImeFocusIdentity? identity = null, bool editable = true, bool hasCaret = true)
        {
            ImeFocusIdentity id = identity ?? SampleIdentity;
            return new ImeFocusSnapshot(
                id,
                editable,
                hasCaret ? new Rectangle(10, 20, 2, 18) : null,
                id.FocusHwnd,
                ImeCaretSource.Win32);
        }

        private static ImeHandleResult HandleFocus(
            ImeFocusSessionPolicy policy,
            FakeOperations ops,
            ImeFocusSnapshot snapshot,
            ImeQueryResult ime,
            ref string? lastLabel,
            ref ImeFocusIdentity? lastLabelIdentity,
            long epoch = 0)
        {
            return ImeProbeHandler.HandleFocus(
                policy, epoch, snapshot, ime, ops.Hide, ops.Show, ops.Schedule,
                ref lastLabel, ref lastLabelIdentity);
        }

        private static ImeHandleResult HandleSwitch(
            ImeFocusSessionPolicy policy,
            FakeOperations ops,
            ImeFocusSnapshot snapshot,
            ImeQueryResult ime,
            ref string? lastLabel,
            ref ImeFocusIdentity? lastLabelIdentity)
        {
            return ImeProbeHandler.HandleSwitch(
                policy, snapshot, ime, ops.Hide, ops.Show,
                ref lastLabel, ref lastLabelIdentity);
        }

        private static ImeQueryResult Trustworthy(ImeDisplay display) => new(display, true);

        [Fact]
        public void Focus_FirstShow_ReturnsShownAndMarksDisplayed()
        {
            var policy = new ImeFocusSessionPolicy();
            var ops = new FakeOperations();
            string? label = null;
            ImeFocusIdentity? identity = null;
            var snapshot = Snapshot();

            ImeHandleResult result = HandleFocus(
                policy, ops, snapshot, Trustworthy(ImeDisplay.Chinese), ref label, ref identity);

            Assert.Equal(ImeHandleResult.Shown, result);
            Assert.Equal("中", label);
            Assert.True(identity.HasValue);
            Assert.Equal(snapshot.Identity, identity.Value);
            Assert.True(policy.IsDisplayed);
            Assert.Equal(1, ops.ShowCount);
            Assert.Equal("中", ops.ShownText);
            Assert.Equal(snapshot.Caret, ops.ShownCaret);
            Assert.Equal(snapshot.DpiHwnd, ops.ShownDpiHwnd);
            Assert.True(ops.ShownIdentity.HasValue);
            Assert.Equal(snapshot.Identity, ops.ShownIdentity.Value);
        }

        [Fact]
        public void Focus_DuplicateNotification_IsIgnoredWithoutShowingAgain()
        {
            var policy = new ImeFocusSessionPolicy();
            var ops = new FakeOperations();
            string? label = null;
            ImeFocusIdentity? identity = null;
            var snapshot = Snapshot();
            ImeQueryResult ime = Trustworthy(ImeDisplay.Chinese);

            Assert.Equal(ImeHandleResult.Shown,
                HandleFocus(policy, ops, snapshot, ime, ref label, ref identity));
            Assert.Equal(ImeHandleResult.Ignored,
                HandleFocus(policy, ops, snapshot, ime, ref label, ref identity));

            Assert.Equal(1, ops.ShowCount);
            Assert.Equal(0, ops.HideCount);
        }

        [Fact]
        public void Focus_StaleEpoch_IsIgnored()
        {
            var policy = new ImeFocusSessionPolicy();
            policy.BeginEvent(); // 代次变为 1
            var ops = new FakeOperations();
            string? label = null;
            ImeFocusIdentity? identity = null;

            ImeHandleResult result = HandleFocus(
                policy, ops, Snapshot(), Trustworthy(ImeDisplay.Chinese),
                ref label, ref identity, epoch: 0);

            Assert.Equal(ImeHandleResult.Ignored, result);
            Assert.Equal(0, ops.ShowCount);
            Assert.Equal(0, ops.HideCount);
        }

        [Fact]
        public void Focus_NotEditable_ReturnsHiddenAndClearsBaseline()
        {
            var policy = new ImeFocusSessionPolicy();
            var ops = new FakeOperations();
            string? label = "中";
            ImeFocusIdentity? identity = SampleIdentity;

            ImeHandleResult result = HandleFocus(
                policy, ops, Snapshot(editable: false), Trustworthy(ImeDisplay.Chinese),
                ref label, ref identity);

            Assert.Equal(ImeHandleResult.Hidden, result);
            Assert.Equal(1, ops.HideCount);
            Assert.Null(label);
            Assert.Null(identity);
        }

        [Fact]
        public void Focus_CaretMissing_SchedulesRetryWithHideOldBadge()
        {
            var policy = new ImeFocusSessionPolicy();
            var ops = new FakeOperations { ScheduleResult = true };
            string? label = "中";
            ImeFocusIdentity? identity = null;

            ImeHandleResult result = HandleFocus(
                policy, ops, Snapshot(hasCaret: false), Trustworthy(ImeDisplay.Chinese),
                ref label, ref identity);

            Assert.Equal(ImeHandleResult.RetryScheduled, result);
            Assert.Equal(1, ops.HideCount);
            Assert.Equal(1, ops.ScheduleCount);
            Assert.Equal(ImeFocusSessionPolicy.FirstRetryDelayMs, ops.ScheduledDelay);
            Assert.False(policy.IsDisplayed);
        }

        [Fact]
        public void Focus_RetryTimerUnavailable_ReturnsIgnored()
        {
            var policy = new ImeFocusSessionPolicy();
            var ops = new FakeOperations { ScheduleResult = false };
            string? label = null;
            ImeFocusIdentity? identity = null;

            ImeHandleResult result = HandleFocus(
                policy, ops, Snapshot(hasCaret: false), Trustworthy(ImeDisplay.Chinese),
                ref label, ref identity);

            Assert.Equal(ImeHandleResult.Ignored, result);
            Assert.Equal(1, ops.ScheduleCount);
            Assert.Equal(1, ops.HideCount);
        }

        [Fact]
        public void Focus_ShowFailure_ReturnsShowFailedWithoutMarkingDisplayed()
        {
            var policy = new ImeFocusSessionPolicy();
            var ops = new FakeOperations { ShowResult = false };
            string? label = null;
            ImeFocusIdentity? identity = null;

            ImeHandleResult result = HandleFocus(
                policy, ops, Snapshot(), Trustworthy(ImeDisplay.Chinese), ref label, ref identity);

            Assert.Equal(ImeHandleResult.ShowFailed, result);
            Assert.Equal(1, ops.ShowCount);
            Assert.Equal(0, ops.HideCount);
            Assert.False(policy.IsDisplayed);
            Assert.Null(label);
            Assert.Null(identity);
        }

        [Fact]
        public void Focus_ImeUntrusted_ReturnsImeRejectedAndKeepsSession()
        {
            var policy = new ImeFocusSessionPolicy();
            var ops = new FakeOperations();
            string? label = "中";
            ImeFocusIdentity? identity = null;

            ImeHandleResult result = HandleFocus(
                policy, ops, Snapshot(), new ImeQueryResult(ImeDisplay.Chinese, false),
                ref label, ref identity);

            Assert.Equal(ImeHandleResult.ImeRejected, result);
            Assert.Equal(1, ops.HideCount);
            Assert.Equal(0, ops.ShowCount);
            Assert.False(policy.IsDisplayed);
            Assert.True(policy.HasSession);
            Assert.Null(label);
            Assert.Null(identity);
        }

        [Fact]
        public void Focus_NoDisplayMode_ReturnsHidden()
        {
            var policy = new ImeFocusSessionPolicy();
            var ops = new FakeOperations();
            string? label = null;
            ImeFocusIdentity? identity = null;

            ImeHandleResult result = HandleFocus(
                policy, ops, Snapshot(), Trustworthy(ImeDisplay.None), ref label, ref identity);

            Assert.Equal(ImeHandleResult.Hidden, result);
            Assert.Equal(1, ops.HideCount);
            Assert.Equal(0, ops.ShowCount);
            Assert.False(policy.IsDisplayed);
        }

        [Fact]
        public void Switch_SameIdentityAndMode_IsIgnoredAfterFirstShow()
        {
            var policy = new ImeFocusSessionPolicy();
            var ops = new FakeOperations();
            string? label = null;
            ImeFocusIdentity? identity = null;
            var snapshot = Snapshot();
            ImeQueryResult ime = Trustworthy(ImeDisplay.Chinese);

            Assert.Equal(ImeHandleResult.Shown,
                HandleSwitch(policy, ops, snapshot, ime, ref label, ref identity));
            Assert.Equal(ImeHandleResult.Ignored,
                HandleSwitch(policy, ops, snapshot, ime, ref label, ref identity));

            Assert.Equal(1, ops.ShowCount);
            Assert.Equal(0, ops.HideCount);
            Assert.True(policy.IsDisplayed);
        }

        [Fact]
        public void Switch_ModeChange_ShowsAgain()
        {
            var policy = new ImeFocusSessionPolicy();
            var ops = new FakeOperations();
            string? label = null;
            ImeFocusIdentity? identity = null;
            var snapshot = Snapshot();

            Assert.Equal(ImeHandleResult.Shown,
                HandleSwitch(policy, ops, snapshot, Trustworthy(ImeDisplay.Chinese), ref label, ref identity));
            Assert.Equal(ImeHandleResult.Shown,
                HandleSwitch(policy, ops, snapshot, Trustworthy(ImeDisplay.English), ref label, ref identity));

            Assert.Equal(2, ops.ShowCount);
            Assert.Equal("英", label);
        }

        [Fact]
        public void Switch_DifferentIdentity_ShowsAgain()
        {
            var policy = new ImeFocusSessionPolicy();
            var ops = new FakeOperations();
            string? label = null;
            ImeFocusIdentity? identity = null;

            Assert.Equal(ImeHandleResult.Shown,
                HandleSwitch(policy, ops, Snapshot(), Trustworthy(ImeDisplay.Chinese),
                    ref label, ref identity));

            var otherIdentity = new ImeFocusIdentity(
                new IntPtr(0x300), new IntPtr(0x400), 8, 5678, "43,1");
            ImeHandleResult result = HandleSwitch(
                policy, ops, Snapshot(otherIdentity), Trustworthy(ImeDisplay.Chinese),
                ref label, ref identity);

            Assert.Equal(ImeHandleResult.Shown, result);
            Assert.Equal(2, ops.ShowCount);
            Assert.True(identity.HasValue);
            Assert.Equal(otherIdentity, identity.Value);
        }

        [Fact]
        public void Switch_NotEditable_ReturnsHiddenAndClearsSession()
        {
            var policy = new ImeFocusSessionPolicy();
            policy.MarkDisplayed(SampleIdentity);
            var ops = new FakeOperations();
            string? label = "中";
            ImeFocusIdentity? identity = SampleIdentity;

            ImeHandleResult result = HandleSwitch(
                policy, ops, Snapshot(editable: false), Trustworthy(ImeDisplay.Chinese),
                ref label, ref identity);

            Assert.Equal(ImeHandleResult.Hidden, result);
            Assert.Equal(1, ops.HideCount);
            Assert.False(policy.HasSession);
            Assert.Null(label);
            Assert.Null(identity);
        }

        [Fact]
        public void Switch_ImeUntrusted_ReturnsImeRejectedAndKeepsSession()
        {
            var policy = new ImeFocusSessionPolicy();
            policy.MarkDisplayed(SampleIdentity);
            var ops = new FakeOperations();
            string? label = "中";
            ImeFocusIdentity? identity = SampleIdentity;

            ImeHandleResult result = HandleSwitch(
                policy, ops, Snapshot(), new ImeQueryResult(ImeDisplay.Chinese, false),
                ref label, ref identity);

            Assert.Equal(ImeHandleResult.ImeRejected, result);
            Assert.Equal(1, ops.HideCount);
            Assert.Equal(0, ops.ShowCount);
            Assert.True(policy.HasSession);
            Assert.Null(label);
            Assert.Null(identity);
        }

        [Fact]
        public void Switch_NoDisplayMode_ReturnsHidden()
        {
            var policy = new ImeFocusSessionPolicy();
            var ops = new FakeOperations();
            string? label = null;
            ImeFocusIdentity? identity = null;

            ImeHandleResult result = HandleSwitch(
                policy, ops, Snapshot(), Trustworthy(ImeDisplay.None), ref label, ref identity);

            Assert.Equal(ImeHandleResult.Hidden, result);
            Assert.Equal(1, ops.HideCount);
            Assert.Equal(0, ops.ShowCount);
            Assert.Null(label);
            Assert.Null(identity);
        }

        [Fact]
        public void Switch_CaretMissing_IsIgnoredButKeepsBaseline()
        {
            var policy = new ImeFocusSessionPolicy();
            var ops = new FakeOperations();
            string? label = null;
            ImeFocusIdentity? identity = null;

            ImeHandleResult result = HandleSwitch(
                policy, ops, Snapshot(hasCaret: false), Trustworthy(ImeDisplay.Chinese),
                ref label, ref identity);

            Assert.Equal(ImeHandleResult.Ignored, result);
            Assert.Equal(0, ops.ShowCount);
            Assert.Equal("中", label);
            Assert.True(identity.HasValue);
            Assert.Equal(SampleIdentity, identity.Value);
        }

        [Fact]
        public void Switch_ShowFailure_ReturnsShowFailedWithoutMarkingDisplayed()
        {
            var policy = new ImeFocusSessionPolicy();
            var ops = new FakeOperations { ShowResult = false };
            string? label = null;
            ImeFocusIdentity? identity = null;

            ImeHandleResult result = HandleSwitch(
                policy, ops, Snapshot(), Trustworthy(ImeDisplay.Chinese), ref label, ref identity);

            Assert.Equal(ImeHandleResult.ShowFailed, result);
            Assert.Equal(1, ops.ShowCount);
            Assert.False(policy.IsDisplayed);
        }
    }
}
