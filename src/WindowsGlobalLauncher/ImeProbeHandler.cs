using System;

using DrawingRectangle = System.Drawing.Rectangle;

namespace CommandLauncher
{
    /// <summary>
    /// 焦点/切换探测的纯处置逻辑（无 WPF、无静态运行态，可单测）。
    /// <para>
    /// 管理器把真实的隐藏/显示/重试操作与会话策略作为参数传入，本类只负责按原顺序
    /// 执行判定与状态更新。测试可用真实 <see cref="ImeFocusSessionPolicy"/> 加假操作
    /// 覆盖全部处置结果，不需要全局可变测试钩子，也不复制一份映射规则。
    /// </para>
    /// </summary>
    internal static class ImeProbeHandler
    {
        /// <summary>显示角标：参数与 <c>ImeIndicatorManager.ShowBadge</c> 一致，返回是否真正显示成功。</summary>
        internal delegate bool ShowBadgeAction(
            string text, DrawingRectangle caret, IntPtr dpiHwnd, ImeFocusIdentity identity);

        /// <summary>
        /// 焦点探测：按会话策略决定显示/隐藏/有限重试，并返回实际处置结果。
        /// <paramref name="lastLabel"/> / <paramref name="lastLabelIdentity"/> 是切换手势的
        /// 对比基线，行为与迁移前完全一致。
        /// </summary>
        internal static ImeHandleResult HandleFocus(
            ImeFocusSessionPolicy policy,
            long epoch,
            ImeFocusSnapshot snapshot,
            ImeQueryResult ime,
            Action hideBadge,
            ShowBadgeAction showBadge,
            Func<int, bool> scheduleRetry,
            ref string? lastLabel,
            ref ImeFocusIdentity? lastLabelIdentity)
        {
            ImeFocusDecision decision = policy.OnProbeResult(
                epoch, snapshot.Identity, snapshot.IsEditable, snapshot.Caret.HasValue);

            switch (decision.Kind)
            {
                case ImeFocusDecisionKind.Ignore:
                    return ImeHandleResult.Ignored;

                case ImeFocusDecisionKind.Hide:
                    hideBadge();
                    // 离开输入框后清除切换手势的对比基线：
                    // 重新进入（或随后切换输入法）应按新会话显示，而不是被旧标签吞掉。
                    lastLabel = null;
                    lastLabelIdentity = null;
                    return ImeHandleResult.Hidden;

                case ImeFocusDecisionKind.Retry:
                    // 焦点已换到另一个输入框、新插入符尚未就绪：旧角标属于上一个输入框，
                    // 立刻隐藏；同一身份的普通重试不会置位，因此不影响既有显示计时。
                    if (decision.HideOldBadge)
                        hideBadge();

                    return scheduleRetry(decision.RetryDelayMs)
                        ? ImeHandleResult.RetryScheduled
                        : ImeHandleResult.Ignored;

                case ImeFocusDecisionKind.Show:
                    if (!ime.Trustworthy)
                    {
                        // 查询失败不猜：隐藏旧角标（可能属于上一个焦点会话），保留会话状态。
                        hideBadge();
                        lastLabel = null;
                        lastLabelIdentity = null;
                        return ImeHandleResult.ImeRejected;
                    }

                    string text = ImeStatusResolver.ToText(ime.Display);
                    if (text.Length == 0 || snapshot.Caret is not { } caret)
                    {
                        hideBadge();
                        lastLabel = null;
                        lastLabelIdentity = null;
                        return ImeHandleResult.Hidden;
                    }

                    if (showBadge(text, caret, snapshot.DpiHwnd, snapshot.Identity))
                    {
                        policy.MarkDisplayed(snapshot.Identity);
                        lastLabel = text;
                        lastLabelIdentity = snapshot.Identity;
                        return ImeHandleResult.Shown;
                    }

                    return ImeHandleResult.ShowFailed;
            }

            // 枚举新增值时的防御性返回：不改变任何策略行为。
            return ImeHandleResult.Ignored;
        }

        /// <summary>
        /// 输入法切换手势：使用本次探测的新坐标，只在模式变化或新焦点会话时显示。
        /// 不复用任何旧坐标缓存；返回实际处置结果。
        /// </summary>
        internal static ImeHandleResult HandleSwitch(
            ImeFocusSessionPolicy policy,
            ImeFocusSnapshot snapshot,
            ImeQueryResult ime,
            Action hideBadge,
            ShowBadgeAction showBadge,
            ref string? lastLabel,
            ref ImeFocusIdentity? lastLabelIdentity)
        {
            if (!snapshot.IsEditable)
            {
                hideBadge();
                // 与获焦路径一致：焦点已不在输入框，清除会话，
                // 这样离开后重新进入同一输入框会按新会话重新提示。
                policy.ClearSession();
                // 非编辑焦点：清掉切换对比基线，重新进入输入框时按新会话显示。
                lastLabel = null;
                lastLabelIdentity = null;
                return ImeHandleResult.Hidden;
            }

            if (!ime.Trustworthy)
            {
                // 查询超时/失败不等于焦点离开输入框：只隐藏，不清会话。
                hideBadge();
                lastLabel = null;
                lastLabelIdentity = null;
                return ImeHandleResult.ImeRejected;
            }

            string text = ImeStatusResolver.ToText(ime.Display);
            if (text.Length == 0)
            {
                // 非中文输入法/布局没有可显示标签，同样不等于焦点离开：只隐藏。
                hideBadge();
                lastLabel = null;
                lastLabelIdentity = null;
                return ImeHandleResult.Hidden;
            }

            bool newIdentity = lastLabelIdentity is not { } last || !last.Equals(snapshot.Identity);
            bool modeChanged = text != lastLabel;

            lastLabel = text;
            lastLabelIdentity = snapshot.Identity;

            if (!newIdentity && !modeChanged)
                return ImeHandleResult.Ignored;

            // 取不到安全插入符就不显示（切换手势不做焦点会话的有限重试）。
            if (snapshot.Caret is not { } caret)
                return ImeHandleResult.Ignored;

            if (showBadge(text, caret, snapshot.DpiHwnd, snapshot.Identity))
            {
                policy.MarkDisplayed(snapshot.Identity);
                return ImeHandleResult.Shown;
            }

            return ImeHandleResult.ShowFailed;
        }
    }
}
