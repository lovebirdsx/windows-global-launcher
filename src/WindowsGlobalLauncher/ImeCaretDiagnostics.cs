namespace CommandLauncher
{
    /// <summary>
    /// 输入法提示最终采用的插入符坐标来源（纯数据）。
    /// <para>
    /// Win32：系统 GUITHREADINFO 插入符；Uia：UIA TextPattern/TextPattern2 的退化范围矩形；
    /// Msaa：MSAA OBJID_CARET 自绘光标；TerminalCell：Windows Terminal 专用适配得到的
    /// 光标所在字符格子（保留完整格子宽度，不压成 1px）。
    /// </para>
    /// </summary>
    internal enum ImeCaretSource
    {
        None = 0,
        Win32 = 1,
        Uia = 2,
        Msaa = 3,
        TerminalCell = 4,
    }

    /// <summary>
    /// 一次焦点/插入符探测的结论原因（纯数据，只用于诊断与限频日志）。
    /// <para>
    /// Reason=None 表示成功拿到（或明确判定不需要）坐标；其余值说明被拒绝的具体环节。
    /// 该枚举刻意不承载任何输入内容或可变长 provider 文本。
    /// </para>
    /// </summary>
    internal enum ImeProbeReason
    {
        /// <summary>成功，未发生拒绝。</summary>
        None = 0,

        /// <summary>调用方没有前台窗口句柄（锁屏/桌面切换等）。</summary>
        NoForeground = 1,

        /// <summary>查询期间前台窗口已变化。</summary>
        ForegroundChanged = 2,

        /// <summary>前台有效但没有 Win32 输入焦点（焦点在菜单/桌面等）。</summary>
        NoFocus = 3,

        /// <summary>焦点明确不可编辑（按钮、菜单、只读文本、密码转换等）。</summary>
        NotEditable = 4,

        /// <summary>控件疑似可编辑但证据不足（不放行）。</summary>
        EditableUnknown = 5,

        /// <summary>非原生控件没有 UIA RuntimeId，无法区分同一 HWND 内的输入框。</summary>
        IdentityMissing = 6,

        /// <summary>UIA 客户端/焦点元素查询失败或异常。</summary>
        UiaUnavailable = 7,

        /// <summary>UIA 查询前后焦点元素身份不一致（含同一 HWND 内 A→B 切换）。</summary>
        UiaIdentityChanged = 8,

        /// <summary>控件不提供 TextPattern/TextPattern2。</summary>
        PatternMissing = 9,

        /// <summary>可编辑，但插入符/光标格子尚未就绪。</summary>
        CaretNotReady = 10,

        /// <summary>候选插入符矩形被可信性/可视范围校验拒绝。</summary>
        CaretRejected = 11,

        /// <summary>原生 Edit/RichEdit 带 ES_READONLY。</summary>
        NativeReadOnly = 12,

        /// <summary>MSAA 光标对象不可用（无 OBJID_CARET/配额/COM 失败）。</summary>
        MsaaUnavailable = 13,

        /// <summary>MSAA 光标对象存在但角色、状态、尺寸或边界校验未通过。</summary>
        MsaaRejected = 14,

        /// <summary>Windows Terminal 专用适配的条件不满足或光标格子不可信。</summary>
        TerminalRejected = 15,

        /// <summary>探测路径内部异常（已吞掉，绝不外抛）。</summary>
        ProbeException = 16,

        /// <summary>
        /// Win32 实际焦点身份（HWND/线程/进程）在查询期间发生变化。
        /// 与 <see cref="UiaIdentityChanged"/> 区分：后者指同一 HWND 内 UIA 焦点元素
        /// （RuntimeId）变化，本值指 Win32 焦点窗口本身已换到别处。
        /// 数值追加在末尾以保持既有枚举值稳定。
        /// </summary>
        FocusChanged = 17,
    }

    /// <summary>
    /// 一次焦点/插入符探测的纯诊断数据。只记录进程号、控件类型/类名、模式可用性、
    /// 矩形形态与 HRESULT 等元信息；绝不包含 Name、Value、窗口标题、URL 或输入文本。
    /// </summary>
    /// <param name="Reason">最终结论原因（None=成功）。</param>
    /// <param name="Source">最终采用的坐标来源。</param>
    /// <param name="ProcessId">目标焦点窗口/元素的进程 ID（失败时尽量填充）。</param>
    /// <param name="ControlType">UIA ControlType ID；0 表示未查询/原生路径。</param>
    /// <param name="ClassName">UIA ClassName（不是窗口标题，也不含输入内容）。</param>
    /// <param name="HasRuntimeId">UIA 焦点元素是否提供 RuntimeId。</param>
    /// <param name="HasTextPattern">是否取得 TextPattern。</param>
    /// <param name="HasTextPattern2">是否取得 TextPattern2。</param>
    /// <param name="CaretRectUsable">是否最终拿到可用插入符矩形。</param>
    /// <param name="RectCount">provider 返回的边界矩形数量（0=无/-1=无法解析）。</param>
    /// <param name="IsPassword">焦点元素是否报告为密码控件。</param>
    /// <param name="HResult">失败环节的 HRESULT；0 表示无特定失败码。</param>
    /// <param name="IsEditable">最终可编辑结论；null 表示未走到该判断（原生路径按需填写）。</param>
    /// <param name="ValuePatternProbed">是否实际尝试读取 ValuePattern（区别于读取后未知）。</param>
    /// <param name="TextPatternProbed">是否实际尝试取得文本模式（区别于 provider 不支持）。</param>
    /// <param name="ValueReadOnly">ValuePattern.IsReadOnly 三态；null 表示未查询或查询后未知。</param>
    /// <param name="TextReadOnly">文本范围 IsReadOnly 三态；null 表示未查询或查询后未知。</param>
    /// <param name="HasKeyboardFocus">UIA HasKeyboardFocus；null 表示未查询。</param>
    /// <param name="IsEnabled">UIA IsEnabled；null 表示未查询。</param>
    internal sealed record ImeProbeDiagnostic(
        ImeProbeReason Reason,
        ImeCaretSource Source,
        uint ProcessId,
        int ControlType = 0,
        string? ClassName = null,
        bool HasRuntimeId = false,
        bool HasTextPattern = false,
        bool HasTextPattern2 = false,
        bool CaretRectUsable = false,
        int RectCount = 0,
        bool IsPassword = false,
        int HResult = 0,
        bool? IsEditable = null,
        bool ValuePatternProbed = false,
        bool TextPatternProbed = false,
        bool? ValueReadOnly = null,
        bool? TextReadOnly = null,
        bool? HasKeyboardFocus = null,
        bool? IsEnabled = null)
    {
        /// <summary>构造只含原因/来源/进程的最小诊断。</summary>
        public static ImeProbeDiagnostic For(
            ImeProbeReason reason,
            ImeCaretSource source = ImeCaretSource.None,
            uint processId = 0)
        {
            return new ImeProbeDiagnostic(reason, source, processId);
        }

        /// <summary>是否成功（Reason=None）。</summary>
        public bool Succeeded => Reason == ImeProbeReason.None;
    }
}
