using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace CommandLauncher
{
    /// <summary>
    /// 输入焦点事件观察器。只负责发信号，不读取任何元素属性、不查询 IME、不做同步 UIA 调用。
    /// <para>
    /// 固定一条后台 MTA 线程（带消息泵）负责 WinEvent 钩子的安装/卸载与 UIA FocusChanged 的
    /// 注册/退订；快速开关由该线程串行收敛到最新 desired 状态，不会重复订阅。
    /// </para>
    /// <para>
    /// 刻意不按本进程排除：本程序自己的搜索框（WPF TextBox）也必须能触发提示。
    /// </para>
    /// <para>
    /// 回调 <c>signal</c> 可能从 watcher 线程或 UIA 事件线程并发到达，实现必须极轻量
    /// （只做标记/Dispatcher.BeginInvoke），绝不能在其中查询 UIA/输入法。
    /// </para>
    /// </summary>
    internal sealed class ImeFocusWatcher : IDisposable
    {
        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint EVENT_OBJECT_FOCUS = 0x8005;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WM_APPLY_SUBSCRIPTION = 0x8000 + 1; // WM_APP + 1
        private const uint PM_NOREMOVE = 0x0000;

        private delegate void WinEventProc(
            IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(
            out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private readonly Action<bool> _signal;
        private readonly Lifecycle _lifecycle = new Lifecycle();

        // 委托必须用字段强引用，否则会被 GC 回收导致钩子/事件静默失效
        private readonly AutomationFocusChangedEventHandler _uiaFocusHandler;
        private readonly WinEventProc _winEventProc;

        private uint _threadId;
        private int _disposed; // 0/1；用 Volatile/Interlocked 访问，避免 volatile 字段传 ref 的警告
        private volatile bool _desiredEnabled;

        /// <summary>
        /// Watcher 线程生命周期：同一时刻最多一个线程；只有旧线程完成退订与清理后才释放名额。
        /// <para>
        /// 清理期间（<see cref="BeginCleanup"/> 之后）到达的显式启用无法由正在退出的线程服务，
        /// 会登记为待启动请求，等旧线程彻底退订、清空 threadId 后由 <see cref="ThreadMain"/>
        /// 消费<b>一次</b>；线程正常运行时的重复启用只走唤醒、不登记（因此普通重复启用不会
        /// 在之后异常退出时变成自动重启）。<see cref="CancelPendingStart"/> 用于禁用/Dispose
        /// 取消待启动，<see cref="AbortStart"/> 保证启动失败不留下重试请求。
        /// </para>
        /// <para>
        /// 刻意不自动重启：没有清理期间的新显式启用时，线程退出后保持空闲；异常退出后必须由
        /// 下一次显式 SetEnabled(true) 恢复，避免持续失败时形成重启循环。
        /// </para>
        /// </summary>
        internal sealed class Lifecycle
        {
            private readonly object _gate = new();

            // 状态（锁内读写）：空闲 / 线程运行中 / 线程正在退订清理。
            private bool _running;
            private bool _cleaning;

            // 清理期间登记的新显式启用：只消费一次，且常规重复启用绝不会置位。
            private bool _restartPending;

            /// <summary>
            /// 尝试占用线程名额。空闲时成功；线程运行中直接拒绝（不登记，避免普通重复启用
            /// 变成异常退出后的自动重启）；正在清理时拒绝并登记一次待启动请求。
            /// </summary>
            public bool TryBeginStart()
            {
                lock (_gate)
                {
                    if (!_running && !_cleaning)
                    {
                        _running = true;
                        _restartPending = false;
                        return true;
                    }

                    if (_cleaning)
                        _restartPending = true;

                    return false;
                }
            }

            /// <summary>
            /// 线程开始退订/清理（ThreadMain finally 的第一行）。必须早于耗时可能很长的
            /// Unsubscribe：此后到达的显式启用才会被登记，不会随旧线程一起沉没。
            /// </summary>
            public void BeginCleanup()
            {
                lock (_gate)
                {
                    if (_running)
                        _cleaning = true;
                }
            }

            /// <summary>
            /// 旧线程完成 Unsubscribe、清空 _threadId 之后调用；释放名额并返回清理期间是否
            /// 有新的显式启用需要消费（true = 调用方应再启动一个线程）。
            /// 必须排在所有退订/清理动作之后，否则新线程可能与旧线程的退订并发，
            /// 造成同一 UIA 委托被先加后删或重复订阅。
            /// </summary>
            public bool CompleteThreadCleanup()
            {
                lock (_gate)
                {
                    _running = false;
                    _cleaning = false;
                    bool pending = _restartPending;
                    _restartPending = false;
                    return pending;
                }
            }

            /// <summary>取消待启动请求（SetEnabled(false)/Dispose 调用），清理结束后不再重启。</summary>
            public void CancelPendingStart()
            {
                lock (_gate)
                    _restartPending = false;
            }

            /// <summary>启动失败：释放名额并清掉待启动请求；失败路径绝不自动重试。</summary>
            public void AbortStart()
            {
                lock (_gate)
                {
                    _running = false;
                    _cleaning = false;
                    _restartPending = false;
                }
            }
        }

        // 以下状态只在 watcher 线程读写
        private bool _uiaSubscribed;
        private IntPtr _foregroundHook;
        private IntPtr _focusHook;

        /// <param name="signal">
        /// true=前台窗口事件，false=焦点事件（WinEvent EVENT_OBJECT_FOCUS 或 UIA FocusChanged）。
        /// 回调可能并发、可能不在 UI 线程，必须自行切回 UI 线程。
        /// </param>
        public ImeFocusWatcher(Action<bool> signal)
        {
            _signal = signal ?? throw new ArgumentNullException(nameof(signal));
            _uiaFocusHandler = OnUiaFocusChanged;
            _winEventProc = OnWinEvent;
        }

        /// <summary>启动固定后台 MTA 线程。幂等、非阻塞（不等待线程就绪）。</summary>
        public void Start()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            // 线程运行中直接拒绝；正在清理时由 Lifecycle 登记待启动，等清理结束后消费。
            if (!_lifecycle.TryBeginStart())
                return;

            try
            {
                var thread = new Thread(ThreadMain)
                {
                    IsBackground = true,
                    Name = "ImeFocusWatcher",
                };
                thread.SetApartmentState(ApartmentState.MTA);
                thread.Start();
            }
            catch (Exception ex)
            {
                // 线程没有真正跑起来：释放名额并清掉任何待启动请求。
                // 这里不自动重试，避免持续失败时形成重启循环。
                _lifecycle.AbortStart();
                Logger.LogWarning($"输入法提示：焦点事件线程启动失败: HRESULT=0x{ex.HResult:X8}");
            }
        }

        /// <summary>
        /// 启用/停用订阅。幂等、非阻塞：只更新 desired 状态并唤醒订阅线程，绝不在调用线程等 COM。
        /// 每次启用且至少一种订阅成功时都会补发一次 <c>signal(false)</c> 供调用方检查当前焦点
        /// （重复信号由调用方去抖/去重）。
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            _desiredEnabled = enabled;

            if (enabled)
            {
                Start(); // 允许先 SetEnabled(true) 再 Start()；Start 本身幂等
            }
            else
            {
                // 禁用必须取消清理期间登记的待启动请求：旧线程随后清理完成也不得自动重启。
                _lifecycle.CancelPendingStart();
            }

            WakeSubscriptionThread();
        }

        /// <summary>
        /// 停止订阅并结束后台线程。幂等、非阻塞：不 Join、不等待 UIA/COM。
        /// 实际退订在 watcher 线程上完成；线程是后台线程，不阻止进程退出。
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _desiredEnabled = false;
            _lifecycle.CancelPendingStart();
            WakeSubscriptionThread();
        }

        private void WakeSubscriptionThread()
        {
            uint threadId = Volatile.Read(ref _threadId);
            if (threadId != 0)
                PostThreadMessage(threadId, WM_APPLY_SUBSCRIPTION, IntPtr.Zero, IntPtr.Zero);
        }

        private void ThreadMain()
        {
            try
            {
                // 关键：先 PeekMessage 建立线程消息队列，再发布 _threadId。
                // 否则 SetEnabled 在“已发布 ID、尚未 GetMessage”的窗口调用 PostThreadMessage，
                // 会因目标线程消息队列不存在而失败，启用请求永久丢失（初始 disabled 时尤其明显）。
                PeekMessage(out MSG _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
                Volatile.Write(ref _threadId, GetCurrentThreadId());

                ApplySubscription();

                while (Volatile.Read(ref _disposed) == 0)
                {
                    int result = GetMessage(out MSG message, IntPtr.Zero, 0, 0);
                    if (result == 0 || result == -1)
                    {
                        // 消息泵失败：线程即将退出。先进入清理态，避免从这里到 finally
                        // 之间到达的显式启用被误判为「线程正常运行」而丢失。
                        _lifecycle.BeginCleanup();
                        break;
                    }

                    if (message.message == WM_APPLY_SUBSCRIPTION)
                    {
                        ApplySubscription();
                        continue;
                    }

                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            }
            catch (Exception ex)
            {
                // 异常路径同样先进入清理态再记日志（日志是同步文件 I/O，可能耗时），
                // 否则这段时间到达的显式启用会因 _cleaning 仍为 false 而无法登记。
                _lifecycle.BeginCleanup();
                Logger.LogWarning($"输入法提示：焦点事件线程异常退出: HRESULT=0x{ex.HResult:X8}");
            }
            finally
            {
                // 顺序不可颠倒：先宣告进入清理（此后到达的显式启用会被登记，不能等
                // Unsubscribe 之后——退订耗时窗口内到达的启用会随旧线程一起丢失），
                // 再退订 UIA/WinEvent，再清 threadId，最后才释放生命周期名额。
                // 这样新线程只会在旧线程清理完成后才开始，不会与旧线程的退订并发，
                // 也不会出现同一 UIA 委托被新线程 Add、旧线程 Remove 的重复订阅竞态。
                _lifecycle.BeginCleanup();

                Unsubscribe();
                Volatile.Write(ref _threadId, 0);
                bool restartRequested = _lifecycle.CompleteThreadCleanup();

                // 只消费「清理期间登记的新显式启用」。Dispose/SetEnabled(false) 会取消待启动，
                // 异常退出且期间无人启用时 restartRequested=false，因此不会自动重启循环；
                // 这里再核对 disposed/desired，覆盖取消动作发生在 CompleteThreadCleanup 之后的情况。
                if (restartRequested && Volatile.Read(ref _disposed) == 0 && _desiredEnabled)
                    Start();
            }
        }

        /// <summary>
        /// 按最新 desired 状态串行应用订阅。只在 watcher 线程调用，
        /// 因此快速关开不会造成重复订阅，也不会与退订并发。
        /// </summary>
        private void ApplySubscription()
        {
            bool want = _desiredEnabled && Volatile.Read(ref _disposed) == 0;
            if (!want)
            {
                Unsubscribe();
                return;
            }

            bool subscribed = EnsureSubscribed();

            // 每次 SetEnabled(true) 都补发一次焦点信号：快速暂停/恢复（快捷键录制）即使
            // 中间的 disable 被合并掉，也不会漏掉恢复后对当前焦点的检查。
            // 管理器侧有去抖与身份去重，重复信号不会重复显示。
            if (subscribed)
                RaiseSignal(foregroundEvent: false);
        }

        private bool EnsureSubscribed()
        {
            bool hadUia = _uiaSubscribed;
            bool hadForeground = _foregroundHook != IntPtr.Zero;
            bool hadFocus = _focusHook != IntPtr.Zero;

            // UIA FocusChanged：覆盖浏览器/Electron 中同一 HWND 内的不同虚拟控件。
            // 必须与退订在同一线程上完成。
            if (!_uiaSubscribed)
            {
                try
                {
                    Automation.AddAutomationFocusChangedEventHandler(_uiaFocusHandler);
                    _uiaSubscribed = true;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"输入法提示：UIA 焦点订阅失败: HRESULT=0x{ex.HResult:X8}");
                }
            }

            // 精确订阅两个 WinEvent：前台切换与对象焦点。
            // 不订阅 foreground→focus 的整个事件区间，也不按进程/本进程过滤。
            if (_foregroundHook == IntPtr.Zero)
            {
                try
                {
                    _foregroundHook = SetWinEventHook(
                        EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                        IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
                    if (_foregroundHook == IntPtr.Zero)
                        Logger.LogWarning("输入法提示：前台事件订阅失败（SetWinEventHook 返回 0）");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"输入法提示：前台事件订阅失败: HRESULT=0x{ex.HResult:X8}");
                }
            }

            if (_focusHook == IntPtr.Zero)
            {
                try
                {
                    _focusHook = SetWinEventHook(
                        EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS,
                        IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
                    if (_focusHook == IntPtr.Zero)
                        Logger.LogWarning("输入法提示：焦点事件订阅失败（SetWinEventHook 返回 0）");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"输入法提示：焦点事件订阅失败: HRESULT=0x{ex.HResult:X8}");
                }
            }

            if (hadUia != _uiaSubscribed || hadForeground != (_foregroundHook != IntPtr.Zero) ||
                hadFocus != (_focusHook != IntPtr.Zero))
            {
                Logger.LogInfo($"输入法提示：焦点订阅状态 UIA={_uiaSubscribed}，前台={_foregroundHook != IntPtr.Zero}，焦点={_focusHook != IntPtr.Zero}");
            }

            return _uiaSubscribed || _foregroundHook != IntPtr.Zero || _focusHook != IntPtr.Zero;
        }

        private void Unsubscribe()
        {
            if (_uiaSubscribed)
            {
                try
                {
                    Automation.RemoveAutomationFocusChangedEventHandler(_uiaFocusHandler);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"输入法提示：UIA 焦点退订失败: HRESULT=0x{ex.HResult:X8}");
                }

                _uiaSubscribed = false;
            }

            if (_foregroundHook != IntPtr.Zero)
            {
                try
                {
                    UnhookWinEvent(_foregroundHook);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"输入法提示：前台事件退订失败: HRESULT=0x{ex.HResult:X8}");
                }

                _foregroundHook = IntPtr.Zero;
            }

            if (_focusHook != IntPtr.Zero)
            {
                try
                {
                    UnhookWinEvent(_focusHook);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"输入法提示：焦点事件退订失败: HRESULT=0x{ex.HResult:X8}");
                }

                _focusHook = IntPtr.Zero;
            }
        }

        /// <summary>WinEvent 回调（watcher 线程）：只发信号，不读任何属性。</summary>
        private void OnWinEvent(
            IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            RaiseSignal(foregroundEvent: eventType == EVENT_SYSTEM_FOREGROUND);
        }

        /// <summary>UIA FocusChanged 回调（可能由 UIA 事件线程投递）：只发信号。</summary>
        private void OnUiaFocusChanged(object sender, AutomationFocusChangedEventArgs e)
        {
            RaiseSignal(foregroundEvent: false);
        }

        private void RaiseSignal(bool foregroundEvent)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_desiredEnabled)
                return;

            try
            {
                _signal(foregroundEvent);
            }
            catch
            {
                // 信号回调由调用方实现；这里 best effort，绝不因为它影响事件分发
            }
        }
    }
}
