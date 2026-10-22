using System;
using System.Drawing;

namespace CommandLauncher
{
    /// <summary>
    /// 文本范围读取的最小后端接口（生产为 UIA COM 包装，测试为假后端）。
    /// 只暴露与插入符判定相关的端点比较、只读属性与边界矩形，不读取任何文本。
    /// </summary>
    internal interface ITextRangeBackend : IDisposable
    {
        /// <summary>CompareEndpoints(Start, this, End)。</summary>
        bool TryCompareStartToEnd(out int compare);

        /// <summary>读取范围 IsReadOnly 属性；null 表示 provider 未提供。</summary>
        bool? GetIsReadOnly();

        /// <summary>读取边界矩形 SAFEARRAY（每 4 个值为一个矩形）。</summary>
        bool TryGetBoundingRectangles(out double[]? bounds);

        /// <summary>克隆范围；失败返回 null。</summary>
        ITextRangeBackend? Clone();

        /// <summary>按 TextUnit_Character 展开到所在字符。</summary>
        bool TryExpandToEnclosingCharacter();

        /// <summary>CompareEndpoints(Start, this, Start, other)。</summary>
        bool TryCompareStartToStart(ITextRangeBackend other, out int compare);
    }

    /// <summary>
    /// 文本模式读取的最小后端接口：只负责提供 TextPattern2 的 caret range
    /// 与 TextPattern 的 selection；具体接受条件全部在 <see cref="CaretBackendCore"/>。
    /// </summary>
    internal interface ITextPatternBackend : IDisposable
    {
        bool HasPattern2 { get; }

        bool HasPattern { get; }

        /// <summary>调用 TextPattern2.GetCaretRange；模式不可用或调用失败返回 false。</summary>
        bool TryGetCaretRange(out bool isActive, out ITextRangeBackend? range);

        /// <summary>调用 TextPattern.GetSelection；模式不可用或调用失败返回 false。</summary>
        bool TryGetSelection(out int rangeCount, out ITextRangeBackend? range);
    }

    /// <summary>UIA 焦点元素的一次读取快照（纯数据，不含 COM 对象与任何文本）。</summary>
    internal readonly record struct UiaElementSnapshot(
        string? RuntimeId,
        uint ProcessId,
        IntPtr Hwnd,
        int ControlType,
        string? ClassName,
        Rectangle? Bounds,
        bool HasKeyboardFocus,
        bool IsKeyboardFocusable,
        bool IsEnabled,
        bool IsPassword,
        string? ProcessName);

    /// <summary>MSAA 自绘光标的一次采样结果（纯数据）。</summary>
    internal readonly record struct MsaaCaretReading(
        bool Success,
        Rectangle Caret,
        ImeProbeReason Reason,
        int HResult);

    /// <summary>
    /// 一次焦点/插入符探测所需的全部后端操作。生产实现是 UIA/MSAA COM 适配器，
    /// 测试实现是假后端；Core 只依赖该接口，不直接接触 COM。
    /// </summary>
    internal interface IUiaFocusBackend : IDisposable
    {
        /// <summary>取窗口所属进程 ID（诊断用）。</summary>
        uint GetProcessId(IntPtr hwnd);

        /// <summary>当前前台窗口。</summary>
        IntPtr GetForeground();

        /// <summary>读取当前 UIA 焦点元素快照；失败时填充 HRESULT。</summary>
        bool TryReadFocusElement(out UiaElementSnapshot element, out int hresult);

        /// <summary>
        /// 再次读取当前焦点元素并与首次读取的元素比较。
        /// CompareElements 结果与当前 RuntimeId 一并返回，由 Core 统一判定身份。
        /// </summary>
        bool TryReadFocusIdentity(
            out int compareHr, out int compareSame, out string? runtimeId, out int hresult);

        /// <summary>读取焦点元素的 ValuePattern.IsReadOnly；不可用返回 null。</summary>
        bool? ReadValuePatternReadOnly();

        /// <summary>取得文本模式后端；调用方负责 Dispose。</summary>
        ITextPatternBackend? GetTextPatternBackend();

