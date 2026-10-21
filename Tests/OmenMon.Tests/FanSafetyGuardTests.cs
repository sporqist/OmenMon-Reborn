  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using OmenMon.Hardware.Platform;

namespace OmenMon.Tests {

    // The fan-safety rules on their own: readings and time are supplied directly
    public class FanSafetyGuardTests {

        private DateTime now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        private SensorReading cpu = SensorReading.None;
        private GpuReading gpu = GpuReading.None;

        private FanSafetyGuard NewGuard() {
            // The flat floor on its own; the warm curve has its own tests (WarmCurveTests)
            return new FanSafetyGuard(new FanSafetyPolicy { WarmCurve = new SafetyStep[0] },
                () => this.cpu, () => this.gpu, () => this.now);
        }

        private void Cpu(int celsius) { this.cpu = new SensorReading(celsius, this.now); }
        private void Gpu(int celsius) { this.gpu = new GpuReading(new SensorReading(celsius, this.now), this.gpu.ZeroAt); }

        // The GPU read 0 successfully (parked); a failed read changes nothing
        private void GpuZero() { this.gpu = new GpuReading(this.gpu.Last, this.now); }
        private void Advance(TimeSpan span) { this.now += span; }

        private static readonly byte[] Off = { 0, 0 };
        private static readonly byte[] Floor = { 20, 22 };

        [Fact]
        public void Warm_RaisesToFloor() {
            var g = NewGuard();
            Cpu(60);
            Assert.Equal(Floor, g.Filter(Off));
            Assert.Equal(new byte[] { 20, 30 }, g.Filter(new byte[] { 0, 30 }));
        }

        [Fact]
        public void AtCoolThreshold_IsWarm() {
            var g = NewGuard();
            Cpu(50);
            Assert.Equal(Floor, g.Filter(Off));
        }

        [Fact]
        public void Cool_AllowsOff() {
            var g = NewGuard();
            Cpu(45);
            Assert.Equal(Off, g.Filter(Off));
        }

        [Fact]
        public void UnknownCpu_IsNeverCool() {
            var g = NewGuard();
            Gpu(30);
            Assert.Equal(Floor, g.Filter(Off));
        }

        [Fact]
        public void StaleCpu_IsNeverCool() {
            var g = NewGuard();
            Cpu(40);
            Advance(g.Policy.ReadingMaxAge + TimeSpan.FromSeconds(1));
            Assert.Equal(Floor, g.Filter(Off));
        }

        [Fact]
        public void StaleRule_ComesFromThePolicy() {
            var g = new FanSafetyGuard(new FanSafetyPolicy { WarmCurve = new SafetyStep[0], ReadingMaxAge = TimeSpan.FromSeconds(10) },
                () => this.cpu, () => this.gpu, () => this.now);
            Cpu(40);
            Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(Off, g.Filter(Off));
            Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(Floor, g.Filter(Off));
            Assert.Null(g.Hottest());
        }

        // A GPU that parks keeps counting at its last value until that is stale (a single
        // 0 can be a glitch); then its successful 0 reads say it is parked
        [Fact]
        public void WarmGpu_BlocksOff_ParkedGpuDoesNot() {
            var g = NewGuard();
            Cpu(40);
            Gpu(55);
            Assert.Equal(Floor, g.Filter(Off));
            GpuZero();
            Assert.Equal(Floor, g.Filter(Off));
            Advance(g.Policy.ReadingMaxAge + TimeSpan.FromSeconds(1));
            Cpu(40);
            GpuZero();
            Assert.Equal(Off, g.Filter(Off));
        }

        // fansafety-0: a GPU that has read before and now only fails (EC lock timeouts)
        // is unknown, not parked: never cool, and no hottest temperature
        [Fact]
        public void GpuReadFailures_AfterAReading_AreUnknown_NeverCool() {
            var g = NewGuard();
            Cpu(40);
            Gpu(45);
            Assert.Equal(Off, g.Filter(Off));
            Advance(g.Policy.ReadingMaxAge + TimeSpan.FromSeconds(1));
            Cpu(40); // the BIOS CPU sensor keeps reading, the EC GPU sensor does not
            Assert.False(g.IsCool());
            Assert.Null(g.Hottest());
            Assert.Equal(Floor, g.Filter(Off));
        }

        [Fact]
        public void GpuReadFailures_RaiseAHeldFanOff() {
            var g = NewGuard();
            Cpu(40);
            Gpu(45);
            g.RecordWrite(Off, g.Filter(Off));
            Assert.Equal(Off, g.Written);
            Advance(TimeSpan.FromSeconds(30));
            Cpu(40);
            Assert.Null(g.Supervise());
            Advance(TimeSpan.FromSeconds(16));
            Cpu(40);
            Assert.Equal(Floor, g.Supervise());
        }

        // A board without a GPU sensor (or a GPU parked since start) never reads: judged
        // on the CPU alone, as before
        [Fact]
        public void GpuNeverObserved_IsJudgedOnTheCpu() {
            var g = NewGuard();
            Cpu(40);
            Assert.True(g.IsCool());
            Assert.Equal(40, g.Hottest());
            Assert.Equal(Off, g.Filter(Off));
        }

