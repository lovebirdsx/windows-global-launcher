using System;
using System.Diagnostics;
using System.Threading;

namespace CommandLauncher
{
    /// <summary>探测请求在队列中的真实状态（用于诊断/测试，区别于全局 <see cref="ImeProbeWorker{T}.IsBusy"/>）。</summary>
    internal enum ImeProbeRequestState
    {
        /// <summary>没有该请求号：未提交、已被覆盖、已取消或已完成。</summary>
        None,

        /// <summary>已提交但尚未被工作线程取走执行（等待中，可能被后续请求覆盖）。</summary>
        Queued,

        /// <summary>已被工作线程取走，正在执行真实查询。</summary>
        Running,
    }

    /// <summary>
    /// 一次后台探测的完成结果（纯数据，可能从工作线程回调）。
    /// <para>
    /// 前三个位置参数保持旧契约不变；时长与失败标记作为附加 init 属性，
    /// 旧调用方按三参数构造/解构仍可正常编译。
    /// </para>
    /// </summary>
    internal readonly record struct ImeProbeResult<T>(long RequestId, IntPtr Foreground, T Value)
    {
        /// <summary>从提交到被工作线程取走执行之间等待的时长。</summary>
        public TimeSpan QueueWait { get; init; }

        /// <summary>真实查询执行本身的时长。</summary>
        public TimeSpan QueryTime { get; init; }

        /// <summary>真实查询是否抛出了异常（此时 <see cref="Value"/> 为默认值）。</summary>
        public bool QueryFailed { get; init; }
    }

    /// <summary>
    /// 输入法焦点探测的单飞工作线程（小型、可注入假后端、可单测）。
    /// <para>
    /// 固定一条后台 MTA 线程串行执行探测；同一时刻只有一个真实查询在跑，
    /// 繁忙期间的新请求只保留<b>最新一条</b>待办（旧的直接丢弃，不排队堆积）。
    /// 占用标记 <see cref="IsBusy"/> 只在真实查询返回后才清除——调用方 300ms 超时
    /// 只能作废自己的结果，不能把仍在执行的 COM 调用当成已结束而另起并行任务。
    /// </para>
    /// <para>
    /// <see cref="Dispose"/> 不 Join、不等待正在执行的查询，线程是后台线程，
    /// 因此退出/禁用路径绝不会在 UI 线程上被阻塞的 COM 调用拖住。查询线程与
    /// 订阅生命周期线程无关，查询卡死也不影响退订。
    /// </para>
    /// </summary>
    internal sealed class ImeProbeWorker<T> : IDisposable
    {
        private sealed class PendingRequest
        {
            public PendingRequest(long requestId, IntPtr foreground, Action<ImeProbeResult<T>> onCompleted)
            {
                RequestId = requestId;
                Foreground = foreground;
                OnCompleted = onCompleted;

                // 提交时刻：QueueWait 的起点，在工作线程取走（StartedTimestamp）时结算。
                SubmittedTimestamp = Stopwatch.GetTimestamp();
            }

            public long RequestId { get; }
            public IntPtr Foreground { get; }
            public Action<ImeProbeResult<T>> OnCompleted { get; }

            /// <summary>提交时刻（Stopwatch 时间戳）。</summary>
            public long SubmittedTimestamp { get; }

            /// <summary>被工作线程取走、即将执行真实查询的时刻（0 = 尚未取走）。</summary>
            public long StartedTimestamp { get; set; }

            /// <summary>真实查询返回（含抛异常）的时刻（0 = 尚未结束）。</summary>
            public long CompletedTimestamp { get; set; }
        }

        private readonly object _gate = new();
        private readonly Func<IntPtr, T> _query;
        private readonly AutoResetEvent _signal = new(false);
        private readonly Thread _thread;

        private PendingRequest? _active;
        private PendingRequest? _pending;
        private bool _busy;
        private bool _disposed;

        /// <summary>
        /// 当前真正在执行真实查询的请求号。独立于 <see cref="_active"/><see cref="_pending"/>，
        /// 且只有在 <see cref="_query"/> 返回后才清除：<see cref="ClearPending"/> 取消排队请求时
        /// 不能把正在执行的请求误报为已消失，也不能用全局 <see cref="IsBusy"/> 把新排队请求
        /// 误报为正在执行。
        /// </summary>
        private long? _runningRequestId;