        /// <summary>从实际焦点 HWND 采样 MSAA 自绘光标。</summary>
        MsaaCaretReading TryReadMsaaCaret(IntPtr focusHwnd, Rectangle? elementBounds);
    }

    /// <summary>文本模式读取结果（纯数据）。ReadOnly=null 表示未知；RectCount=-1 表示无法解析。</summary>
    internal readonly record struct TextCaretReading(
        bool PatternAvailable,
        bool? ReadOnly,
        bool CaretActive,
        Rectangle? Caret,
        ImeCaretSource Source,
        bool Pattern2Available,
        int RectCount,
        ImeProbeReason Reason);

    /// <summary>
    /// 后端读取/编排的唯一 Core：生产 COM 适配器与测试假后端都走这里的同一套
    /// 可编辑性判定、Terminal 严格流程、MSAA 启用条件与前后身份校验。
    /// </summary>
    internal static class CaretBackendCore
    {
        /// <summary>
        /// 焦点读取与坐标合成的完整编排。查询前后都核对前台窗口与焦点元素身份，
        /// MSAA 采样位于核对区间之内；任何异常都只转成 ProbeException，绝不外抛。
        /// </summary>
        internal static UiaFocusReading? QueryCore(
            IntPtr expectedForeground,
            IntPtr focusHwnd,
            bool allowMsaaCaret,
            IUiaFocusBackend backend,
            out ImeProbeDiagnostic diagnostic)
        {
            diagnostic = ImeProbeDiagnostic.For(ImeProbeReason.NoForeground);

            if (expectedForeground == IntPtr.Zero)
                return null;

            uint targetPid = backend.GetProcessId(expectedForeground);
            diagnostic = ImeProbeDiagnostic.For(
                ImeProbeReason.NoForeground, ImeCaretSource.None, targetPid);

            try
            {
                if (backend.GetForeground() != expectedForeground)
                {
                    diagnostic = diagnostic with { Reason = ImeProbeReason.ForegroundChanged };
                    return null;
                }

                if (!backend.TryReadFocusElement(out UiaElementSnapshot element, out int focusHr))
                {
                    diagnostic = diagnostic with
                    {
                        Reason = ImeProbeReason.UiaUnavailable,
                        HResult = focusHr,
                    };
                    return null;
                }

                bool isEdit = element.ControlType == UiaCaretReader.UIA_EditControlTypeId;
                bool isDocument = element.ControlType == UiaCaretReader.UIA_DocumentControlTypeId;
                bool isComboBox = element.ControlType == UiaCaretReader.UIA_ComboBoxControlTypeId;
                bool isCustom = element.ControlType == UiaCaretReader.UIA_CustomControlTypeId;

                // Windows Terminal 专用适配的限定条件：Text + TermControl + WindowsTerminal 进程。
                bool terminalCandidate = element.ControlType == UiaCaretReader.UIA_TextControlTypeId
                    && string.Equals(
                        element.ClassName, UiaCaretReader.TerminalControlClassName, StringComparison.Ordinal);
                bool isTerminal = terminalCandidate
                    && UiaCaretReader.IsTerminalProvider(
                        element.ControlType, element.ClassName, element.ProcessName);

                // 身份/焦点等元数据一采到就落进诊断；后续任何提前返回都用 with 保留，
                // 避免「身份变化 / 前台变化」把已采样的控件能力信息一起丢掉。
                diagnostic = diagnostic with
                {
                    ProcessId = element.ProcessId,
                    ControlType = element.ControlType,
                    ClassName = element.ClassName,
                    HasRuntimeId = !string.IsNullOrWhiteSpace(element.RuntimeId),
                    IsPassword = element.IsPassword,
                    HasKeyboardFocus = element.HasKeyboardFocus,
                    IsEnabled = element.IsEnabled,
                };

                // ComboBox/Custom 只是候选类型而非可编辑证据：真实焦点、启用、非密码、
                // 非空 RuntimeId 缺一不可。前置条件不满足时绝不读取模式，
                // 否则诊断里的 textPattern=false 会被误读成 provider 不支持。
                bool strictCandidate = isComboBox || isCustom;
                bool preconditionsMet = !strictCandidate
                    || (element.HasKeyboardFocus
                        && element.IsEnabled
                        && !element.IsPassword
                        && !string.IsNullOrWhiteSpace(element.RuntimeId));

                bool? valueReadOnly = null;
                TextCaretReading text = default;
                if ((isEdit || isDocument || strictCandidate || isTerminal) && preconditionsMet)
                {
                    // 先记「已尝试」，即使调用本身抛异常也保留该语义。
                    diagnostic = diagnostic with { ValuePatternProbed = true };

                    // Edit 常见 ValuePattern 只读；Document/ComboBox/Custom 通常不支持。
                    // 明确只读时不再读取文本范围。
                    valueReadOnly = backend.ReadValuePatternReadOnly();

                    // 读取成功后立即落盘，后续文本查询失败也不会丢已采样的结果。
                    diagnostic = diagnostic with { ValueReadOnly = valueReadOnly };

                    if (valueReadOnly != true && !(isTerminal && element.IsPassword))
                    {
                        diagnostic = diagnostic with { TextPatternProbed = true };
                        using (ITextPatternBackend? textBackend = backend.GetTextPatternBackend())
                        {
                            text = textBackend == null
                                ? new TextCaretReading(
                                    PatternAvailable: false, ReadOnly: null, CaretActive: false,
                                    Caret: null, Source: ImeCaretSource.None, Pattern2Available: false,
                                    RectCount: 0, Reason: ImeProbeReason.PatternMissing)
                                : (isTerminal
                                    ? ReadTerminalGeometryCore(textBackend, element.Bounds)
                                    : ReadTextGeometryCore(textBackend, element.IsPassword));
                        }

                        diagnostic = diagnostic with
                        {
                            TextReadOnly = text.ReadOnly,
                            HasTextPattern = text.PatternAvailable,
                            HasTextPattern2 = text.Pattern2Available,
                        };
                    }
                    else if (isTerminal)
                    {
                        // 终端密码框/明确只读：绝不读取文本范围，也不进入格子适配。
                        text = new TextCaretReading(
                            PatternAvailable: false, ReadOnly: true, CaretActive: false,
                            Caret: null, Source: ImeCaretSource.None, Pattern2Available: false,
                            RectCount: 0, Reason: ImeProbeReason.TerminalRejected);

                        diagnostic = diagnostic with
                        {
                            TextReadOnly = text.ReadOnly,
                            HasTextPattern = text.PatternAvailable,
                            HasTextPattern2 = text.Pattern2Available,
                        };
                    }
                }

                bool editable = UiaCaretReader.DecideProviderEditable(
                    element.ControlType, isTerminal, element.HasKeyboardFocus,
                    element.IsKeyboardFocusable, element.IsEnabled, element.IsPassword,
                    valueReadOnly, text.PatternAvailable, text.ReadOnly, text.CaretActive,
                    // 新类型要求「非空」而不是「非 null」；空白 RuntimeId 无法证明身份。
                    hasRuntimeId: !string.IsNullOrWhiteSpace(element.RuntimeId));

                // 先落定 IsEditable：后续身份/前台核对失败时仍能保留这一判断。
                diagnostic = diagnostic with { IsEditable = editable };

                // MSAA：仅用于「已由 UIA 证明可编辑、非 Terminal、RuntimeId 稳定、且没有可信
                // Win32/UIA 矩形」的控件。采样位于前后身份核对之间，A→B 切换会整体作废。
                bool triedMsaa = false;
                ImeProbeReason msaaReason = ImeProbeReason.None;
                int caretHResult = 0;
                if (UiaCaretReader.ShouldTryMsaa(
                        editable, isTerminal, !string.IsNullOrWhiteSpace(element.RuntimeId),
                        text.Caret.HasValue, allowMsaaCaret))
                {
                    triedMsaa = true;
                    MsaaCaretReading msaa = backend.TryReadMsaaCaret(focusHwnd, element.Bounds);
                    caretHResult = msaa.HResult;

                    if (msaa.Success)
                    {
                        text = text with
                        {
                            Caret = msaa.Caret,
                            Source = ImeCaretSource.Msaa,
                            CaretActive = true,
                            RectCount = 1,
                            Reason = ImeProbeReason.None,
                        };
                    }
                    else
                    {
                        // 不覆盖已有 UIA 矩形（本分支本就只在无矩形时进入），只记录原因。
                        msaaReason = msaa.Reason;
                        text = text with { Reason = msaaReason };
                    }
                }

                Rectangle? caret = editable ? text.Caret : null;
                ImeCaretSource source = caret.HasValue ? text.Source : ImeCaretSource.None;

                // 查询结束前再次核对：前台窗口 + 焦点元素身份（同一 HWND 内 A→B 也能识别）
                if (backend.GetForeground() != expectedForeground)
                {
                    diagnostic = diagnostic with { Reason = ImeProbeReason.ForegroundChanged };
                    return null;
                }

                if (!backend.TryReadFocusIdentity(
                        out int compareHr, out int compareSame,
                        out string? runtimeId2, out int identityHr))
                {
                    // 身份「读取失败」与「读到了另一个元素」必须区分：
                    // 前者是探测链路不可用，保留已采样的模式/焦点诊断。
                    diagnostic = diagnostic with
                    {
                        Reason = ImeProbeReason.UiaUnavailable,
                        HResult = identityHr,
                    };
                    return null;
                }

                if (!UiaCaretReader.IsSameFocusIdentity(
                        compareHr, compareSame, element.RuntimeId, runtimeId2))
                {
                    // CompareElements 不可用且 RuntimeId 不足，或同一 HWND 内已换成另一个控件
                    diagnostic = diagnostic with { Reason = ImeProbeReason.UiaIdentityChanged };
                    return null;
                }

                if (element.RuntimeId != null && runtimeId2 != null
                    && !string.Equals(element.RuntimeId, runtimeId2, StringComparison.Ordinal))
                {
                    diagnostic = diagnostic with { Reason = ImeProbeReason.UiaIdentityChanged };
                    return null;
                }

                if (backend.GetForeground() != expectedForeground)
                {
                    diagnostic = diagnostic with { Reason = ImeProbeReason.ForegroundChanged };
                    return null;
                }

                ImeProbeReason reason;
                if (!editable)
                {
                    reason = DecideRejectedReason(
                        element, isEdit, isDocument, isComboBox, isCustom, isTerminal,
                        valueReadOnly, text);
                }
                else if (caret.HasValue)
                {
                    reason = ImeProbeReason.None;
                }
                else if (triedMsaa)
                {
                    reason = msaaReason;
                }
                else
                {
                    reason = text.Reason != ImeProbeReason.None
                        ? text.Reason
                        : ImeProbeReason.CaretNotReady;
                }

                diagnostic = diagnostic with
                {
                    Reason = reason,
                    Source = source,
                    CaretRectUsable = caret.HasValue,
                    RectCount = text.RectCount,
                    HResult = caretHResult,
                };

                return new UiaFocusReading(
                    element.RuntimeId, element.ProcessId, element.Hwnd,
                    editable, element.IsPassword, caret,
                    source, element.ControlType, element.ClassName,
                    text.PatternAvailable, text.Pattern2Available, text.RectCount,
                    isTerminal, reason);
            }
            catch (Exception ex)
            {
                // provider 卡顿/异常/COM 失败一律视为不可用，只保留 HRESULT，不记录原始消息。
                diagnostic = diagnostic with
                {
                    Reason = ImeProbeReason.ProbeException,
                    HResult = ex.HResult,
                };
                return null;
            }
        }

