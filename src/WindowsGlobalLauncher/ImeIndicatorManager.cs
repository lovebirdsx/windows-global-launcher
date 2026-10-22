using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

// 与 WPF 的 Rectangle 区分（本类只用 System.Drawing.Rectangle 的物理像素坐标）
using DrawingRectangle = System.Drawing.Rectangle;

namespace CommandLauncher
{
    /// <summary>一次探测的完整结果：焦点快照 + 与同一焦点身份匹配的输入法状态。</summary>
    internal readonly record struct ImeProbeOutcome(
        ImeFocusSnapshot? Snapshot, ImeQueryResult Ime, bool FocusVerified,
        ImeProbeDiagnostic? Diagnostic = null);

    /// <summary>
    /// 输入法状态提示的运行时编排：输入框获焦 → 查询输入法状态与真实插入符 →
    /// 在插入符旁短暂显示「中 / 英」角标。
    /// <para>
    /// 触发不再依赖键入（没有 TypingBurst/ModeRefresh）：<see cref="ImeFocusWatcher"/>
    /// 的焦点/前台事件经约 80ms 去抖后发起一次探测；切换输入法手势仍保留 120ms 延迟查询。
    /// </para>
    /// <para>
    /// 线程模型：全部状态只在 UI 线程读写；<see cref="ImeFocusWatcher"/> 的信号可能来自
    /// 多个后台线程，回调里只做 Interlocked 序号与 Dispatcher.BeginInvoke；
    /// 真正的跨进程探测（UIA + IME）固定在 <see cref="ImeProbeWorker{T}"/> 的单条后台 MTA
    /// 线程上串行执行，结果再切回 UI 线程核对代次/请求号/前台/开关/录制/截图。
    /// </para>
    /// </summary>
    internal static class ImeIndicatorManager
    {
        /// <summary>焦点通知的合并去抖（只合并焦点检查，前台切换仍立即隐藏）。</summary>
        private const int FocusDebounceMs = 80;

        /// <summary>修饰键松开后延迟检查（等待系统完成输入法切换）。</summary>
        private const int SwitchCheckDelayMs = 120;

        /// <summary>角标显示后自动隐藏的延迟。</summary>
        private const int HideDelayMs = 1000;

        /// <summary>角标显示期间监视前台窗口变化的间隔（切走即隐藏的兜底）。</summary>
        private const int ForegroundWatchMs = 200;

        /// <summary>
        /// 单次探测结果的等待上限：只让本次结果过期；
        /// 底层 COM 仍在执行时 <see cref="ImeProbeWorker{T}.IsBusy"/> 保持为 true，
        /// 绝不会因此另起并行查询。
        /// </summary>
        private const int ProbeTimeoutMs = 300;

        // 修饰键 VK（自愈时用 GetAsyncKeyState 核对物理状态）
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_RSHIFT = 0xA1;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_LMENU = 0xA4;
        private const int VK_RMENU = 0xA5;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        /// <summary>一次探测的触发原因，决定结果如何参与显示判定。</summary>
        private enum ImeCheckReason
        {
            /// <summary>焦点事件：新会话直接显示当前有效模式；重复通知不重显。</summary>
            Focus,

            /// <summary>输入法切换手势：仅在模式变化或新焦点会话时显示。</summary>
            Switch,
        }

        private static KeyboardHook? _hook;
        private static Dispatcher? _dispatcher;
        private static ImeIndicatorWindow? _window;
        private static ImeFocusWatcher? _watcher;
        private static ImeProbeWorker<ImeProbeOutcome>? _worker;

        private static DispatcherTimer? _hideTimer;
        private static DispatcherTimer? _watchTimer;
        private static DispatcherTimer? _switchTimer;
        private static DispatcherTimer? _debounceTimer;
        private static DispatcherTimer? _retryTimer;
        private static DispatcherTimer? _probeTimeoutTimer;

        private static readonly ImeFocusSessionPolicy _policy = new();
        private static readonly ImeKeyObserver _keys =
            new(new ImeSwitchChordTracker(), ReadModifierState);

