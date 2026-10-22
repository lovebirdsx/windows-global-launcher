using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests
{
    /// <summary>
    /// 输入法焦点探测工作线程（<see cref="ImeProbeWorker{T}"/>）的测试。
    /// 用假后端 + ManualResetEventSlim 控制时序，不依赖真实系统钩子与 sleep。
    /// </summary>
    public class ImeProbeWorkerTests
    {
        [Fact]
        public void RunsOnSingleBackgroundMtaThread()
        {
            var threadIds = new ConcurrentQueue<int>();
            var apartmentStates = new ConcurrentQueue<ApartmentState>();
            var done1 = new ManualResetEventSlim(false);
            var done2 = new ManualResetEventSlim(false);

            using var worker = new ImeProbeWorker<int>(_ =>
            {
                threadIds.Enqueue(Environment.CurrentManagedThreadId);
                apartmentStates.Enqueue(Thread.CurrentThread.GetApartmentState());
                return 0;
            });

            // 顺序提交：确保两次查询都真正执行（未开始的请求会被最新请求替换）。
            worker.Request(1, IntPtr.Zero, _ => done1.Set());
            Assert.True(done1.Wait(TimeSpan.FromSeconds(5)));

            worker.Request(2, IntPtr.Zero, _ => done2.Set());
            Assert.True(done2.Wait(TimeSpan.FromSeconds(5)));

            Assert.Equal(2, threadIds.Count);
            Assert.All(apartmentStates, state => Assert.Equal(ApartmentState.MTA, state));

            threadIds.TryDequeue(out int first);
            threadIds.TryDequeue(out int second);
            Assert.Equal(first, second);
        }

        [Fact]
        public void Busy_StaysTrueUntilQueryReturns()
        {
            var started = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var completed = new ManualResetEventSlim(false);

            using var worker = new ImeProbeWorker<int>(_ =>
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                return 1;
            });

            worker.Request(1, IntPtr.Zero, _ => completed.Set());

            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(worker.IsBusy);

            release.Set();

            Assert.True(completed.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(worker.IsBusy);
        }

        [Fact]
        public void Busy_CoalescesToLatestPendingRequest()
        {
            var firstStarted = new ManualResetEventSlim(false);
            var releaseFirst = new ManualResetEventSlim(false);
            var done1 = new ManualResetEventSlim(false);
            var done2 = new ManualResetEventSlim(false);
            var done3 = new ManualResetEventSlim(false);
            var results = new ConcurrentQueue<long>();
            int call = 0;

            using var worker = new ImeProbeWorker<int>(_ =>
            {
                int n = Interlocked.Increment(ref call);
                if (n == 1)
                {
                    firstStarted.Set();
                    releaseFirst.Wait(TimeSpan.FromSeconds(5));
                }

                return n;
            });

            worker.Request(1, IntPtr.Zero, result =>
            {
                results.Enqueue(result.RequestId);
                done1.Set();
            });

            Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));

            // 繁忙期间提交两条：中间那条应被最新一条覆盖，不排队执行。
            // 回调记录请求号（而不是后端返回值），才能精确断言「2 被丢弃、3 被执行」。
            worker.Request(2, IntPtr.Zero, _ => done2.Set());
            worker.Request(3, IntPtr.Zero, result =>
            {
                results.Enqueue(result.RequestId);
                done3.Set();
            });

            releaseFirst.Set();

            Assert.True(done1.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(done3.Wait(TimeSpan.FromSeconds(5)));

            Assert.False(done2.Wait(TimeSpan.FromMilliseconds(200)));
            Assert.Equal(new long[] { 1, 3 }, results.ToArray());
        }

        [Fact]
        public void ClearPending_DropsQueuedRequest()
        {
            var firstStarted = new ManualResetEventSlim(false);
            var releaseFirst = new ManualResetEventSlim(false);
            var done1 = new ManualResetEventSlim(false);
            var done2 = new ManualResetEventSlim(false);

            using var worker = new ImeProbeWorker<int>(_ =>
            {
                firstStarted.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(5));
                return 1;
            });

            worker.Request(1, IntPtr.Zero, _ => done1.Set());
            Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));

            worker.Request(2, IntPtr.Zero, _ => done2.Set());
            worker.ClearPending();

            releaseFirst.Set();

            Assert.True(done1.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(done2.Wait(TimeSpan.FromMilliseconds(200)));
        }

        [Fact]
        public void Dispose_WhileBusy_ReturnsWithoutWaitingOrCompleting()
        {
            var started = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var completed = new ManualResetEventSlim(false);

            var worker = new ImeProbeWorker<int>(_ =>
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                return 1;
            });

            worker.Request(1, IntPtr.Zero, _ => completed.Set());
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

            var stopwatch = Stopwatch.StartNew();
            worker.Dispose();
            stopwatch.Stop();

            // Dispose 只唤醒线程，不 Join 正在阻塞的查询。
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));

            release.Set();

            // 已销毁：正在执行的查询完成后也不得回调。
            Assert.False(completed.Wait(TimeSpan.FromMilliseconds(300)));
        }

        [Fact]
        public void RequestState_TracksRunningQueuedAndPendingOverwrite()
        {
            var firstStarted = new ManualResetEventSlim(false);
            var releaseFirst = new ManualResetEventSlim(false);
            var done1 = new ManualResetEventSlim(false);
            var done2 = new ManualResetEventSlim(false);
            var done3 = new ManualResetEventSlim(false);

            using var worker = new ImeProbeWorker<int>(_ =>
            {
                firstStarted.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(5));
                return 1;
            });

            worker.Request(1, IntPtr.Zero, _ => done1.Set());
            Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));

            // 请求 1 已阻塞在真实查询里：它是唯一 Running，且与全局 IsBusy 无关。
            Assert.True(worker.IsBusy);
            Assert.Equal(ImeProbeRequestState.Running, worker.GetRequestState(1));

            // 请求 2 进入待办：全局 IsBusy 为 true，但它只是 Queued，绝不能被误报为 Running。
            worker.Request(2, IntPtr.Zero, _ => done2.Set());
            Assert.Equal(ImeProbeRequestState.Queued, worker.GetRequestState(2));
            Assert.NotEqual(ImeProbeRequestState.Running, worker.GetRequestState(2));

            // 请求 3 覆盖待办中的请求 2：2 变回 None，3 成为唯一 Queued。
            worker.Request(3, IntPtr.Zero, _ => done3.Set());
            Assert.Equal(ImeProbeRequestState.Queued, worker.GetRequestState(3));
            Assert.Equal(ImeProbeRequestState.None, worker.GetRequestState(2));

            // 未知请求号同样是 None。
            Assert.Equal(ImeProbeRequestState.None, worker.GetRequestState(999));

            releaseFirst.Set();

            Assert.True(done1.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(done3.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(done2.Wait(TimeSpan.FromMilliseconds(200)));

            // 执行结束后全部回到 None。
            Assert.Equal(ImeProbeRequestState.None, worker.GetRequestState(1));
            Assert.Equal(ImeProbeRequestState.None, worker.GetRequestState(3));
        }

        [Fact]
        public void ClearPending_KeepsRunningStateAndDropsQueued()
        {
            var firstStarted = new ManualResetEventSlim(false);
            var releaseFirst = new ManualResetEventSlim(false);
            var done1 = new ManualResetEventSlim(false);
            var done2 = new ManualResetEventSlim(false);

            using var worker = new ImeProbeWorker<int>(_ =>
            {
                firstStarted.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(5));
                return 1;
            });

            worker.Request(1, IntPtr.Zero, _ => done1.Set());
            Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));
            worker.Request(2, IntPtr.Zero, _ => done2.Set());
            Assert.Equal(ImeProbeRequestState.Queued, worker.GetRequestState(2));

            // ClearPending 只取消尚未执行的请求，正在执行的请求必须仍可精确查到。
            worker.ClearPending();

            Assert.True(worker.IsBusy);
            Assert.Equal(ImeProbeRequestState.Running, worker.GetRequestState(1));
            Assert.Equal(ImeProbeRequestState.None, worker.GetRequestState(2));

            releaseFirst.Set();

            Assert.True(done1.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(done2.Wait(TimeSpan.FromMilliseconds(200)));
            Assert.Equal(ImeProbeRequestState.None, worker.GetRequestState(1));
        }

        [Fact]
        public void Result_ReportsNonNegativeQueueWaitAndQueryTime()
        {
            var firstStarted = new ManualResetEventSlim(false);
            var releaseFirst = new ManualResetEventSlim(false);
            var done2 = new ManualResetEventSlim(false);
            ImeProbeResult<int> captured = default;
            int call = 0;

            using var worker = new ImeProbeWorker<int>(_ =>
            {
                if (Interlocked.Increment(ref call) == 1)
                {
                    firstStarted.Set();
                    releaseFirst.Wait(TimeSpan.FromSeconds(5));
                }

                return 7;
            });

            worker.Request(1, IntPtr.Zero, _ => { });
            Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));

            // 在请求 1 执行期间提交请求 2，确保 QueueWait 有真实的等待区间。
            worker.Request(2, IntPtr.Zero, result =>
            {
                captured = result;
                done2.Set();
            });
            Assert.Equal(ImeProbeRequestState.Queued, worker.GetRequestState(2));

            releaseFirst.Set();
            Assert.True(done2.Wait(TimeSpan.FromSeconds(5)));

            Assert.Equal(2, captured.RequestId);
            Assert.Equal(7, captured.Value);
            Assert.False(captured.QueryFailed);
            Assert.True(captured.QueueWait >= TimeSpan.Zero);
            Assert.True(captured.QueryTime >= TimeSpan.Zero);
        }

        [Fact]
        public void LegacyTripleConstructor_DefaultsExtraFields()
        {
            // 三位置参数是既有契约，新增时长/失败标记只能作为 init 属性附加。
            var result = new ImeProbeResult<int>(5, IntPtr.Zero, 11);

            Assert.Equal(5, result.RequestId);
            Assert.Equal(IntPtr.Zero, result.Foreground);
            Assert.Equal(11, result.Value);
            Assert.Equal(TimeSpan.Zero, result.QueueWait);
            Assert.Equal(TimeSpan.Zero, result.QueryTime);
            Assert.False(result.QueryFailed);
        }

        [Fact]
        public void QueryException_StillCompletesWithDefault()
        {
            var done = new ManualResetEventSlim(false);
            ImeProbeResult<int> captured = default;

            using var worker = new ImeProbeWorker<int>(_ => throw new InvalidOperationException("boom"));

            worker.Request(42, IntPtr.Zero, result =>
            {
                captured = result;
                done.Set();
            });

            Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(42, captured.RequestId);
            Assert.Equal(0, captured.Value);
            Assert.True(captured.QueryFailed);
            Assert.True(captured.QueueWait >= TimeSpan.Zero);
            Assert.True(captured.QueryTime >= TimeSpan.Zero);
            Assert.Equal(ImeProbeRequestState.None, worker.GetRequestState(42));
        }
    }
}
