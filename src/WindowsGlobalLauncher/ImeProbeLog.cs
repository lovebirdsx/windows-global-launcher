using System;

namespace CommandLauncher
{
    /// <summary>
    /// 一次探测完成后管理器实际执行的处置（纯数据，只用于诊断日志与测试）。
    /// <para>
    /// 与策略决策（<see cref="ImeFocusDecisionKind"/>）刻意分开：策略的 Show 只表示
    /// 「可以显示」，只有 <see cref="ImeHandleResult.Shown"/> 才代表 <c>ShowBadge</c> 真正返回成功。
    /// </para>
    /// </summary>
    internal enum ImeHandleResult
    {
        /// <summary>探测完成但无需执行任何显示/隐藏动作（重复通知、切换手势无变化等）。</summary>
        Ignored = 0,

        /// <summary>隐藏了角标（离开输入框、无可显示标签、前台变化等）。</summary>
        Hidden = 1,

        /// <summary>安排了一次有限重试（同一焦点会话最多两次）。</summary>
        RetryScheduled = 2,

        /// <summary>角标已实际显示（仅 ShowBadge 返回 true）。</summary>
        Shown = 3,

        /// <summary>输入法状态不可信/查询失败，隐藏角标而不猜测。</summary>
        ImeRejected = 4,

        /// <summary>尝试显示角标但 ShowBadge 返回失败。</summary>
        ShowFailed = 5,
    }

    /// <summary>
    /// 输入法探测日志的纯格式化函数（无 I/O、无 Win32/UI 依赖，可单测）。
    /// <para>
    /// 隐私约束：只输出数值、布尔、内部枚举与 HRESULT；绝不输出 ClassName、Name、Value、
    /// 窗口标题、URL、完整 RuntimeId 或原始异常消息。
    /// </para>
    /// </summary>
    internal static class ImeProbeLog
    {
        /// <summary>未经过焦点/切换处理器的探测结果（过期、焦点未核实等）。</summary>
        public const string UnhandledResult = "Unhandled";

        /// <summary>把处置结果格式化为有限枚举文本；null 表示未进入处理器。</summary>
        public static string HandleText(ImeHandleResult? result) =>
            result?.ToString() ?? UnhandledResult;

        /// <summary>布尔三态：null=未能查询/未知。</summary>
        public static string BoolState(bool? value) =>
            value is null ? "未知" : value.Value ? "true" : "false";

        /// <summary>
        /// 只读属性三态，并区分「未查询」与「查询后未知」：
        /// 未尝试读取该模式 → 未查询；尝试了但属性仍为 null → 未知。
        /// </summary>
        public static string ReadOnlyState(bool? readOnly, bool probed)
        {
            if (!probed)
                return "未查询";
            if (readOnly is null)
                return "未知";
            return readOnly.Value ? "只读" : "可写";
        }

        /// <summary>
        /// 限频键：只由有限集合组成（成功/原因、坐标来源、控件类型、处置结果），
        /// 不包含请求号、时间戳、RuntimeId 等会持续变化的值。
        /// </summary>
        public static string BuildThrottleKey(
            bool success,
            ImeCaretSource source,
            string reason,
            int controlType,
            ImeHandleResult? result)
        {
            string prefix = success ? $"Ready:{source}" : reason;
            return $"{prefix}:ct{controlType}:{HandleText(result)}";
        }

        /// <summary>构造完整日志行：reason 保持纯原因，复合限频键不会出现在消息里。</summary>
        public static string BuildMessage(uint processId, string reason, string details) =>
            $"输入法探测：pid={processId}，reason={reason}，{details}";

        /// <summary>构造单条探测日志的明细（不读取任何 provider 文本字段）。</summary>
        public static string BuildDetails(
            long requestId,
            TimeSpan queueWait,
            TimeSpan queryTime,
            ImeProbeDiagnostic diagnostic,
            ImeHandleResult? handled)
        {
            return
                $"request={requestId}，source={diagnostic.Source}，probeReason={diagnostic.Reason}，" +
                $"controlType={diagnostic.ControlType}，runtimeId={diagnostic.HasRuntimeId}，" +
                $"isEditable={BoolState(diagnostic.IsEditable)}，" +
                $"valueProbed={diagnostic.ValuePatternProbed}，" +
                $"valueReadOnly={ReadOnlyState(diagnostic.ValueReadOnly, diagnostic.ValuePatternProbed)}，" +
                $"textProbed={diagnostic.TextPatternProbed}，" +
                $"textReadOnly={ReadOnlyState(diagnostic.TextReadOnly, diagnostic.TextPatternProbed)}，" +
                $"textPattern={diagnostic.HasTextPattern}，textPattern2={diagnostic.HasTextPattern2}，" +
                $"hasKeyboardFocus={BoolState(diagnostic.HasKeyboardFocus)}，" +
                $"isEnabled={BoolState(diagnostic.IsEnabled)}，" +
                $"rectCount={diagnostic.RectCount}，rectUsable={diagnostic.CaretRectUsable}，" +
                $"HRESULT=0x{diagnostic.HResult:X8}，result={HandleText(handled)}，" +
                $"排队={queueWait.TotalMilliseconds:F0}ms，查询={queryTime.TotalMilliseconds:F0}ms";
        }
    }
}
