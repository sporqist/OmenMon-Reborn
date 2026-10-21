  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;
using OmenMon.Core;
using OmenMon.Library;

namespace OmenMon.Hardware.Platform {

    // The fan-safety policy as configured in OmenMon.xml. The core reads no configuration,
    // so this stays in the application; as a static extension it is still called as
    // FanSafetyPolicy.FromConfig().
    public static class FanSafetyConfig {

        extension(FanSafetyPolicy) {

            public static FanSafetyPolicy FromConfig() {
                return new FanSafetyPolicy {
                    FloorCpu = Config.FanSafetyFloorCpu,
                    FloorGpu = Config.FanSafetyFloorGpu,
                    CoolTemperature = Config.FanSafetyCoolTemperature,
                    BelowFloorMaxDuration = TimeSpan.FromMinutes(Config.FanOffMaxMinutes),
                    WarmCurve = SafetyStep.ParseList(Config.FanSafetyWarmCurve) ?? FanSafetyPolicy.DefaultWarmCurve() };
            }

        }

    }

}
