using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests
{
    /// <summary>
    /// Watcher 线程生命周期的纯状态机测试：不启动真实线程、不访问 UIA/Win32，
    /// 也不验证真实 UIA/WinEvent 退订是否成功（那需要真实 provider 的集成测试）。
    /// <para>
    /// 回归点一：旧线程完成退订与清 threadId 后必须释放名额，否则线程异常退出后
    /// 托盘关开（下一次 SetEnabled(true)/Start）无法恢复。
    /// </para>
    /// <para>
    /// 回归点二：线程进入清理后到达的显式启用必须被登记，并在清理完成后消费一次；
    /// 线程正常运行期间的普通重复启用不得登记（否则线程之后异常退出会变成自动重启），
    /// 禁用/Dispose/启动失败则必须取消待启动请求，不能形成重启循环。
    /// </para>
    /// </summary>
    public class ImeFocusWatcherTests
    {
        [Fact]
        public void TryBeginStart_WhileThreadAlive_RejectsDuplicateStart()
        {
            var lifecycle = new ImeFocusWatcher.Lifecycle();

            Assert.True(lifecycle.TryBeginStart());
            Assert.False(lifecycle.TryBeginStart()); // 旧线程运行中不能起第二个
        }

        [Fact]
        public void CompleteThreadCleanup_AllowsNextExplicitStart()
        {
            var lifecycle = new ImeFocusWatcher.Lifecycle();

            Assert.True(lifecycle.TryBeginStart());
            Assert.False(lifecycle.TryBeginStart());

            // 模拟 ThreadMain finally：先 Unsubscribe、清 threadId，最后才 CompleteThreadCleanup
            lifecycle.BeginCleanup();
            Assert.False(lifecycle.CompleteThreadCleanup()); // 期间无新启用 → 不要求重启

            Assert.True(lifecycle.TryBeginStart());  // 下一次显式启用可以重启
            Assert.False(lifecycle.TryBeginStart()); // 重启后仍只有一个线程
        }

        [Fact]
        public void CompleteThreadCleanup_IsIdempotent()
        {
            var lifecycle = new ImeFocusWatcher.Lifecycle();

            Assert.False(lifecycle.CompleteThreadCleanup());
            Assert.False(lifecycle.CompleteThreadCleanup());

            Assert.True(lifecycle.TryBeginStart());
        }

        [Fact]
        public void EnableDuringCleanup_IsDeferredAndConsumedExactlyOnce()
        {
            var lifecycle = new ImeFocusWatcher.Lifecycle();

            Assert.True(lifecycle.TryBeginStart()); // 旧线程运行
            lifecycle.BeginCleanup();               // 开始退订（可能耗时）

            // 清理期间到达的显式启用：不能起第二个线程，但必须登记。
            Assert.False(lifecycle.TryBeginStart());
            Assert.False(lifecycle.TryBeginStart()); // 清理未完成，仍不能启动

            // 清理完成：消费一次登记，允许调用方重启。
            Assert.True(lifecycle.CompleteThreadCleanup());

            // 重启后的线程也退出：没有新的显式启用，不得再次要求重启。
            Assert.True(lifecycle.TryBeginStart());
            lifecycle.BeginCleanup();
            Assert.False(lifecycle.CompleteThreadCleanup());
        }

        [Fact]
        public void NormalRepeatedEnable_DoesNotBecomeRestartAfterThreadExit()
        {
            var lifecycle = new ImeFocusWatcher.Lifecycle();

            Assert.True(lifecycle.TryBeginStart());

            // 线程正常运行期间的重复启用：只走唤醒，不登记待启动。
            Assert.False(lifecycle.TryBeginStart());
            Assert.False(lifecycle.TryBeginStart());

            lifecycle.BeginCleanup();

            // 线程之后异常退出：普通重复启用不得被误当成「清理期间的新启用」而自动重启。
            Assert.False(lifecycle.CompleteThreadCleanup());
        }

        [Fact]
        public void DisableDuringCleanup_CancelsPendingRestart()
        {
            var lifecycle = new ImeFocusWatcher.Lifecycle();

            Assert.True(lifecycle.TryBeginStart());
            lifecycle.BeginCleanup();

            Assert.False(lifecycle.TryBeginStart()); // 清理期间启用 → 登记
            lifecycle.CancelPendingStart();          // SetEnabled(false)/Dispose

            Assert.False(lifecycle.CompleteThreadCleanup()); // 不得自动重启
            Assert.True(lifecycle.TryBeginStart());          // 之后显式启用仍可正常启动
        }

        [Fact]
        public void DisableThenReenableDuringCleanup_Restarts()
        {
            var lifecycle = new ImeFocusWatcher.Lifecycle();

            Assert.True(lifecycle.TryBeginStart());
            lifecycle.BeginCleanup();

            Assert.False(lifecycle.TryBeginStart()); // 启用以登记
            lifecycle.CancelPendingStart();          // 禁用取消
            Assert.False(lifecycle.TryBeginStart()); // 再次启用重新登记

            Assert.True(lifecycle.CompleteThreadCleanup());
        }

        [Fact]
        public void AbortStart_ReleasesSlotAndClearsPendingRestart()
        {
            var lifecycle = new ImeFocusWatcher.Lifecycle();

            // thread.Start 抛异常：释放名额，下一次显式启用可以重试。
            Assert.True(lifecycle.TryBeginStart());
            lifecycle.AbortStart();
            Assert.True(lifecycle.TryBeginStart());

            // 清理期间已登记待启动，但重启用启动又失败：AbortStart 必须清掉它，
            // 否则清理结束时会再自动重试一次，形成失败循环。
            lifecycle.BeginCleanup();
            Assert.False(lifecycle.TryBeginStart());
            lifecycle.AbortStart();

            Assert.False(lifecycle.CompleteThreadCleanup());
            Assert.True(lifecycle.TryBeginStart());
        }
    }
}
