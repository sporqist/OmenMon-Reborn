  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using OmenMon.Core;

namespace OmenMon.Hardware.Platform {

    // The I/O half of thermal panic: the decision (ThermalPanicState) is in the core,
    // reading the sensors and raising or restoring the fans stays here. An extension
    // method, so the GUI still calls thermalPanic.Check(platform, ...).
    public static class ThermalPanicCheck {

        // One thermal-panic pass on a platform whose sensors were just read (the GUI
        // monitor calls this under the hardware lock): takes the hottest validated
        // reading, which is incomplete while the GPU's state is unknown (panic may then
        // start but not end), applies it and raises or restores the fans as the
        // transition says. hottest is the reading it was judged on, for the notification.
        public static ThermalPanicTransition Check(this ThermalPanicState state, Platform platform, bool enabled, byte threshold, byte hysteresis, out SensorReading hottest) {

            hottest = platform.GetHottestReading(false, out bool complete);
            ThermalPanicTransition transition = state.Update(enabled, hottest, complete, threshold, hysteresis);

            switch(transition) {

                // Level-based: every fan write is raised to the fan-table maximum and the
                // monitor re-sends it on every pass (Fans.Supervise). Max Fan (0x27) is
                // not used: it is ignored on 8BAC, freezes the EC on the
                // FanArray.HasMaxFanFreeze boards and has been linked to firmware
                // hibernation on battery.
                case ThermalPanicTransition.Entered:
                    platform.Fans.SetPanic(true);
                    break;

                // Cleared at or below (threshold - hysteresis), or the feature was turned off
                case ThermalPanicTransition.Cleared:
                case ThermalPanicTransition.Disabled:
                    platform.Fans.SetPanic(false);
                    break;

            }

            return transition;

        }

    }

}
