  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using OmenMon.Core;
using OmenMon.Hardware.Platform;

namespace OmenMon.Tests.Fakes {

    // The safety part of one GUI monitor pass (GuiMonitor.Pass and TickHeartbeat) over a
    // TestRig, without the GUI: sample the sensors, run the thermal-panic check, let the
    // fan-safety guard supervise, then the 0x10 heartbeat if it is due and allowed. The
    // order is the monitor's. Program ticks, the constant-speed countdown and the AC
    // debounce are left out: they only request levels, which callers do directly.
    //
    // This is the loop the shared vectors and the property tests drive; the future
    // service loop is meant to replace it with the same observable behaviour.
    public sealed class SafetyLoop {

        public TestRig Rig { get; }
        public ThermalPanicState Panic { get; } = new ThermalPanicState();

        public bool PanicEnabled = true;
        public byte PanicThreshold = 90;  // the installed 8BAC configuration
        public byte PanicHysteresis = 5;

        // The heartbeat (Config.BiosHeartbeatInterval); paused models battery power
        // with BiosHeartbeatPauseOnBattery
        public TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
        public bool HeartbeatPaused;

        private DateTime lastBeatAttempt = DateTime.MinValue;

        public SafetyLoop(TestRig rig) {
            this.Rig = rig;
        }

        // One pass; returns the thermal-panic transition it caused
        public ThermalPanicTransition Tick() {
            this.Rig.Platform.UpdateTemperature();
            ThermalPanicTransition transition = this.Panic.Check(this.Rig.Platform,
                this.PanicEnabled, this.PanicThreshold, this.PanicHysteresis, out _);
            this.Rig.Fans.Supervise();
            DateTime now = this.Rig.Bios.Now;
            if(now - this.lastBeatAttempt >= this.HeartbeatInterval) {
                this.lastBeatAttempt = now;
                if(!this.HeartbeatPaused && this.Rig.Fans.HeartbeatAllowed)
                    this.Rig.BiosCtl.GetFanCount();
            }
            return transition;
        }

    }

}
