  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using OmenMon.Hardware.Platform;

namespace OmenMon.Tests {

    // The fan-safety rules on their own: readings and time are supplied directly
    public class FanSafetyGuardTests {

        private DateTime now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        private SensorReading cpu = SensorReading.None;
        private SensorReading gpu = SensorReading.None;

        private FanSafetyGuard NewGuard() {
            return new FanSafetyGuard(new FanSafetyPolicy(), () => this.cpu, () => this.gpu, () => this.now);
        }

        private void Cpu(int celsius) { this.cpu = new SensorReading(celsius, this.now); }
        private void Gpu(int celsius) { this.gpu = new SensorReading(celsius, this.now); }
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
            Advance(Platform.ReadingMaxAge + TimeSpan.FromSeconds(1));
            Assert.Equal(Floor, g.Filter(Off));
        }

        [Fact]
        public void WarmGpu_BlocksOff_ParkedGpuDoesNot() {
            var g = NewGuard();
            Cpu(40);
            Gpu(55);
            Assert.Equal(Floor, g.Filter(Off));
            this.gpu = SensorReading.None;
            Assert.Equal(Off, g.Filter(Off));
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
            Advance(Platform.ReadingMaxAge + TimeSpan.FromSeconds(1));
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
            Advance(TimeSpan.FromSeconds(129));
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
