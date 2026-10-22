  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using OmenMon.Hardware.Platform;

namespace OmenMon.Tests.Fakes {

    // The graphics driver's GPU temperature (NVML) as an unknown BIOS-only board reads
    // it: a value while the dGPU is awake, nothing while it is parked
    public sealed class FakeGpuDriver : IGpuTemperatureSource {

        public int? Celsius;   // null: parked (or no driver)
        public int Calls;

        public bool TryRead(out int celsius) {
            this.Calls++;
            celsius = this.Celsius ?? 0;
            return this.Celsius.HasValue;
        }

    }

}