        private static volatile bool _enabled;
        private static volatile bool _paused;
        private static volatile bool _shutdown;
        private static bool _initialized;

        // 原始信号序号：每个焦点/前台通知在「信号到达」时（可能仍在后台线程）立即递增；
        // 探测发起时记录、完成时核对，因此即使 UI 回调尚未执行，旧结果也会被丢弃。
        private static readonly ImeSignalEpoch _signalEpoch = new();

        // 探测请求号：超时/新请求/禁用/暂停都会递增，迟到的探测结果据此作废。
        private static long _activeRequestId;
        private static long _timeoutRequestId;
        private static uint _probeProcessId;
        private static long _probeSubmittedAt;
        private static readonly ImeDiagnosticThrottle _diagnosticThrottle = new();

        // 切换手势判定的基线（最近一次可靠结果的模式与身份）
        private static string? _lastLabel;
        private static ImeFocusIdentity? _lastLabelIdentity;

        private static IntPtr _shownForeground;

        /// <summary>提示是否启用（托盘菜单可切换）。</summary>
        public static bool IsEnabled => _enabled;

        /// <summary>
        /// 由 SwitcherWindow 在键盘钩子装配完成后调用（UI 线程）。
        /// 首次调用创建计时器、探测线程与焦点监听；重复调用只更新钩子并按当前开关重挂监听器。
        /// </summary>
        public static void Initialize(KeyboardHook hook)
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _hook = hook;

            if (!_initialized)
            {
                _shutdown = false;
                _paused = false;
                CreateTimers();

                try
                {
                    _worker = new ImeProbeWorker<ImeProbeOutcome>(ProbeFocus);
                }
                catch (Exception ex)
                {
                    _worker = null;
                    Logger.LogWarning($"输入法提示：探测线程初始化失败: {ex.Message}");
                }

                try
                {
                    _watcher = new ImeFocusWatcher(OnFocusSignal);
                    _watcher.Start();
                }
                catch (Exception ex)
                {
                    try
                    {
                        _watcher?.Dispose();
                    }
                    catch
                    {
                        // 初始化失败路径的清理同样是 best-effort。
                    }

                    _watcher = null;
                    Logger.LogWarning($"输入法提示：焦点监听初始化失败: {ex.Message}");
                }

                _initialized = true;
                SetEnabledState(AppState.Instance.GetImeIndicatorEnabled(), persist: false);
                Logger.LogInfo($"输入法提示：初始化完成（当前{(_enabled ? "启用" : "禁用")}）");
                return;
            }

            ApplyListenerState();
            SetWatcherEnabled(_enabled && !_paused && !_shutdown);
            Logger.LogInfo($"输入法提示：钩子已更新（当前{(_enabled ? "启用" : "禁用")}）");
        }

        /// <summary>启用/禁用提示（托盘菜单调用），并持久化开关。</summary>
        public static void SetEnabled(bool enabled)
        {
            if (enabled == _enabled)
                return;

            SetEnabledState(enabled, persist: true);
        }

        /// <summary>
        /// 清空组合键追踪状态。快捷键录制会吞掉 keyup，录制前后必须调用，
        /// 否则录制期间松开的修饰键会永远被当成按住。
        /// </summary>
        public static void ResetChordState()
        {
            _keys.Reset();
        }

        /// <summary>
        /// 快捷键录制开始：暂停焦点监听并作废全部待办，避免录制期间的按键/焦点事件
        /// 触发提示。不能只依赖「键盘观察点在录制分支之后」。
        /// </summary>
        public static void PauseForRecording()
        {
            if (_shutdown || _paused)
                return;

            _paused = true;
            ResetRuntimeState();
            ApplyListenerState();
            SetWatcherEnabled(false);
            HideBadge();
            Logger.LogInfo("输入法提示：快捷键录制期间暂停");
        }

        /// <summary>快捷键录制结束：恢复正常焦点监听（重新订阅失败只记 WARN）。</summary>
        public static void ResumeAfterRecording()
        {
            if (_shutdown || !_paused)
                return;

            _paused = false;
            ResetRuntimeState();
            ApplyListenerState();
            SetWatcherEnabled(_enabled);
            Logger.LogInfo("输入法提示：快捷键录制结束，恢复焦点监听");
        }

