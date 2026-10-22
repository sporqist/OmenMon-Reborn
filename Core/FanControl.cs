  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;

namespace OmenMon.Core {

    // How a board's fans are controlled
    public enum FanControlKind {

        // A model whose EC fan registers are used as configured (the legacy path): rate,
        // countdown, manual, mode and switch registers are read and, depending on the
        // settings, written
        Ec,

        // BIOS only: levels through 0x10/0x2E (via the fan-safety guard), the fan speed is
        // the 0x2D speed x 100, temperatures come from BIOS 0x23 and the EC registers the
        // model names. No EC fan register is read or written. For every board without a
        // verified EC map: on 8C77 (measured) the default fan registers point into ID,
        // serial and SMBus bytes.
        Bios

    }

    // The decisions of BIOS-only fan control, without I/O
    public static class BiosFanControl {

        // How long a fan level holds after the last 0x10/0x2E (8BAC and 8C77: ~124 s,
        // see BoardProfile.LevelExpiry). Used for the countdown a BIOS-only board shows.
        public static readonly TimeSpan DefaultLevelHold = TimeSpan.FromSeconds(124);

        // 0x2D reports each fan's speed in units of 100 rpm, measured: on 8BAC it ramps over
        // ~15 s after a set and jitters at steady state; on 8C77 it ramps through
        // intermediate values and its CPU byte equals EC 0x11 (FRPM, the fan-1 speed)
        public const int RpmPerLevel = 100;

        // The speed shown for a 0x2D level [rpm]
        public static int Rpm(int level) {
            return level <= 0 ? 0 : level * RpmPerLevel;
        }

        // Seconds a held level has left: from the last 0x10/0x2E OmenMon sent; 0 if it
        // holds none (null) or the time is up
        public static int HoldSecondsLeft(DateTime? lastRefresh, DateTime now, TimeSpan hold) {
            if(!lastRefresh.HasValue)
                return 0;
            double left = (lastRefresh.Value + hold - now).TotalSeconds;
            return left <= 0 ? 0 : (int) Math.Ceiling(left);
        }

        // Whether the level OmenMon holds has both fans off (0/0)
        public static bool IsOff(byte[] written) {
            return written != null && written.Length >= 2 && written[0] == 0 && written[1] == 0;
        }

        // The temperature sensors of a BIOS-only model: BIOS 0x23 (CPU), and the EC
        // registers the model names (TempCpuReg as "CPUT", TempGpuReg as "GPTM"; 0: none).
        // Nothing else: the global <Temperature> sensors (CPUT 0x57, GPTM 0xB7, RTMP 0x58,
        // TMP1 0x59, TNTx) are other data on these boards (on 8C77 0x57 is the keyboard
        // colour, 0x58 flag bits, 0x59 the thermal policy, which reads "80 °C" in Cool
        // mode), so they feed neither the display, the maximum, panic nor the programs.
        // Every sensor listed counts for the maximum temperature.
        public static TemperatureSensorPlan[] Sensors(byte tempCpuReg, byte tempGpuReg) {
            var plan = new System.Collections.Generic.List<TemperatureSensorPlan>();
            if(tempCpuReg != 0)
                plan.Add(new TemperatureSensorPlan("CPUT", TemperatureSource.Ec, tempCpuReg));
            if(tempGpuReg != 0)
                plan.Add(new TemperatureSensorPlan("GPTM", TemperatureSource.Ec, tempGpuReg));
            plan.Add(new TemperatureSensorPlan("BIOS", TemperatureSource.Bios, 0));
            return plan.ToArray();
        }

    }

    // Where a temperature reading comes from
    public enum TemperatureSource {
        Bios,  // BIOS 0x23
        Ec     // an EC register
    }

    // One temperature sensor to set up: its name (what the fan logic looks it up by),
    // its source and, for the EC, the register
    public readonly struct TemperatureSensorPlan {

        public readonly string Name;
        public readonly TemperatureSource Source;
        public readonly byte Register;

        public TemperatureSensorPlan(string name, TemperatureSource source, byte register) {
            this.Name = name;
            this.Source = source;
            this.Register = register;
        }

        public override string ToString() {
            return this.Name + " (" + this.Source + (this.Source == TemperatureSource.Ec ? " 0x" + this.Register.ToString("X2") : "") + ")";
        }

    }

}
