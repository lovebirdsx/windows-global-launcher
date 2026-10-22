using System;
using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests
{
    /// <summary>
    /// 输入法焦点会话策略（<see cref="ImeFocusSessionPolicy"/>）的纯逻辑测试。
    /// 覆盖：重复焦点不重显、新身份显示、离开后返回重新显示、有限重试预算、迟到结果作废。
    /// </summary>
    public class ImeFocusSessionPolicyTests
    {
        private static ImeFocusIdentity Identity(int n)
        {
            // 位置参数与 ImeFocusIdentity 的声明保持一致。
            return new ImeFocusIdentity(
                (IntPtr)n,
                (IntPtr)(n * 10),
                (uint)(n * 100),
                (uint)(n * 1000),
                $"rid-{n}");
        }

        [Fact]
        public void NewIdentityWithCaret_Shows()
        {
            var policy = new ImeFocusSessionPolicy();
            long epoch = policy.BeginEvent();

            ImeFocusDecision decision = policy.OnProbeResult(
                epoch, Identity(1), isEditable: true, hasCaret: true);

            Assert.Equal(ImeFocusDecisionKind.Show, decision.Kind);
        }

        [Fact]
        public void NewIdentityWithoutCaret_RetriesTwiceThenGivesUp()
        {
            var policy = new ImeFocusSessionPolicy();
            long epoch = policy.BeginEvent();
            ImeFocusIdentity id = Identity(1);

            ImeFocusDecision first = policy.OnProbeResult(epoch, id, true, hasCaret: false);
            Assert.Equal(ImeFocusDecisionKind.Retry, first.Kind);
            Assert.Equal(ImeFocusSessionPolicy.FirstRetryDelayMs, first.RetryDelayMs);
            // 新身份但插入符未就绪：必须先隐藏上一个输入框残留的角标。
            Assert.True(first.HideOldBadge);

            ImeFocusDecision second = policy.OnProbeResult(epoch, id, true, hasCaret: false);
            Assert.Equal(ImeFocusDecisionKind.Retry, second.Kind);
            Assert.Equal(ImeFocusSessionPolicy.SecondRetryDelayMs, second.RetryDelayMs);
            // 同一身份的后续重试不得再次隐藏，否则会打断既有显示/隐藏计时。
            Assert.False(second.HideOldBadge);

            ImeFocusDecision third = policy.OnProbeResult(epoch, id, true, hasCaret: false);
            Assert.Equal(ImeFocusDecisionKind.Ignore, third.Kind);
        }

        [Fact]
        public void DuplicateNotification_AfterShow_DoesNotShowAgain()
        {
            var policy = new ImeFocusSessionPolicy();
            ImeFocusIdentity id = Identity(1);

            long firstEpoch = policy.BeginEvent();
            ImeFocusDecision first = policy.OnProbeResult(firstEpoch, id, true, hasCaret: true);
            Assert.Equal(ImeFocusDecisionKind.Show, first.Kind);
            policy.MarkDisplayed(id);

            // 同一输入框的重复焦点通知：即使插入符仍可用，也不重新显示（不重置隐藏计时）。
            long duplicateEpoch = policy.BeginEvent();
            ImeFocusDecision duplicate = policy.OnProbeResult(duplicateEpoch, id, true, hasCaret: true);
            Assert.Equal(ImeFocusDecisionKind.Ignore, duplicate.Kind);
            // 重复通知不得要求隐藏角标（会打断正在计时的自动隐藏）。
            Assert.False(duplicate.HideOldBadge);
            Assert.True(policy.IsDisplayed);
        }

        [Fact]
        public void DuplicateNotification_DoesNotResetRetryBudget()
        {
            var policy = new ImeFocusSessionPolicy();
            ImeFocusIdentity id = Identity(1);

            long firstEpoch = policy.BeginEvent();
            Assert.Equal(
                ImeFocusDecisionKind.Retry,
                policy.OnProbeResult(firstEpoch, id, true, hasCaret: false).Kind);

            long secondEpoch = policy.BeginEvent();
            ImeFocusDecision second = policy.OnProbeResult(secondEpoch, id, true, hasCaret: false);
            Assert.Equal(ImeFocusDecisionKind.Retry, second.Kind);
            Assert.Equal(ImeFocusSessionPolicy.SecondRetryDelayMs, second.RetryDelayMs);

            // 预算已经用尽：新的重复焦点通知不得重新从 100ms 开始。
            long thirdEpoch = policy.BeginEvent();
            Assert.Equal(
                ImeFocusDecisionKind.Ignore,
                policy.OnProbeResult(thirdEpoch, id, true, hasCaret: false).Kind);

            long fourthEpoch = policy.BeginEvent();
            Assert.Equal(
                ImeFocusDecisionKind.Ignore,
                policy.OnProbeResult(fourthEpoch, id, true, hasCaret: false).Kind);
        }

        [Fact]
        public void CaretBecomesReadyOnRetry_Shows()
        {
            var policy = new ImeFocusSessionPolicy();
            ImeFocusIdentity id = Identity(1);

            long firstEpoch = policy.BeginEvent();
            Assert.Equal(
                ImeFocusDecisionKind.Retry,
                policy.OnProbeResult(firstEpoch, id, true, hasCaret: false).Kind);

            long retryEpoch = policy.BeginEvent();
            Assert.Equal(
                ImeFocusDecisionKind.Show,
                policy.OnProbeResult(retryEpoch, id, true, hasCaret: true).Kind);
        }

        [Fact]
        public void NewIdentity_ShowsEvenWhenPreviousWasDisplayed()
        {
            var policy = new ImeFocusSessionPolicy();
            ImeFocusIdentity first = Identity(1);
            ImeFocusIdentity second = Identity(2);

            long firstEpoch = policy.BeginEvent();
            Assert.Equal(
                ImeFocusDecisionKind.Show,
                policy.OnProbeResult(firstEpoch, first, true, hasCaret: true).Kind);
            policy.MarkDisplayed(first);

            long secondEpoch = policy.BeginEvent();
            Assert.Equal(
                ImeFocusDecisionKind.Show,
                policy.OnProbeResult(secondEpoch, second, true, hasCaret: true).Kind);
        }

        [Fact]
        public void LeavingEditable_HidesAndReenterShowsAgain()
        {
            var policy = new ImeFocusSessionPolicy();
            ImeFocusIdentity id = Identity(1);

            long firstEpoch = policy.BeginEvent();
            Assert.Equal(
                ImeFocusDecisionKind.Show,
                policy.OnProbeResult(firstEpoch, id, true, hasCaret: true).Kind);
            policy.MarkDisplayed(id);

            // 焦点落到按钮/只读文本：隐藏并清除会话。
            long leaveEpoch = policy.BeginEvent();
            Assert.Equal(
                ImeFocusDecisionKind.Hide,
                policy.OnProbeResult(leaveEpoch, id, isEditable: false, hasCaret: false).Kind);
            Assert.False(policy.HasSession);

            // 重新进入原输入框：算新会话，应重新显示。
            long reenterEpoch = policy.BeginEvent();
            Assert.Equal(
                ImeFocusDecisionKind.Show,
                policy.OnProbeResult(reenterEpoch, id, true, hasCaret: true).Kind);
        }

        [Fact]
        public void EditableUnknown_DoesNotShowOrSpendRetryBudget()
        {
            var policy = new ImeFocusSessionPolicy();
            ImeFocusIdentity id = Identity(1);
            policy.MarkDisplayed(id);
            var snapshot = new ImeFocusSnapshot(
                id, IsEditable: false, Caret: null, DpiHwnd: id.FocusHwnd,
                Diagnostic: ImeProbeDiagnostic.For(ImeProbeReason.EditableUnknown));

            // 能力未知不借用「已可编辑但光标未就绪」的重试通道，也不保留旧角标。
            for (int i = 0; i < 3; i++)
            {
                long epoch = policy.BeginEvent();
                var decision = policy.OnProbeResult(
                    epoch, snapshot.Identity, snapshot.IsEditable, snapshot.Caret.HasValue);
                Assert.Equal(ImeFocusDecisionKind.Hide, decision.Kind);
                Assert.False(policy.HasSession);
                Assert.False(policy.IsDisplayed);
            }

            // 后续新事件取得明确可写证据后，仍有完整的有限重试预算。
            long confirmedEpoch = policy.BeginEvent();
            var retry = policy.OnProbeResult(confirmedEpoch, id, true, hasCaret: false);
            Assert.Equal(ImeFocusDecisionKind.Retry, retry.Kind);
            Assert.Equal(ImeFocusSessionPolicy.FirstRetryDelayMs, retry.RetryDelayMs);
        }

        [Fact]
        public void ClearSession_AfterSwitchHandoff_SameIdentityShowsAgain()
        {
            var policy = new ImeFocusSessionPolicy();
            ImeFocusIdentity id = Identity(1);

            policy.MarkDisplayed(id);

            // 切换手势探测发现焦点已不在输入框：显式清会话（与获焦路径一致）。
            policy.ClearSession();
            Assert.False(policy.HasSession);

            // 重新进入同一输入框：算新会话，应重新显示。
            long epoch = policy.BeginEvent();
            Assert.Equal(
                ImeFocusDecisionKind.Show,
                policy.OnProbeResult(epoch, id, true, hasCaret: true).Kind);
        }

        [Fact]
        public void StaleEpoch_IsIgnored()
        {
            var policy = new ImeFocusSessionPolicy();
            long oldEpoch = policy.BeginEvent();
            policy.BeginEvent(); // 新的原始焦点事件

            Assert.Equal(
                ImeFocusDecisionKind.Ignore,
                policy.OnProbeResult(oldEpoch, Identity(1), true, hasCaret: true).Kind);
        }

        [Fact]
        public void ForegroundChange_HidesAndClearsSession()
        {
            var policy = new ImeFocusSessionPolicy();
            ImeFocusIdentity id = Identity(1);

            long firstEpoch = policy.BeginEvent();
            Assert.Equal(
                ImeFocusDecisionKind.Show,
                policy.OnProbeResult(firstEpoch, id, true, hasCaret: true).Kind);
            policy.MarkDisplayed(id);

            long newEpoch = policy.OnForegroundChanged();
            Assert.NotEqual(firstEpoch, newEpoch);
            Assert.False(policy.HasSession);

            // 旧代次的结果迟到：作废。
            Assert.Equal(
                ImeFocusDecisionKind.Ignore,
                policy.OnProbeResult(firstEpoch, id, true, hasCaret: true).Kind);

            // 新代次下即使身份相同也按新会话显示。
            Assert.Equal(
                ImeFocusDecisionKind.Show,
                policy.OnProbeResult(newEpoch, id, true, hasCaret: true).Kind);
        }

        [Fact]
        public void NullIdentity_HidesAndClearsSession()
        {
            var policy = new ImeFocusSessionPolicy();
            long epoch = policy.BeginEvent();

            Assert.Equal(
                ImeFocusDecisionKind.Hide,
                policy.OnProbeResult(epoch, identity: null, isEditable: true, hasCaret: false).Kind);
            Assert.False(policy.HasSession);
        }

        [Fact]
        public void MarkDisplayed_AdoptsSwitchIdentity()
        {
            var policy = new ImeFocusSessionPolicy();
            ImeFocusIdentity id = Identity(7);

            // 切换手势先于焦点探测显示：把该身份接纳为当前会话。
            policy.MarkDisplayed(id);

            long epoch = policy.BeginEvent();
            Assert.Equal(
                ImeFocusDecisionKind.Ignore,
                policy.OnProbeResult(epoch, id, true, hasCaret: true).Kind);
        }

        [Fact]
        public void Reset_InvalidatesEpochAndSession()
        {
            var policy = new ImeFocusSessionPolicy();
            long epoch = policy.BeginEvent();
            policy.MarkDisplayed(Identity(1));

            policy.Reset();

            Assert.False(policy.HasSession);
            Assert.False(policy.IsDisplayed);
            Assert.Equal(
                ImeFocusDecisionKind.Ignore,
                policy.OnProbeResult(epoch, Identity(1), true, hasCaret: true).Kind);
        }

        [Fact]
        public void Invalidate_ClearsLateResultButKeepsSession()
        {
            var policy = new ImeFocusSessionPolicy();
            ImeFocusIdentity id = Identity(1);
            long epoch = policy.BeginEvent();
            policy.MarkDisplayed(id);

            long newEpoch = policy.Invalidate();

            Assert.True(policy.HasSession);
            Assert.True(policy.IsDisplayed);
            Assert.Equal(
                ImeFocusDecisionKind.Ignore,
                policy.OnProbeResult(epoch, id, true, hasCaret: true).Kind);
            Assert.True(policy.IsCurrent(newEpoch));
        }
    }
}
