  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System.Diagnostics;
using OmenMon.Hardware.Platform;
using OmenMon.Tests.Fakes;

namespace OmenMon.Tests {

    // The last-chance hand-back on exit or crash (FanHandBack is process-wide)
    [Collection(HardwareStateCollection.Name)]
    public class FanHandBackTests {

        // fansafety-2: a thread that still holds the hardware lock (a crash inside a
        // control sequence, a slow WMI call at exit) must not make Run() give up before
        // the hand-back starts. It used to wait 2 s for the lock inside a 1.5 s budget.
        [Fact]
        public void HeldLock_HandsBackWithoutIt_WithinTheBudget() {
            object hardwareLock = new object();
            bool ran = false, hadLock = true;
            FanHandBack.Register(() => { ran = true; hadLock = Monitor.IsEntered(hardwareLock); }, hardwareLock);
            try {
                lock(hardwareLock) {
                    var watch = Stopwatch.StartNew();
                    Assert.True(FanHandBack.Run(1500));
                    Assert.True(watch.ElapsedMilliseconds < 1500);
                }
                Assert.True(ran);
                Assert.False(hadLock);
            } finally {
                FanHandBack.Register(null);
            }
        }

        [Fact]
        public void FreeLock_HandsBackUnderIt() {
            object hardwareLock = new object();
            bool hadLock = false;
            FanHandBack.Register(() => hadLock = Monitor.IsEntered(hardwareLock), hardwareLock);
            try {
                Assert.True(FanHandBack.Run(1500));
                Assert.True(hadLock);
                Assert.True(Monitor.TryEnter(hardwareLock)); // released again
                Monitor.Exit(hardwareLock);
            } finally {
                FanHandBack.Register(null);
            }
        }

        [Fact]
        public void LockWait_LeavesMostOfTheBudget() {
            Assert.Equal(400, FanHandBack.LockWaitMs(1500));
            Assert.True(FanHandBack.LockWaitMs(600) <= 200);
            Assert.Equal(0, FanHandBack.LockWaitMs(0));
        }

        [Fact]
        public void NothingRegistered_IsDone() {
            FanHandBack.Register(null);
            Assert.True(FanHandBack.Run(10));
        }

    }

}
