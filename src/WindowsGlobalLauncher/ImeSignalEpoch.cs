namespace CommandLauncher
{
    /// <summary>
    /// 原始焦点/前台信号的线程安全状态（纯逻辑、可单测）。
    /// <para>
    /// 关键区别：信号可能在后台线程到达、而 UI 回调要排队等 UI 线程。序号在
    /// <see cref="Bump"/> 时（信号到达的瞬间）就递增，探测在发起时用
    /// <see cref="Capture"/> 记录序号、完成时用 <see cref="IsCurrent"/> 核对——
    /// 即使该信号的 UI 回调还没执行，迟到的旧探测结果也会被立即作废，
    /// 不会把上一个输入框（旧焦点身份）的角标显示出来。
    /// </para>
    /// <para>
    /// 前台变化另有一个待处理标记：<see cref="BumpForeground"/> 在同一次锁内
    /// 置位并递增序号；UI 回调通过 <see cref="TryConsume"/> 在锁内一次核对
    /// 「是否最新」并取走标记。这样被更新信号超越的旧回调不可能抢先消费
    /// 新信号的前台变化标记，也不会递增任何策略代次。
    /// </para>
    /// <para>
    /// 锁内只有几条内存操作，不包任何回调、COM 或 Win32 调用。
    /// </para>
    /// </summary>
    internal sealed class ImeSignalEpoch
    {
        private readonly object _gate = new();
        private long _value;
        private bool _pendingForeground;

        /// <summary>收到一次原始信号：递增并返回新序号（可在任意线程调用）。</summary>
        public long Bump()
        {
            lock (_gate)
            {
                return ++_value;
            }
        }

        /// <summary>
        /// 收到前台变化信号：在同一次锁内置位「待处理前台变化」标记并递增序号。
        /// 标记与序号原子更新，不会与本条信号脱节。
        /// </summary>
        public long BumpForeground()
        {
            lock (_gate)
            {
                _pendingForeground = true;
                return ++_value;
            }
        }

        /// <summary>
        /// 原子核对并消费：仅当 <paramref name="raw"/> 仍是最新序号时返回 true，
        /// 并在同一次锁内取走待处理的前台变化标记。
        /// 旧 raw 返回 false 且不修改任何状态——标记保留给真正最新的 raw 消费。
        /// </summary>
        public bool TryConsume(long raw, out bool foregroundChanged)
        {
            lock (_gate)
            {
                if (raw != _value)
                {
                    foregroundChanged = false;
                    return false;
                }

                foregroundChanged = _pendingForeground;
                _pendingForeground = false;
                return true;
            }
        }

        /// <summary>读取当前序号（探测发起时记录）。</summary>
        public long Capture()
        {
            lock (_gate)
            {
                return _value;
            }
        }

        /// <summary>发起时记录的序号是否仍是最新（完成时核对）。</summary>
        public bool IsCurrent(long captured)
        {
            lock (_gate)
            {
                return captured == _value;
            }
        }
    }
}
