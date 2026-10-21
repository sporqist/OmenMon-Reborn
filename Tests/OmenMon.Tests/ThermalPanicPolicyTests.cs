  //\   OmenMon: Hardware Monitoring & Control Utility
 //  \  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using OmenMon.Hardware.Platform;
using OmenMon.Library;
using OmenMon.Tests.Fakes;

namespace OmenMon.Tests {

    public class ThermalPanicPolicyTests {

        private static readonly DateTime T = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

        private static SensorReading R(int celsius) {
            return new SensorReading(celsius, T);
        }

        [Fact]
        public void Activates_AtThreshold() {
            Assert.False(ThermalPanicPolicy.Next(false, R(89), 90, 5));
            Assert.True(ThermalPanicPolicy.Next(false, R(90), 90, 5));
        }

        [Fact]
        public void Activates_Above100() {
            Assert.True(ThermalPanicPolicy.Next(false, R(104), 90, 5));
        }

        [Fact]
        public void State_EntersClearsAndReportsEachTransitionOnce() {
            var s = new ThermalPanicState();
            Assert.Equal(ThermalPanicTransition.None, s.Update(true, R(94), true, 95, 10));
            Assert.Equal(ThermalPanicTransition.Entered, s.Update(true, R(95), true, 95, 10));
            Assert.True(s.IsActive);
            Assert.Equal(ThermalPanicTransition.None, s.Update(true, R(97), true, 95, 10));
            Assert.Equal(ThermalPanicTransition.None, s.Update(true, R(86), true, 95, 10));
            Assert.Equal(ThermalPanicTransition.Cleared, s.Update(true, R(85), true, 95, 10));
            Assert.False(s.IsActive);
            Assert.Equal(ThermalPanicTransition.None, s.Update(true, R(85), true, 95, 10));
        }

        [Fact]
        public void State_MissingOrIncompleteReading_NeverClears() {
            var s = new ThermalPanicState();
            Assert.Equal(ThermalPanicTransition.Entered, s.Update(true, R(96), false, 95, 10));
            Assert.Equal(ThermalPanicTransition.None, s.Update(true, SensorReading.None, true, 95, 10));
            Assert.Equal(ThermalPanicTransition.None, s.Update(true, R(40), false, 95, 10));
            Assert.True(s.IsActive);
            Assert.Equal(ThermalPanicTransition.Cleared, s.Update(true, R(40), true, 95, 10));
        }

        [Fact]
        public void State_DisabledWhileActive_ClearsQuietly() {
            var s = new ThermalPanicState();
            Assert.Equal(ThermalPanicTransition.None, s.Update(false, R(99), true, 95, 10));
            Assert.False(s.IsActive);
            s.Update(true, R(99), true, 95, 10);
            Assert.Equal(ThermalPanicTransition.Disabled, s.Update(false, R(99), true, 95, 10));
            Assert.False(s.IsActive);
            Assert.Equal(ThermalPanicTransition.None, s.Update(false, R(99), true, 95, 10));
            s.Update(true, R(99), true, 95, 10);
            Assert.True(s.Clear());
            Assert.False(s.Clear());
        }

        // An incomplete reading (the GPU's state is unknown) is a lower bound: it may
        // start panic, it never ends it
        [Fact]
        public void IncompleteReading_MayActivate_NeverClears() {
            Assert.False(ThermalPanicPolicy.Next(false, R(89), false, 90, 5));
            Assert.True(ThermalPanicPolicy.Next(false, R(90), false, 90, 5));
            Assert.True(ThermalPanicPolicy.Next(true, R(40), false, 90, 5));
            Assert.False(ThermalPanicPolicy.Next(true, R(40), true, 90, 5));
            Assert.False(ThermalPanicPolicy.Next(false, SensorReading.None, false, 90, 5));
            Assert.True(ThermalPanicPolicy.Next(true, SensorReading.None, false, 90, 5));
        }

        [Fact]
        public void Clears_AtThresholdMinusHysteresis() {
            Assert.True(ThermalPanicPolicy.Next(true, R(86), 90, 5));
            Assert.False(ThermalPanicPolicy.Next(true, R(85), 90, 5));
        }

        [Fact]
        public void MissingReading_NeverClears() {
            bool panic = true;
            for(int i = 0; i < 10; i++)
                panic = ThermalPanicPolicy.Next(panic, SensorReading.None, 90, 5);
            Assert.True(panic);
        }

        [Fact]
        public void MissingReading_NeverTriggers() {
            Assert.False(ThermalPanicPolicy.Next(false, SensorReading.None, 90, 5));
        }

        [Fact]
        public void HysteresisAboveThreshold_IsClamped() {
            Assert.True(ThermalPanicPolicy.Next(true, R(2), 10, 50));
            Assert.False(ThermalPanicPolicy.Next(true, R(1), 10, 50));
        }

    }

    [Collection(HardwareStateCollection.Name)]
    public class HighTemperatureTests {

