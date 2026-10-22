using System;
using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests
{
    public class ImeDiagnosticThrottleTests
    {
        [Fact]
        public void SameProcessAndReason_IsThrottledUntilBoundary()
        {
            var throttle = new ImeDiagnosticThrottle();
            Assert.True(throttle.ShouldWrite(1, "NoCaret", 0));
            Assert.False(throttle.ShouldWrite(1, "NoCaret", 59_999));
            Assert.True(throttle.ShouldWrite(1, "NoCaret", 60_000));
        }

        [Fact]
        public void DifferentProcessOrReason_DoesNotHideOtherFailures()
        {
            var throttle = new ImeDiagnosticThrottle();
            Assert.True(throttle.ShouldWrite(1, "NoCaret", 0));
            Assert.True(throttle.ShouldWrite(2, "NoCaret", 1));
            Assert.True(throttle.ShouldWrite(1, "ImeUntrusted", 2));
        }

        [Fact]
        public void Capacity_EvictsOldestAndRemainsBounded()
        {
            var throttle = new ImeDiagnosticThrottle(capacity: 2);
            Assert.True(throttle.ShouldWrite(1, "NoCaret", 0));
            Assert.True(throttle.ShouldWrite(2, "NoCaret", 1));
            Assert.True(throttle.ShouldWrite(3, "NoCaret", 2));
            Assert.Equal(2, throttle.Count);
            Assert.False(throttle.ShouldWrite(2, "NoCaret", 3));
            Assert.True(throttle.ShouldWrite(1, "NoCaret", 4));
            Assert.Equal(2, throttle.Count);
        }

        [Fact]
        public void ClockRollback_DoesNotPermanentlySuppress()
        {
            var throttle = new ImeDiagnosticThrottle();
            Assert.True(throttle.ShouldWrite(1, "NoCaret", 100));
            Assert.True(throttle.ShouldWrite(1, "NoCaret", 0));
            Assert.False(throttle.ShouldWrite(1, "NoCaret", 1));
        }

        [Fact]
        public void InvalidBounds_AreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ImeDiagnosticThrottle(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ImeDiagnosticThrottle(intervalMs: -1));
        }
    }
}