        /// <summary>
        /// 不可编辑时的原因分类：明确只读/禁用/不支持类型给出确定拒绝，
        /// 候选类型缺正向证据时归为 <see cref="ImeProbeReason.EditableUnknown"/>
        /// （仅用于诊断，不显示角标、也不新增重试）。
        /// </summary>
        private static ImeProbeReason DecideRejectedReason(
            in UiaElementSnapshot element,
            bool isEdit, bool isDocument, bool isComboBox, bool isCustom, bool isTerminal,
            bool? valueReadOnly, in TextCaretReading text)
        {
            // 按钮/菜单/普通 Text 等不支持的类型：明确拒绝，不扩大到任意容器。
            if (!isEdit && !isDocument && !isComboBox && !isCustom && !isTerminal)
                return ImeProbeReason.NotEditable;

            // Edit/Terminal 保持既有原因口径：优先采用文本读取阶段给出的具体原因。
            if (isEdit || isTerminal)
            {
                return text.Reason != ImeProbeReason.None
                    ? text.Reason
                    : ImeProbeReason.NotEditable;
            }

            if (isDocument)
            {
                bool focusState = element.HasKeyboardFocus || element.IsKeyboardFocusable;
                if (!focusState || !element.IsEnabled)
                    return ImeProbeReason.NotEditable;

                // 任意已采样的只读 true 都是明确拒绝。
                if (valueReadOnly == true || text.ReadOnly == true)
                    return ImeProbeReason.NotEditable;

                // 既没有 ValuePattern=false，也没有原有文本证据：未知，不猜。
                return ImeProbeReason.EditableUnknown;
            }

            // ComboBox / Custom：先给出明确的前置拒绝，再把「证据不足」归为未知。
            if (!element.IsEnabled || element.IsPassword)
                return ImeProbeReason.NotEditable;
            if (string.IsNullOrWhiteSpace(element.RuntimeId))
                return ImeProbeReason.IdentityMissing;
            if (!element.HasKeyboardFocus)
                return ImeProbeReason.EditableUnknown;
            if (valueReadOnly == true || text.ReadOnly == true)
                return ImeProbeReason.NotEditable;
            return ImeProbeReason.EditableUnknown;
        }

