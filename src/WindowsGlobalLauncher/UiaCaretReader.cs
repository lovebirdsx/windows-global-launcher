using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace CommandLauncher
{
    /// <summary>
    /// 一次 UIA 焦点读取的纯数据结果（不含任何 COM 对象）。
    /// RuntimeId 为 null 表示 provider 未提供；Hwnd 为 0 表示虚拟控件（浏览器/Electron 常见）。
    /// </summary>
    /// <param name="Caret">可信插入符/终端字符格子矩形；null=未就绪或证据不足。</param>
    /// <param name="CaretSource">矩形来源（Uia / TerminalCell / Msaa）。</param>
    /// <param name="ControlType">UIA ControlType ID（诊断用）。</param>
    /// <param name="ClassName">UIA ClassName（诊断用，不含标题/输入内容）。</param>
    /// <param name="HasTextPattern">是否取得 TextPattern。</param>
    /// <param name="HasTextPattern2">是否取得 TextPattern2。</param>
    /// <param name="CaretRectCount">provider 返回的边界矩形数量（0=无/-1=无法解析）。</param>
    /// <param name="TerminalProvider">是否为受支持的 Windows Terminal TermControl。</param>
    /// <param name="Reason">未取得坐标时的具体原因（成功为 None）。</param>
    internal readonly record struct UiaFocusReading(
        string? RuntimeId,
        uint ProcessId,
        IntPtr Hwnd,
        bool IsEditable,
        bool IsPassword,
        Rectangle? Caret,
        ImeCaretSource CaretSource = ImeCaretSource.None,
        int ControlType = 0,
        string? ClassName = null,
        bool HasTextPattern = false,
        bool HasTextPattern2 = false,
        int CaretRectCount = 0,
        bool TerminalProvider = false,
        ImeProbeReason Reason = ImeProbeReason.None);

    /// <summary>
    /// 原生 UI Automation 焦点元素读取。.NET 8 的托管 UIA 没有 TextPattern2，
    /// 因此这里按 Windows SDK（UIAutomationClient.h/idl）的 IID、继承顺序与 vtable
    /// 手工声明必要接口；未使用的接口方法也保留槽位（前置槽位不能省略）。
    /// <para>
    /// 所有 COM 调用都留在调用线程，返回的只有纯数据。必须在专用后台 MTA 线程调用，
    /// 绝不能在 UI 线程或低级钩子回调里调用。
    /// </para>
    /// <para>
    /// 安全约束：不调用 GetText、不读取/记录任何输入文本；密码控件只使用 provider 给出的
    /// 插入符矩形，不做邻字推导。无法证明可信插入符时返回 null，绝不猜测。
    /// </para>
    /// </summary>
    internal static class UiaCaretReader
    {
        #region UIA 常量（UIAutomationClient.h）

        // 属性 ID
        private const int UIA_BoundingRectanglePropertyId = 30001;
        private const int UIA_ProcessIdPropertyId = 30002;
        private const int UIA_ControlTypePropertyId = 30003;
        private const int UIA_HasKeyboardFocusPropertyId = 30008;
        private const int UIA_IsKeyboardFocusablePropertyId = 30009;
        private const int UIA_IsEnabledPropertyId = 30010;
        private const int UIA_ClassNamePropertyId = 30012;
        private const int UIA_IsPasswordPropertyId = 30019;
        private const int UIA_NativeWindowHandlePropertyId = 30020;

        // 控件类型 ID
        internal const int UIA_ComboBoxControlTypeId = 50003;
        internal const int UIA_EditControlTypeId = 50004;
        internal const int UIA_TextControlTypeId = 50020;
        internal const int UIA_CustomControlTypeId = 50026;
        internal const int UIA_DocumentControlTypeId = 50030;

        // 模式 ID
        private const int UIA_ValuePatternId = 10002;
        internal const int UIA_TextPatternId = 10014;
        // 微软文档：UIA_TextPattern2Id = 10024。此前误用 10014 请求 Pattern2，
        // 导致 GetCurrentPatternAs 永远拿不到 caret 接口（真实兼容缺口）。
        internal const int UIA_TextPattern2Id = 10024;

        // 文本属性 ID
        private const int UIA_IsReadOnlyAttributeId = 40015;

        // TextPatternRangeEndpoint_Start / End
        private const int TextPatternRangeEndpoint_Start = 0;
        private const int TextPatternRangeEndpoint_End = 1;

        // UIA TextUnit_Character
        private const int TextUnitCharacter = 0;

        /// <summary>Windows Terminal 的 TermControl 自动化类名（官方 provider 契约）。</summary>
        internal const string TerminalControlClassName = "TermControl";

        /// <summary>Windows Terminal 进程名（不带 .exe）。</summary>
        internal const string TerminalProcessName = "WindowsTerminal";

        private static readonly Guid CUIAutomationClsid = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
        internal static readonly Guid IidIUIAutomationTextPattern = new("32EBA289-3583-42C9-9C59-3B6D9A1E9B6A");
        internal static readonly Guid IidIUIAutomationTextPattern2 = new("506A921A-FCC9-409F-B23B-37EB74106872");
        private static readonly Guid IidIUIAutomationValuePattern = new("A94CD8B1-0844-4CD6-9D2D-640537AB39E9");

        #endregion

        #region Win32

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        #endregion

        /// <summary>每个线程一个 UIA 客户端实例（UIA 客户端有线程亲和性，不能跨线程复用）。</summary>
        [ThreadStatic]
        private static IUIAutomation? t_uia;

        /// <summary>
        /// 同步读取当前焦点元素的身份、可编辑性与可信插入符。
        /// 查询前后都会核对前台窗口与焦点元素（CompareElements/RuntimeId），期间焦点变化返回 null。
        /// <para>
        /// 实际编排在 <see cref="CaretBackendCore.QueryCore"/>；本方法只负责创建生产 COM 适配器，
        /// 与测试假后端驱动同一套 Core。
        /// </para>
        /// </summary>
        /// <param name="expectedForeground">调用方记录的前台窗口。</param>
        /// <param name="focusHwnd">当前 Win32 实际焦点 HWND（MSAA 必须从它取 OBJID_CARET）。</param>
        /// <param name="allowMsaaCaret">
        /// 是否允许在无可信 Win32 系统插入符且 UIA 无直接矩形时采样 MSAA；
        /// 已有 Win32 坐标时传 false，避免无谓的跨进程调用。
        /// </param>
        /// <param name="diagnostic">纯诊断数据（失败时也填充 Reason/ProcessId）。</param>
        internal static UiaFocusReading? Query(
            IntPtr expectedForeground, IntPtr focusHwnd, bool allowMsaaCaret,
            out ImeProbeDiagnostic diagnostic)
        {
            using var backend = new ComUiaFocusBackend();
            return CaretBackendCore.QueryCore(
                expectedForeground, focusHwnd, allowMsaaCaret, backend, out diagnostic);
        }

        /// <summary>
        /// 纯决策：根据 UIA 控件类型、焦点/启用状态与 ValuePattern/TextPattern 的只读属性
        /// 判断是否为可编辑输入控件。
        /// <para>
        /// Edit 控件本身是可编辑的强证据（密码框常不暴露 TextPattern），但任意一个
        /// 只读属性为 true 都否定。Document 不再强制要求 TextPattern：ValuePattern 明确
        /// 报告 IsReadOnly=false 本身就是独立正向证据；原有的 TextPattern 证据（active caret
        /// 或明确可写文本范围）继续保留。任何已采样的只读 true 一律否决。
        /// </para>
        /// </summary>
        internal static bool DecideEditable(
            bool isEdit, bool isDocument, bool focusState, bool enabled,
            bool? valueReadOnly, bool textPatternAvailable, bool? textReadOnly, bool caretActive)
        {
            if (!focusState || !enabled)
                return false;

            if (isEdit)
                return valueReadOnly != true && textReadOnly != true;

            if (isDocument)
            {
                // 明确只读是最终否决，不能被另一模式的 false 覆盖。
                if (valueReadOnly == true || textReadOnly == true)
                    return false;

                // ValuePattern 明确可写 + 原有文本范围证据，二者任一即可。
                return valueReadOnly == false
                    || (textPatternAvailable && (caretActive || textReadOnly == false));
            }

            return false;
        }

        /// <summary>
        /// 纯决策：按 provider 类型选择通用输入控件、ComboBox/Custom 候选或 Terminal 专用规则。
        /// 普通 Text（非经严格限定的 TermControl）在通用规则里恒为不可编辑。
        /// <para>
        /// ComboBox 与 Custom 是「候选类型」而不是可编辑证据：两者都要求真实
        /// HasKeyboardFocus、启用、非密码与非空 RuntimeId（focusable/存在矩形/active 标志
        /// 都不能替代）。ComboBox 只接受 ValuePattern.IsReadOnly=false；Custom 接受
        /// ValuePattern=false 或「文本模式可用且文本范围明确指出非只读」。任何已采样的
        /// 只读 true 一律否决，包括与另一模式的 false 矛盾时。
        /// </para>
        /// </summary>
        internal static bool DecideProviderEditable(
            int controlType, bool terminalProvider, bool hasKeyboardFocus, bool focusable,
            bool enabled, bool isPassword, bool? valueReadOnly,
            bool textPatternAvailable, bool? textReadOnly, bool caretActive,
            bool hasRuntimeId = false)
        {
            if (terminalProvider)
            {
                return DecideTerminalEditable(
                    hasKeyboardFocus, enabled, isPassword, valueReadOnly,
                    textPatternAvailable, textReadOnly);
            }

            // 新候选类型的严格前置条件：不满足时调用方不应读取任何模式。
            bool strictPreconditions = hasKeyboardFocus
                && enabled
                && !isPassword
                && hasRuntimeId;

            if (controlType == UIA_ComboBoxControlTypeId)
            {
                // 类型本身不保证可编辑：接受任意文本的 ComboBox 才支持 ValuePattern。
                // 只有 ValuePattern 明确可写才放行；文本模式仅用于捕捉矛盾的只读 true。
                return strictPreconditions
                    && valueReadOnly == false
                    && textReadOnly != true;
            }

            if (controlType == UIA_CustomControlTypeId)
            {
                // Custom 没有类型级可编辑语义：只承认 Value=false 或
                // 「文本模式可用且范围明确可写」两种正向证据。
                return strictPreconditions
                    && valueReadOnly != true
                    && textReadOnly != true
                    && (valueReadOnly == false
                        || (textPatternAvailable && textReadOnly == false));
            }

            // 焦点状态：元素本身是 GetFocusedElement 的结果，但部分 provider 会把
            // HasKeyboardFocus 报告为 false，故与 IsKeyboardFocusable 取或，避免误判。
            bool focusState = hasKeyboardFocus || focusable;

            return DecideEditable(
                controlType == UIA_EditControlTypeId,
                controlType == UIA_DocumentControlTypeId,
                focusState, enabled, valueReadOnly,
                textPatternAvailable, textReadOnly, caretActive);
        }

        /// <summary>
        /// 纯决策：Windows Terminal 专用适配的可编辑条件。
        /// 要求真正的 HasKeyboardFocus（不接受 focusable 兜底）、启用、非密码、
        /// ValuePattern 未报告只读，且文本范围的 IsReadOnly 必须明确为 false（未知不放行）。
        /// </summary>
        internal static bool DecideTerminalEditable(
            bool hasKeyboardFocus, bool enabled, bool isPassword, bool? valueReadOnly,
            bool textPatternAvailable, bool? textReadOnly)
        {
            return hasKeyboardFocus
                && enabled
                && !isPassword
                && valueReadOnly != true
                && textPatternAvailable
                && textReadOnly == false;
        }

        /// <summary>
        /// 纯决策：是否为受支持的 Windows Terminal provider。
        /// 只认 Text + ClassName=TermControl + 进程 WindowsTerminal；不按窗口标题识别，
        /// 也不把其它 Text/Pane 当作输入控件。
        /// </summary>
        internal static bool IsTerminalProvider(int controlType, string? className, string? processName)
        {
            return controlType == UIA_TextControlTypeId
                && string.Equals(className, TerminalControlClassName, StringComparison.Ordinal)
                && string.Equals(processName, TerminalProcessName, StringComparison.Ordinal);
        }

        /// <summary>
        /// 纯决策：是否应该尝试 MSAA 自绘光标。
        /// 只用于已被 UIA 证明可编辑、非 Terminal、RuntimeId 稳定且没有现成矩形的情形。
        /// </summary>
        internal static bool ShouldTryMsaa(
            bool editable, bool terminalProvider, bool hasRuntimeId,
            bool hasUiaCaret, bool allowMsaa)
        {
            return allowMsaa
                && editable
                && !terminalProvider
                && hasRuntimeId
                && !hasUiaCaret;
        }

        /// <summary>
        /// 纯决策：UIA 查询前后是否仍是同一个焦点元素。
        /// CompareElements 成功时以它为准；不可用时只有两边都有 RuntimeId 才能证明相同；
        /// 其余情况（尤其同一 HWND 内 A→B 切换）一律判为不同，绝不猜。
        /// </summary>
        internal static bool IsSameFocusIdentity(
            int compareHr, int compareSame, string? runtimeId, string? runtimeId2)
        {
            if (compareHr == 0)
                return compareSame != 0;

            if (runtimeId != null && runtimeId2 != null)
                return string.Equals(runtimeId, runtimeId2, StringComparison.Ordinal);

            return false;
        }

        /// <summary>
        /// 纯决策：接受 Windows Terminal 展开后的单个字符格子矩形。
        /// <para>
        /// 硬性条件：原光标范围必须退化；展开范围的起点必须与原光标起点一致
        /// （绝不接受 end 匹配，避免文档末端向前夹取变成“前一个字符”）；展开后必须非退化；
        /// 只接受单个有限、正尺寸且落在终端元素可视边界内的矩形。
        /// </para>
        /// <para>
        /// 刻意不套用通用插入符的窄宽阈值：终端格子可以包含宽字符，来源标记为
        /// TerminalCell，宽度保持原样，不压成 1px。
        /// </para>
        /// </summary>
        internal static Rectangle? AcceptTerminalCellRect(
            bool caretRangeDegenerate,
            bool expandedStartMatchesCaretStart,
            bool expandedNonDegenerate,
            double[]? bounds,
            Rectangle? elementBounds)
        {
            if (!caretRangeDegenerate || !expandedStartMatchesCaretStart || !expandedNonDegenerate)
                return null;

            if (bounds == null || bounds.Length != 4)
                return null;

            for (int i = 0; i < 4; i++)
            {
                if (!double.IsFinite(bounds[i]))
                    return null;
            }

            int left = (int)Math.Round(bounds[0]);
            int top = (int)Math.Round(bounds[1]);
            int width = (int)Math.Round(bounds[2]);
            int height = (int)Math.Round(bounds[3]);
            if (width <= 0 || height <= 0)
                return null;

            if (!elementBounds.HasValue)
                return null;

            var cell = new Rectangle(left, top, width, height);

            // 终端格子必须落在当前焦点元素的边界内；只有“同一个顶层窗口”不足以证明
            // 该矩形属于光标所在的可视区域（滚动历史/分屏/浮层都可能越界）。
            var allowed = elementBounds.Value;
            allowed.Inflate(2, 2);
            if (!allowed.Contains(cell))
                return null;

            return cell;
        }

        private static IUIAutomation? GetUia()
        {
            if (t_uia != null)
                return t_uia;

            try
            {
                var type = Type.GetTypeFromCLSID(CUIAutomationClsid, throwOnError: false);
                if (type == null)
                    return null;

                t_uia = Activator.CreateInstance(type) as IUIAutomation;
            }
            catch
            {
                t_uia = null;
            }

            return t_uia;
        }

        private static bool? ReadBoolAttribute(IUIAutomationTextRange range, int attributeId)
        {
            try
            {
                if (range.GetAttributeValue(attributeId, out object? value) != 0)
                    return null;

                return value switch
                {
                    bool b => b,
                    int i => i != 0,
                    _ => null,
                };
            }
            catch
            {
                return null;
            }
        }

        private static bool? ReadValuePatternReadOnly(IUIAutomationElement element)
        {
            Guid valuePatternIid = IidIUIAutomationValuePattern;
            var pattern = TryGetPattern<IUIAutomationValuePattern>(
                element, UIA_ValuePatternId, ref valuePatternIid, typeof(IUIAutomationValuePattern));

            if (pattern == null)
                return null;

            try
            {
                if (pattern.GetCurrentIsReadOnly(out int readOnly) != 0)
                    return null;
                return readOnly != 0;
            }
            catch
            {
                return null;
            }
            finally
            {
                ReleaseCom(pattern);
            }
        }

        /// <summary>文本模式请求的唯一配对入口；测试可观察生产实际使用的 patternId 与 IID。</summary>
        internal static T? RequestTextPattern<T>(bool extended, Func<int, Guid, T?> request)
            where T : class
        {
            return extended
                ? request(UIA_TextPattern2Id, IidIUIAutomationTextPattern2)
                : request(UIA_TextPatternId, IidIUIAutomationTextPattern);
        }

        /// <summary>
        /// 按 patternId + IID 取模式接口。GetCurrentPatternAs 返回的接口指针在这里创建 RCW，
        /// 原始指针立即释放；返回的 RCW 由调用方释放。
        /// </summary>
        private static T? TryGetPattern<T>(IUIAutomationElement element, int patternId, ref Guid iid, Type type)
            where T : class
        {
            IntPtr patternPtr = IntPtr.Zero;
            try
            {
                if (element.GetCurrentPatternAs(patternId, ref iid, out patternPtr) != 0
                    || patternPtr == IntPtr.Zero)
                {
                    return null;
                }

                return Marshal.GetTypedObjectForIUnknown(patternPtr, type) as T;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (patternPtr != IntPtr.Zero)
                    Marshal.Release(patternPtr);
            }
        }

        private static string? ReadRuntimeId(IUIAutomationElement element)
        {
            try
            {
                // SAFEARRAY(int) 由 COM interop 自动封送并释放，不能再手工 SafeArrayDestroy
                if (element.GetRuntimeId(out int[]? ids) != 0 || ids == null || ids.Length == 0)
                    return null;

                return string.Join(",", ids);
            }
            catch
            {
                return null;
            }
        }

        private static int ReadIntProperty(IUIAutomationElement element, int propertyId)
        {
            try
            {
                if (element.GetCurrentPropertyValue(propertyId, out object? value) != 0)
                    return 0;

                return value switch
                {
                    int i => i,
                    short s => s,
                    byte b => b,
                    _ => 0,
                };
            }
            catch
            {
                return 0;
            }
        }

        private static uint ReadUIntProperty(IUIAutomationElement element, int propertyId)
        {
            int value = ReadIntProperty(element, propertyId);
            return value > 0 ? (uint)value : 0;
        }

        private static bool ReadBoolProperty(IUIAutomationElement element, int propertyId)
        {
            try
            {
                if (element.GetCurrentPropertyValue(propertyId, out object? value) != 0)
                    return false;

                return value switch
                {
                    bool b => b,
                    int i => i != 0,
                    _ => false,
                };
            }
            catch
            {
                return false;
            }
        }

        private static void ReleaseCom(object? obj)
        {
            if (obj == null)
                return;

            try
            {
                if (Marshal.IsComObject(obj))
                    Marshal.ReleaseComObject(obj);
            }
            catch
            {
                // best effort：释放失败不能影响查询结果
            }
        }

        private static string? ReadStringProperty(IUIAutomationElement element, int propertyId)
        {
            try
            {
                if (element.GetCurrentPropertyValue(propertyId, out object? value) != 0)
                    return null;

                return value as string;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 读取 UIA BoundingRectangle（left, top, width, height，double[4]）。
        /// 只用于校验终端格子/MSAA 光标是否落在焦点元素可视范围内，不包含任何文本。
        /// </summary>
        private static Rectangle? ReadBoundingRectProperty(IUIAutomationElement element)
        {
            try
            {
                if (element.GetCurrentPropertyValue(UIA_BoundingRectanglePropertyId, out object? value) != 0)
                    return null;

                if (!TryConvertToDoubles(value, out double[] values) || values.Length != 4)
                    return null;

                for (int i = 0; i < 4; i++)
                {
                    if (!double.IsFinite(values[i]))
                        return null;
                }

                int width = (int)Math.Round(values[2]);
                int height = (int)Math.Round(values[3]);
                if (width <= 0 || height <= 0)
                    return null;

                return new Rectangle(
                    (int)Math.Round(values[0]),
                    (int)Math.Round(values[1]),
                    width,
                    height);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 把 UIA VARIANT 中的数值 SAFEARRAY 统一转成 double[]；
        /// 不同 provider/封送路径可能给出 double[]、object[] 或其它数值数组。
        /// </summary>
        private static bool TryConvertToDoubles(object? value, out double[] values)
        {
            // 先给出确定的 out 值：object[] 分支与异常路径可能提前返回 false。
            values = Array.Empty<double>();

            switch (value)
            {
                case double[] doubles:
                    values = doubles;
                    return true;

                case float[] floats:
                    values = Array.ConvertAll(floats, v => (double)v);
                    return true;

                case int[] ints:
                    values = Array.ConvertAll(ints, v => (double)v);
                    return true;

                case object?[] objects:
                {
                    var converted = new double[objects.Length];
                    for (int i = 0; i < objects.Length; i++)
                    {
                        if (objects[i] is not { } item)
                            return false;

                        try
                        {
                            converted[i] = Convert.ToDouble(item);
                        }
                        catch
                        {
                            return false;
                        }
                    }

                    values = converted;
                    return true;
                }

                default:
                    return false;
            }
        }

        /// <summary>
        /// 取 UIA 元素所属进程名（仅用于 Terminal 严格限定；失败返回 null，不猜）。
        /// 只在候选已命中 Text+TermControl 时调用，避免对每个焦点元素做跨进程查询。
        /// </summary>
        private static string? TryGetProcessName(uint processId)
        {
            if (processId == 0)
                return null;

            try
            {
                using var process = Process.GetProcessById(unchecked((int)processId));
                return process.ProcessName;
            }
            catch
            {
                return null;
            }
        }

        #region 生产 COM 适配器（与测试假后端共享 CaretBackendCore）

        /// <summary>
        /// 生产 UIA 焦点后端：所有 COM 读写都留在调用线程（固定后台 MTA），
        /// 只把纯数据交给 <see cref="CaretBackendCore"/>。
        /// </summary>
        private sealed class ComUiaFocusBackend : IUiaFocusBackend
        {
            private IUIAutomation? _uia;
            private IntPtr _elementPtr;
            private IUIAutomationElement? _element;

            public uint GetProcessId(IntPtr hwnd)
            {
                GetWindowThreadProcessId(hwnd, out uint processId);
                return processId;
            }

            public IntPtr GetForeground() => GetForegroundWindow();

            public bool TryReadFocusElement(out UiaElementSnapshot element, out int hresult)
            {
                element = default;
                hresult = 0;

                try
                {
                    _uia = GetUia();
                    if (_uia == null)
                        return false;

                    hresult = _uia.GetFocusedElement(out _elementPtr);
                    if (hresult != 0 || _elementPtr == IntPtr.Zero)
                        return false;

                    _element = Marshal.GetTypedObjectForIUnknown(
                        _elementPtr, typeof(IUIAutomationElement)) as IUIAutomationElement;
                    if (_element == null)
                        return false;

                    // NativeWindowHandle 在 UIA 里是 32 位 int，按 int 做符号扩展才是正确的 HWND
                    int nativeHwndValue = ReadIntProperty(_element, UIA_NativeWindowHandlePropertyId);

                    element = new UiaElementSnapshot(
                        RuntimeId: ReadRuntimeId(_element),
                        ProcessId: ReadUIntProperty(_element, UIA_ProcessIdPropertyId),
                        Hwnd: nativeHwndValue != 0 ? new IntPtr(nativeHwndValue) : IntPtr.Zero,
                        ControlType: ReadIntProperty(_element, UIA_ControlTypePropertyId),
                        ClassName: ReadStringProperty(_element, UIA_ClassNamePropertyId),
                        Bounds: ReadBoundingRectProperty(_element),
                        HasKeyboardFocus: ReadBoolProperty(_element, UIA_HasKeyboardFocusPropertyId),
                        IsKeyboardFocusable: ReadBoolProperty(_element, UIA_IsKeyboardFocusablePropertyId),
                        IsEnabled: ReadBoolProperty(_element, UIA_IsEnabledPropertyId),
                        IsPassword: ReadBoolProperty(_element, UIA_IsPasswordPropertyId),
                        ProcessName: ReadCandidateProcessName(_element));
                    return true;
                }
                catch
                {
                    element = default;
                    return false;
                }
            }

            public bool TryReadFocusIdentity(
                out int compareHr, out int compareSame, out string? runtimeId, out int hresult)
            {
                compareHr = 0;
                compareSame = 0;
                runtimeId = null;
                hresult = 0;

                if (_uia == null || _elementPtr == IntPtr.Zero)
                    return false;

                IntPtr element2Ptr = IntPtr.Zero;
                IUIAutomationElement? element2 = null;

                try
                {
                    hresult = _uia.GetFocusedElement(out element2Ptr);
                    if (hresult != 0 || element2Ptr == IntPtr.Zero)
                        return false;

                    element2 = Marshal.GetTypedObjectForIUnknown(
                        element2Ptr, typeof(IUIAutomationElement)) as IUIAutomationElement;
                    if (element2 == null)
                        return false;

                    runtimeId = ReadRuntimeId(element2);
                    compareHr = _uia.CompareElements(_elementPtr, element2Ptr, out compareSame);
                    return true;
                }
                catch
                {
                    return false;
                }
                finally
                {
                    ReleaseCom(element2);
                    if (element2Ptr != IntPtr.Zero)
                        Marshal.Release(element2Ptr);
                }
            }

            public bool? ReadValuePatternReadOnly()
            {
                return _element == null ? null : UiaCaretReader.ReadValuePatternReadOnly(_element);
            }

            public ITextPatternBackend? GetTextPatternBackend()
            {
                return _element == null ? null : new ComTextPatternBackend(_element);
            }

            public MsaaCaretReading TryReadMsaaCaret(IntPtr focusHwnd, Rectangle? elementBounds)
            {
                return MsaaCaretReader.TryReadCaret(
                        focusHwnd, elementBounds,
                        out Rectangle caret, out ImeProbeReason reason, out int hresult)
                    ? new MsaaCaretReading(true, caret, reason, hresult)
                    : new MsaaCaretReading(false, default, reason, hresult);
            }

            public void Dispose()
            {
                ReleaseCom(_element);
                _element = null;

                if (_elementPtr != IntPtr.Zero)
                {
                    Marshal.Release(_elementPtr);
                    _elementPtr = IntPtr.Zero;
                }
            }

            /// <summary>
            /// 只为 Terminal 候选（Text + TermControl）解析进程名，避免对每个焦点元素做跨进程查询。
            /// </summary>
            private static string? ReadCandidateProcessName(IUIAutomationElement element)
            {
                int controlType = ReadIntProperty(element, UIA_ControlTypePropertyId);
                string? className = ReadStringProperty(element, UIA_ClassNamePropertyId);
                if (controlType != UIA_TextControlTypeId
                    || !string.Equals(className, TerminalControlClassName, StringComparison.Ordinal))
                {
                    return null;
                }

                return TryGetProcessName(ReadUIntProperty(element, UIA_ProcessIdPropertyId));
            }
        }

        /// <summary>生产 TextPattern/TextPattern2 适配器；持有模式 RCW，Dispose 时释放。</summary>
        private sealed class ComTextPatternBackend : ITextPatternBackend
        {
            private readonly IUIAutomationTextPattern2? _pattern2;
            private readonly IUIAutomationTextPattern? _pattern;

            internal ComTextPatternBackend(IUIAutomationElement element)
            {
                _pattern2 = RequestTextPattern(true, (int patternId, Guid iid) =>
                    TryGetPattern<IUIAutomationTextPattern2>(
                        element, patternId, ref iid, typeof(IUIAutomationTextPattern2)));

                _pattern = RequestTextPattern(false, (int patternId, Guid iid) =>
                    TryGetPattern<IUIAutomationTextPattern>(
                        element, patternId, ref iid, typeof(IUIAutomationTextPattern)));
            }

            public bool HasPattern2 => _pattern2 != null;

            public bool HasPattern => _pattern != null;

            public bool TryGetCaretRange(out bool isActive, out ITextRangeBackend? range)
            {
                isActive = false;
                range = null;

                if (_pattern2 == null)
                    return false;

                try
                {
                    if (_pattern2.GetCaretRange(out int active, out IUIAutomationTextRange? caretRange) != 0)
                    {
                        isActive = false;
                        ReleaseCom(caretRange);
                        return false;
                    }

                    isActive = active != 0;
                    if (caretRange != null)
                        range = new ComTextRangeBackend(caretRange);

                    return true;
                }
                catch
                {
                    isActive = false;
                    range = null;
                    return false;
                }
            }

            public bool TryGetSelection(out int rangeCount, out ITextRangeBackend? range)
            {
                rangeCount = 0;
                range = null;

                if (_pattern == null)
                    return false;

                IUIAutomationTextRangeArray? ranges = null;
                try
                {
                    if (_pattern.GetSelection(out ranges) != 0 || ranges == null)
                        return false;

                    if (ranges.GetLength(out rangeCount) != 0)
                    {
                        rangeCount = 0;
                        return false;
                    }

                    if (rangeCount <= 0)
                        return true;

                    if (ranges.GetElement(0, out IUIAutomationTextRange? first) != 0 || first == null)
                    {
                        ReleaseCom(first);
                        rangeCount = 0;
                        return false;
                    }

                    range = new ComTextRangeBackend(first);
                    return true;
                }
                catch
                {
                    rangeCount = 0;
                    range = null;
                    return false;
                }
                finally
                {
                    ReleaseCom(ranges);
                }
            }

            public void Dispose()
            {
                ReleaseCom(_pattern2);
                ReleaseCom(_pattern);
            }
        }

        /// <summary>生产文本范围适配器；Dispose 时释放范围 RCW。</summary>
        private sealed class ComTextRangeBackend : ITextRangeBackend
        {
            private readonly IUIAutomationTextRange _range;

            internal ComTextRangeBackend(IUIAutomationTextRange range)
            {
                _range = range;
            }

            public bool TryCompareStartToEnd(out int compare)
            {
                return _range.CompareEndpoints(
                    TextPatternRangeEndpoint_Start, _range,
                    TextPatternRangeEndpoint_End, out compare) == 0;
            }

            public bool? GetIsReadOnly()
            {
                return ReadBoolAttribute(_range, UIA_IsReadOnlyAttributeId);
            }

            public bool TryGetBoundingRectangles(out double[]? bounds)
            {
                return _range.GetBoundingRectangles(out bounds) == 0;
            }

            public ITextRangeBackend? Clone()
            {
                try
                {
                    if (_range.Clone(out IUIAutomationTextRange? cloned) != 0 || cloned == null)
                        return null;

                    return new ComTextRangeBackend(cloned);
                }
                catch
                {
                    return null;
                }
            }

            public bool TryExpandToEnclosingCharacter()
            {
                return _range.ExpandToEnclosingUnit(TextUnitCharacter) == 0;
            }

            public bool TryCompareStartToStart(ITextRangeBackend other, out int compare)
            {
                compare = 0;
                if (other is not ComTextRangeBackend com)
                    return false;

                return _range.CompareEndpoints(
                    TextPatternRangeEndpoint_Start, com._range,
                    TextPatternRangeEndpoint_Start, out compare) == 0;
            }

            public void Dispose()
            {
                ReleaseCom(_range);
            }
        }

        #endregion

        #region 原生 COM 接口声明（IID/vtable 顺序对应 UIAutomationClient.idl）

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [ComImport]
        [Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IUIAutomation
        {
            // 1..5 仅为保留 vtable 槽位，未使用
            [PreserveSig] int CompareElements(IntPtr el1, IntPtr el2, out int areSame);
            [PreserveSig] int CompareRuntimeIds(IntPtr runtimeId1, IntPtr runtimeId2, out int areSame);
            [PreserveSig] int GetRootElement(out IntPtr root);
            [PreserveSig] int ElementFromHandle(IntPtr hwnd, out IntPtr element);
            [PreserveSig] int ElementFromPoint(POINT pt, out IntPtr element);
            // 6
            [PreserveSig] int GetFocusedElement(out IntPtr element);
        }

        [ComImport]
        [Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IUIAutomationElement
        {
            // 1..7 仅为保留 vtable 槽位，未使用
            [PreserveSig] int SetFocus();
            [PreserveSig] int GetRuntimeId(
                [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] out int[]? runtimeId);
            [PreserveSig] int FindFirst(int scope, IntPtr condition, out IntPtr found);
            [PreserveSig] int FindAll(int scope, IntPtr condition, out IntPtr found);
            [PreserveSig] int FindFirstBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IntPtr found);
            [PreserveSig] int FindAllBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IntPtr found);
            [PreserveSig] int BuildUpdatedCache(IntPtr cacheRequest, out IntPtr updatedElement);
            // 8
            [PreserveSig] int GetCurrentPropertyValue(
                int propertyId, [MarshalAs(UnmanagedType.Struct)] out object? value);
            // 9..11 仅为保留 vtable 槽位，未使用
            [PreserveSig] int GetCurrentPropertyValueEx(
                int propertyId, int ignoreDefault, [MarshalAs(UnmanagedType.Struct)] out object? value);
            [PreserveSig] int GetCachedPropertyValue(
                int propertyId, [MarshalAs(UnmanagedType.Struct)] out object? value);
            [PreserveSig] int GetCachedPropertyValueEx(
                int propertyId, int ignoreDefault, [MarshalAs(UnmanagedType.Struct)] out object? value);
            // 12
            [PreserveSig] int GetCurrentPatternAs(int patternId, ref Guid riid, out IntPtr pattern);
        }

        [ComImport]
        [Guid("A543CC6A-F4AE-494B-8239-C814481187A8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IUIAutomationTextRange
        {
            [PreserveSig] int Clone(
                [MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange? clonedRange);
            [PreserveSig] int Compare(
                [MarshalAs(UnmanagedType.Interface)] IUIAutomationTextRange? range, out int areSame);
            [PreserveSig] int CompareEndpoints(
                int srcEndPoint,
                [MarshalAs(UnmanagedType.Interface)] IUIAutomationTextRange? range,
                int targetEndPoint, out int compValue);
            [PreserveSig] int ExpandToEnclosingUnit(int textUnit);
            [PreserveSig] int FindAttribute(int attr, IntPtr val, int backward, out IntPtr found);
            [PreserveSig] int FindText([MarshalAs(UnmanagedType.BStr)] string text, int backward, int ignoreCase, out IntPtr found);
            [PreserveSig] int GetAttributeValue(
                int attr, [MarshalAs(UnmanagedType.Struct)] out object? value);
            [PreserveSig] int GetBoundingRectangles(
                [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_R8)] out double[]? boundingRects);
            [PreserveSig] int GetEnclosingElement(out IntPtr enclosingElement);
            [PreserveSig] int GetText(int maxLength, [MarshalAs(UnmanagedType.BStr)] out string? text);
            [PreserveSig] int Move(int unit, int count, out int moved);
            [PreserveSig] int MoveEndpointByUnit(int endpoint, int unit, int count, out int moved);
            [PreserveSig] int MoveEndpointByRange(
                int srcEndPoint,
                [MarshalAs(UnmanagedType.Interface)] IUIAutomationTextRange? range,
                int targetEndPoint);
            [PreserveSig] int Select();
            [PreserveSig] int AddToSelection();
            [PreserveSig] int RemoveFromSelection();
            [PreserveSig] int ScrollIntoView(int alignToTop);
            [PreserveSig] int GetChildren(out IntPtr children);
        }

        [ComImport]
        [Guid("CE4AE76A-E717-4C98-81EA-47371D028EB6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IUIAutomationTextRangeArray
        {
            [PreserveSig] int GetLength(out int length);
            [PreserveSig] int GetElement(
                int index,
                [MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange? element);
        }

        [ComImport]
        [Guid("32EBA289-3583-42C9-9C59-3B6D9A1E9B6A")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IUIAutomationTextPattern
        {
            [PreserveSig] int RangeFromPoint(POINT pt, out IntPtr range);
            [PreserveSig] int RangeFromChild(IntPtr child, out IntPtr range);
            [PreserveSig] int GetSelection(
                [MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRangeArray? ranges);
            [PreserveSig] int GetVisibleRanges(
                [MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRangeArray? ranges);
            [PreserveSig] int GetDocumentRange(
                [MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange? range);
            [PreserveSig] int GetSupportedTextSelection(out int supportedTextSelection);
        }

        // 注意：这里刻意不写 C# 接口继承（: IUIAutomationTextPattern）。
        // ComImport 接口继承不能保证派生接口的 vtable 会自动先排入基接口方法；
        // 只声明两个新方法会让 RangeFromAnnotation/GetCaretRange 落到错误槽位。
        // 因此按 UIAutomationClient.idl 的顺序把 6 个基槽 + 2 个新槽完整平铺。
        // 反射契约测试 UiaCaretReaderTests 会锁定这 8 个槽位的声明形状。
        [ComImport]
        [Guid("506A921A-FCC9-409F-B23B-37EB74106872")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IUIAutomationTextPattern2
        {
            // 6 个基接口槽位（IUIAutomationTextPattern）
            [PreserveSig] int RangeFromPoint(POINT pt, out IntPtr range);
            [PreserveSig] int RangeFromChild(IntPtr child, out IntPtr range);
            [PreserveSig] int GetSelection(
                [MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRangeArray? ranges);
            [PreserveSig] int GetVisibleRanges(
                [MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRangeArray? ranges);
            [PreserveSig] int GetDocumentRange(
                [MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange? range);
            [PreserveSig] int GetSupportedTextSelection(out int supportedTextSelection);

            // 2 个 TextPattern2 槽位
            [PreserveSig] int RangeFromAnnotation(IntPtr annotation, out IntPtr range);
            [PreserveSig] int GetCaretRange(
                out int isActive,
                [MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange? range);
        }

        [ComImport]
        [Guid("A94CD8B1-0844-4CD6-9D2D-640537AB39E9")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IUIAutomationValuePattern
        {
            [PreserveSig] int SetValue([MarshalAs(UnmanagedType.BStr)] string val);
            [PreserveSig] int GetCurrentValue([MarshalAs(UnmanagedType.BStr)] out string? retVal);
            [PreserveSig] int GetCurrentIsReadOnly(out int retVal);
        }

        #endregion
    }
}
