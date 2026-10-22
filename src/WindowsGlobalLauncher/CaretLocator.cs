using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace CommandLauncher
{
    /// <summary>跨进程取插入符位置：GetGUIThreadInfo 快路径 + UI Automation 回退。</summary>
    public static class CaretLocator
    {
        #region Win32 P/Invoke（插入符定位）

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize;
            public int dwFlags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        #endregion

        /// <summary>
        /// 取前台窗口插入符的屏幕矩形（物理像素）。自绘光标的应用（浏览器等）取不到，返回 false。
        /// </summary>
        public static bool TryGetCaretScreenRect(IntPtr foreground, out Rectangle rect)
        {
            rect = default;
            if (foreground == IntPtr.Zero)
                return false;

            uint threadId = GetWindowThreadProcessId(foreground, out _);
            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (!GetGUIThreadInfo(threadId, ref info) || info.hwndCaret == IntPtr.Zero)
                return false;

            // 宽高均为 0 说明插入符不可见
            if (info.rcCaret.Right <= info.rcCaret.Left && info.rcCaret.Bottom <= info.rcCaret.Top)
                return false;

            // rcCaret 是插入符窗口的客户区坐标，分别换算左上角与右下角到屏幕坐标
            var topLeft = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
            var bottomRight = new POINT { X = info.rcCaret.Right, Y = info.rcCaret.Bottom };
            if (!ClientToScreen(info.hwndCaret, ref topLeft) || !ClientToScreen(info.hwndCaret, ref bottomRight))
                return false;

            rect = new Rectangle(
                topLeft.X,
                topLeft.Y,
                Math.Max(1, bottomRight.X - topLeft.X),
                Math.Max(1, bottomRight.Y - topLeft.Y));
            return true;
        }

        /// <summary>
        /// 兼容旧语义：只取插入符左下角作为定位点。
        /// </summary>
        public static bool TryGetCaretScreenPoint(IntPtr foreground, out Point point)
        {
            point = default;
            if (!TryGetCaretScreenRect(foreground, out var rect))
                return false;

            point = new Point(rect.Left, rect.Bottom);
            return true;
        }

        /// <summary>
        /// 经 UI Automation 异步取焦点元素的插入符屏幕矩形（物理像素）。
        /// UIA 客户端 API 可能因目标应用卡顿而长时间阻塞，故放到后台线程执行并由调用方控制超时；
        /// 超时/失败/异常一律返回 null（不刷日志），被放弃的后台任务任其自行结束。
        /// </summary>
        public static async Task<Rectangle?> GetCaretScreenRectViaUiaAsync(int timeoutMs)
        {
            try
            {
                var task = Task.Run(() => GetCaretRectViaUiaOnBackground());
                var completed = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (completed != task)
                    return null;
                return await task.ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 同步兼容包装：最多阻塞 timeoutMs 等待 UIA 结果；失败时按旧文案记 WARN。
        /// </summary>
        public static bool TryGetCaretScreenPointViaUia(out Point point, int timeoutMs)
        {
            point = default;
            try
            {
                var rect = GetCaretScreenRectViaUiaAsync(timeoutMs).GetAwaiter().GetResult();
                if (!rect.HasValue)
                {
                    Logger.LogWarning("UI Automation 未取到插入符位置（超时或目标不支持），回退到屏幕居中");
                    return false;
                }

                point = new Point(rect.Value.Left, rect.Value.Bottom);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"UI Automation 取插入符位置失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 后台线程执行 UIA 查询，只把纯数据矩形（Rectangle?）传回 UI 线程；
        /// AutomationElement 等 UIA COM 对象全部留在后台线程内，不跨线程带出。
        /// </summary>
        private static Rectangle? GetCaretRectViaUiaOnBackground()
        {
            try
            {
                var focused = System.Windows.Automation.AutomationElement.FocusedElement;
                if (focused == null)
                    return null;
                if (!focused.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out var patternObj))
                    return null;

                var ranges = ((System.Windows.Automation.TextPattern)patternObj).GetSelection();
                if (ranges.Length == 0)
                    return null;

                // 多行选区时取最后一个矩形（光标在选区末尾）；插入符矩形的宽度为 0 属正常
                var rects = ranges[^1].GetBoundingRectangles();
                if (rects.Length == 0)
                    return null;
                var rect = rects[^1];
                if (rect.IsEmpty || rect.Height <= 0)
                    return null;

                return new Rectangle(
                    (int)Math.Round(rect.Left),
                    (int)Math.Round(rect.Top),
                    Math.Max(1, (int)Math.Round(rect.Width)),
                    Math.Max(1, (int)Math.Round(rect.Height)));
            }
            catch
            {
                return null;
            }
        }

        #region 输入法提示：严格焦点快照

        // GetAncestor 的 GA_ROOT
        private const uint GA_ROOT = 2;

        // 标准 Edit 控件的只读样式位
        private const int GWL_STYLE = -16;
        private const int ES_READONLY = 0x0800;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        /// <summary>一次 Win32 焦点采集结果（纯数据）。</summary>
        private readonly record struct Win32FocusState(
            IntPtr Foreground,
            IntPtr FocusHwnd,
            uint FocusThreadId,
            uint ProcessId,
            IntPtr CaretHwnd,
            RECT CaretRect,
            bool HasFocus)
        {
            public bool IsValid => Foreground != IntPtr.Zero;

            /// <summary>前台与实际焦点是否完全一致（含同 HWND 内不可见状态变化的粗粒度校验）。</summary>
            public bool SameAs(in Win32FocusState other)
            {
                return Foreground == other.Foreground
                    && FocusHwnd == other.FocusHwnd
                    && FocusThreadId == other.FocusThreadId
                    && ProcessId == other.ProcessId
                    && HasFocus == other.HasFocus;
            }
        }

        /// <summary>
        /// 输入法提示专用的严格焦点探测入口（兼容旧调用）。
        /// 内部委托到带诊断重载；不需要诊断时使用本重载。
        /// </summary>
        internal static ImeFocusSnapshot? TryCaptureFocusSnapshot(IntPtr expectedForeground)
        {
            return TryCaptureFocusSnapshot(expectedForeground, out _);
        }

        /// <summary>
        /// 输入法提示专用的严格焦点探测入口（只能在专用后台 MTA 线程调用，不能在 UI 线程调用）。
        /// <para>
        /// 返回 null 表示无有效焦点、前台已变化、身份不足或查询失败；返回非 null 时
        /// IsEditable=false 表示焦点明确不可编辑，Caret=null 表示尚未就绪或无法安全定位。
        /// </para>
        /// <para>
        /// 无论成功还是失败，<paramref name="diagnostic"/> 都会填充原因/来源/进程等纯数据，
        /// 不含任何输入内容。失败路径绝不伪造坐标。
        /// </para>
        /// <para>
        /// 身份来源分层：
        /// 1) 原生独立 HWND 的 Edit/RichEdit：HWND 本身唯一，保留纯 Win32 快路径。
        /// 2) 其它控件：必须由 UIA 提供 RuntimeId 与实际可编辑性；坐标优先级
        ///    Win32 系统插入符 &gt; Terminal 字符格子 &gt; UIA 直接矩形 &gt; MSAA 自绘光标。
        /// 3) 身份不足（无 RuntimeId / 焦点不匹配 / 查询失败）时返回 null，绝不猜。
        /// </para>
        /// </summary>
        internal static ImeFocusSnapshot? TryCaptureFocusSnapshot(
            IntPtr expectedForeground, out ImeProbeDiagnostic diagnostic)
        {
            var diag = ImeProbeDiagnostic.For(ImeProbeReason.NoForeground);
            ImeFocusSnapshot? snapshot;

            try
            {
                snapshot = CaptureFocusSnapshotCore(expectedForeground, ref diag);
            }
            catch
            {
                // 探测路径绝不向工作线程抛异常；异常也必须留下可归因的诊断原因。
                diag = diag with { Reason = ImeProbeReason.ProbeException, Source = ImeCaretSource.None };
                snapshot = null;
            }

            diagnostic = diag;
            return snapshot;
        }

        private static ImeFocusSnapshot? CaptureFocusSnapshotCore(
            IntPtr expectedForeground, ref ImeProbeDiagnostic diagnostic)
        {
            if (expectedForeground == IntPtr.Zero)
                return null;

            GetWindowThreadProcessId(expectedForeground, out uint expectedPid);
            diagnostic = ImeProbeDiagnostic.For(
                ImeProbeReason.NoForeground, ImeCaretSource.None, expectedPid);

            var before = CaptureWin32Focus(expectedForeground);
            if (!before.IsValid)
            {
                diagnostic = diagnostic with
                {
                    Reason = ImeProbeReason.ForegroundChanged,
                    ProcessId = before.ProcessId != 0 ? before.ProcessId : expectedPid,
                };
                return null;
            }

            // 1) 没有 Win32 输入焦点：明确不可编辑（焦点可能已落到菜单/桌面）。
            if (!before.HasFocus)
            {
                var afterNoFocus = CaptureWin32Focus(expectedForeground);
                if (!afterNoFocus.IsValid || !before.SameAs(afterNoFocus))
                {
                    diagnostic = diagnostic with
                    {
                        Reason = ClassifyFocusStateChange(
                            expectedForeground, afterNoFocus.Foreground),
                    };
                    return null;
                }

                var noFocusIdentity = new ImeFocusIdentity(
                    afterNoFocus.Foreground, afterNoFocus.FocusHwnd,
                    afterNoFocus.FocusThreadId, afterNoFocus.ProcessId, RuntimeId: null);

                var noFocusDiagnostic = diagnostic with
                {
                    Reason = ImeProbeReason.NoFocus,
                    Source = ImeCaretSource.None,
                    ProcessId = afterNoFocus.ProcessId,
                    // 这是本机 Win32 的确定结论，不是「未判断」。
                    IsEditable = false,
                    HasKeyboardFocus = false,
                };
                diagnostic = noFocusDiagnostic;

                return new ImeFocusSnapshot(
                    noFocusIdentity, IsEditable: false, Caret: null, DpiHwnd: afterNoFocus.FocusHwnd,
                    ImeCaretSource.None, noFocusDiagnostic);
            }

            // 2) 原生独立 HWND 的 Edit/RichEdit：窗口类本身足以确定控件身份，
            //    纯 Win32 快路径不查 UIA；只读用 ES_READONLY 判断。
            string? focusClassName = ReadClassName(before.FocusHwnd);
            if (IsNativeTextClassName(focusClassName))
            {
                var afterNative = CaptureWin32Focus(expectedForeground);
                if (!afterNative.IsValid || !before.SameAs(afterNative))
                {
                    diagnostic = diagnostic with
                    {
                        Reason = ClassifyFocusStateChange(
                            expectedForeground, afterNative.Foreground),
                    };
                    return null;
                }

                bool nativeEnabled = IsWindowEnabled(afterNative.FocusHwnd);
                bool nativeReadOnly = IsNativeTextReadOnly(
                    GetWindowLong(afterNative.FocusHwnd, GWL_STYLE));
                bool nativeEditable = nativeEnabled && !nativeReadOnly;

                Rectangle? nativeCaret = null;
                IntPtr nativeDpiHwnd = afterNative.FocusHwnd;
                if (nativeEditable
                    && TryGetTrustedWin32Caret(afterNative, out var nativeCaretRect, out var caretHwnd))
                {
                    nativeCaret = nativeCaretRect;
                    if (caretHwnd != IntPtr.Zero)
                        nativeDpiHwnd = caretHwnd;
                }

                var nativeIdentity = new ImeFocusIdentity(
                    afterNative.Foreground, afterNative.FocusHwnd,
                    afterNative.FocusThreadId, afterNative.ProcessId, RuntimeId: null);

                var nativeDiagnostic = new ImeProbeDiagnostic(
                    nativeEditable
                        ? (nativeCaret.HasValue ? ImeProbeReason.None : ImeProbeReason.CaretNotReady)
                        : (nativeReadOnly ? ImeProbeReason.NativeReadOnly : ImeProbeReason.NotEditable),
                    nativeCaret.HasValue ? ImeCaretSource.Win32 : ImeCaretSource.None,
                    afterNative.ProcessId,
                    ControlType: 0,
                    ClassName: focusClassName,
                    CaretRectUsable: nativeCaret.HasValue)
                {
                    // 原生路径不查询 ValuePattern/TextPattern，对应三态保持 null / false；
                    // 这里只填写已经由 Win32 确定的焦点、启用与可编辑结论。
                    IsEditable = nativeEditable,
                    HasKeyboardFocus = true,
                    IsEnabled = nativeEnabled,
                };
                diagnostic = nativeDiagnostic;

                return new ImeFocusSnapshot(
                    nativeIdentity, nativeEditable, nativeCaret, nativeDpiHwnd,
                    nativeCaret.HasValue ? ImeCaretSource.Win32 : ImeCaretSource.None,
                    nativeDiagnostic);
            }

            // 3) 其它控件：UIA 提供实际身份（同 HWND 内不同字段靠 RuntimeId 区分）
            //    与可编辑性；坐标优先可信 Win32 系统插入符，其次 Terminal/UIA/MSAA。
            //
            // 已有可信 Win32 光标时不允许 Query 采样 MSAA：避免无谓的跨进程调用。
            bool hasWin32CaretBefore = TryGetTrustedWin32Caret(before, out _, out _);

            UiaFocusReading? reading = UiaCaretReader.Query(
                expectedForeground, before.FocusHwnd,
                allowMsaaCaret: !hasWin32CaretBefore,
                out ImeProbeDiagnostic uiaDiagnostic);

            // 查询前后核对前台 + 实际焦点 HWND/线程/进程；同 HWND 内的焦点变化由
            // UiaCaretReader 内部的 CompareElements/RuntimeId 前后核对负责。
            var after = CaptureWin32Focus(expectedForeground);
            if (!after.IsValid || !before.SameAs(after))
            {
                diagnostic = uiaDiagnostic with
                {
                    Reason = ClassifyFocusStateChange(
                        expectedForeground, after.Foreground),
                    Source = ImeCaretSource.None,
                };
                return null;
            }

            diagnostic = uiaDiagnostic with { ProcessId = after.ProcessId };

            if (reading is not { } uia || !IsUiaReadingForFocus(uia, after))
            {
                if (reading != null)
                {
                    // UIA 元素属于别的窗口/宿主链：身份不可信，绝不把它的坐标用于当前焦点。
                    diagnostic = diagnostic with
                    {
                        Reason = ImeProbeReason.UiaIdentityChanged,
                        Source = ImeCaretSource.None,
                    };
                }

                return null;
            }

            string? runtimeId = string.IsNullOrWhiteSpace(uia.RuntimeId) ? null : uia.RuntimeId;
            var identity = new ImeFocusIdentity(
                after.Foreground, after.FocusHwnd, after.FocusThreadId,
                after.ProcessId, runtimeId);

            // 明确的不可编辑结论（按钮/菜单/只读 WPF TextBox/普通 Text）：不需要身份也能安全隐藏。
            if (!uia.IsEditable)
            {
                diagnostic = diagnostic with
                {
                    Reason = uia.Reason != ImeProbeReason.None
                        ? uia.Reason
                        : ImeProbeReason.NotEditable,
                    Source = ImeCaretSource.None,
                    HasRuntimeId = runtimeId != null,
                    CaretRectUsable = false,
                };

                return new ImeFocusSnapshot(
                    identity, IsEditable: false, Caret: null, DpiHwnd: after.FocusHwnd,
                    ImeCaretSource.None, diagnostic);
            }

            // 可编辑但身份不足（如 provider 不提供 RuntimeId）：无法区分同一 HWND 内的
            // 不同字段，返回 null 让调用方按“未取得有效焦点”处理，绝不猜。
            if (!HasSufficientIdentity(isNativeTextControl: false, runtimeId))
            {
                diagnostic = diagnostic with
                {
                    Reason = ImeProbeReason.IdentityMissing,
                    Source = ImeCaretSource.None,
                    HasRuntimeId = false,
                };
                return null;
            }

            Rectangle? win32Caret = null;
            IntPtr win32CaretHwnd = IntPtr.Zero;
            if (TryGetTrustedWin32Caret(after, out var trustedWin32Caret, out var trustedCaretHwnd))
            {
                win32Caret = trustedWin32Caret;
                win32CaretHwnd = trustedCaretHwnd;
            }

            var selection = SelectCaret(win32Caret, uia.CaretSource, uia.Caret);

            IntPtr dpiHwnd = after.FocusHwnd;
            if (selection.Source == ImeCaretSource.Win32 && win32CaretHwnd != IntPtr.Zero)
            {
                dpiHwnd = win32CaretHwnd;
            }
            else if (selection.Caret.HasValue
                && selection.Source != ImeCaretSource.Win32
                && uia.Hwnd != IntPtr.Zero)
            {
                dpiHwnd = uia.Hwnd;
            }

            diagnostic = diagnostic with
            {
                Reason = selection.Caret.HasValue
                    ? ImeProbeReason.None
                    : (uia.Reason != ImeProbeReason.None
                        ? uia.Reason
                        : ImeProbeReason.CaretNotReady),
                Source = selection.Source,
                HasRuntimeId = true,
                CaretRectUsable = selection.Caret.HasValue,
            };

            return new ImeFocusSnapshot(
                identity, true, selection.Caret, dpiHwnd, selection.Source, diagnostic);
        }

        /// <summary>最终选用的插入符来源与矩形（纯数据）。</summary>
        internal readonly record struct CaretSelection(ImeCaretSource Source, Rectangle? Caret);

        /// <summary>
        /// 纯决策：按「Win32 &gt; TerminalCell &gt; UIA &gt; MSAA」合成最终坐标来源。
        /// Terminal 格子保留完整宽度，不套用通用插入符的窄宽阈值（宽字符格子合法）；
        /// 其余来源沿用既有可信性校验，宽矩形绝不压成 1px。
        /// </summary>
        internal static CaretSelection SelectCaret(
            Rectangle? win32Caret, ImeCaretSource uiaSource, Rectangle? uiaCaret)
        {
            if (win32Caret is { } win32)
            {
                // Win32 系统插入符已在 TryGetTrustedWin32Caret 中完成可信性校验
                // （含窄宽、宿主链、可见性与屏幕相交），这里不得再用 UIA 的窄宽阈值
                // 二次收紧——辅助功能加粗的系统光标合法，不能被误杀。
                return new CaretSelection(ImeCaretSource.Win32, win32);
            }

            if (uiaCaret is not { } caret)
                return new CaretSelection(ImeCaretSource.None, null);

            if (uiaSource == ImeCaretSource.TerminalCell)
                return new CaretSelection(ImeCaretSource.TerminalCell, caret);

            if (!IsTrustedCaretRect(caret))
                return new CaretSelection(ImeCaretSource.None, null);

            return new CaretSelection(
                uiaSource == ImeCaretSource.None ? ImeCaretSource.Uia : uiaSource,
                caret);
        }

        /// <summary>
        /// 采集前台窗口与实际焦点信息。实际焦点线程与前台线程不同时，改用焦点线程的
        /// GUI 信息读取插入符（GetGUIThreadInfo 的插入符属于该线程）。
        /// </summary>
        private static Win32FocusState CaptureWin32Focus(IntPtr expectedForeground)
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground != expectedForeground)
                return default;

            uint foregroundThread = GetWindowThreadProcessId(foreground, out uint foregroundPid);
            if (foregroundThread == 0)
                return default;

            var foregroundInfo = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (!GetGUIThreadInfo(foregroundThread, ref foregroundInfo))
                return default;

            bool hasFocus = foregroundInfo.hwndFocus != IntPtr.Zero;
            IntPtr focusHwnd = hasFocus ? foregroundInfo.hwndFocus : foreground;

            uint focusThread = GetWindowThreadProcessId(focusHwnd, out uint focusPid);
            if (focusThread == 0)
            {
                focusHwnd = foreground;
                focusThread = foregroundThread;
                focusPid = foregroundPid;
            }

            // 实际焦点可能位于另一个线程（跨线程宿主控件）：插入符必须按实际焦点线程取。
            GUITHREADINFO focusInfo = foregroundInfo;
            if (focusThread != foregroundThread)
            {
                focusInfo = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                if (!GetGUIThreadInfo(focusThread, ref focusInfo))
                    focusInfo = foregroundInfo;
            }

            return new Win32FocusState(
                foreground, focusHwnd, focusThread, focusPid,
                focusInfo.hwndCaret, focusInfo.rcCaret, hasFocus);
        }

        /// <summary>
        /// 校验并换算 Win32 系统插入符。要求插入符窗口属于实际焦点线程且与焦点窗口
        /// 同属一条宿主链（同一个窗口或父子关系，不接受同根兄弟）；ClientToScreen
        /// 始终以 hwndCaret 为基准；宽到像整行/整个控件的矩形一律拒绝。
        /// </summary>
        private static bool TryGetTrustedWin32Caret(
            in Win32FocusState state, out Rectangle rect, out IntPtr dpiHwnd)
        {
            rect = default;
            dpiHwnd = IntPtr.Zero;

            if (!state.HasFocus || state.CaretHwnd == IntPtr.Zero)
                return false;

            IntPtr caretHwnd = state.CaretHwnd;
            if (!IsWindowVisible(caretHwnd) || IsIconic(caretHwnd))
                return false;

            uint caretThread = GetWindowThreadProcessId(caretHwnd, out _);
            if (caretThread != state.FocusThreadId)
                return false;

            if (!IsCaretWindowRelatedToFocus(caretHwnd, state.FocusHwnd))
                return false;

            if (!GetClientRect(caretHwnd, out RECT client))
                return false;

            RECT rc = state.CaretRect;
            if (rc.Bottom <= rc.Top)
                return false;

            // rcCaret 是插入符窗口的客户区坐标；允许紧贴右/下边界（零宽插入符常见）
            if (rc.Left < client.Left - 2 || rc.Top < client.Top - 2
                || rc.Left > client.Right + 2 || rc.Top > client.Bottom + 2)
            {
                return false;
            }

            var topLeft = new POINT { X = rc.Left, Y = rc.Top };
            var bottomRight = new POINT { X = rc.Right, Y = rc.Bottom };
            if (!ClientToScreen(caretHwnd, ref topLeft) || !ClientToScreen(caretHwnd, ref bottomRight))
                return false;

            // 保留系统插入符宽度；零宽插入符合法，只把零宽补成 1 像素。
            int width = Math.Max(1, bottomRight.X - topLeft.X);
            int height = bottomRight.Y - topLeft.Y;
            if (height <= 0)
                return false;

            var candidate = new Rectangle(topLeft.X, topLeft.Y, width, height);
            if (!IsTrustedWin32CaretRect(candidate))
                return false;

            // 与插入符窗口屏幕矩形相交：窗口已移走/最小化时拒绝，避免返回陈旧坐标。
            if (!GetWindowRect(caretHwnd, out RECT window))
                return false;

            var windowRect = Rectangle.FromLTRB(window.Left, window.Top, window.Right, window.Bottom);
            windowRect.Inflate(2, 2);
            if (!windowRect.IntersectsWith(candidate))
                return false;

            rect = candidate;
            dpiHwnd = caretHwnd;
            return true;
        }

        /// <summary>
        /// 系统插入符宽度可以因辅助功能设置变粗，但不能宽到像整行/整个控件。
        /// 这不是 UIA 范围矩形的可信性判据（那个更严格，见 <see cref="IsTrustedCaretRect"/>）。
        /// </summary>
        private static bool IsTrustedWin32CaretRect(Rectangle rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                return false;

            return rect.Width <= 32;
        }

        /// <summary>读取窗口类名；读不到返回 null（不猜）。</summary>
        private static string? ReadClassName(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return null;

            var buffer = new char[256];
            int length = GetClassName(hwnd, buffer, buffer.Length);
            return length > 0 ? new string(buffer, 0, length) : null;
        }

        /// <summary>
        /// 是否为原生独立 HWND 的 Edit/RichEdit 控件。只有这类控件的 HWND 本身
        /// 足以唯一确定输入框，才允许跳过 UIA 走纯 Win32 快路径；
        /// WPF 的 HwndWrapper、Chromium/Electron 与自定义控件都不算。
        /// </summary>
        internal static bool IsNativeTextClassName(string? className)
        {
            if (string.IsNullOrEmpty(className))
                return false;

            return className.Equals("Edit", StringComparison.OrdinalIgnoreCase)
                || className.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>原生 Edit/RichEdit 的只读样式判定（纯函数，便于单测）。</summary>
        internal static bool IsNativeTextReadOnly(int style)
        {
            return (style & ES_READONLY) != 0;
        }

        /// <summary>
        /// 前后 Win32 焦点状态不一致时的原因分类：前台窗口仍是期望值 → FocusChanged
        /// （同一前台内焦点 HWND/线程/进程变化）；前台已变（含无法读取为 0）→ ForegroundChanged。
        /// </summary>
        internal static ImeProbeReason ClassifyFocusStateChange(
            IntPtr expectedForeground, IntPtr actualForeground)
        {
            return actualForeground == expectedForeground
                ? ImeProbeReason.FocusChanged
                : ImeProbeReason.ForegroundChanged;
        }

        /// <summary>
        /// 身份是否足以区分输入框：原生独立 HWND 的 Edit/RichEdit 靠 HWND；
        /// 其它控件必须提供 UIA RuntimeId，否则同一 HWND 内的不同字段无法区分，不猜。
        /// </summary>
        internal static bool HasSufficientIdentity(bool isNativeTextControl, string? runtimeId)
        {
            return isNativeTextControl || !string.IsNullOrWhiteSpace(runtimeId);
        }

        /// <summary>
        /// 判断 UIA 焦点元素是否属于当前焦点窗口。浏览器/Electron 的 UIA 元素可能来自
        /// 渲染进程，因此不能用 ProcessId 判定；NativeWindowHandle 为 0 的虚拟控件合法。
        /// </summary>
        private static bool IsUiaReadingForFocus(UiaFocusReading reading, in Win32FocusState state)
        {
            if (reading.Hwnd == IntPtr.Zero)
                return true;

            return IsRelatedWindow(reading.Hwnd, state.FocusHwnd)
                || IsRelatedWindow(reading.Hwnd, state.Foreground);
        }

        /// <summary>
        /// 插入符窗口必须与实际焦点同属一条宿主链（同一个窗口、父子关系），
        /// 不接受同根兄弟窗口：那可能是焦点移走后遗留的旧插入符。
        /// </summary>
        private static bool IsCaretWindowRelatedToFocus(IntPtr caretHwnd, IntPtr focusHwnd)
        {
            if (caretHwnd == IntPtr.Zero || focusHwnd == IntPtr.Zero)
                return false;

            if (caretHwnd == focusHwnd)
                return true;

            return IsChild(focusHwnd, caretHwnd) || IsChild(caretHwnd, focusHwnd);
        }

        /// <summary>两个窗口是否相同、互为父子或同根（不跨进程附加，无挂起风险）。</summary>
        private static bool IsRelatedWindow(IntPtr hwnd, IntPtr expected)
        {
            if (hwnd == IntPtr.Zero || expected == IntPtr.Zero)
                return false;

            if (hwnd == expected)
                return true;

            if (IsChild(expected, hwnd) || IsChild(hwnd, expected))
                return true;

            IntPtr rootA = GetAncestor(hwnd, GA_ROOT);
            IntPtr rootB = GetAncestor(expected, GA_ROOT);
            return rootA != IntPtr.Zero && rootA == rootB;
        }

        #region 纯几何/范围校验（可单测）

        /// <summary>
        /// 从 UIA 的 SAFEARRAY(double) 边界矩形中读取「单个可信插入符」。
        /// 只接受恰好一个矩形；空数组、多个矩形、整行/控件宽矩形、非法数值都返回 null。
        /// 宽矩形绝不压成 1px 冒充插入符；零宽插入符（合法的插入点退化表示）补成 1px。
        /// </summary>
        internal static Rectangle? ReadSingleCaretRect(double[]? bounds)
        {
            if (bounds == null || bounds.Length != 4)
                return null;

            for (int i = 0; i < 4; i++)
            {
                if (!double.IsFinite(bounds[i]))
                    return null;
            }

            int height = (int)Math.Round(bounds[3]);
            if (height <= 0)
                return null;

            int width = (int)Math.Round(bounds[2]);
            if (width < 0)
                return null;

            var rect = new Rectangle(
                (int)Math.Round(bounds[0]),
                (int)Math.Round(bounds[1]),
                Math.Max(1, width),
                height);

            // 宽度为 0 是合法插入点；宽度为正时必须通过可信性校验（拒绝整行宽矩形）
            return width == 0 || IsTrustedCaretRect(rect) ? (Rectangle?)rect : null;
        }

        /// <summary>
        /// UIA 范围矩形是否能作为可信插入符：必须窄而高，宽高比明显异常（整行/整个控件）时拒绝。
        /// 零宽插入符合法，保持原样交给调用方补 1px。
        /// </summary>
        internal static bool IsTrustedCaretRect(Rectangle rect)
        {
            if (rect.Height <= 0 || rect.Width < 0)
                return false;

            if (rect.Width == 0)
                return true;

            return rect.Width <= Math.Max(8, rect.Height / 2);
        }

        /// <summary>
        /// 从相邻单字符矩形推导插入符。仅接受从左到右（<paramref name="isRightToLeft"/> 必须为 false）、
        /// 单行、单字符的矩形；插入点必须正好在字符左端或右端。无法证明时返回 null，不猜。
        /// <para>
        /// 注意：当前 UIA 读取路径刻意不调用本方法。ExpandToEnclosingUnit(Character) 在
        /// 软换行/行尾可能选到另一可视行的字符，仅凭 UIA 几何无法严格证明“插入点与邻字
        /// 在同一可视行”；为保守起见该分支已禁用。本方法仅保留为纯几何工具与回归测试。
        /// </para>
        /// </summary>
        /// <param name="neighbor">相邻字符的边界矩形（物理像素）。</param>
        /// <param name="caretAtEnd">插入点是否在该字符右端。</param>
        /// <param name="isRightToLeft">文本方向：false=从左到右；true=从右到左；null=未知。</param>
        internal static Rectangle? DeriveCaretFromNeighbor(
            Rectangle neighbor, bool caretAtEnd, bool? isRightToLeft)
        {
            if (neighbor.Height <= 0 || neighbor.Width <= 0)
                return null;

            // 只处理可证明的 LTR；RTL/混合双向/方向未知都不推导
            if (isRightToLeft != false)
                return null;

            // 单字符矩形不应宽到像整行/多字符（同时容纳 CJK 与空格）
            if (neighbor.Width > Math.Max(16, neighbor.Height * 3))
                return null;

            int x = caretAtEnd ? neighbor.Right : neighbor.Left;
            return new Rectangle(x, neighbor.Top, 1, neighbor.Height);
        }

        /// <summary>
        /// 回退到 TextPattern selection 前的硬性条件：必须恰好一个范围、CompareEndpoints
        /// 证明其起止相同（退化），且边界矩形为空或恰好一个。非空选区绝不折叠到末尾。
        /// </summary>
        /// <param name="rangeCount">selection 中的范围数量。</param>
        /// <param name="compareStartToEnd">同一范围 Start 与 End 的 CompareEndpoints 结果（0=退化）。</param>
        /// <param name="rectCount">边界矩形数量；-1 表示无法解析（拒绝）。</param>
        internal static bool CanUseSelectionAsCaret(int rangeCount, int compareStartToEnd, int rectCount)
        {
            if (rangeCount != 1 || compareStartToEnd != 0)
                return false;

            return rectCount is >= 0 and <= 1;
        }

        #endregion

        #endregion
    }
}
