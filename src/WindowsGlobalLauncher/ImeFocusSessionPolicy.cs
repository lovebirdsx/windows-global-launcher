using System;

namespace CommandLauncher
{
    /// <summary>一次焦点探测结果的处置方式。</summary>
    internal enum ImeFocusDecisionKind
    {
        /// <summary>不需要做任何事（重复通知、过期结果、重试预算耗尽等）。</summary>
        Ignore,

        /// <summary>隐藏角标（离开输入框/前台切换/不可编辑）。</summary>
        Hide,

        /// <summary>定位已就绪，可以显示。</summary>
        Show,

        /// <summary>插入符尚未就绪：在同一焦点会话内延迟重试。</summary>
        Retry,
    }

    /// <summary>焦点策略的决策（纯数据）。</summary>
    /// <param name="HideOldBadge">
    /// 处理本次决策前是否先隐藏当前仍可见的角标。仅用于「焦点已换到另一个输入框、
    /// 但新插入符尚未就绪（进入有限重试）」：旧角标属于上一个输入框，必须立即隐藏，
    /// 不能留在旧位置直到自动隐藏计时结束。同一身份的重复通知不得置位，
    /// 否则会打断既有的显示/隐藏计时。
    /// </param>
    internal readonly record struct ImeFocusDecision(
        ImeFocusDecisionKind Kind, int RetryDelayMs, bool HideOldBadge)
    {
        public static ImeFocusDecision Ignore => new(ImeFocusDecisionKind.Ignore, 0, false);
        public static ImeFocusDecision Hide => new(ImeFocusDecisionKind.Hide, 0, false);
        public static ImeFocusDecision Show => new(ImeFocusDecisionKind.Show, 0, false);
        public static ImeFocusDecision Retry(int delayMs, bool hideOldBadge = false) =>
            new(ImeFocusDecisionKind.Retry, delayMs, hideOldBadge);
    }

    /// <summary>
    /// 输入法提示的焦点会话策略（纯内存、无 Win32/UI 依赖、可单测）。
    /// <para>
    /// 两个概念刻意分开：
    /// <list type="bullet">
    /// <item>原始事件代次 <see cref="CurrentEpoch"/>：每次收到焦点/前台通知都会 +1，
    /// 用于立即作废尚未显示的旧探测结果（迟到的结果不得串到新焦点上）。</item>
    /// <item>焦点会话：由 <see cref="ImeFocusIdentity"/> 区分。只有身份真正变化才开启新会话；
    /// 同一身份的重复通知不得重新显示、不得重置自动隐藏计时或重试预算。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 离开输入框（不可编辑）会清除会话，因此「离开后重新进入原输入框」算新会话、会重新显示；
    /// 但同一输入框内的普通打字、焦点重复通知都不会重新弹出。
    /// </para>
    /// </summary>
    internal sealed class ImeFocusSessionPolicy
    {
        /// <summary>插入符未就绪时的最多重试次数（总计两次）。</summary>
        public const int MaxRetries = 2;

        /// <summary>第一次重试延迟。</summary>
        public const int FirstRetryDelayMs = 100;

        /// <summary>第二次重试延迟。</summary>
        public const int SecondRetryDelayMs = 250;

        private long _epoch;
        private ImeFocusIdentity? _identity;
        private bool _displayed;
        private bool _gaveUp;
        private int _retryCount;

        /// <summary>当前原始事件代次（每次焦点/前台通知递增）。</summary>
        public long CurrentEpoch => _epoch;

        /// <summary>当前是否存在焦点会话。</summary>
        public bool HasSession => _identity.HasValue;

        /// <summary>当前会话是否已经显示过（用于抑制重复通知）。</summary>
        public bool IsDisplayed => _displayed;

        /// <summary>收到一次原始焦点事件：递增代次并返回新值。</summary>
        public long BeginEvent() => ++_epoch;

        /// <summary>只作废在途结果、不动会话（截图/隐藏等场景）。</summary>
        public long Invalidate() => ++_epoch;

        /// <summary>清除会话状态（不改变代次）。</summary>
        public void ClearSession()
        {
            _identity = null;
            _displayed = false;
            _gaveUp = false;
            _retryCount = 0;
        }

        /// <summary>
        /// 前台窗口变化：立即开启新一代、清除旧会话。
        /// 返回新的代次，调用方据此作废在途探测。
        /// </summary>
        public long OnForegroundChanged()
        {
            long epoch = ++_epoch;
            ClearSession();
            return epoch;
        }

        /// <summary>
        /// 一次探测完成后的策略判定。
        /// </summary>
        /// <param name="epoch">发起探测时的代次；与当前代次不一致说明结果已过期。</param>
        /// <param name="identity">探测到的焦点身份；null 表示无法可靠识别。</param>
        /// <param name="isEditable">焦点是否是可编辑输入控件。</param>
        /// <param name="hasCaret">是否拿到了可用的插入符矩形。</param>
        public ImeFocusDecision OnProbeResult(
            long epoch, ImeFocusIdentity? identity, bool isEditable, bool hasCaret)
        {
            if (epoch != _epoch)
                return ImeFocusDecision.Ignore;

            if (identity is not { } id || !isEditable)
            {
                // 离开输入框/焦点落到按钮、菜单、只读文本：隐藏并清除会话，
                // 以后重新进入原输入框会作为新会话重新显示。
                ClearSession();
                return ImeFocusDecision.Hide;
            }

            if (_identity is not { } session || !session.Equals(id))
            {
                // 真正的焦点身份变化：开启新会话，重试预算从头开始。
                _identity = id;
                _displayed = false;
                _gaveUp = false;
                _retryCount = 0;

                if (hasCaret)
                    return ImeFocusDecision.Show;

                // 新身份但插入符未就绪：旧角标属于上一个输入框，先隐藏再重试。
                _retryCount = 1;
                return ImeFocusDecision.Retry(FirstRetryDelayMs, hideOldBadge: true);
            }

            // 同一焦点会话的重复通知：已经显示过就不再重新显示、不重置隐藏计时。
            if (_displayed)
                return ImeFocusDecision.Ignore;

            if (hasCaret)
                return ImeFocusDecision.Show;

            if (_gaveUp)
                return ImeFocusDecision.Ignore;

            if (_retryCount < MaxRetries)
            {
                _retryCount++;
                return ImeFocusDecision.Retry(
                    _retryCount == 1 ? FirstRetryDelayMs : SecondRetryDelayMs);
            }

            // 重试预算耗尽：同一会话内不再继续尝试，避免每次焦点通知都发起查询。
            _gaveUp = true;
            return ImeFocusDecision.Ignore;
        }

        /// <summary>
        /// 记录某身份已经显示过（由管理器在实际显示成功后调用）。
        /// 切换手势可能先于焦点探测显示，此时顺便把该身份接纳为当前会话。
        /// </summary>
        public void MarkDisplayed(ImeFocusIdentity identity)
        {
            if (_identity is not { } current || !current.Equals(identity))
            {
                _identity = identity;
                _retryCount = 0;
            }

            _displayed = true;
            _gaveUp = false;
        }

        /// <summary>禁用/暂停/退出：作废代次并清空全部会话状态。</summary>
        public void Reset()
        {
            _epoch++;
            ClearSession();
        }

        /// <summary>代次是否仍是当前代次（UI 回调二次核对用）。</summary>
        public bool IsCurrent(long epoch) => epoch == _epoch;
    }
}