        /// <summary>
        /// 通用文本输入控件的插入符读取：Pattern2 优先，失败/未就绪时回退到
        /// TextPattern selection（只接受可证明退化的单选区）。
        /// </summary>
        internal static TextCaretReading ReadTextGeometryCore(
            ITextPatternBackend source, bool isPassword)
        {
            TextCaretReading pattern2Result = default;
            bool pattern2Available = source.HasPattern2;

            try
            {
                if (source.TryGetCaretRange(out bool isActive, out ITextRangeBackend? caretRange))
                {
                    using (caretRange)
                    {
                        if (isActive && caretRange != null)
                        {
                            var result = ReadRangeCaretCore(
                                caretRange, isPassword, requireDegenerate: true);
                            pattern2Result = new TextCaretReading(
                                true, result.ReadOnly, true, result.Caret,
                                ImeCaretSource.Uia, true, result.RectCount, result.Reason);

                            // 明确只读是最终否决，不能被另一模式的矛盾结果覆盖。
                            if (result.ReadOnly == true || result.Caret.HasValue)
                                return pattern2Result;
                        }
                    }
                }
            }
            catch
            {
                // Pattern2 读取异常：保留默认结果，继续回退到 TextPattern selection，
                // 与既有「Pattern2 失败不阻断旧路径」的行为一致。
            }

            // 2) TextPattern 回退：只接受 CompareEndpoints 证明为退化的单选区。
            //    TextPattern2 active 但矩形为空时也会走到这里；仍无矩形则交给上层尝试 MSAA。
            if (!source.HasPattern)
            {
                // Pattern2 已可用但基接口取不到属异常；有 Pattern2 无矩形时按“未就绪”处理。
                return new TextCaretReading(
                    pattern2Available, pattern2Result.ReadOnly, pattern2Result.CaretActive, null,
                    ImeCaretSource.None, pattern2Available, pattern2Result.RectCount,
                    pattern2Available ? ImeProbeReason.CaretNotReady : ImeProbeReason.PatternMissing);
            }

            try
            {
                if (!source.TryGetSelection(out int count, out ITextRangeBackend? range))
                {
                    return new TextCaretReading(
                        true, pattern2Result.ReadOnly, pattern2Result.CaretActive, null,
                        ImeCaretSource.None, pattern2Available, 0, ImeProbeReason.CaretNotReady);
                }

                using (range)
                {
                    if (count != 1)
                    {
                        // 多选区/无选区：不猜 active end
                        return new TextCaretReading(
                            true, pattern2Result.ReadOnly, pattern2Result.CaretActive, null,
                            ImeCaretSource.None, pattern2Available, 0, ImeProbeReason.CaretRejected);
                    }

                    if (range == null)
                    {
                        return new TextCaretReading(
                            true, pattern2Result.ReadOnly, pattern2Result.CaretActive, null,
                            ImeCaretSource.None, pattern2Available, 0, ImeProbeReason.CaretNotReady);
                    }

                    // 可编辑证据与坐标可用性分开：几何/端点失败不能抹掉范围的只读属性。
                    var result = ReadRangeCaretCore(range, isPassword, requireDegenerate: true);
                    return new TextCaretReading(
                        true,
                        result.ReadOnly ?? pattern2Result.ReadOnly,
                        pattern2Result.CaretActive,
                        result.Caret,
                        ImeCaretSource.Uia,
                        pattern2Available,
                        result.RectCount,
                        result.Reason);
                }
            }
            catch
            {
                return new TextCaretReading(
                    true, pattern2Result.ReadOnly, pattern2Result.CaretActive, null,
                    ImeCaretSource.None, pattern2Available, 0, ImeProbeReason.CaretNotReady);
            }
        }