        [Theory]
        [InlineData(100)]
        [InlineData(105)]
        [InlineData(120)]
        public void SensorAccepts_100AndAbove(byte celsius) {
            var rig = new TestRig();
            rig.SetCpuTemperature(celsius);
            Assert.Equal(celsius, rig.Platform.GetCpuReading(true).Value);
            Assert.Equal(celsius, rig.Platform.GetHottestReading(false).Value);
        }

        // The GPU read 92 and its reads then fail: the CPU's 55 is only a lower bound,
        // a hot CPU still counts
        [Fact]
        public void HottestReading_WithUnknownGpu_IsIncomplete() {
            var rig = new TestRig();
            rig.SetCpuTemperature(55);
            rig.Ec.GpuTemperature = 92;
            Assert.Equal(92, rig.Platform.GetHottestReading(true, out bool complete).Value);
            Assert.True(complete);

            rig.Ec.LockFails = true;
            rig.Advance(Config.TemperatureMaxAge + 1);
            Assert.Equal(55, rig.Platform.GetHottestReading(true, out complete).Value);
            Assert.False(complete);
            Assert.False(rig.Platform.GetHottestReading(false).HasValue);

            rig.SetCpuTemperature(97); // only the BIOS sensor reads
            Assert.Equal(97, rig.Platform.GetHottestReading(true, out complete).Value);
            Assert.False(complete);
        }

        // ThermalPanicState.Check is what the GUI monitor runs on every supervised pass.
        // Panic started on the GPU's 92, then the GPU stops reading: the CPU's 55 is an
        // incomplete reading and must not clear it. Once the GPU reads again, it may.
        [Fact]
        public void Check_IncompleteReading_NeverClearsPanic() {
            var rig = new TestRig();
            var panic = new ThermalPanicState();
            rig.SetCpuTemperature(55);
            rig.Ec.GpuTemperature = 92;
            rig.Platform.UpdateTemperature();
            Assert.Equal(ThermalPanicTransition.Entered, panic.Check(rig.Platform, true, 90, 5, out SensorReading hottest));
            Assert.Equal(92, hottest.Value);
            Assert.True(rig.Fans.Guard.IsPanic);

            rig.Ec.LockFails = true;
            rig.Advance(Config.TemperatureMaxAge + 1);
            rig.Platform.UpdateTemperature();
            Assert.Equal(ThermalPanicTransition.None, panic.Check(rig.Platform, true, 90, 5, out hottest));
            Assert.Equal(55, hottest.Value);
            Assert.True(panic.IsActive);
            Assert.True(rig.Fans.Guard.IsPanic);

            rig.Ec.LockFails = false;
            rig.Ec.GpuTemperature = 60;
            rig.Platform.UpdateTemperature();
            Assert.Equal(ThermalPanicTransition.Cleared, panic.Check(rig.Platform, true, 90, 5, out hottest));
            Assert.Equal(60, hottest.Value);
            Assert.False(panic.IsActive);
            Assert.False(rig.Fans.Guard.IsPanic);
        }

        // An incomplete reading at or above the threshold starts panic: the GPU's state
        // is unknown and the BIOS CPU sensor alone reads 95
        [Fact]
        public void Check_IncompleteReading_CanStartPanic() {
            var rig = new TestRig();
            var panic = new ThermalPanicState();
            rig.SetCpuTemperature(55);
            rig.Ec.GpuTemperature = 50;
            rig.Platform.UpdateTemperature();
            Assert.Equal(ThermalPanicTransition.None, panic.Check(rig.Platform, true, 90, 5, out _));

            rig.Ec.LockFails = true;
            rig.Advance(Config.TemperatureMaxAge + 1);
            rig.SetCpuTemperature(95);
            rig.Platform.UpdateTemperature();
            Assert.Equal(ThermalPanicTransition.Entered, panic.Check(rig.Platform, true, 90, 5, out SensorReading hottest));
            Assert.Equal(95, hottest.Value);
            Assert.True(rig.Fans.Guard.IsPanic);
        }

        // Turned off while active: panic ends without a reading and the fans are restored
        [Fact]
        public void Check_Disabled_RestoresTheFans() {
            var rig = new TestRig();
            var panic = new ThermalPanicState();
            rig.SetCpuTemperature(95);
            rig.Platform.UpdateTemperature();
            Assert.Equal(ThermalPanicTransition.Entered, panic.Check(rig.Platform, true, 90, 5, out _));
            Assert.True(rig.Fans.Guard.IsPanic);

            Assert.Equal(ThermalPanicTransition.Disabled, panic.Check(rig.Platform, false, 90, 5, out _));
            Assert.False(panic.IsActive);
            Assert.False(rig.Fans.Guard.IsPanic);
        }

        [Fact]
        public void SensorRejects_FirmwareGarbage() {
            var rig = new TestRig();
            rig.SetCpuTemperature(0xFF);
            Assert.False(rig.Platform.GetCpuReading(true).HasValue);
        }

    }

}
