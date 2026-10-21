  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using OmenMon.Core;
using OmenMon.Hardware.Platform;
using OmenMon.Library;
using OmenMon.Tests.Fakes;

namespace OmenMon.Tests {

    // What moving the decisions into OmenMon.Core added or had to pin down. The rules
    // themselves are tested where they were before (FanSafetyGuardTests, WarmCurveTests,
    // ThermalPanicPolicyTests, the safety vectors and the property tests).
    [Collection(HardwareStateCollection.Name)]
    public class CoreTests {

        // The core never reads the system clock, so a guard without one is a bug
        // (before, it quietly fell back to DateTime.UtcNow; every caller passes one)
        [Fact]
        public void Guard_RequiresAClock() {
            Assert.Throws<ArgumentNullException>(() =>
                new FanSafetyGuard(new FanSafetyPolicy(), () => SensorReading.None, () => GpuReading.None, null!));
        }

        // The defaults the core now owns are the ones the application had
        [Fact]
        public void Policy_Defaults_AreTheApplicationsDefaults() {
            var policy = new FanSafetyPolicy();
            Assert.Equal(TimeSpan.FromSeconds(45), policy.ReadingMaxAge);
            Assert.Equal(Config.TemperatureMaxAge, FanSafetyPolicy.DefaultReadingMaxAgeSeconds);
            Assert.Equal(20, policy.FloorCpu);
            Assert.Equal(22, policy.FloorGpu);
            Assert.Equal(50, policy.CoolTemperature);
            Assert.Equal(TimeSpan.FromMinutes(15), policy.BelowFloorMaxDuration);
            Assert.Equal(TimeSpan.FromSeconds(150), policy.HandBackQuietPeriod);
            Assert.Equal("60:30/33,70:40/43,80:60/63", string.Join(",", policy.WarmCurve.Select(s => s.ToString())));
        }

        // FanSafetyPolicy.FromConfig() stays in the application (the core reads no
        // configuration) and still maps every setting
        [Fact]
        public void PolicyFromConfig_MapsEverySetting() {
            byte floorCpu = Config.FanSafetyFloorCpu, floorGpu = Config.FanSafetyFloorGpu;
            int cool = Config.FanSafetyCoolTemperature, offMinutes = Config.FanOffMaxMinutes;
            string curve = Config.FanSafetyWarmCurve;
            try {
                Config.FanSafetyFloorCpu = 25;
                Config.FanSafetyFloorGpu = 27;
                Config.FanSafetyCoolTemperature = 45;
                Config.FanOffMaxMinutes = 5;
                Config.FanSafetyWarmCurve = "65:35/38";
                FanSafetyPolicy policy = FanSafetyPolicy.FromConfig();
                Assert.Equal(25, policy.FloorCpu);
                Assert.Equal(27, policy.FloorGpu);
                Assert.Equal(45, policy.CoolTemperature);
                Assert.Equal(TimeSpan.FromMinutes(5), policy.BelowFloorMaxDuration);
                Assert.Equal("65:35/38", string.Join(",", policy.WarmCurve.Select(s => s.ToString())));
                Assert.Equal(TimeSpan.FromSeconds(Config.TemperatureMaxAge), policy.ReadingMaxAge);

                // An invalid curve falls back to the default one
                Config.FanSafetyWarmCurve = "nonsense";
                Assert.Equal(3, FanSafetyPolicy.FromConfig().WarmCurve.Length);
            } finally {
                Config.FanSafetyFloorCpu = floorCpu;
                Config.FanSafetyFloorGpu = floorGpu;
                Config.FanSafetyCoolTemperature = cool;
                Config.FanOffMaxMinutes = offMinutes;
                Config.FanSafetyWarmCurve = curve;
            }
        }

        // Fan-program curve evaluation, now in the core (FanCurve); FanProgram.SelectLevels
        // delegates to it and SharedTemperatureTests cover it through the program
        private static FanCurve Curve() {
            var table = new SortedDictionary<byte, byte[]> {
                { 36, new byte[] { 22, 25 } },
                { 50, new byte[] { 30, 33 } },
                { 70, new byte[] { 45, 48 } },
                { 85, new byte[] { 55, 58 } } };
            return new FanCurve(table.Keys, table);
        }

        [Theory]
        [InlineData(0, 36)]     // below the lowest threshold: the lowest row
        [InlineData(36, 36)]
        [InlineData(49, 36)]
        [InlineData(50, 50)]
        [InlineData(84, 70)]
        [InlineData(85, 85)]
        [InlineData(255, 85)]
        public void Curve_Threshold(byte temperature, byte threshold) {
            Assert.Equal(threshold, Curve().Threshold(temperature));
        }