        /// <summary>
        /// Windows Terminal 专用光标格子适配。只在 Text + TermControl + WindowsTerminal
        /// 且范围明确 IsReadOnly=false 时进入：单个退化 selection → 克隆 → 按 Character
        /// 展开（不改变真实选择、不读文本）→ 展开非退化且起点等于原 caret 起点
        /// （绝不接受 end 匹配）→ 单个落在元素可视边界内的格子矩形。
        /// </summary>
        internal static TextCaretReading ReadTerminalGeometryCore(
            ITextPatternBackend source, Rectangle? elementBounds)
        {
            if (!source.HasPattern)
            {
                return new TextCaretReading(
                    false, null, false, null, ImeCaretSource.None, false,
                    0, ImeProbeReason.PatternMissing);
            }

            try
            {
                if (!source.TryGetSelection(out int count, out ITextRangeBackend? range))
                {
                    return new TextCaretReading(
                        true, null, false, null, ImeCaretSource.None, false,
                        0, ImeProbeReason.CaretNotReady);
                }

                using (range)
                {
                    // 必须恰好一个范围；多选区不折叠、不猜
                    if (count != 1 || range == null)
                    {
                        return new TextCaretReading(
                            true, null, false, null, ImeCaretSource.None, false,
                            0, ImeProbeReason.TerminalRejected);
                    }

                    // 1) 原光标范围必须严格退化；非空选择绝不折叠、不取末端
                    if (!range.TryCompareStartToEnd(out int originalSpan) || originalSpan != 0)
                    {
                        return new TextCaretReading(
                            true, null, false, null, ImeCaretSource.None, false,
                            0, ImeProbeReason.TerminalRejected);
                    }

                    // 2) 明确非只读才继续；未知不放行
                    bool? readOnly = range.GetIsReadOnly();
                    if (readOnly != false)
                    {
                        return new TextCaretReading(
                            true, readOnly, false, null, ImeCaretSource.None, false,
                            0, ImeProbeReason.TerminalRejected);
                    }

                    // 3) 克隆后按字符展开：只改克隆件，不改变用户的真实选择，也不读取文本
                    using (ITextRangeBackend? expanded = range.Clone())
                    {
                        if (expanded == null)
                        {
                            return new TextCaretReading(
                                true, false, false, null, ImeCaretSource.None, false,
                                0, ImeProbeReason.TerminalRejected);
                        }

                        if (!expanded.TryExpandToEnclosingCharacter())
                        {
                            return new TextCaretReading(
                                true, false, false, null, ImeCaretSource.None, false,
                                0, ImeProbeReason.TerminalRejected);
                        }

                        // 4) 展开后必须非退化
                        if (!expanded.TryCompareStartToEnd(out int expandedSpan) || expandedSpan == 0)
                        {
                            return new TextCaretReading(
                                true, false, false, null, ImeCaretSource.None, false,
                                0, ImeProbeReason.TerminalRejected);
                        }

                        // 5) 关键：展开起点必须与原退化光标起点一致。
                        //    绝不比较 end——文档末端向前夹取会变成“前一个字符”，必须拒绝。
                        if (!expanded.TryCompareStartToStart(range, out int startCompare)
                            || startCompare != 0)
                        {
                            return new TextCaretReading(
                                true, false, false, null, ImeCaretSource.None, false,
                                0, ImeProbeReason.TerminalRejected);
                        }

                        if (!expanded.TryGetBoundingRectangles(out double[]? bounds))
                        {
                            return new TextCaretReading(
                                true, false, false, null, ImeCaretSource.None, false,
                                -1, ImeProbeReason.CaretRejected);
                        }

                        int rectCount = bounds == null ? 0 : bounds.Length / 4;

                        Rectangle? cell = UiaCaretReader.AcceptTerminalCellRect(
                            caretRangeDegenerate: true,
                            expandedStartMatchesCaretStart: true,
                            expandedNonDegenerate: true,
                            bounds,
                            elementBounds);

                        if (cell is not { } cellRect)
                        {
                            return new TextCaretReading(
                                true, false, false, null, ImeCaretSource.None, false,
                                rectCount,
                                bounds != null && bounds.Length > 0
                                    ? ImeProbeReason.CaretRejected
                                    : ImeProbeReason.CaretNotReady);
                        }

                        return new TextCaretReading(
                            true, false, true, cellRect, ImeCaretSource.TerminalCell, false,
                            rectCount, ImeProbeReason.None);
                    }
                }
            }
            catch
            {
                // 终端 provider 异常与其它不可信形态一样拒绝，绝不外抛或伪造格子。
                return new TextCaretReading(
                    true, null, false, null, ImeCaretSource.None, false,
                    0, ImeProbeReason.TerminalRejected);
            }
        }

