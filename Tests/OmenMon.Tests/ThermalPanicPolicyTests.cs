  //\   OmenMon: Hardware Monitoring & Control Utility
 //  \  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

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

        [Fact]
        public void SensorRejects_FirmwareGarbage() {
            var rig = new TestRig();
            rig.SetCpuTemperature(0xFF);
            Assert.False(rig.Platform.GetCpuReading(true).HasValue);
        }

    }

}
