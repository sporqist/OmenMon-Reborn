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

    }

}