        /// <summary>
        /// 读取一个（已证明退化的）文本范围的插入符矩形与只读属性。
        /// 有矩形时只接受单个可信插入符矩形（宽矩形/多矩形直接拒绝，绝不压成 1px）；
        /// 零长范围没有矩形时返回 null：生产路径不做邻字推导，无法证明就返回“未就绪”。
        /// </summary>
        private static (Rectangle? Caret, bool? ReadOnly, int RectCount, ImeProbeReason Reason)
            ReadRangeCaretCore(ITextRangeBackend range, bool isPassword, bool requireDegenerate)
        {
            bool? readOnly = range.GetIsReadOnly();

            // 明确只读：不再读取边界矩形（不提取任何文本范围内容）
            if (readOnly == true)
                return (null, true, 0, ImeProbeReason.NotEditable);

            if (requireDegenerate)
            {
                if (!range.TryCompareStartToEnd(out int compare) || compare != 0)
                    return (null, readOnly, -1, ImeProbeReason.CaretRejected);
            }

            if (!range.TryGetBoundingRectangles(out double[]? bounds))
                return (null, readOnly, -1, ImeProbeReason.CaretRejected);

            int rectCount = bounds == null ? 0 : bounds.Length / 4;

            if (bounds != null && bounds.Length > 0)
            {
                // 有矩形：只接受恰好一个、经过可信性校验的插入符矩形。
                // 整行/整个控件宽矩形会在这里被拒绝，而不会被压成 1px。
                Rectangle? caret = CaretLocator.ReadSingleCaretRect(bounds);
                return (caret, readOnly, rectCount,
                    caret.HasValue ? ImeProbeReason.None : ImeProbeReason.CaretRejected);
            }

            // 零长范围没有边界矩形时，不启用邻字推导：
            // ExpandToEnclosingUnit(Character) 在软换行/行尾可能选到另一可视行的字符，
            // 仅凭 UIA 几何无法严格证明“插入点与邻字在同一可视行”。无法证明就返回 null，
            // 由调用方按“caret 未就绪”处理，绝不猜。密码控件同样不走该路径。
            return (null, readOnly, 0, ImeProbeReason.CaretNotReady);
        }
    }
}
