using System;
using System.Collections.Generic;

namespace CommandLauncher
{
    /// <summary>
    /// 输入法探测诊断的有界节流器。只在 UI 线程使用，时间由调用方提供，便于纯测试。
    /// 键只由进程 ID 与内部原因/来源、控件类型和处置结果组成，不能传入 provider 文本或输入内容。
    /// </summary>
    internal sealed class ImeDiagnosticThrottle
    {
        private readonly int _capacity;
        private readonly long _intervalMs;
        private readonly Dictionary<(uint ProcessId, string Reason), long> _last = new();

        public ImeDiagnosticThrottle(int capacity = 64, long intervalMs = 60_000)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            if (intervalMs < 0)
                throw new ArgumentOutOfRangeException(nameof(intervalMs));
            _capacity = capacity;
            _intervalMs = intervalMs;
        }

        internal int Count => _last.Count;

        public bool ShouldWrite(uint processId, string reason, long now)
        {
            var key = (processId, reason);
            if (_last.TryGetValue(key, out long previous))
            {
                if (now >= previous && now - previous < _intervalMs)
                    return false;
            }
            else if (_last.Count >= _capacity)
            {
                // 淘汰最早写出的一项，不维护额外无界队列。
                var oldest = default((uint ProcessId, string Reason));
                long oldestTime = long.MaxValue;
                foreach (var item in _last)
                {
                    if (item.Value <= oldestTime)
                    {
                        oldest = item.Key;
                        oldestTime = item.Value;
                    }
                }
                _last.Remove(oldest);
            }

            _last[key] = now;
            return true;
        }
    }
}
