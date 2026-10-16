  //\   OmenMon: Hardware Monitoring & Control Utility
 //  \  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;

namespace OmenMon.Hardware.Platform {

    // Decides the thermal-panic state from the hottest temperature reading.
    // Pure function of its inputs, so it can be tested without a GUI.
    public static class ThermalPanicPolicy {

        // Returns whether panic should be active after this reading.
        //   * Activates at or above the threshold.
        //   * Clears at or below threshold - hysteresis (hysteresis clamped so the
        //     off point stays below the threshold).
        //   * A missing reading (SensorReading.None: failed, implausible or stale reads)
        //     never changes the state. Before, a failed read was 0 °C, and two of them
        //     cleared an active panic.
        public static bool Next(bool active, SensorReading hottest, byte threshold, byte hysteresis) {

            if(!hottest.HasValue)
                return active;

            int temperature = hottest.Value;

            if(!active)
                return temperature >= threshold;

            int maxHysteresis = threshold == 0 ? 0 : threshold - 1;
            int offThreshold = threshold - Math.Min((int) hysteresis, maxHysteresis);
            return temperature > offThreshold;

        }

    }

}