        [Fact]
        public void Curve_UnknownCpu_GetsTheTopRow() {
            byte[] levels = Curve().Select(SensorReading.None, GpuState.Fresh, 40, false, out bool safe);
            Assert.True(safe);
            Assert.Equal(new byte[] { 55, 58 }, levels);
        }

        // Out-of-range values are clamped to 0..255 before the look-up, as before
        [Fact]
        public void Curve_ClampsTemperatures() {
            DateTime now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
            Assert.Equal(new byte[] { 22, 58 }, Curve().Select(new SensorReading(-5, now), GpuState.Fresh, 300, false, out bool safe));
            Assert.False(safe);
        }

        // SafeLevel: only the core can make one, so a SafeLevel is a level the rules decided on
        [Fact]
        public void SafeLevel_HasNoPublicConstructor() {
            Assert.Empty(typeof(SafeLevel).GetConstructors());
        }

        // default(SafeLevel) is not 0/0 (fans off): reading it throws
        [Fact]
        public void SafeLevel_Default_IsNotALevel() {
            SafeLevel level = default;
            Assert.False(level.IsValid);
            Assert.Throws<InvalidOperationException>(() => level.Cpu);
            Assert.Throws<InvalidOperationException>(() => level.Gpu);
            Assert.Throws<InvalidOperationException>(() => level.ToArray());
            Assert.Equal("none", level.ToString());
        }

        // The guard's SafeLevels are exactly its byte[] results
        [Theory]
        [InlineData(40, 0, 0)]      // cool: Fan Off allowed
        [InlineData(55, 0, 0)]      // warm: the floor
        [InlineData(65, 0, 0)]      // warm curve 60 °C step
        [InlineData(65, 50, 255)]   // one fan "no change"
        [InlineData(85, 70, 70)]    // above the curve already
        public void Guard_Allow_IsFilter(int cpuC, byte cpuLevel, byte gpuLevel) {
            DateTime now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
            SensorReading cpu = new SensorReading(cpuC, now);
            var guard = new FanSafetyGuard(new FanSafetyPolicy(), () => cpu, () => GpuReading.None, () => now);
            byte[] request = new byte[] { cpuLevel, gpuLevel };
            foreach(FanLevelSource source in new[] { FanLevelSource.Manual, FanLevelSource.Program }) {
                SafeLevel safe = guard.Allow(request, source);
                Assert.True(safe.IsValid);
                Assert.Equal(guard.Filter(request, source), safe.ToArray());
                Assert.Equal(safe.ToArray()[0], safe.Cpu);
                Assert.Equal(safe.ToArray()[1], safe.Gpu);
            }
            guard.RecordWrite(request, guard.Filter(request));
            Assert.Equal(guard.HandBackLevel(), guard.AllowHandBack().ToArray());
        }

        [Fact]
        public void Guard_TrySupervise_IsSupervise() {
            DateTime now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
            SensorReading cpu = new SensorReading(40, now);
            var guard = new FanSafetyGuard(new FanSafetyPolicy(), () => cpu, () => GpuReading.None, () => now);

            // Nothing written: nothing to supervise
            Assert.False(guard.TrySupervise(out SafeLevel none));
            Assert.False(none.IsValid);

            // Fan Off while cool, then the system warms up: raised to the floor
            guard.RecordWrite(new byte[] { 0, 0 }, guard.Filter(new byte[] { 0, 0 }));
            cpu = new SensorReading(55, now);
            Assert.True(guard.TrySupervise(out SafeLevel raised));
            Assert.Equal(guard.Supervise(), raised.ToArray());
            Assert.Equal(new byte[] { 20, 22 }, raised.ToArray());
        }

        [Fact]
        public void SafeLevel_Equality() {
            DateTime now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
            var guard = new FanSafetyGuard(new FanSafetyPolicy(), () => new SensorReading(55, now), () => GpuReading.None, () => now);
            SafeLevel a = guard.Allow(new byte[] { 30, 33 });
            Assert.Equal(a, guard.Allow(new byte[] { 30, 33 }));
            Assert.NotEqual(a, guard.Allow(new byte[] { 30, 34 }));
            Assert.NotEqual(a, default(SafeLevel));
            Assert.Equal("30/33", a.ToString());
        }

        // The result is a new array: changing it never changes the table
        [Fact]
        public void Curve_ReturnsACopy() {
            FanCurve curve = Curve();
            byte[] levels = curve.Select(SensorReading.None, GpuState.Parked, 0, true, out _);
            levels[0] = 0;
            Assert.Equal(new byte[] { 55, 58 }, curve.Top);
        }

    }

}