        [Fact]
        public void GpuReading_States() {
            TimeSpan age = TimeSpan.FromSeconds(45);
            DateTime t = this.now;
            SensorReading at = new SensorReading(70, t);
            Assert.Equal(GpuState.Parked, GpuReading.None.StateAt(t, age));
            Assert.Equal(GpuState.Parked, new GpuReading(SensorReading.None, default(DateTime)).StateAt(t.AddHours(1), age));
            Assert.Equal(GpuState.Fresh, new GpuReading(at, default(DateTime)).StateAt(t.AddSeconds(45), age));
            Assert.Equal(GpuState.Unknown, new GpuReading(at, default(DateTime)).StateAt(t.AddSeconds(46), age));
            // A later 0 does not override a fresh reading, and parks it once that is stale
            Assert.Equal(GpuState.Fresh, new GpuReading(at, t.AddSeconds(5)).StateAt(t.AddSeconds(10), age));
            Assert.Equal(GpuState.Parked, new GpuReading(at, t.AddSeconds(40)).StateAt(t.AddSeconds(60), age));
            // ...until the 0 is stale too
            Assert.Equal(GpuState.Unknown, new GpuReading(at, t.AddSeconds(40)).StateAt(t.AddSeconds(86), age));
            // A 0 from before the last reading is staler than it
            Assert.Equal(GpuState.Unknown, new GpuReading(at, t.AddSeconds(-1)).StateAt(t.AddSeconds(46), age));
        }

        [Fact]
        public void LevelsAboveFloor_AndNoChange_PassThrough() {
            var g = NewGuard();
            Cpu(90);
            Assert.Equal(new byte[] { 45, 50 }, g.Filter(new byte[] { 45, 50 }));
            Assert.Equal(new byte[] { 0xFF, 0xFF }, g.Filter(new byte[] { 0xFF, 0xFF }));
        }

        [Fact]
        public void ManualOff_IsTimeLimited_EvenWhileCool() {
            var g = NewGuard();
            Cpu(40);
            g.RecordWrite(Off, g.Filter(Off));
            Advance(TimeSpan.FromMinutes(14));
            Cpu(40);
            Assert.Null(g.Supervise());
            Advance(TimeSpan.FromMinutes(1));
            Cpu(40);
            Assert.Equal(Floor, g.Supervise());
            Assert.Equal(Floor, g.Filter(Off));
        }

        [Fact]
        public void ProgramLevels_AreNotTimeLimited() {
            var g = NewGuard();
            Cpu(40);
            g.RecordWrite(Off, g.Filter(Off, FanLevelSource.Program), FanLevelSource.Program);
            Advance(TimeSpan.FromHours(2));
            Cpu(40);
            Assert.Null(g.Supervise());
            Assert.Equal(Off, g.Filter(Off, FanLevelSource.Program));
        }

        [Fact]
        public void Supervise_RaisesOff_WhenItWarmsUp() {
            var g = NewGuard();
            Cpu(40);
            g.RecordWrite(Off, g.Filter(Off));
            Assert.True(g.NeedsSupervision);
            Cpu(58);
            Assert.Equal(Floor, g.Supervise());
        }

        [Fact]
        public void Supervise_RaisesOff_WhenSensorsStopReading() {
            var g = NewGuard();
            Cpu(40);
            g.RecordWrite(Off, g.Filter(Off));
            Advance(g.Policy.ReadingMaxAge + TimeSpan.FromSeconds(1));
            Assert.Equal(Floor, g.Supervise());
        }

        [Fact]
        public void Panic_RaisesEveryWrite_AndIsResentEachPass() {
            var g = NewGuard();
            Cpu(97);
            g.SetPanic(new byte[] { 55, 57 });
            Assert.Equal(new byte[] { 55, 57 }, g.Filter(new byte[] { 30, 30 }));
            g.RecordWrite(new byte[] { 30, 30 }, new byte[] { 55, 57 }, FanLevelSource.Program);
            Assert.Equal(new byte[] { 55, 57 }, g.Supervise());
            Assert.Equal(new byte[] { 55, 57 }, g.Supervise());
            Assert.Equal(new byte[] { 30, 30 }, g.Requested);
        }

        [Fact]
        public void Panic_FromAutomatic_WritesPanicLevel() {
            var g = NewGuard();
            Cpu(97);
            g.SetPanic(new byte[] { 55, 57 });
            Assert.Equal(new byte[] { 55, 57 }, g.Supervise());
        }

        [Fact]
        public void HandBackLevel_IsNeverBelowFloor() {
            var g = NewGuard();
            Cpu(40);
            g.RecordWrite(Off, g.Filter(Off));
            Assert.Equal(Floor, g.HandBackLevel());
            Cpu(80);
            g.RecordWrite(new byte[] { 45, 48 }, g.Filter(new byte[] { 45, 48 }));
            Assert.Equal(new byte[] { 45, 48 }, g.HandBackLevel());
        }

        [Fact]
        public void HandBack_SilencesHeartbeat_ForQuietPeriod() {
            var g = NewGuard();
            Assert.True(g.HeartbeatAllowed);
            g.RecordRelease();
            Assert.False(g.HeartbeatAllowed);
            Advance(TimeSpan.FromSeconds(149));
            Assert.False(g.HeartbeatAllowed);
            Advance(TimeSpan.FromSeconds(1));
            Assert.True(g.HeartbeatAllowed);
        }

        [Fact]
        public void NewWrite_EndsQuietPeriod() {
            var g = NewGuard();
            Cpu(60);
            g.RecordRelease();
            g.RecordWrite(new byte[] { 30, 30 }, g.Filter(new byte[] { 30, 30 }));
            Assert.True(g.HeartbeatAllowed);
            Assert.True(g.HoldsLevel);
        }

    }

}
