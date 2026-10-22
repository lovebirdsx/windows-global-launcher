using System.Linq;
using System.Threading.Tasks;
using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests
{
    /// <summary>
    /// 原始焦点信号序号（<see cref="ImeSignalEpoch"/>）的纯逻辑测试。
    /// 回归点：信号在后台线程到达、UI 回调尚未执行时，旧探测结果必须立即失效。
    /// </summary>
    public class ImeSignalEpochTests
    {
        [Fact]
        public void Capture_IsCurrent_UntilNewSignalArrives()
        {
            var epoch = new ImeSignalEpoch();
            long captured = epoch.Capture();

            Assert.True(epoch.IsCurrent(captured));

            epoch.Bump();

            Assert.False(epoch.IsCurrent(captured));
        }

        [Fact]
        public void SignalBeforeUiCallback_InvalidatesInFlightProbe()
        {
            var epoch = new ImeSignalEpoch();

            // 探测发起：记录此刻的信号序号。
            epoch.Bump();
            long captured = epoch.Capture();

            // 新焦点信号到达后台线程：序号立刻递增，但对应 UI 回调还没执行
            // （策略代次尚未变化）。旧探测此时完成必须被作废。
            epoch.Bump();
            Assert.False(epoch.IsCurrent(captured));

            // 为随后的新信号发起探测时，重新记录的序号是有效的。
            long next = epoch.Capture();
            Assert.True(epoch.IsCurrent(next));
        }

        [Fact]
        public void BumpFromMultipleThreads_ProducesUniqueIncreasingValues()
        {
            var epoch = new ImeSignalEpoch();
            var values = new System.Collections.Concurrent.ConcurrentBag<long>();

            Parallel.For(0, 64, _ => values.Add(epoch.Bump()));

            Assert.Equal(64, values.Count);
            Assert.Equal(64, values.Distinct().Count());
            Assert.False(epoch.IsCurrent(0));
        }

        [Fact]
        public void ForegroundThenFocus_LatestConsumesForegroundOnce()
        {
            var epoch = new ImeSignalEpoch();
            long foreground = epoch.BumpForeground();
            long focus = epoch.Bump();

            // 旧 foreground 回调已被更新的焦点通知超越：不能消费标记。
            Assert.False(epoch.TryConsume(foreground, out bool staleChanged));
            Assert.False(staleChanged);

            // 最新焦点回调消费一次「前台已变化」标记，之后不再重复清除会话。
            Assert.True(epoch.TryConsume(focus, out bool changed));
            Assert.True(changed);
            Assert.True(epoch.TryConsume(focus, out bool changedAgain));
            Assert.False(changedAgain);
        }

        [Fact]
        public void FocusOnly_DoesNotMarkForegroundChanged()
        {
            var epoch = new ImeSignalEpoch();
            long raw = epoch.Bump();

            Assert.True(epoch.TryConsume(raw, out bool changed));
            Assert.False(changed);
        }

        [Fact]
        public void ConsecutiveForegroundSignals_CoalesceToOnePendingFlag()
        {
            var epoch = new ImeSignalEpoch();
            long first = epoch.BumpForeground();
            long second = epoch.BumpForeground();

            // 旧信号不能消费，标记必须留给最新信号。
            Assert.False(epoch.TryConsume(first, out _));

            // 标记是「是否发生过」而非计数：多个前台信号只需清一次会话。
            Assert.True(epoch.TryConsume(second, out bool changed));
            Assert.True(changed);
            Assert.True(epoch.TryConsume(second, out bool changedAgain));
            Assert.False(changedAgain);
        }

        [Fact]
        public void StaleRaw_KeepsPendingFlagForNewestRaw()
        {
            var epoch = new ImeSignalEpoch();
            long oldForeground = epoch.BumpForeground();

            // 新信号已到达（可能是焦点信号），旧 foreground 回调此刻才执行。
            long newest = epoch.Bump();

            Assert.False(epoch.TryConsume(oldForeground, out bool staleChanged));
            Assert.False(staleChanged);

            // 标记没有被旧回调吃掉，最新 raw 仍能消费到它并清除会话。
            Assert.True(epoch.TryConsume(newest, out bool changed));
            Assert.True(changed);
        }
    }
}