        public ImeProbeWorker(Func<IntPtr, T> query)
        {
            _query = query ?? throw new ArgumentNullException(nameof(query));

            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "ImeProbeWorker",
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        /// <summary>是否有一条真实查询正在执行（调用返回前一直为 true）。</summary>
        public bool IsBusy
        {
            get
            {
                lock (_gate)
                    return _busy;
            }
        }

        /// <summary>
        /// 提交一次探测请求。繁忙时只保留最新一条待办（替换更早的待办）。
        /// 完成回调在后台工作线程上执行，调用方必须自行切回 UI 线程。
        /// </summary>
        public void Request(long requestId, IntPtr foreground, Action<ImeProbeResult<T>> onCompleted)
        {
            var request = new PendingRequest(requestId, foreground, onCompleted);

            lock (_gate)
            {
                if (_disposed)
                    return;

                if (!_busy)
                {
                    // 没有真实查询在跑：最新请求直接成为活动请求（尚未开始的旧请求被替换）。
                    _active = request;
                    _signal.Set();
                }
                else
                {
                    // 最多一条待办：最新请求覆盖更早的待办。
                    _pending = request;
                }
            }
        }

        /// <summary>
        /// 查询指定请求号此刻的真实状态（锁内按请求号精确匹配）。
        /// <para>
        /// <see cref="ImeProbeRequestState.Running"/> 只认真正被工作线程取走、正在执行
        /// <see cref="ImeProbeWorker{T}"/> 查询的那一个请求号，不受 <see cref="IsBusy"/>
        /// 全局标记影响；<see cref="ImeProbeRequestState.Queued"/> 覆盖已提交但尚未取走的
        /// 活动/待办请求（可能随后被覆盖或取消）。
        /// </para>
        /// </summary>
        public ImeProbeRequestState GetRequestState(long requestId)
        {
            lock (_gate)
            {
                if (_runningRequestId == requestId)
                    return ImeProbeRequestState.Running;

                if (_active?.RequestId == requestId || _pending?.RequestId == requestId)
                    return ImeProbeRequestState.Queued;

                return ImeProbeRequestState.None;
            }
        }

        /// <summary>
        /// 撤销尚未开始执行的请求（待办与已排队但未取走的请求）。
        /// 已经进入真实查询的请求无法撤销，其结果由调用方按请求号作废。
        /// </summary>
        public void ClearPending()
        {
            lock (_gate)
            {
                _pending = null;
                _active = null;
            }
        }

        /// <summary>非阻塞销毁：正在执行的查询任其自行结束，之后线程退出。</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _pending = null;
                _active = null;

                // 唤醒必须在锁内置位：与工作线程退出时的 _signal.Dispose 配对，
                // 保证不会出现「事件已释放后又 Set」的竞态。此后 Request 在锁内
                // 看到 _disposed 会直接返回，也不会再碰事件。
                _signal.Set();
            }

            // 不 Join：可能正卡在跨进程 COM 调用里。
        }

        private void Loop()
        {
            try
            {
                while (true)
                {
                    _signal.WaitOne();

                    PendingRequest? request;
                    lock (_gate)
                    {
                        if (_disposed)
                            return;

                        request = _active;
                        _active = null;
                        if (request is null)
                            continue;

                        _busy = true;
                        // 取走执行：记录 QueueWait 的终点，并登记「真正在执行」的请求号。
                        request.StartedTimestamp = Stopwatch.GetTimestamp();
                        _runningRequestId = request.RequestId;
                    }

                    T value;
                    bool queryFailed = false;
                    try
                    {
                        value = _query(request.Foreground);
                    }
                    catch
                    {
                        value = default!;
                        queryFailed = true;
                    }

                    lock (_gate)
                    {
                        // 查询已结束：先结算时长并清掉执行中的请求号，
                        // 再清除占用标记（IsBusy 语义保持不变）。
                        request.CompletedTimestamp = Stopwatch.GetTimestamp();
                        _runningRequestId = null;
                        _busy = false;

                        if (_disposed)
                            return;

                        if (_pending is not null)
                        {
                            // 把最新待办提升为下一条活动请求，本轮结束后继续串行执行。
                            _active = _pending;
                            _pending = null;
                            _signal.Set();
                        }
                    }

                    try
                    {
                        request.OnCompleted(
                            new ImeProbeResult<T>(request.RequestId, request.Foreground, value)
                            {
                                QueueWait = Stopwatch.GetElapsedTime(
                                    request.SubmittedTimestamp, request.StartedTimestamp),
                                QueryTime = Stopwatch.GetElapsedTime(
                                    request.StartedTimestamp, request.CompletedTimestamp),
                                QueryFailed = queryFailed,
                            });
                    }
                    catch
                    {
                        // 完成回调由调用方提供：其异常不得弄死工作线程。
                    }
                }
            }
            finally
            {
                // 只在工作线程退出时释放事件：Dispose 的 Set 在锁内且先于线程退出，
                // Request 在锁内看到 _disposed 后也不再碰事件，因此不存在释放后再 Set。
                _signal.Dispose();
            }
        }
    }
}