        /// <summary>进程退出时清理：摘监听器、停计时器、解除焦点订阅、关闭角标。</summary>
        public static void Shutdown()
        {
            _shutdown = true;
            _enabled = false;
            _paused = false;

            // 每一步都独立兜底：任何清理失败都不能阻断 App.OnExit 后续的钩子/剪贴板清理。
            try
            {
                if (_hook != null)
                    _hook.KeyActivityListener = null;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"输入法提示：摘除按键监听失败: {ex.Message}");
            }

            _hook = null;

            ResetRuntimeState();

            try
            {
                _worker?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"输入法提示：释放探测线程失败: {ex.Message}");
            }

            _worker = null;

            try
            {
                _watcher?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"输入法提示：解除焦点监听失败: {ex.Message}");
            }

            _watcher = null;

            HideBadge();

            try
            {
                _window?.Close();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"输入法提示：关闭角标窗失败: {ex.Message}");
            }

            _window = null;
            _dispatcher = null;
            _initialized = false;

            Logger.LogInfo("输入法提示：已关闭");
        }

        /// <summary>
        /// 若角标可见则立即隐藏，并作废在途查询与待办（截图/贴图等需要干净画面的场景）。
        /// 只保证「旧请求/旧结果不会在截图结束后复活」；截图结束后若发生新的获焦
        /// 或切换手势，仍会照常提示（这是期望行为，不做额外抑制）。
        /// 返回 true 表示确实隐藏了一个可见角标——调用方需据此等待 DWM 合成一帧，
        /// 否则立刻抓屏可能仍拿到上一帧里含角标的画面。
        /// </summary>
        public static bool HideIfVisible()
        {
            bool wasVisible = _window?.IsVisible == true;

            try
            {
                // 只作废代次/请求，不清会话：截图结束后旧结果与在途请求不会复活角标；
                // 但新的获焦/切换手势信号仍可正常发起查询并显示（不做时间窗抑制）。
                _policy.Invalidate();
                _activeRequestId++;
                _timeoutRequestId = 0;
                _debounceTimer?.Stop();
                _retryTimer?.Stop();
                _probeTimeoutTimer?.Stop();
                _switchTimer?.Stop();
                _worker?.ClearPending();
                HideBadge();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"输入法提示：截图前隐藏失败: {ex.Message}");
            }

            return wasVisible;
        }

        private static void SetEnabledState(bool enabled, bool persist)
        {
            _enabled = enabled;
            ResetRuntimeState();
            ApplyListenerState();
            SetWatcherEnabled(enabled && !_paused && !_shutdown);

            if (!enabled)
                HideBadge();

            if (persist)
            {
                try
                {
                    AppState.Instance.SetImeIndicatorEnabled(enabled);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"输入法提示：保存开关状态失败: {ex.Message}");
                }
            }

            Logger.LogInfo($"输入法提示：已{(enabled ? "启用" : "禁用")}");
        }

        /// <summary>重置所有运行态（禁用/暂停/退出）：作废旧探测、清会话、停计时器。</summary>
        private static void ResetRuntimeState()
        {
            _policy.Reset();
            _keys.Reset();
            _activeRequestId++;
            _timeoutRequestId = 0;

            StopAllTimers();
            _worker?.ClearPending();

            _lastLabel = null;
            _lastLabelIdentity = null;
            _shownForeground = IntPtr.Zero;
        }

        /// <summary>按当前开关/录制状态挂/摘被动按键监听器（摘掉后钩子可能因无其它用途而自动卸载）。</summary>
        private static void ApplyListenerState()
        {
            if (_hook == null)
                return;

            _hook.KeyActivityListener =
                (_enabled && !_paused && !_shutdown) ? OnKeyActivity : null;
        }

        private static void SetWatcherEnabled(bool enabled)
        {
            try
            {
                _watcher?.SetEnabled(enabled);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"输入法提示：切换焦点监听失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 焦点监听回调（可能来自 watcher 的 MTA 线程或 UIA 事件线程，可能并发）。
        /// 只递增原始序号并投递到 UI 线程，绝不在这里查 UIA/IME。
        /// </summary>
        private static void OnFocusSignal(bool foreground)
        {
            if (_shutdown || !_enabled || _paused)
                return;

            // 前台变化先在信号层置位「待处理」标记再递增序号（顺序见 ImeSignalEpoch），
            // 由最新信号的 UI 回调统一消费；旧回调不修改任何状态。
            long raw = foreground ? _signalEpoch.BumpForeground() : _signalEpoch.Bump();
            Post(() => OnFocusSignalOnUi(raw, foreground));
        }

        /// <summary>UI 线程处理焦点/前台通知。</summary>
        private static void OnFocusSignalOnUi(long raw, bool foreground)
        {
            if (_shutdown || !_enabled || _paused || _dispatcher == null)
                return;

            // 原子核对「是否最新」并取走前台变化标记：两步必须同锁完成，
            // 否则旧回调可能在核对通过后、消费前被新信号超越，抢先取走新标记。
            // 非最新信号的回调直接让位：若它仍去递增策略代次，会把最新通知
            // 已经发起的探测作废，造成漏提示。
            if (!_signalEpoch.TryConsume(raw, out bool foregroundChanged))
                return;

            // 截图遮罩自己会抢前台：这不是用户切走，不能清掉焦点会话。
            // 标记在此消费后直接丢弃，避免它泄漏到截图结束后的处理。
            if (ScreenshotManager.IsCapturing)
            {
                if (foreground)
                    InvalidateProbe();
                return;
            }

            // 自上次消费以来发生过前台变化（标记可能由更早、已被超越的 foreground
            // 信号设置）：由当前这次最新通知统一清会话与对比基线。
            // 「切出 → 立刻切回同一输入框」因此仍按新会话重新提示。
            if (foregroundChanged)
            {
                HideBadge();
                _policy.OnForegroundChanged();
                _lastLabel = null;
                _lastLabelIdentity = null;
                _switchTimer?.Stop();
            }

            if (foreground)
            {
                InvalidateProbe();
                StartDebounce();
                return;
            }

            _policy.BeginEvent();
            InvalidateProbe();
            StartDebounce();
        }

        /// <summary>作废在途探测与待办（不隐藏角标、不动会话）。</summary>
        private static void InvalidateProbe()
        {
            _activeRequestId++;
            _timeoutRequestId = 0;
            _debounceTimer?.Stop();
            _retryTimer?.Stop();
            _probeTimeoutTimer?.Stop();
            _worker?.ClearPending();
        }

        private static void StartDebounce()
        {
            _debounceTimer?.Stop();
            _debounceTimer?.Start();
        }

        /// <summary>
        /// 发起一次真实探测（UI 线程）。请求号递增后，旧请求的迟到结果一律作废；
        /// 底层仍是单线程单飞，繁忙时最新请求只替换待办。
        /// </summary>
        private static void StartProbe(long epoch, ImeCheckReason reason)
        {
            if (_shutdown || !_enabled || _paused)
                return;

            // 截图期间绝不创建新的探测任务。
            if (ScreenshotManager.IsCapturing)
                return;

            ImeProbeWorker<ImeProbeOutcome>? worker = _worker;
            if (worker == null)
                return;

            if (!_policy.IsCurrent(epoch))
                return;

            if (reason == ImeCheckReason.Switch)
            {
                // 明确的切换手势优先于尚未完成的重试/去抖。
                _debounceTimer?.Stop();
                _retryTimer?.Stop();
            }

            IntPtr foreground = ForegroundActivator.GetForeground();
            if (foreground == IntPtr.Zero)
            {
                // 锁屏/无前台窗口：不显示，也不发起跨进程查询。
                HideBadge();
                return;
            }

            long requestId = ++_activeRequestId;
            _timeoutRequestId = requestId;
            GetWindowThreadProcessId(foreground, out _probeProcessId);
            _probeSubmittedAt = Stopwatch.GetTimestamp();
            _probeTimeoutTimer?.Stop();
            _probeTimeoutTimer?.Start();

            // 记录发起探测时的原始信号序号：完成回调必须核对，
            // 因为新信号的 UI 回调可能还排在 dispatcher 队列里，策略代次尚未递增。
            long rawEpoch = _signalEpoch.Capture();

            worker.Request(
                requestId,
                foreground,
                result => Post(() => OnProbeCompleted(epoch, rawEpoch, requestId, reason, result)));
        }

        /// <summary>
        /// 探测线程上的真实查询：快照（含严格插入符定位）→ Win32 焦点核对 →
        /// 按真实焦点线程/句柄查询输入法 → 再核对一次焦点。
        /// </summary>
        private static ImeProbeOutcome ProbeFocus(IntPtr foreground)
        {
            ImeProbeDiagnostic diagnostic = ImeProbeDiagnostic.For(ImeProbeReason.ProbeException);
            try
            {
                ImeFocusSnapshot? snapshot = CaretLocator.TryCaptureFocusSnapshot(foreground, out diagnostic);
                if (snapshot is null)
                    return new ImeProbeOutcome(null, default, false, diagnostic);

                if (snapshot.Identity.Foreground != foreground)
                    return new ImeProbeOutcome(null, default, false,
                        diagnostic with { Reason = ImeProbeReason.ForegroundChanged });

                // 查询前后均核对实际焦点；诊断只带原因，不保留输入内容。
                bool focusAvailable = ImeStatusReader.TryGetFocusWindow(
                    foreground, out IntPtr focus, out uint thread, out _);
                if (!focusAvailable || focus != snapshot.Identity.FocusHwnd ||
                    thread != snapshot.Identity.FocusThreadId)
                {
                    return new ImeProbeOutcome(null, default, false,
                        diagnostic with { Reason = focusAvailable ? ImeProbeReason.FocusChanged : ImeProbeReason.NoFocus });
                }

                ImeQueryResult ime = ImeStatusReader.Query(
                    foreground, snapshot.Identity.FocusHwnd, snapshot.Identity.FocusThreadId);

                focusAvailable = ImeStatusReader.TryGetFocusWindow(
                    foreground, out focus, out thread, out _);
                if (!focusAvailable || focus != snapshot.Identity.FocusHwnd ||
                    thread != snapshot.Identity.FocusThreadId)
                {
                    return new ImeProbeOutcome(null, default, false,
                        diagnostic with { Reason = focusAvailable ? ImeProbeReason.FocusChanged : ImeProbeReason.NoFocus });
                }

                return new ImeProbeOutcome(snapshot, ime, true, diagnostic);
            }
            catch (Exception ex)
            {
                // provider 异常消息可能夹带内容，只留下 HRESULT。
                return new ImeProbeOutcome(null, default, false,
                    diagnostic with { Reason = ImeProbeReason.ProbeException, HResult = ex.HResult });
            }
        }

        /// <summary>探测结果回到 UI 线程：核对代次/请求号/开关/录制/前台/截图后再处理。</summary>
        private static void OnProbeCompleted(
            long epoch,
            long rawEpoch,
            long requestId,
            ImeCheckReason reason,
            ImeProbeResult<ImeProbeOutcome> result)
        {
            if (_shutdown || !_enabled || _paused)
                return;

            // 探测发起后又有新的原始焦点/前台信号到达（即使它的 UI 回调还没执行），
            // 本次结果就可能属于旧焦点：用信号到达瞬间递增的序号立即作废，
            // 避免把上一个输入框（旧 RuntimeId）的角标显示出来。
            if (!_signalEpoch.IsCurrent(rawEpoch) || requestId != _activeRequestId || !_policy.IsCurrent(epoch))
            {
                LogProbeResult(result, overrideReason: "Expired");
                return;
            }

            if (ScreenshotManager.IsCapturing)
                return;

            if (_timeoutRequestId == requestId)
                _probeTimeoutTimer?.Stop();

            ImeProbeOutcome outcome = result.Value;
            if (!outcome.FocusVerified || outcome.Snapshot is not { } snapshot)
            {
                // 先执行已有的清会话/隐藏，再写日志：获焦分支确实隐藏了记 Hidden；
                // 切换手势在这些校验失败时不做任何处置，保持 Unhandled。
                ImeHandleResult? earlyHandled = null;
                if (reason == ImeCheckReason.Focus)
                {
                    _policy.ClearSession();
                    HideBadge();
                    _lastLabel = null;
                    _lastLabelIdentity = null;
                    earlyHandled = ImeHandleResult.Hidden;
                }

                LogProbeResult(result, earlyHandled);
                return;
            }

            // 探测期间前台若已变化，结果作废（焦点 watcher 会另发通知）。
            if (ForegroundActivator.GetForeground() != snapshot.Identity.Foreground)
            {
                ImeHandleResult? earlyHandled = null;
                if (reason == ImeCheckReason.Focus)
                {
                    _policy.ClearSession();
                    HideBadge();
                    _lastLabel = null;
                    _lastLabelIdentity = null;
                    earlyHandled = ImeHandleResult.Hidden;
                }

                LogProbeResult(result, earlyHandled, "ForegroundChanged");
                return;
            }

            string? finalReason = snapshot.IsEditable && snapshot.Caret.HasValue
                ? (!outcome.Ime.Trustworthy ? "ImeUntrusted"
                    : ImeStatusResolver.ToText(outcome.Ime.Display).Length == 0 ? "NoDisplayMode" : null)
                : null;
            // 先完成显示/隐藏决策，低频同步日志不能挡在有效提示之前。
            // 处理器返回实际处置结果（Show 决策不等于已显示），作为单条日志字段。
            ImeHandleResult finalHandled = reason == ImeCheckReason.Focus
                ? HandleFocusProbe(epoch, snapshot, outcome.Ime)
                : HandleSwitchProbe(snapshot, outcome.Ime);
            LogProbeResult(result, finalHandled, finalReason);
        }

        /// <summary>焦点探测的薄包装：真实操作交给 <see cref="ImeProbeHandler"/>，便于用假操作单测。</summary>
        private static ImeHandleResult HandleFocusProbe(
            long epoch, ImeFocusSnapshot snapshot, ImeQueryResult ime)
        {
            return ImeProbeHandler.HandleFocus(
                _policy, epoch, snapshot, ime, HideBadge, ShowBadge, ScheduleRetry,
                ref _lastLabel, ref _lastLabelIdentity);
        }

        /// <summary>切换手势的薄包装：真实操作交给 <see cref="ImeProbeHandler"/>，便于用假操作单测。</summary>
        private static ImeHandleResult HandleSwitchProbe(ImeFocusSnapshot snapshot, ImeQueryResult ime)
        {
            return ImeProbeHandler.HandleSwitch(
                _policy, snapshot, ime, HideBadge, ShowBadge,
                ref _lastLabel, ref _lastLabelIdentity);
        }

        /// <summary>按策略延迟重试（同一焦点会话最多两次），返回是否真的排入计时器。</summary>
        private static bool ScheduleRetry(int delayMs)
        {
            if (_retryTimer == null || delayMs <= 0)
                return false;

            _retryTimer.Stop();
            _retryTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
            _retryTimer.Start();
            return true;
        }

        /// <summary>显示角标并重启自动隐藏与前台监视计时器。</summary>
        private static bool ShowBadge(
            string text, DrawingRectangle caret, IntPtr dpiHwnd, ImeFocusIdentity identity)
        {
            if (text.Length == 0 || _shutdown || !_enabled || _paused)
                return false;

            if (ScreenshotManager.IsCapturing)
                return false;

            try
            {
                _window ??= new ImeIndicatorWindow();

                IntPtr hwnd = dpiHwnd != IntPtr.Zero ? dpiHwnd : identity.Foreground;
                _window.ShowAt(text, caret, hwnd);

                _shownForeground = identity.Foreground;
                _hideTimer?.Stop();
                _hideTimer?.Start();
                _watchTimer?.Stop();
                _watchTimer?.Start();
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("输入法提示：显示角标失败", ex);
                _window = null; // 已关闭的窗口不能再 Show，下次重建
                return false;
            }
        }

        /// <summary>只隐藏可见角标，不动作废/会话状态（自动隐藏计时器与决策路径共用）。</summary>
        private static void HideWindowOnly()
        {
            try
            {
                _window?.HideBadge();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"输入法提示：隐藏角标失败: {ex.Message}");
            }

            _shownForeground = IntPtr.Zero;
        }

        /// <summary>隐藏角标并停掉自动隐藏/前台监视计时器（不动探测请求）。</summary>
        private static void HideBadge()
        {
            try
            {
                _hideTimer?.Stop();
                _watchTimer?.Stop();
                HideWindowOnly();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"输入法提示：隐藏角标失败: {ex.Message}");
            }
        }

        private static void CreateTimers()
        {
            _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HideDelayMs) };
            _hideTimer.Tick += (s, e) =>
            {
                // 计时隐藏只隐藏：不触碰探测请求/会话，避免破坏正在进行的新查询。
                _hideTimer?.Stop();
                _watchTimer?.Stop();
                HideWindowOnly();
            };

            _watchTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(ForegroundWatchMs),
            };
            _watchTimer.Tick += (s, e) =>
            {
                if (_window == null || !_window.IsVisible)
                {
                    _watchTimer?.Stop();
                    return;
                }

                if (ForegroundActivator.GetForeground() != _shownForeground)
                    HideBadge();
            };

            _switchTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(SwitchCheckDelayMs),
            };
            _switchTimer.Tick += (s, e) =>
            {
                _switchTimer?.Stop();
                if (_shutdown || !_enabled || _paused)
                    return;

                StartProbe(_policy.CurrentEpoch, ImeCheckReason.Switch);
            };

            _debounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(FocusDebounceMs),
            };
            _debounceTimer.Tick += (s, e) =>
            {
                _debounceTimer?.Stop();
                if (_shutdown || !_enabled || _paused || ScreenshotManager.IsCapturing)
                    return;

                StartProbe(_policy.CurrentEpoch, ImeCheckReason.Focus);
            };

            _retryTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(ImeFocusSessionPolicy.FirstRetryDelayMs),
            };
            _retryTimer.Tick += (s, e) =>
            {
                _retryTimer?.Stop();
                if (_shutdown || !_enabled || _paused || ScreenshotManager.IsCapturing)
                    return;

                StartProbe(_policy.CurrentEpoch, ImeCheckReason.Focus);
            };

            _probeTimeoutTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(ProbeTimeoutMs),
            };
            _probeTimeoutTimer.Tick += (s, e) =>
            {
                _probeTimeoutTimer?.Stop();

                // 只让本次结果过期；worker 的占用标记不会被清除，
                // 底层 COM 仍在执行时也不会另起并行任务。
                if (_timeoutRequestId != _activeRequestId)
                    return;

                // 只读观察请求自己的状态，不能拿别的请求占用中的 IsBusy 判断本次在执行。
                var state = _worker?.GetRequestState(_timeoutRequestId) ?? ImeProbeRequestState.None;
                string timeoutReason = state == ImeProbeRequestState.Running ? "TimeoutRunning"
                    : state == ImeProbeRequestState.Queued ? "TimeoutQueued" : "TimeoutCompletionPending";
                _activeRequestId++;
                // 上方已确认没有更新请求；仅取消本次超时且注定作废的待办，不释放正在执行的 COM。
                _worker?.ClearPending();
                WriteProbeDiagnostic(_probeProcessId, timeoutReason,
                    $"request={_timeoutRequestId}，等待={Stopwatch.GetElapsedTime(_probeSubmittedAt).TotalMilliseconds:F0}ms");
            };
        }

        private static void StopAllTimers()
        {
            _hideTimer?.Stop();
            _watchTimer?.Stop();
            _switchTimer?.Stop();
            _debounceTimer?.Stop();
            _retryTimer?.Stop();
            _probeTimeoutTimer?.Stop();
        }

        /// <summary>
        /// 钩子回调：被动观察按键（不吞键）。所有 keydown/keyup 都喂给
        /// <see cref="ImeKeyObserver"/>；只保留切换手势触发，不再有键入触发。
        /// </summary>
        private static void OnKeyActivity(int vk, bool isDown)
        {
            if (_shutdown || !_enabled || _paused)
                return;

            long now = Environment.TickCount64;
            if (_keys.OnKey(vk, isDown, now))
            {
                // 独立的修饰键点击 / Ctrl+Space / Win+Space：
                // 延迟一小段等系统完成切换后再查（仍会重新取插入符）。
                _switchTimer?.Stop();
                _switchTimer?.Start();
            }
        }

        /// <summary>用 GetAsyncKeyState 读取修饰键物理状态（供低频自愈）。</summary>
        private static ImeModifierState ReadModifierState()
        {
            return new ImeModifierState(
                IsAnyPhysicallyDown(VK_LSHIFT, VK_RSHIFT, VK_SHIFT),
                IsAnyPhysicallyDown(VK_LCONTROL, VK_RCONTROL, VK_CONTROL),
                IsAnyPhysicallyDown(VK_LMENU, VK_RMENU, VK_MENU),
                IsAnyPhysicallyDown(VK_LWIN, VK_RWIN));
        }

        private static bool IsAnyPhysicallyDown(int vk1, int vk2, int vk3 = 0)
        {
            return IsPhysicallyDown(vk1)
                || IsPhysicallyDown(vk2)
                || (vk3 != 0 && IsPhysicallyDown(vk3));
        }

        private static bool IsPhysicallyDown(int vk)
        {
            // 高位为 1 表示当前物理按下
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        /// <summary>把动作调度到 UI 线程（钩子/监听回调本就不在 UI 线程时也安全）。</summary>
        private static void Post(Action action)
        {
            try
            {
                _dispatcher?.BeginInvoke(new Action(action));
            }
            catch (Exception ex)
            {
                Logger.LogError("输入法提示：调度任务失败", ex);
            }
        }

        /// <summary>
        /// 记录纯元数据；类名虽由 provider 提供，也不直接写出，避免混入任意文本。
        /// 处理结果只记内部有限枚举（未进入处理器时为 Unhandled），
        /// 其余只保留我们自己的请求号与耗时。
        /// </summary>
        private static void LogProbeResult(
            ImeProbeResult<ImeProbeOutcome> result,
            ImeHandleResult? handled = null,
            string? overrideReason = null)
        {
            var diagnostic = result.Value.Diagnostic ?? result.Value.Snapshot?.Diagnostic
                ?? ImeProbeDiagnostic.For(ImeProbeReason.ProbeException);
            string reason = overrideReason ?? (result.QueryFailed ? "WorkerException" : diagnostic.Reason.ToString());
            bool success = overrideReason == null && !result.QueryFailed && diagnostic.Succeeded;

            // 限频键纳入控件类型与实际处置结果：同 PID 下 Custom 的拒绝
            // 不会遮蔽 ComboBox 的记录，也不会把 Show 与 Ignore 合并。
            // 键只用于限频，日志里的 reason 仍保留纯原因。
            string throttleKey = ImeProbeLog.BuildThrottleKey(
                success, diagnostic.Source, reason, diagnostic.ControlType, handled);
            string details = ImeProbeLog.BuildDetails(
                result.RequestId, result.QueueWait, result.QueryTime, diagnostic, handled);

            WriteProbeDiagnostic(diagnostic.ProcessId, reason, details,
                success || reason == "Expired" || reason == "NotEditable" || reason == "NoDisplayMode",
                throttleKey);
        }

        private static void WriteProbeDiagnostic(
            uint processId,
            string reason,
            string details,
            bool informational = false,
            string? throttleKey = null)
        {
            // 诊断失败绝不能阻断本来有效的提示，或改变超时/单飞状态。
            try
            {
                if (!_diagnosticThrottle.ShouldWrite(processId, throttleKey ?? reason, Environment.TickCount64))
                    return;
                string message = ImeProbeLog.BuildMessage(processId, reason, details);
                if (informational)
                    Logger.LogInfo(message);
                else
                    Logger.LogWarning(message);
            }
            catch
            {
                // 日志为 best-effort，不再递归记录异常。
            }
        }
    }
}
