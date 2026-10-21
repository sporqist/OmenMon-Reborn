  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using OmenMon.Core;

namespace OmenMon.Tests {

    // SensorReadings (core): how the platform's readings combine into the temperatures
    // fan control decides on. Platform.GetCpuReading and GetHottestReading delegate to
    // it; SensorValidityTests and ThermalPanicPolicyTests cover them through the platform.
    public class SensorReadingsTests {

        private static readonly DateTime Now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(45);

        private static SensorReading R(int value, int ageSeconds = 0) {
            return new SensorReading(value, Now.AddSeconds(-ageSeconds));
        }

        private static GpuReading Gpu(int? value, int ageSeconds = 0, int? zeroAgeSeconds = null) {
            return new GpuReading(value.HasValue ? R(value.Value, ageSeconds) : SensorReading.None,
                zeroAgeSeconds.HasValue ? Now.AddSeconds(-zeroAgeSeconds.Value) : default(DateTime));
        }

        [Fact]
        public void Hotter_IgnoresMissing_AndKeepsTheFirstOnATie() {
            Assert.False(SensorReadings.Hotter(SensorReading.None, SensorReading.None).HasValue);
            Assert.Equal(50, SensorReadings.Hotter(SensorReading.None, R(50)).Value);
            Assert.Equal(50, SensorReadings.Hotter(R(50), SensorReading.None).Value);
            Assert.Equal(60, SensorReadings.Hotter(R(50), R(60)).Value);
            Assert.Equal(Now.AddSeconds(-1), SensorReadings.Hotter(R(50, 1), R(50, 2)).TakenAt);
        }

        [Fact]
        public void HottestFresh_SkipsStaleReadings() {
            Assert.False(SensorReadings.HottestFresh(new SensorReading[0], Now, MaxAge).HasValue);
            Assert.False(SensorReadings.HottestFresh(new[] { R(90, 46), SensorReading.None }, Now, MaxAge).HasValue);
            Assert.Equal(55, SensorReadings.HottestFresh(new[] { R(90, 46), R(55, 45), R(40) }, Now, MaxAge).Value);
            Assert.Equal(Now.AddSeconds(-3), SensorReadings.HottestFresh(new[] { R(55, 3), R(55, 1) }, Now, MaxAge).TakenAt);
        }

        // (CPU, GPU value, GPU age, GPU zero age, other) -> (hottest, complete)
        [Theory]
        [InlineData(60, 70, 0, null, null, 70, true)]       // both read: the hotter one
        [InlineData(60, 50, 0, null, null, 60, true)]
        [InlineData(60, null, 0, null, null, 60, true)]     // GPU never read: CPU alone
        [InlineData(60, 80, 60, 1, null, 60, true)]         // GPU parked (recent 0): CPU alone
        [InlineData(60, 80, 60, null, null, 60, false)]     // GPU unknown: incomplete
        [InlineData(null, 97, 0, null, null, 97, false)]    // no CPU: the GPU still counts
        [InlineData(null, null, 0, null, null, null, false)] // nothing reads
        [InlineData(60, 70, 0, null, 75, 75, true)]         // another sensor is hotter
        public void Hottest(int? cpu, int? gpu, int gpuAge, int? zeroAge, int? other, int? hottest, bool complete) {
            var others = other.HasValue ? new[] { R(other.Value), R(99, 46) } : new[] { R(99, 46) };
            SensorReading result = SensorReadings.Hottest(cpu.HasValue ? R(cpu.Value) : SensorReading.None,
                Gpu(gpu, gpuAge, zeroAge), others, Now, MaxAge, out bool isComplete);
            Assert.Equal(hottest.HasValue, result.HasValue);
            if(hottest.HasValue)
                Assert.Equal(hottest.Value, result.Value);
            Assert.Equal(complete, isComplete);
        }

    }

}
