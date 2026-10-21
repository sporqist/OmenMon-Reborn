  //\   OmenMon: Hardware Monitoring & Control Utility
 //  \  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

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
            return Next(active, hottest, true, threshold, hysteresis);
        }

        // The same, for a reading that may be incomplete: complete = false means a sensor
        // that should read did not (the GPU's state is unknown), so the hottest
        // temperature is at least the reading. Such a reading may activate panic but
        // never clears it.
        public static bool Next(bool active, SensorReading hottest, bool complete, byte threshold, byte hysteresis) {

            if(!hottest.HasValue)
                return active;

            int temperature = hottest.Value;

            if(!complete)
                return active || temperature >= threshold;

            if(!active)
                return temperature >= threshold;

            int maxHysteresis = threshold == 0 ? 0 : threshold - 1;
            int offThreshold = threshold - Math.Min((int) hysteresis, maxHysteresis);
            return temperature > offThreshold;

        }

    }

    // What a thermal-panic check changed
    public enum ThermalPanicTransition {
        None,       // no change
        Entered,    // panic starts: raise the fans
        Cleared,    // the temperature came down: restore fan control
        Disabled    // the feature was turned off while panic was active: restore quietly
    }

    // The thermal-panic state and its transitions, without the I/O: the GUI feeds it the
    // hottest reading on every supervised pass and does what the transition says (fans,
    // balloon). Written by the monitor thread, read by others (tray tooltip, menu).
    public sealed class ThermalPanicState {

        private volatile bool active;

        public bool IsActive { get { return this.active; } }

        // Applies one reading (see ThermalPanicPolicy.Next for the rules)
        public ThermalPanicTransition Update(bool enabled, SensorReading hottest, bool complete, byte threshold, byte hysteresis) {

            if(!enabled)
                return Clear() ? ThermalPanicTransition.Disabled : ThermalPanicTransition.None;

            bool next = ThermalPanicPolicy.Next(this.active, hottest, complete, threshold, hysteresis);
            if(next == this.active)
                return ThermalPanicTransition.None;

            this.active = next;
            return next ? ThermalPanicTransition.Entered : ThermalPanicTransition.Cleared;

        }

        // One thermal-panic pass on a platform whose sensors were just read (the GUI
        // monitor calls this under the hardware lock): takes the hottest validated
        // reading, which is incomplete while the GPU's state is unknown (panic may then
        // start but not end), applies it and raises or restores the fans as the
        // transition says. hottest is the reading it was judged on, for the notification.
        public ThermalPanicTransition Check(Platform platform, bool enabled, byte threshold, byte hysteresis, out SensorReading hottest) {

            hottest = platform.GetHottestReading(false, out bool complete);
            ThermalPanicTransition transition = Update(enabled, hottest, complete, threshold, hysteresis);

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

        // Leaves panic without a reading; true if it was active
        public bool Clear() {
            if(!this.active)
                return false;
            this.active = false;
            return true;
        }

    }

}
